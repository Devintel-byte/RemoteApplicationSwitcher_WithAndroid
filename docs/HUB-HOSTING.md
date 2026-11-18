# Hosting the hub

The hub gives phones and PCs one address that works from anywhere: **https://hub.movingsurface.com.ng**.
It's a small background program (a service) behind HTTPS. Pick the way that matches your account:

| Your hosting | Do this | Time |
|---|---|---|
| **Reseller or plain cPanel account** (no root, no WHM Terminal) | **[Way B: a small VPS](#way-b-a-small-vps-recommended-for-reseller-accounts)**. Your domain and email stay on cPanel; only the `hub` subdomain points to the VPS | 20 min, about US$5/month |
| **Your own server with WHM root** | **[Way A: on the cPanel server](#way-a-on-your-cpanel-server-root)** behind cPanel's Apache and AutoSSL | 20 min |

A reseller account can't run background services or change Apache, and its hosting company usually stops
long-running programs, so Way A isn't possible there. That's a limit of the plan, not a setting you can turn on.

---

# Way B: a small VPS (recommended for reseller accounts)

```
phones ─┐                       ┌──────────── VPS (Ubuntu) ────────────┐
        ├─ https / wss ────────▶│ Caddy (free certificate, auto-renew) │
PCs ────┘   port 443            │   └─▶ 127.0.0.1:47900 ─▶ hub service  │
                                └───────────────────────────────────────┘
hub.movingsurface.com.ng ──(A record in your cPanel Zone Editor)──▶ the VPS's IP
```

**B1. Rent the VPS.** Any provider works (Hetzner, Contabo, DigitalOcean, Vultr, Linode…). Choose
**Ubuntu 24.04**, the smallest plan (1 vCPU, 1 GB RAM is plenty), a region near your venues. Note its
**IP address** and **root password** (or add your SSH key).

**B2. Point the subdomain at it.** In **cPanel → Domains → Zone Editor** → `movingsurface.com.ng` →
**+ A Record**: name `hub`, address = the VPS's IP. (If `hub.movingsurface.com.ng` already exists as a
cPanel subdomain, remove it first under *Domains*, or edit its A record to the VPS's IP.) It usually
works within minutes; check with `nslookup hub.movingsurface.com.ng` on your PC.

**B3. Build the package.** On your PC, double-click **`BUILD.cmd`**. It makes
`installer\Output\showcontrol-hub-1.4.0-linux-x64.tar.gz`.

**B4. Upload and install.** In PowerShell on your PC (Windows has `ssh` and `scp` built in):

```powershell
scp "C:\Users\MOVING SURFACE\Documents\Android_PC_RemoteControlApp\installer\Output\showcontrol-hub-1.4.0-linux-x64.tar.gz" root@VPS_IP:/root/
ssh root@VPS_IP
```

Then on the VPS:

```bash
mkdir -p /root/showcontrol-hub && cd /root/showcontrol-hub
tar xzf /root/showcontrol-hub-1.4.0-linux-x64.tar.gz
bash install-hub-vps.sh hub.movingsurface.com.ng
```

It checks the A record, installs the hub as a service (starts with the server, restarts itself), installs
**Caddy** and gets the HTTPS certificate, opens ports 80 and 443 if the firewall is on, and prints the
**setup code**. Then continue with [First sign-in](#5-first-sign-in).

**Updating later:** upload the new `.tar.gz` and run the same three lines. Data is kept.
**Logs:** `journalctl -u showcontrol-hub -f` (hub), `journalctl -u caddy -n 50` (certificate).
**Backups:** copy `/var/lib/showcontrol-hub` (for example `tar czf hub-backup.tgz -C /var/lib showcontrol-hub`).

---

# Way A: on your cPanel server (root)

This puts the hub on the server that runs your cPanel, behind its Apache with AutoSSL.

```
phones ─┐                     ┌─────────────── your server ───────────────┐
        ├─ https / wss ──────▶│ Apache (cPanel, AutoSSL certificate)      │
PCs ────┘   port 443          │   └─ forwards to 127.0.0.1:47900 ─▶ hub   │
                              │      (systemd service, data in            │
                              │       /var/lib/showcontrol-hub)           │
                              └───────────────────────────────────────────┘
```

**You need:** WHM signed in as **root** (WHM → Server Configuration → Terminal), or root over SSH.

---

## 1. Create the subdomain

In **cPanel → Domains → Create A New Domain**:

- **Domain:** `hub.movingsurface.com.ng`
- Leave the document root as suggested (the hub doesn't use it). Untick *Share document root* if offered.
- **Submit.**

Note the **cPanel username** shown at the top right of cPanel (or in WHM → *List Accounts*). You'll need
it in step 4.

**DNS.** If your domain's nameservers point at this server, cPanel adds the `hub` record itself. If the
DNS lives elsewhere (your registrar, Cloudflare…), add an **A record** there:
`hub` → your server's IP address (shown in cPanel's right-hand *General Information* panel).

> **Cloudflare:** set the `hub` record to **DNS only** (grey cloud) at least until step 2 has issued the
> certificate. You can switch the orange cloud on afterwards (WebSockets work through Cloudflare); then
> set SSL/TLS mode to **Full (strict)**.

## 2. Get the HTTPS certificate

**cPanel → SSL/TLS Status** → tick `hub.movingsurface.com.ng` → **Run AutoSSL**. A new DNS record can take
a few minutes (up to an hour) before this succeeds.

Check: open `https://hub.movingsurface.com.ng` in a browser. You should get a padlock and a default
cPanel page. That's fine: the hub replaces it in step 4.

## 3. Build the hub package (on your PC)

Double-click **`BUILD.cmd`** in the project folder. Among other things it makes:

```
installer\Output\showcontrol-hub-1.4.0-linux-x64.tar.gz
```

It's self-contained, so the server doesn't need .NET installed.

## 4. Upload and install

**Upload:** in **cPanel → File Manager**, open your home folder (`/home/<cpanel-user>`) and **Upload**
the `.tar.gz`. (Or `scp` it to the server.)

**Install:** open **WHM → Server Configuration → Terminal** (a root shell in the browser; or `ssh root@…`).
Run these three lines, replacing `CPUSER` with your cPanel username:

```bash
mkdir -p /root/showcontrol-hub && cd /root/showcontrol-hub
tar xzf /home/CPUSER/showcontrol-hub-1.4.0-linux-x64.tar.gz
bash install-hub.sh hub.movingsurface.com.ng CPUSER
```

The script reports six steps:

1. checks the subdomain belongs to that cPanel user
2. installs the hub in `/opt/showcontrol-hub` with its data in `/var/lib/showcontrol-hub`
3. runs it as the `showcontrol-hub` service (own system user, starts with the server, restarts if it ever stops)
4. makes sure Apache's proxy and WebSocket modules are on (installs them through EasyApache 4 if not)
5. adds two small files that cPanel keeps across updates:
   `/etc/apache2/conf.d/userdata/ssl/2_4/CPUSER/hub.movingsurface.com.ng/showcontrol-hub.conf` (HTTPS → hub) and
   `…/std/2_4/…/showcontrol-hub.conf` (HTTP → HTTPS redirect), then rebuilds Apache's config
6. checks `https://hub.movingsurface.com.ng` answers, and prints the **setup code**

## 5. First sign-in

Open **https://hub.movingsurface.com.ng**, enter the setup code and create your admin account. Then:

- **Branding:** name, logo, accent colour, time zone (*Africa/Lagos*)
- **People:** accounts for your team (*admin*, *operator*) and tour guides (*viewer*: they only use the kiosks
  you assign them)
- **PCs → Add a PC:** gives a one-time code. On each show PC: **dashboard → Hub** → address
  `https://hub.movingsurface.com.ng` → code → **Connect**
- **Phones:** in the app, **Settings → Remote access → Sign in to a hub** → `hub.movingsurface.com.ng`

## Add the site hub at the venue

The hosted hub gives you access from anywhere. Add a **site hub** on a PC at the venue so the local
network keeps working without internet (kiosk screens load from it):

1. On the venue PC: unzip `ShowControlHub-1.4.0-windows.zip` (also made by `BUILD.cmd`) and double-click
   **Install-Hub.cmd**. It starts with Windows in the background on port 47900 and prints its address.
2. Here on **https://hub.movingsurface.com.ng → Linked hubs → Make a link code**.
3. On the venue PC's hub (`http://localhost:47900`): **Join an existing hub** → this address and the code.

From then on the two stay in sync both ways, PCs connect to both, and each schedule runs once. Details:
*docs/SETUP.md → 4d*. Nothing extra is needed on the server: the site hub connects out to it over HTTPS
(`wss://hub.movingsurface.com.ng/hubsync`), through the same Apache setup.

## Updating the hub later

Build a new package (BUILD.cmd), upload it, and run the same three lines. Accounts, PCs, schedules and
shared rooms are kept; PCs and phones reconnect by themselves within a few seconds.

## Backups

cPanel's account backups **don't** include the hub (it lives outside your home folder). Add a nightly copy
in **WHM Terminal**:

```bash
( crontab -l 2>/dev/null; echo '15 3 * * * tar czf /home/CPUSER/hub-backup-$(date +\%a).tar.gz -C /var/lib showcontrol-hub' ) | crontab -
```

That keeps seven rolling backups (one per weekday) in your home folder, where cPanel's backups pick them up.
To restore one: `systemctl stop showcontrol-hub`, unpack it into `/var/lib`, then
`chown -R schub:schub /var/lib/showcontrol-hub && systemctl start showcontrol-hub`.

## Useful commands (WHM Terminal)

| What | Command |
|---|---|
| Is it running? | `systemctl status showcontrol-hub` |
| Live log | `journalctl -u showcontrol-hub -f` |
| Restart | `systemctl restart showcontrol-hub` |
| Check without Apache | `curl http://127.0.0.1:47900/api/health` |
| Setup code (first run only) | `cat /var/lib/showcontrol-hub/setup-code.txt` |

## Troubleshooting

| Symptom | Fix |
|---|---|
| Browser shows cPanel's default page | The Apache files aren't active. Run the install line again and look at step 5's output. |
| **503 Service Unavailable** | The hub isn't running: `systemctl status showcontrol-hub`, then `journalctl -u showcontrol-hub -n 50`. |
| The admin page loads but PCs/phones never connect, or the live view stays on *Connecting* | WebSockets are blocked. Check `httpd -M \| grep wstunnel` shows `proxy_wstunnel_module`. If you use Cloudflare, make sure *Network → WebSockets* is on. |
| Sign-in or saving gives **403** / "Forbidden" | ModSecurity is blocking API requests. **cPanel → ModSecurity** → turn it **off for hub.movingsurface.com.ng** only. |
| AutoSSL fails | DNS isn't pointing here yet (check with `dig +short hub.movingsurface.com.ng`), or Cloudflare's proxy is on. Use *DNS only* until the certificate is issued. |
| PCs connect, then drop every minute or so | Something between them and the server cuts idle connections. The hub pings every 20 s; if you use Cloudflare, that's fine. On the server, `ProxyTimeout 3600` is already set. |
| Port 47900 is already used by something else | Install with another port: `SC_HUB_PORT=47910 bash install-hub.sh hub.movingsurface.com.ng CPUSER`. |

## Removing it

```bash
systemctl disable --now showcontrol-hub
rm /etc/systemd/system/showcontrol-hub.service
rm /etc/apache2/conf.d/userdata/{ssl,std}/2_4/CPUSER/hub.movingsurface.com.ng/showcontrol-hub.conf
/usr/local/cpanel/scripts/rebuildhttpdconf && /usr/local/cpanel/scripts/restartsrv_httpd
rm -rf /opt/showcontrol-hub          # and /var/lib/showcontrol-hub to delete all hub data
```

## No root access

Use [Way B](#way-b-a-small-vps-recommended-for-reseller-accounts). Alternatively, run the hub on an office
PC (`ShowControlHub-1.4.0-windows.zip` → Install-Hub.cmd) and publish it with **Cloudflare Tunnel**; that
needs your domain's DNS on Cloudflare and the PC switched on.
