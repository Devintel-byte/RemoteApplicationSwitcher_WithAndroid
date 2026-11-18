# Setup

## 1. What to install

**On your development PC**

| Tool | Why |
|---|---|
| [Android Studio](https://developer.android.com/studio) (current stable) | Builds and installs the phone app. It bundles the JDK and downloads Gradle and the Android SDK. |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer) | Builds the PC agent. |
| Visual Studio 2022 *or* VS Code with the C# Dev Kit (optional) | For editing the agent. The command line is enough to build it. |

**On each show PC:** Windows 10/11. If you publish the agent as self-contained (below), nothing else is needed.

**Phone:** Android 8.0 or newer, on the same Wi-Fi/LAN as the PCs.

## 2. Build and run the PC agent

Open a terminal in `agent\ShowControl.Agent` and run:

```powershell
dotnet run
```

A yellow play icon appears in the system tray, with a notification showing the PC name and the **pairing PIN**.

- **Windows Firewall** will ask about *ShowControlAgent*. Allow it on **Private networks**. If you missed the prompt, run this once as administrator:
  ```powershell
  netsh advfirewall firewall add rule name="Show Control Agent" dir=in action=allow protocol=TCP localport=47800 profile=private
  netsh advfirewall firewall add rule name="Show Control Agent discovery" dir=in action=allow protocol=UDP localport=47801 profile=private
  ```
  Also make sure the PC's network is set to **Private**, not Public (Settings → Network → your connection).
- **The dashboard opens by itself on first run.** Later, double-click the tray icon (or right-click → *Open dashboard*). It opens as its own window and is where everything on this PC is set up. Section 5 walks through it.

### Register your apps

Use the dashboard: **Apps → Add app → Choose on this PC** (section 5). Everything is saved to `%LOCALAPPDATA%\ShowControl\agent.json`, which you can still edit by hand. It reloads as soon as you save it, and `agent.sample.json` next to the code documents every option. Typical entry:

```jsonc
{
  "id": "touchdesigner",
  "name": "TouchDesigner",
  "path": "C:\\Program Files\\Derivative\\TouchDesigner\\bin\\TouchDesigner.exe",
  "arguments": "\"D:\\Shows\\Lobby\\mapping.toe\"",
  "processName": "TouchDesigner",
  "color": "violet",
  "watchdog": true
}
```

- `processName` is the name in Task Manager's *Details* tab without `.exe` (e.g. Resolume is `Arena`, OBS is `obs64`).
- For a **Unity build**, point `path` at the build's `.exe`. Use `arguments` for `-screen-fullscreen 1 -monitor 2`, etc.
- OBS needs `workingDirectory` set to its `bin\64bit` folder. Its `--disable-shutdown-check` flag avoids the "safe mode" prompt after a force close.

Scenes are lists of steps (`start`, `stop`, `restart`, `stopAll`, `wait`, `waitForPort`) that run in order on that PC. See `agent.sample.json`.

### Deploy to a show PC: the installer

Build the installer once, on your development PC (it needs the .NET 8 SDK; the show PCs need nothing):

```powershell
winget install JRSoftware.InnoSetup        # once, for the Setup.exe (optional)
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

`installer\Output` then holds:

| File | Use |
|---|---|
| `ShowControlAgent-Setup-1.4.0.exe` | The normal installer. Installs to Program Files, adds a Windows Firewall rule for the agent, starts it when the user signs in, adds it to the Start menu and starts it. Upgrading over an older version keeps all settings |
| `ShowControlAgent-1.4.0-portable.zip` | The same exe plus `install.ps1`, for PCs where you'd rather not run a Setup.exe. Unzip and run `powershell -ExecutionPolicy Bypass -File install.ps1` (add `-PcName "Stage Left"`, `-NoStartup` or `-Uninstall`) |

The exe is **self-contained**: the .NET runtime is inside it (about 70 MB), so nothing else needs installing on the show PC.

**Many PCs at once (silent):**

```powershell
ShowControlAgent-Setup-1.4.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PCNAME="Stage Left"
```

Run it while the show user is signed in: start-with-Windows and the PC name are set for that user. Add `/TASKS="firewall"` to skip start-with-Windows, or `/TASKS="firewall,startup,desktopicon"` for a desktop shortcut too.

Settings, apps, scenes and paired phones live in `%LOCALAPPDATA%\ShowControl\agent.json` and are never touched by upgrades or uninstalling. For unattended show PCs, also set Windows to **sign in automatically**: the agent runs in the signed-in user's session, which is what lets it open windowed apps on the screens.

**Without the installer**, you can still `dotnet run` from `agent\ShowControl.Agent` (above) or publish by hand:
`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish`.

## 3. Build and run the Android app

1. Android Studio → **Open** → select the `android` folder (not the repo root).
   - If you see *"Incompatible Gradle JVM version … Gradle JVM version 25"*: **File → Settings → Build, Execution, Deployment → Build Tools → Gradle → Gradle JDK** → choose a **21** JDK (or *Download JDK…* → 21), then sync again. Decline Gradle 9 / AGP 9 upgrade prompts for now.
2. Wait for the Gradle sync. The first time, it downloads Gradle 8.9, the Android SDK 35 platform and libraries, which takes a few minutes. If Studio offers to upgrade the Android Gradle Plugin, it's fine to accept.
3. On your phone: Settings → About phone → tap *Build number* 7 times. Then Developer options → enable **USB debugging** (or *Wireless debugging*).
4. Plug the phone in, pick it in the device dropdown, and press **Run ▶**.

**Using the app:** tap **Add room** and create a room (e.g. *Test Lab*). Open the room and tap **+**. Pick your PC from the scan list (or type its IP), enter the PIN from the tray menu, then tap **Pair and add**.

To build an installable APK for other phones: **Build → Build App Bundle(s) / APK(s) → Build APK(s)**. The release build is signed with the debug key for now. Set up a proper keystore before distributing it widely.

### Claude in Android Studio

Android Studio is built on IntelliJ, so Claude Code runs in its built-in terminal (`claude`), and the JetBrains integration shows diffs in the editor. It's handy for fixing Gradle errors or reading logcat output in place.

## 4. Show scenes, cues and Unity (milestone 2)

**Show scenes (on the phone)**: in a room, tap **New show scene** (or *Edit tiles* → **New tile**). Give it a name, colour label, icon and safety mode, then add steps: *Wake PC*, *Start app*, *Close app*, *Restart app*, *Run PC scene*, *Close all apps on a PC*, *Wait*. Steps run in order. Each one can wait until it's done (app running, scene finished, PC online) before the next. Use *Edit tiles* to reorder or edit tiles.

**Cues (on a PC)**: add scenes with `osc`, `udp`, `http` or `obs` steps to `agent.json`, and give them a `"group"` such as `"Cues"` so the phone shows them in their own section. See `agent.sample.json` for ready-made examples:

| Target | Setup on the target | Step |
|---|---|---|
| Resolume | Preferences → OSC → enable *OSC Input*, port 7000 | `{ "action": "osc", "port": 7000, "address": "/composition/layers/1/clips/1/connect", "args": [1] }` |
| TouchDesigner | OSC In CHOP or DAT on a port, e.g. 10000 | `{ "action": "osc", "port": 10000, "address": "/reset", "args": [1] }` |
| OBS | Tools → WebSocket Server Settings → enable; copy the password into `"obs"` in agent.json | `{ "action": "obs", "request": "SetCurrentProgramScene", "data": { "sceneName": "Live" } }` |
| Unity build | Add `unity/ShowControlReceiver.cs` (below) | `{ "action": "osc", "port": 9000, "address": "/game/level", "args": ["next"] }` |
| Anything with a web API | – | `{ "action": "http", "method": "POST", "url": "http://…", "body": "{…}" }` |

Cues can target another PC too: set `"host"` to its IP.

**Unity**: copy `unity/ShowControlReceiver.cs` into your project's `Assets` folder and add the component to a GameObject in the first scene. Then, in the Inspector, add *Bindings*: a command such as `/game/start` or `reset`, wired to your own methods. It understands OSC and plain text on UDP port 9000. Built-ins: `/showcontrol/quit`, `/showcontrol/reload` and `/showcontrol/scene <name>`. When you build, allow the game through the Windows firewall (Private).

**Wake-on-LAN**: the phone learns each PC's MAC address the first time it connects. On each show PC, enable *Wake on LAN* / *Power On by PCI-E* in the BIOS. In Device Manager → network adapter → *Power Management*, tick *Allow this device to wake the computer*, and under *Advanced* enable *Wake on Magic Packet*. Use wired Ethernet: Wi-Fi rarely supports it. If the phone's broadcast doesn't reach (e.g. different switch or VLAN), any online PC in the same room relays the wake signal automatically.

**Live view**: the PC detail screen streams what each display is showing, up to 30 frames a second (a red **LIVE · fps** badge shows the real rate). Tap it for full screen, or pick another display under it. It only runs while that screen is open and the app is in front. **Settings → Live view** sets the quality: *Smooth* (30 fps) for the venue network, *Balanced* (15 fps), or *Data saver* (5 fps, smaller) on mobile data through the hub. The stream never builds up delay: on a slow link you get fewer frames, not older ones. It needs the PC signed in and unlocked (otherwise the badge says *paused* and it resumes by itself). Apps in *exclusive* fullscreen may show black, so use borderless/windowed fullscreen (Unity: *Fullscreen Window* mode). Watching a PC's screen is in the **Android app only**: the PC dashboard and the hub don't show it, so a PC only spends CPU/GPU on capturing while a phone is actually watching.

## 4b. Watch-only phones, crash alerts and sharing rooms (milestone 4)

**Watch-only phones.** Hand a phone to a client or crew member who should see but not touch:
- On the local network: in the PC's dashboard → **Phones**, set that phone to **Watch only** (back to *Can control* any time). The phone reconnects straight away.
- Hub accounts with the **Viewer** role are *tour guides* (section 4d), not watch-only phones. For a hub
  user who should only watch, pair their phone on the LAN and set it to *Watch only*, or give them an
  operator account only if they're trusted to control.

A watch-only phone sees status, the live view, CPU and memory, but no Start, Close, Run, Stop all or Power buttons, and its tiles show *View only*. The PC (and the hub) refuse those commands from it as well, so hiding them isn't the only protection.

**Crash alerts.** The phone notifies you when an app on any PC crashes (and quietly updates the notification when the watchdog has it running again). Tap it to open that PC. In **Settings → Alerts**:
- *App crashes*: on by default. On Android 13+ the app asks for notification permission once; if you said no, Settings shows **Allow notifications**.
- *PC offline*: a notification when a PC has been unreachable for 30 seconds, and when it's back.
- *Keep watching in the background*: alerts keep arriving after you close the app. Android requires a small ongoing "Watching your show PCs" notification while this is on (with a *Stop watching* button). Without it, alerts arrive while the app is open or recently used.

**Share a room's tiles.** Open a room and tap the **Share** icon:
- **Send as a code**: shares a message (WhatsApp, email, Slack…). On the other phone, share that message *to Show Control*, or copy it and use **Rooms → Import** (the download icon at the top) → *Paste*.
- **Publish to your hub**: everyone signed in to the hub sees it under **Rooms → Import → Published on …** and can import it. Publishing again updates it; importing again updates their copy. Admins see and remove shared rooms on the hub's **Shared rooms** page.

The import screen shows what will happen first. PCs the other phone already has are linked straight away; hub PCs work once it's signed in to the hub; any other PC is added and asks for its **pairing PIN** when opened. Pairing keys and passwords are never shared.

## 4c. Room kiosks and whole-site power (milestone 5)

**Room kiosks** are touch screens for visitors: a grid of covers they tap to switch what plays in the room.
They're set up on the hub and shown by a PC in the room.

1. **The kiosk PC.** Any Windows PC with a touch screen (or mouse) in the room, with **Microsoft Edge** and
   the Show Control Agent installed (the installer), connected to the hub like any other PC (its dashboard → Hub).
   For an unattended screen also: Windows auto sign-in, *Sleep: never*, and Windows notifications off.
2. **On the hub → Kiosks → New kiosk:**
   - name, the room name shown on screen, and a welcome line
   - **PCs in this room**: the PCs whose scenes and apps the kiosk can use
   - **Items**: for each, pick the PC, *Run a scene* or *Start an app*, the title and small text, a colour,
     and a **cover image** (16:9 JPG/PNG/WebP, up to 3 MB; 1920 × 1080 is plenty)
   - **Display PC**: the kiosk PC. It opens the screen within seconds
   - **Kiosk mode**: on = locked full screen (no address bar, tabs or menus, and it reopens itself if closed);
     off = a normal window
   - **Close the previous app when a visitor switches**: for items that start apps. For anything more
     involved (stop A, fade, start B), make a scene on the content PC and use *Run a scene*
   - **Staff exit PIN** (optional): on the screen, press and hold the logo for 3 seconds, then enter the PIN.
     The kiosk closes and stays closed until someone presses **Resume** on the hub
3. Visitors tap a cover: the item starts, its tile shows **Now playing**, and tiles whose PC is off are
   dimmed. Every choice appears in the hub's **Activity**.

Without a display PC, use **Open screen** / **Copy link** to show the kiosk on any device: a tablet's
browser, a smart TV. **New link** makes the old link stop working (a tablet that walked off). On the kiosk PC,
the agent's tray menu has **Close kiosk screen / Open kiosk screen** for staff on site.

> Edge's kiosk mode stops visitors leaving the screen with touch, mouse and normal keys. With a full keyboard,
> Ctrl+Alt+Del still works (Windows doesn't let apps block it); the agent reopens the kiosk within seconds
> if it's closed. For a fully locked-down PC, also use Windows *Assigned Access* (Settings → Accounts → Other
> users → Set up a kiosk) for a dedicated kiosk account that runs the agent.

**Whole-site power.** On the phone's main screen, **All PCs** has *Turn on*, *Restart* and *Shut down* for
every PC on the phone, and each room has the same for its own PCs (at the bottom of the room). The
confirmation names the PCs it will act on and the ones it skips: PCs without *Allow restart and shut down*
(PC dashboard → Settings), offline ones, ones with no MAC address learned yet, and watch-only ones.
*Turn on* uses Wake-on-LAN: broadcast from the phone, repeated by every online PC, and through the hub for
hub PCs. On the hub, admins have the same three buttons on the **PCs** page, and **Schedules** can now
*Restart* or *Shut down* PCs too (for example *Wake* at 08:00 and *Shut down* at 23:30 every day).

## 4d. Tour guides, the site hub and linked hubs (milestone 6)

**Tour guides (Viewer accounts).** A viewer is someone who walks visitors round and switches what plays:
they see *only* the kiosks assigned to them, nothing else on the hub.

1. Hub → **People → Add person**, role **Viewer (tour guide)**.
2. Hub → **Kiosks** → open a kiosk → **Tour guides** → tap their name (as many kiosks as you like, on any PCs).
3. They open the hub's address on a tablet or phone and sign in. They land on **Your kiosks**: pick a PC,
   then one of its kiosks, and it opens **full screen** with the same cover grid visitors see. **‹ Kiosks**
   goes back; the corner button leaves full screen; *Sign out* is on the PC list.

In the Android app, a viewer account shows **Open my kiosks** instead of the PC list. Viewers can't open PCs,
shared rooms or the admin pages (the hub refuses them, not just hides them). Operators and admins can use
**Tour guide view** (bottom of the hub's menu) to see every kiosk the same way.

**The site hub: the hub on the venue's network.** Install the hub on one always-on PC at the venue too. It
keeps everything working on the local network without internet, and the kiosk screens load from it.

1. `BUILD.cmd` makes `installer\Output\ShowControlHub-<version>-windows.zip`. Copy it to the venue PC,
   unzip, and double-click **Install-Hub.cmd** (it asks for administrator rights).
2. It installs to `C:\Program Files\ShowControl\Hub`, keeps its data in `C:\ProgramData\ShowControl\Hub`,
   starts with Windows **in the background** (Task Scheduler → *ShowControl Hub*, runs before anyone signs in,
   restarts itself if it stops), and opens port **47900** for *private* networks only. It prints the setup
   code and the address, e.g. `http://192.168.1.10:47900`. Give that PC a fixed IP (DHCP reservation).
3. Open that address, choose **Join an existing hub**, and enter your hosted hub's address
   (`https://hub.movingsurface.com.ng`) and a **link code** from it (hosted hub → **Linked hubs → Make a link
   code**). The site hub takes the hosted hub's people, PCs, kiosks and settings; sign in with your usual account.

It doesn't conflict with the hosted hub: it's a separate program on a separate machine, reachable only on
the venue network. To update, run Install-Hub.cmd from a newer zip (data is kept); `Install-Hub.cmd /uninstall`
removes it.

**What linking does.**
- Both hubs share, in both directions and within a second or two: people (and password changes), PCs, kiosks
  with their covers, schedules, shared rooms, branding and the activity log. Sign-ins stay per hub.
- **PCs connect to both.** Enrol a PC on either hub as usual; the hubs tell it about each other and it joins
  the other one by itself with the same key (its dashboard → Hub lists both).
- **Kiosks open from the site hub** when the PC can reach it, so a visitor screen keeps working if the internet
  drops. If the site hub stops, the kiosk switches to the hosted hub's link, and back again later.
- **Each schedule runs once**: on the site hub for PCs connected to it, on the hosted hub only for PCs no site
  hub can reach. The PC also ignores a repeat of the same scheduled command.
- If the link drops, both keep working and catch up when it's back (newest change wins).

**The PC dashboard's site pages.** The agent dashboard's menu has **Site · on the hub**: Kiosks, Shared rooms,
Schedules, People, Branding, Hub activity and Linked hubs. They open the hub's pages in the same window (the
site hub when the PC is connected to one), with **This PC** to come back. These pages live on the hub because
every PC and phone shares them; locally they're served by the site hub.

