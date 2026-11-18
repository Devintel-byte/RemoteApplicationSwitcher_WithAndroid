#!/usr/bin/env bash
# Installs or updates Show Control Hub on a plain Linux server (VPS) running Ubuntu or Debian,
# with Caddy in front for HTTPS (free certificate, renewed automatically).
#
# Use this when you don't have root on your cPanel server (a reseller account): rent a small VPS,
# point hub.yourdomain.com at it in cPanel's Zone Editor (A record), then on the VPS, as root:
#
#   bash install-hub-vps.sh hub.movingsurface.com.ng
#
# Running it again updates the hub and keeps all its data.
set -euo pipefail

DOMAIN="${1:-}"
PORT="${SC_HUB_PORT:-47900}"
HERE="$(cd "$(dirname "$0")" && pwd)"

say()  { printf '\n\033[1;36m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✔\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
die()  { printf '\n\033[1;31m✘ %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "Run this as root (sudo -i first)."
[ -n "$DOMAIN" ] || die "Usage: bash install-hub-vps.sh hub.yourdomain.com"
[ -f "$HERE/ShowControlHub" ] || die "ShowControlHub not found next to this script. Unpack the whole tar.gz first."
command -v apt-get >/dev/null || die "This script is for Ubuntu or Debian. On other systems, see docs/HUB-HOSTING.md (manual setup)."

say "1/5  Checking the address"
MYIP=$(curl -fsS --max-time 8 https://api.ipify.org 2>/dev/null || true)
DNSIP=$(getent ahostsv4 "$DOMAIN" 2>/dev/null | awk 'NR==1{print $1}' || true)
SHOWIP="${MYIP:-the IP address of this server}"
if [ -z "$DNSIP" ]; then
  warn "$DOMAIN doesn't resolve yet. In cPanel → Zone Editor add an A record: ${DOMAIN%%.*} → $SHOWIP. Continuing; HTTPS starts once it resolves."
elif [ -n "$MYIP" ] && [ "$DNSIP" != "$MYIP" ]; then
  warn "$DOMAIN points to $DNSIP, but this server is $MYIP. Fix the A record in cPanel → Zone Editor, then run this again."
else
  ok "$DOMAIN → $DNSIP (this server)"
fi

say "2/5  Installing the hub"
apt-get update -qq
apt-get install -y -qq curl ca-certificates libicu-dev >/dev/null 2>&1 || apt-get install -y -qq curl ca-certificates >/dev/null
id schub >/dev/null 2>&1 || useradd --system --home-dir /var/lib/showcontrol-hub --shell /usr/sbin/nologin schub
mkdir -p /opt/showcontrol-hub /var/lib/showcontrol-hub
systemctl stop showcontrol-hub 2>/dev/null || true
install -m 0755 "$HERE/ShowControlHub" /opt/showcontrol-hub/ShowControlHub
for f in "$HERE"/*.so; do [ -e "$f" ] && install -m 0644 "$f" /opt/showcontrol-hub/; done
chown -R schub:schub /var/lib/showcontrol-hub
chmod 0750 /var/lib/showcontrol-hub
ICU_LINE=""
ldconfig -p | grep -q libicuuc || ICU_LINE="Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"
sed -e "s/__PORT__/$PORT/" -e "s|__ICU__|$ICU_LINE|" -e "s/Only Apache (on this server)/Only Caddy (on this server)/" \
  "$HERE/showcontrol-hub.service" > /etc/systemd/system/showcontrol-hub.service
systemctl daemon-reload
systemctl enable --now showcontrol-hub >/dev/null
for i in $(seq 1 30); do curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 && break; sleep 0.5; done
curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 || { journalctl -u showcontrol-hub -n 30 --no-pager; die "The hub didn't start (log above)."; }
ok "Running on 127.0.0.1:$PORT (service showcontrol-hub, starts with the server)"

say "3/5  Installing Caddy (HTTPS)"
if ! command -v caddy >/dev/null; then
  apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https gnupg >/dev/null
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -qq && apt-get install -y -qq caddy >/dev/null
fi
ok "$(caddy version | cut -d' ' -f1)"

say "4/5  Connecting $DOMAIN to the hub"
cat > /etc/caddy/showcontrol-hub.caddy <<CADDY
# Show Control Hub (written by install-hub-vps.sh). WebSockets for PCs and phones pass through as is.
$DOMAIN {
	encode gzip
	reverse_proxy 127.0.0.1:$PORT
}
CADDY
if grep -qE '^:80[[:space:]]*\{' /etc/caddy/Caddyfile 2>/dev/null; then
  echo "import /etc/caddy/showcontrol-hub.caddy" > /etc/caddy/Caddyfile   # fresh install: replace the welcome page
elif ! grep -q 'showcontrol-hub.caddy' /etc/caddy/Caddyfile 2>/dev/null; then
  echo "import /etc/caddy/showcontrol-hub.caddy" >> /etc/caddy/Caddyfile
fi
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
systemctl enable caddy >/dev/null 2>&1 || true
systemctl reload caddy 2>/dev/null || systemctl restart caddy
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null; ok "Firewall: opened 80 and 443"
fi
ok "Caddy serves https://$DOMAIN"

say "5/5  Checking from outside"
code=""
for i in $(seq 1 20); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "https://$DOMAIN/api/health" || true)
  [ "$code" = 200 ] && break
  sleep 3
done
if [ "$code" = 200 ]; then ok "https://$DOMAIN answers"
else warn "https://$DOMAIN isn't answering yet ('$code'). The certificate is issued once the A record points here; check again in a few minutes (journalctl -u caddy -n 50)."
fi

SETUP=/var/lib/showcontrol-hub/setup-code.txt
echo
if [ -f "$SETUP" ]; then
  printf '\033[1mOpen https://%s and enter the setup code:  \033[1;33m%s\033[0m\n' "$DOMAIN" "$(cat "$SETUP")"
else
  printf '\033[1mUpdated. Open https://%s and sign in as before.\033[0m\n' "$DOMAIN"
fi
echo "Logs: journalctl -u showcontrol-hub -f     Data to back up: /var/lib/showcontrol-hub"
