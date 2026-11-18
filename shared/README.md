# shared/

Code and assets used by more than one program.

| Path | What | Used by |
|---|---|---|
| `web/sc-ui.css` | The design system: Inter font face, tokens (type scale, weights, spacing, radii, colours for dark/light and the hub's accents), layout (app shell with a side rail on wide screens and a slide-in menu below 900 px, page header, grids), components (buttons, fields, switches, chips, status, cards, rows, stat tiles, tables, drawer, modal, toasts) and the cursor/selection rules | PC dashboard, hub |
| `web/sc-ui.js` | Small helpers the page scripts share: `$ $$ esc ago initials`, the icon set (`icon()`), `toast`, `guarded`, the side drawer, `Theme`, and `Shell` (renders the app shell) | PC dashboard, hub |
| `web/inter.woff2` | Inter variable font (100–900), SIL Open Font License (`inter-LICENSE.txt`) | all web pages |
| `web/icon.svg` | The app icon (favicon) | all web pages |
| `dotnet/WebAssets.cs` | Serves the files above (embedded in each program) at `/assets/…`, with ETags and versioned caching, and sets the pages' security headers | agent, hub |

Each program embeds these files at build time (see the `EmbeddedResource` lines in its `.csproj`), so the
installed agent and hub need nothing else and work offline. Pages link them as `/assets/name?v=__V__`;
the server fills in its version so browsers pick up a new build straight away.

Page-specific code stays with each program: `agent/…/wwwroot/dashboard.{html,css,js}` and
`hub/…/wwwroot/admin.{html,css,js}`, `kiosk.{html,css,js}`. The pages load scripts from the server only
(no inline scripts), which lets the security policy forbid inline code.

The Android app uses the same font (bundled in `res/font`), palette and indigo brand (`ui/theme`).