## 4e. The phone's menu, and the new look (milestone 7)

**Advanced operations are in the menu (☰).**
- **Main screen** (top left): *All PCs* (Turn on with Wake-on-LAN, Restart, Shut down, Stop all apps),
  *Rooms* (Add a room, Import a shared room) and *App* (Remote access, Light/Dark mode, Settings).
- **A room** (top right): Turn on / Restart / Shut down this room's PCs, Wake offline PCs, Add a PC,
  Edit tiles, Share this room's tiles, Rename or delete. *Hold to stop all* stays at the bottom for shows.
- **A PC** (top right): Turn on (Wake-on-LAN), Restart PC, Shut down PC, Stop all apps, Reconnect, Rename, Remove.

Every power action still asks first and says exactly which PCs it acts on and which it skips.

**One design across the app, the PC dashboard and the hub:** the Inter typeface (bundled, so it works
offline) in four weights, the indigo brand colour of the app icon, the same neutral palette, and
layouts that adapt from phone to tablet to desktop (the PC dashboard and hub get a menu button on
narrow screens; the app shows more columns of rooms and tiles on a tablet). The hub's branding can still
switch the accent colour.

## 5. The PC dashboard (milestone 3)

Open it from the tray icon. On the PC itself it needs no password.

| Page | What it's for |
|---|---|
| **Overview** | CPU, memory, uptime and apps running; pairing PIN, address and hub status; app status. (The live view of the screen is in the Android app.) |
| **Apps** | Add, edit, reorder, start and stop apps |
| **Scenes** | Build step-by-step scenes: start / close / restart apps, wait, wait for a port, OSC, UDP, web request, OBS command, wake a PC. Each step has a **Test** button |
| **Phones** | Phones paired with this PC. Set one to **Watch only**, or remove it to revoke it at once |
| **Settings** | PC name, allow restart / shut down, startup (delay + scene), OBS connection with **Test connection**, remote dashboard access, ports |
| **Hub** | Connect this PC to your hub (section 6) |
| **Activity log** | What happened, live |

