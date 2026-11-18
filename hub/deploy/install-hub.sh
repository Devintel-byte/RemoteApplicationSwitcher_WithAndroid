#!/usr/bin/env bash
# Installs or updates Show Control Hub on a cPanel/WHM server (AlmaLinux, Rocky, CloudLinux or Ubuntu).
# Run as root from the folder you unpacked showcontrol-hub-linux-x64.tar.gz into:
#
#   bash install-hub.sh hub.movingsurface.com.ng <cpanel-username>
#
# What it does: installs the hub in /opt/showcontrol-hub (data in /var/lib/showcontrol-hub), runs it as a
# systemd service listening only on 127.0.0.1, and plugs it into cPanel's Apache virtual host for the
# subdomain so it's served over HTTPS with the AutoSSL certificate. Running it again updates the hub and
# keeps all data (accounts, PCs, schedules, shared rooms).
set -euo pipefail

DOMAIN="${1:-}"
CPUSER="${2:-}"
PORT="${SC_HUB_PORT:-47900}"
HERE="$(cd "$(dirname "$0")" && pwd)"

say()  { printf '\n\033[1;36m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✔\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
die()  { printf '\n\033[1;31m✘ %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "Run this as root (WHM → Server Configuration → Terminal, or ssh root@your-server)."
[ -n "$DOMAIN" ] && [ -n "$CPUSER" ] || die "Usage: bash install-hub.sh hub.yourdomain.com <cpanel-username>"
[ -f "$HERE/ShowControlHub" ] || die "ShowControlHub not found next to this script. Unpack the whole tar.gz first."
id "$CPUSER" >/dev/null 2>&1 || die "No cPanel user '$CPUSER'. It's the username you sign in to cPanel with (WHM → List Accounts)."

say "1/6  Checking the subdomain"
if [ -d /var/cpanel/userdata/"$CPUSER" ] && ! ls /var/cpanel/userdata/"$CPUSER" | grep -qx "$DOMAIN"; then
  die "cPanel doesn't know $DOMAIN for user $CPUSER yet. Create it first: cPanel → Domains → Create A New Domain."
fi
ok "$DOMAIN belongs to $CPUSER"

say "2/6  Installing the hub"
id schub >/dev/null 2>&1 || useradd --system --home-dir /var/lib/showcontrol-hub --shell /sbin/nologin schub
mkdir -p /opt/showcontrol-hub /var/lib/showcontrol-hub
systemctl stop showcontrol-hub 2>/dev/null || true
install -m 0755 "$HERE/ShowControlHub" /opt/showcontrol-hub/ShowControlHub
# Any native libraries published next to it (normally none for a single-file build)
for f in "$HERE"/*.so; do [ -e "$f" ] && install -m 0644 "$f" /opt/showcontrol-hub/; done
chown -R schub:schub /var/lib/showcontrol-hub
chmod 0750 /var/lib/showcontrol-hub
ok "/opt/showcontrol-hub/ShowControlHub, data in /var/lib/showcontrol-hub"

# .NET uses ICU for text handling; install it if missing, otherwise run in invariant mode.
ICU_LINE=""
if ! ldconfig -p | grep -q libicuuc; then
  if command -v dnf >/dev/null; then dnf -y -q install libicu >/dev/null 2>&1 || true
  elif command -v yum >/dev/null; then yum -y -q install libicu >/dev/null 2>&1 || true
  elif command -v apt-get >/dev/null; then apt-get -y -q install libicu-dev >/dev/null 2>&1 || true
  fi
fi
ldconfig -p | grep -q libicuuc || { ICU_LINE="Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"; warn "libicu not available: running in invariant-globalization mode (fine for the hub)"; }

say "3/6  Starting it as a service"
sed -e "s/__PORT__/$PORT/" -e "s|__ICU__|$ICU_LINE|" "$HERE/showcontrol-hub.service" > /etc/systemd/system/showcontrol-hub.service
systemctl daemon-reload
systemctl enable --now showcontrol-hub >/dev/null
for i in $(seq 1 30); do
  curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 && break
  sleep 0.5
done
curl -fsS "http://127.0.0.1:$PORT/api/health" >/dev/null 2>&1 || { journalctl -u showcontrol-hub -n 30 --no-pager; die "The hub didn't start (log above)."; }
ok "showcontrol-hub is running on 127.0.0.1:$PORT and starts with the server"

say "4/6  Apache modules"
need=()
for m in proxy_module proxy_http_module proxy_wstunnel_module rewrite_module headers_module; do
  httpd -M 2>/dev/null | grep -q "$m" || apachectl -M 2>/dev/null | grep -q "$m" || need+=("$m")
done
if [ ${#need[@]} -gt 0 ]; then
  warn "Missing: ${need[*]} — installing via EasyApache 4"
  pkgs=()
  for m in "${need[@]}"; do
    case "$m" in
      proxy_module) pkgs+=(ea-apache24-mod_proxy) ;;
      proxy_http_module) pkgs+=(ea-apache24-mod_proxy_http) ;;
      proxy_wstunnel_module) pkgs+=(ea-apache24-mod_proxy_wstunnel) ;;
      headers_module) pkgs+=(ea-apache24-mod_headers) ;;
    esac
  done
  if [ ${#pkgs[@]} -gt 0 ]; then
    if command -v dnf >/dev/null; then dnf -y install "${pkgs[@]}"
    elif command -v yum >/dev/null; then yum -y install "${pkgs[@]}"
    elif command -v apt-get >/dev/null; then apt-get -y install $(printf '%s\n' "${pkgs[@]}" | tr '_' '-')
    fi
  fi
fi
ok "proxy, proxy_http, proxy_wstunnel, rewrite, headers"

say "5/6  Connecting $DOMAIN to the hub"
SSL_DIR=/etc/apache2/conf.d/userdata/ssl/2_4/"$CPUSER"/"$DOMAIN"
STD_DIR=/etc/apache2/conf.d/userdata/std/2_4/"$CPUSER"/"$DOMAIN"
mkdir -p "$SSL_DIR" "$STD_DIR"
sed "s/__PORT__/$PORT/g" "$HERE/apache-ssl.conf" > "$SSL_DIR/showcontrol-hub.conf"
cp "$HERE/apache-std.conf" "$STD_DIR/showcontrol-hub.conf"
if [ -x /usr/local/cpanel/scripts/rebuildhttpdconf ]; then
  /usr/local/cpanel/scripts/rebuildhttpdconf >/dev/null
  /usr/local/cpanel/scripts/restartsrv_httpd >/dev/null
else
  apachectl configtest && apachectl graceful
fi
ok "Apache now forwards https://$DOMAIN to the hub"

say "6/6  Checking from outside"
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "https://$DOMAIN/api/health" || true)
if [ "$code" = 200 ]; then
  ok "https://$DOMAIN answers"
else
  warn "https://$DOMAIN gave '$code'. If the certificate isn't issued yet: cPanel → SSL/TLS Status → Run AutoSSL, wait a few minutes, then open the address."
fi

SETUP=/var/lib/showcontrol-hub/setup-code.txt
echo
if [ -f "$SETUP" ]; then
  printf '\033[1mOpen https://%s and enter the setup code:  \033[1;33m%s\033[0m\n' "$DOMAIN" "$(cat "$SETUP")"
else
  printf '\033[1mUpdated. Open https://%s and sign in as before.\033[0m\n' "$DOMAIN"
fi
echo "Logs: journalctl -u showcontrol-hub -f     Data to back up: /var/lib/showcontrol-hub"
