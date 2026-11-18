# Show Control icon

One mark everywhere: a **monitor** with a **hub node broadcasting to both sides**. It reads as "a computer
you reach remotely" and stays clear down to 16 px. White on an indigo-to-navy tile, symmetric and centred
(the glyph sits 2 px above the geometric centre so it looks centred, because the stand adds visual weight below).

| File | Used for |
|---|---|
| `showcontrol.svg` | Master artwork (256 × 256, corner radius 58) |
| `showcontrol-small.svg` | 16–24 px version: heavier lines, one arc each side, so the tray icon stays crisp |
| `showcontrol.ico` | Windows: agent and hub `.exe`, tray icon, installer, Start menu (16, 20, 24 from the small version; 32, 40, 48, 64, 128, 256) |
| `showcontrol-1024.png`, `-512.png`, `-192.png` | Play Store listing, documents, web |
| `android/.../drawable/ic_launcher_foreground.xml`, `ic_launcher_monochrome.xml`, `ic_launcher_background.xml` | Android adaptive and themed (Android 13+) launcher icon; the glyph fits the 66 dp safe circle |
| `make-icons.py` | The generator: every file above comes from the shapes defined in it |

Colours: indigo `#4F46E5` → navy `#1E3A8A` (top to bottom), glyph `#FFFFFF`.
The agent dashboard, hub and kiosk pages use the same SVG as their favicon.
These are committed files, so every build produces exactly the same icon.