**Adding an app.** Click **Add app**, then:
- **Choose on this PC** opens the normal Windows file picker. Pick the `.exe`, a shortcut (`.lnk`, which is followed to its target) or a `.bat`.
- **Browse folders** is the same thing inside the dashboard. Use it when the dashboard is opened from another computer.
- **Pick a running app**: start the program the normal way, then pick its window. This is the easiest way when you don't know where it's installed.

The name, process name and start folder are filled in for you. **Add a file** next to *Arguments* picks the project to open (e.g. a `.toe`, Resolume composition or `.mad` file). *Launch when the agent starts* plus *Settings → Startup* make an unattended install come up by itself after a power cut.

**Opening the dashboard from another computer.** Settings → *Opening this dashboard from another computer* → set a password (8+ characters) and switch it on. Then browse to `http://<pc-ip>:47800`. The Windows file picker only appears on the PC's own screen, so use *Browse folders* remotely.

## 6. Remote control over the internet: the hub (milestone 3)

The hub is a small server that every show PC connects **out** to. That means no port forwarding at the venue. Phones sign in to the hub with a username and password and control any PC from anywhere, under your company's name and logo.

```
phone ──https──> HUB <──outbound── PC agent (airport)
                  ^ <──outbound── PC agent (event hall)
       hub admin page: PCs, people & roles, branding, schedules, activity
```

