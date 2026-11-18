# Show Control

Control the apps on your show PCs (TouchDesigner, Resolume, Unity builds, OBS…) from an Android phone.
PCs are grouped into rooms; each PC runs a small agent that only launches the apps you register on it.

```
ShowControl/
  protocol/PROTOCOL.md   message format shared by the phone and the PC agent
  agent/                 Windows PC agent (C# / .NET 8, tray app)
  android/               Android controller (Kotlin / Jetpack Compose)
  hub/                   Show Control Hub: remote control over the internet, accounts, branding, schedules
  unity/                 ShowControlReceiver.cs: lets Unity builds react to cues
  shared/                design system for the web pages (sc-ui.css/js, Inter font, icon) and WebAssets.cs
  branding/              the app icon in every size, and its generator
  installer/             self-contained Setup.exe / portable zip for show PCs
  docs/SETUP.md          install, first run and troubleshooting
```

Start with **docs/SETUP.md**. To build the installers, double-click **BUILD.cmd**. To host the hub on a cPanel server, see **docs/HUB-HOSTING.md**.

## Status: milestone 7 (production polish: menus, one design system, app-only live view)

| Feature | Agent | Android |
|---|---|---|
| LAN discovery + PIN pairing | ✅ | ✅ |
| Start / close / restart / force-close apps | ✅ | ✅ |
| Live app status (running, crashed, …) | ✅ | ✅ |
| Watchdog auto-restart | ✅ | shows status |
| Scenes (multi-step, per PC) | ✅ | ✅ run + progress |
| Stop all (hold to confirm) | ✅ | ✅ |
| CPU / RAM / uptime | ✅ | ✅ |
| Restart / shut down PC (opt-in) | ✅ | ✅ |
| Dark / light / system theme, accent colour, brand name | – | ✅ |
| **PC dashboard**: apps, scenes, phones, settings, health, activity log | ✅ | – |
| **Show scenes across several PCs** (wake, start, close, run PC scene, wait) | – | ✅ runs on the phone |
| **Tile editor**: name, colour label, icon, Tap / Confirm / Hold safety, steps, reorder | – | ✅ |
| **Cues**: OSC, UDP, HTTP, OBS WebSocket steps; scene groups | ✅ | ✅ grouped tiles |
| **Wake-on-LAN** (phone + agent relay, MAC learned automatically) | ✅ | ✅ |
| **Live view**: streamed output, up to 30 fps, per display, LAN and hub (watched in the app only) | ✅ streams | ✅ full screen, quality setting |
| Unity receiver script | `unity/` | – |
| **Add apps from the dashboard**: Windows file picker, folder browser, pick a running app, shortcuts | ✅ | – |
| **Startup**: launch apps + a scene when the PC starts | ✅ | – |
| **Hub**: PCs connect outbound, phones sign in from anywhere | ✅ `hub/` | ✅ Settings → Remote access |
| **Accounts and roles** (admin / operator / viewer = tour guide), activity log | hub | ✅ |
| **Branding**: name, logo, accent colour | hub | ✅ applied on sign-in; logo from gallery too |
| **Schedules** (run scene, start or close apps, wake) in the venue's time zone | hub | – |
| **Watch-only phones**: controls hidden (set per phone on the agent dashboard) | ✅ enforced | ✅ |
| **Crash alerts**: notifications for crashes and PCs going offline, optional background watching | events | ✅ |
| **Share a room's tiles**: share code by any messenger, or publish to the hub | hub 1.1 | ✅ Share / Import |
| **Installer for show PCs**: self-contained, firewall, start with Windows, silent deploy | `installer/` | – |
| **Room kiosks**: visitor touch screen with cover tiles, set on the hub, locked full screen on a room PC | hub 1.2 + agent | – |
| **Whole-site power**: turn on / restart / shut down all PCs (or a room); power schedules | hub | ✅ main screen + rooms |
| **Tour guides**: viewer accounts see only their assigned kiosks, pick a PC, run them full screen | hub 1.3 | ✅ Open my kiosks |
| **Site hub**: the hub on a venue PC, in the background from Windows start-up, works without internet | hub 1.3 (`Install-Hub.cmd`) | – |
| **Linked hubs**: site ↔ hosted hub sync both ways; PCs connect to both; kiosks prefer the local hub; schedules run once | hub 1.3 + agent 1.5 | – |
| **Site pages on the PC dashboard**: kiosks, shared rooms, schedules, people, branding, activity | agent 1.5 | – |
| **Menus**: advanced operations (power, Wake-on-LAN, stop all, share, remove) behind ☰ on every screen | – | ✅ |
| **Design system**: Inter typeface (4 weights), indigo brand, shared tokens and components, responsive layouts | `shared/` | ✅ |
| **App icons**: one deterministic icon (amber tile, play + signal) for Android, the agent, the hub and installers | `branding/` | ✅ adaptive + themed |

**Ideas for next:** alerts when the phone is fully off (hub → WhatsApp/Telegram/email), a hub view of show scenes, signing the Android release with your own key.