### Run the hub

It's one self-contained program (`hub/ShowControl.Hub`). It runs on Linux or Windows, needs no database, and keeps its data in a `data` folder.

**On a server with cPanel/WHM (e.g. your own domain's server):** follow **[HUB-HOSTING.md](HUB-HOSTING.md)**. `BUILD.cmd` makes the package and `install-hub.sh` sets everything up behind cPanel's Apache with AutoSSL.

**On a small cloud server (recommended).** Any 1 GB Ubuntu VPS works.
```bash
# on your PC
cd hub/ShowControl.Hub
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
# copy publish/ to the server, e.g. /opt/showcontrol-hub, then on the server:
sudo /opt/showcontrol-hub/ShowControlHub          # first run prints the SETUP CODE
```
Put HTTPS in front of it with [Caddy](https://caddyserver.com), which gets a free certificate automatically. Point a DNS name (e.g. `hub.movingsurface.com.ng`) at the server, then use this `/etc/caddy/Caddyfile`:
```
hub.movingsurface.com.ng {
    reverse_proxy localhost:47900
}
```
Run the hub as a service so it starts on boot. A systemd unit with `ExecStart=/opt/showcontrol-hub/ShowControlHub` and `Restart=always` is enough. The port is set by `SC_HUB_PORT` (default 47900), and the data folder by `SC_HUB_DATA` or `--data <folder>`. Back up the data folder.

**On a PC at the venue (the site hub).** `Install-Hub.cmd` from `ShowControlHub-<version>-windows.zip` runs it in the background from Windows start-up. Link it to your hosted hub (section 4d). To publish an office PC's hub to the internet instead, use *Cloudflare Tunnel* or *Tailscale Funnel* (HTTPS without opening ports).

### First steps on the hub
1. Open the hub's address in a browser. Enter the **setup code** (printed by the hub and saved in `data/setup-code.txt`) and create the first admin account.
2. **Branding**: company or client name, logo (PNG or SVG; phones show PNG/JPG/WebP), accent colour, time zone for schedules.
3. **PCs → Add a PC** shows a one-time code (valid 15 minutes). On that PC's dashboard: **Hub → enter the hub address and the code → Connect**. The PC appears on the hub within seconds, and stays connected through restarts.
4. **People**: add accounts. **Admin** can do everything. **Operator** controls PCs, kiosks and schedules. **Viewer** is a tour guide: they only use the kiosks assigned to them (section 4d).
5. **Schedules**: run a scene, start or close an app, close everything, or wake a PC at a time of day on chosen weekdays (e.g. *Doors open* 08:30 on weekdays, *Night shutdown* 23:00 every day). **Run now** tests one.
6. **Activity** shows who did what, from where, and when.

### On the phone
Settings → **Remote access → Sign in to a hub**, then enter the hub address, username and password. The app takes on the hub's name, logo and accent colour. To add a hub PC to a room: open the room → **+** → **From your hub** → **Add**. No PIN is needed. Hub PCs work exactly like local ones: tiles, scenes across PCs, live output and wake.

**Without a hub.** For a quick remote setup, install [Tailscale](https://tailscale.com) on the phone and the show PCs, then add each PC by its Tailscale IP (100.x.y.z). Traffic is encrypted end to end, and nothing else is needed. The hub adds accounts, roles, branding, schedules and an activity log on top.

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| Scan finds nothing | Phone and PC must be on the same subnet. Guest Wi-Fi and "AP isolation" block discovery: type the IP manually. Check the firewall rule for UDP 47801. |
| "Can't reach …" when pairing | Firewall (TCP 47800), network set to Public, or the agent isn't running. Try `http://<pc-ip>:47800` in the phone's browser. |
| App shows *running* but nothing on screen | The agent must run as the signed-in user, not as a service or another account. |
| App flips to *crashed* right after starting | Its launcher starts a different process. Set `processName` to the real one from Task Manager. |
| "Executable not found" | Fix `path` in agent.json. Remember to double the backslashes. |
| Phone says *Pair again* | The phone's entry was removed from agent.json, or the config was reset. Pair again with the current PIN. |
| Cue step fails with "not reachable" / no effect | Check the target's OSC/UDP port and firewall; OSC addresses are case-sensitive. The scene stops at the failing step and the phone shows why. |
| Wake does nothing | BIOS / adapter Wake-on-LAN settings (section 4), wired connection, and the PC must have been fully shut down (not unplugged). |
| Screenshot says "Screen unavailable" | The PC is locked or at the login screen. Enable auto sign-in for show PCs. |
| Agent dashboard says the file picker isn't available | It only opens on the PC's own screen. From another computer use *Browse folders*. |
| PC stuck on "Connecting" on the hub page | Check the hub address on the PC's Hub page (include `https://`) and that the PC has internet. The Hub page shows the last error. |
| Phone: "Your hub sign-in has expired" | Settings → Remote access → sign out and in again. A hub admin may have reset your password or removed your devices. |
| Phone shows a hub PC as Offline | The PC is off or lost its internet. Hub PCs can be woken if another PC at the same venue is online. |
| Live view is jerky or slow | Lower **Settings → Live view** to *Balanced* or *Data saver*, especially through the hub on mobile data. The badge shows the real frame rate. A PC streams to at most 6 viewers at once. |
| Live view says *paused · Screen unavailable* | The PC is locked or at the login screen. It resumes by itself when the desktop is back. |
| No crash notifications | Settings → Alerts: check *App crashes* and that notifications are allowed. With the app closed, turn on *Keep watching in the background*. Some phones (Xiaomi, Huawei, Samsung "deep sleep") also need Show Control set to *Unrestricted* battery use. |
| Imported room: a PC says *Pair this PC* | That PC wasn't known on this phone. Enter its PIN (tray menu or dashboard → Phones). Every tile that uses it then works. |
| Setup.exe warns "Windows protected your PC" | The installer isn't code-signed. Choose *More info → Run anyway*, or sign it with your company's certificate (`signtool`). |
| Kiosk PC doesn't open the screen | Its agent must be connected to the hub (dashboard → Hub) and chosen as the kiosk's *Display PC*. Edge must be installed. The agent's Hub page shows the kiosk status. |
| Kiosk tiles are dim / "Unavailable" | The PC that plays that item is offline. Check it on the hub's PCs page. |
| "Restart all" skips a PC | Turn on *Allow phones to restart and shut down this PC* in that PC's dashboard → Settings. |
| Tour guide sees "No kiosks yet" | Assign them on the hub: Kiosks → the kiosk → Tour guides. |
| Phone app: "Viewer accounts use their kiosks only" | That account is a tour guide. Use **Open my kiosks**, or ask an admin for an operator account. |
| Linked hubs: site hub shows *Offline* on the hosted hub | The venue PC is off or has no internet. Everything at the venue keeps working; it catches up when it's back. On that PC: Task Scheduler → *ShowControl Hub*, and `C:\ProgramData\ShowControl\Hub\hub.log`. |
| PC's Hub page: linked hub "doesn't know this PC yet" | The hubs haven't synced yet (it retries by itself), or they aren't linked. Check **Linked hubs** on either hub. |
| Logs | Tray → *Open log folder* (`%LOCALAPPDATA%\ShowControl\logs`). Android: Logcat, filter `AgentConnection`. |

## Ports

| Port | Use |
|---|---|
| TCP 47800 | WebSocket (phone ↔ agent) and the web test panel |
| UDP 47801 | Discovery broadcast |
| TCP 47900 | Hub (behind HTTPS on 443 when hosted). PCs only make **outbound** connections to it. On the site hub, open on private networks only |

Both can be changed in agent.json (`port`, `discoveryPort`).
