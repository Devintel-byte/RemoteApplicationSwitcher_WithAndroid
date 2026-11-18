# Show Control protocol · v1 (agent 1.6, hub 1.4)

The Android controller (and the built-in web panel) talks to each PC agent over a
WebSocket carrying JSON text messages. This file is the contract both sides follow.

## Transport

| What | Value |
|---|---|
| WebSocket | `ws://<pc-ip>:47800/ws` |
| Web test panel | `http://<pc-ip>:47800/` |
| LAN discovery | UDP broadcast to port `47801` |
| Encoding | UTF-8 JSON, one message per WebSocket text frame. Live-view frames are binary messages (see below) |

Every message is an object with a `type` field. Requests from the controller may
carry an `id` (any string); the agent echoes it in the matching `ack` or `error`.

## Security model

1. **The agent only runs what is registered in its `agent.json`.** Controllers send
   app/scene **ids**, never paths, arguments or shell commands.
2. **Pairing.** A new controller sends the 6-digit PIN shown in the agent's tray menu.
   The agent returns a long random token. The controller stores it and uses it from
   then on. Tokens can be revoked by deleting them from `agent.json`.
3. Until a connection is authenticated, the agent only answers `pair`, `hello`
   and `ping`. Five wrong PINs on one connection closes it.

## Discovery (UDP)

Controller broadcasts the ASCII text `SHOWCTL_DISCOVER_V1` to `255.255.255.255:47801`.
Each agent replies (unicast, to the sender) with JSON:

```json
{ "type": "agent_announce", "agentId": "8f2c…", "name": "PC2 · Visuals",
  "hostname": "SHOW-PC2", "port": 47800, "version": "1.0.0" }
```

## Authentication

```json
→ { "type": "pair",  "id": "1", "pin": "482913", "clientName": "Kome's Pixel" }
← { "type": "paired", "id": "1", "token": "b64…" }

→ { "type": "hello", "id": "2", "token": "b64…", "clientName": "Kome's Pixel" }
← { "type": "welcome", "id": "2", "role": "operator", "agent": { …AgentInfo }, "apps": [ …AppInfo ],
    "scenes": [ …SceneInfo ], "stats": { …Stats } }
```

`role` (agent 1.3+) is what this connection may do: `admin` (the agent's own dashboard), `operator`, or
`viewer`. A paired phone is an operator unless it was set to **Watch only** on the agent dashboard's Phones
page; through the hub it's the hub user's role. Controllers should hide controls for viewers. The agent
refuses them anyway. Changing a phone's role closes its connection so it reconnects with the new role.

Wrong PIN or token → `{ "type": "error", "id": "…", "code": "unauthorized", "message": "…" }`.

## Commands (controller → agent, after `hello`)

| type | fields | effect |
|---|---|---|
| `start_app` | `appId` | Launch the app (no-op if already running) |
| `stop_app` | `appId`, `force?` | Graceful close, then force kill after the app's timeout. `force: true` kills at once |
| `restart_app` | `appId` | Stop then start |
| `run_scene` | `sceneId` | Run a scene defined on this PC |
| `stop_all` | – | Stop every registered app on this PC |
| `get_state` | – | Agent replies with a fresh `welcome`-shaped `state` message |
| `power` | `action`: `restart` \| `shutdown` | Restart or shut down the PC (if `allowPower` is true in config) |
| `screenshot` | `display?` (0-based, default 0), `maxWidth?` (default 640) | Agent replies with a `screenshot` message. Limited to about one per second per connection (`busy` error otherwise). Use the live stream instead where `canStream` is true |
| `stream_start` | `display?`, `maxWidth?` (160–1920, default 640), `fps?` (1–30, default 15), `quality?` (30–90, default 60) | Start (or retune) this connection's live view. Reply `stream_started {display, displays, fps, maxWidth, window}`, then binary frames. At most 6 streams per PC (`busy` error) |
| `stream_ack` | `seq` | The controller has shown frame `seq` (no reply). Required: see flow control |
| `stream_stop` | – | Stop the live view (`ack`). Closing the connection also stops it |
| `wake` | `mac` | Agent sends a Wake-on-LAN packet on its own networks (relay for PCs the phone's broadcast can't reach) |
| `ping` | – | Agent replies `pong` |

Each accepted command gets `{ "type": "ack", "id": "…" }` straight away. The outcome
arrives as events.

## Events (agent → controller)

```json
{ "type": "app_status", "appId": "resolume", "state": "running",
  "pid": 10432, "since": "2026-10-19T14:02:11Z", "message": null }

{ "type": "scene_progress", "sceneId": "main-show", "runId": "a1b2",
  "step": 3, "total": 6, "label": "Launch TouchDesigner", "state": "running" }
  // state: running | done | failed   (failed carries "message")

{ "type": "stats", "cpu": 34.2, "ramUsedMb": 18200, "ramTotalMb": 32600,
  "uptimeSec": 22320 }   // every second

{ "type": "config_changed" }   // agent.json was reloaded; send get_state

{ "type": "screenshot", "id": "…", "display": 0, "width": 640, "height": 360,
  "displays": [ …DisplayInfo ], "jpeg": "<base64>" }   // reply to a screenshot request

{ "type": "stream_error", "display": 0, "message": "Screen unavailable (PC locked…)" }  // live view paused, retrying every 2 s
{ "type": "stream_resumed", "display": 0 }
```

## Live view (agent 1.3+)

Only the Android app watches the stream; the PC dashboard and the hub admin pages don't (since agent 1.6 / hub 1.4), so a PC captures its screen only while a phone has the view open.

After `stream_start` the agent pushes **binary** WebSocket messages, one per frame:

```text
offset  size  field
0       1     'S' (0x53)
1       1     kind = 1 (frame)
2       1     display index
3       1     flags (0)
4       4     sequence number, uint32 big-endian (starts at 1)
8       2     width, uint16 big-endian
10      2     height, uint16 big-endian
12      …     JPEG
```

**Flow control.** The controller answers each frame it has decoded with `{"type":"stream_ack","seq":n}`.
The agent keeps at most **2** frames unacknowledged, so on a slow phone or link it sends fewer, fresher
frames rather than queueing them up: what you see is never more than about one network round trip
behind. With no ack for 2 s (phone in the background) it sends one frame and waits again. An unchanged
screen sends one frame a second as a keep-alive.

Capture is GDI (`StretchBlt` straight from the screen into a reused, scaled bitmap), so windowed and
borderless-fullscreen output works; exclusive-fullscreen DirectX apps can appear black.

## Shapes

```text
AgentInfo  { agentId, name, hostname, version, ips[], macs[], allowPower,
             canScreenshot, canStream, displays[] }
DisplayInfo { index, name, width, height, primary }
AppInfo    { id, name, color?, icon?, state, pid?, since?, message?, watchdog }
SceneInfo  { id, name, color?, icon?, group?, confirm, steps }   // steps = count
Stats      { cpu, ramUsedMb, ramTotalMb, uptimeSec }
AppState   "stopped" | "starting" | "running" | "stopping" | "crashed"
```

`color` is a label name the controller maps to its own palette:
`teal | amber | violet | blue | coral | red | gray`.

Error codes: `unauthorized`, `bad_json`, `bad_request`, `unknown_type`, `unknown_app`,
`unknown_scene`, `forbidden`, `unsupported`, `busy`, `capture_failed`.

`icon` names shared by phone and agent: `play film videocam camera music light sparkle
gamepad refresh timer door power stop star wave`.

## Scene steps (agent.json)

| action | fields | does |
|---|---|---|
| `start` / `stop` / `restart` | `appId`, `waitForRunning`, `timeoutSec` | App control |
| `stopAll` | – | Close every registered app |
| `wait` | `seconds` | Pause |
| `waitForPort` | `port`, `timeoutSec` | Wait until a local TCP port accepts connections |
| `osc` | `host?` (127.0.0.1), `port`, `address`, `args[]` | Send one OSC message. Whole numbers → int, decimals → float, text → string, true/false → T/F |
| `udp` | `host?`, `port`, `text` | Send a UTF-8 text datagram |
| `http` | `url`, `method?` (GET), `body?` | HTTP request; non-2xx fails the step. JSON bodies get `application/json` |
| `obs` | `request`, `data?` | One obs-websocket v5 request using the `obs` connection settings |
| `wake` | `mac` | Wake-on-LAN packet |

## Phone-side show scenes

Scenes that span several PCs are stored and run **on the phone**. The phone sends ordinary
commands (`start_app`, `run_scene`, …) to each agent in turn and watches `app_status` /
`scene_progress` events to know when a step is done. No agent changes are needed for them.

## Through the hub

Phones reach PCs at other venues through the hub (`hub/`). Everything above still applies.
Only the address and the hello token change:

| What | Value |
|---|---|
| Phone ↔ PC | `wss://<hub>/ws/agent/<agentId>`. The first message is `hello` with the **hub token** from `POST /api/login` |
| PC → hub | `wss://<hub>/agent`, opened by the agent (outbound only) |
| Phone REST | `POST /api/login {username,password,device}` → `{token,user,workspace}`; `GET /api/me`; `GET /api/agents`; `POST /api/agents/{id}/wake`; `POST /api/logout` |

The hub checks the user's role before relaying anything. Hub **viewer** accounts are tour guides (hub 1.3+):
they can't open PCs at all (`error forbidden`, close code **4403**) and use their kiosks instead (below). A
LAN phone set to *Watch only* is still a `viewer` session on the PC itself: it may only send `hello`,
`get_state`, `ping`, `screenshot` and the `stream_*` messages. If the PC is offline, the hub closes the phone's socket with code **4404**. An expired sign-in gets
`error unauthorized` and close code **4401**.

Agent ↔ hub frames are JSON with a `t` field. `auth`/`enroll` → `authed`/`enrolled`/`error`, then
`open {sid,user,role}`, `msg {sid,data}`, `close {sid}` and `ping`/`pong`. The agent also sends
`info {name,version,apps,running,crashed,macs}` every 15 s. Hub 1.3 also sends `hubs {hubs:[{url,kind}]}`:
other linked hubs the PC should connect to as well, with the same agent id and key (`kind` site | cloud). Each `sid` is one relayed phone session,
and `data` carries the normal protocol messages unchanged.

Live-view frames travel agent → hub as binary messages `[0x01][sid length, 1 byte][sid, ASCII][frame]`;
the hub forwards the frame bytes untouched to that session's phone as a binary message. Acks go back as
ordinary `msg` frames, so flow control works end to end through the hub.

### Kiosks (hub 1.2+)

The hub tells a PC which room kiosk to show with the agent frame `kiosk {path, locked, name}` (`path` null =
none), sent when the kiosk changes and whenever the PC connects. The agent opens `<hub url><path>` in Edge,
locked (`--kiosk … --edge-kiosk-type=fullscreen`) or as an app window, with its own browser profile, and
saves it in agent.json (`kiosk`) so it reopens after a reboot.

The screen (`GET /kiosk#k=<key>`) signs in with its key in the `X-Kiosk-Key` header:
`GET /api/kiosk/config`, `POST /api/kiosk/run {itemId}` (hub runs it on the PC as `Kiosk: <name>`),
`POST /api/kiosk/exit {pin}` (5 wrong PINs = 5-minute lockout), covers at `/api/kiosk/cover/{itemId}?k=<key>`.
Managing kiosks needs operator or admin: `GET/POST /api/kiosks`, `GET/PUT/DELETE /api/kiosks/{id}`,
`POST /api/kiosks/{id}/key` (new link), `POST /api/kiosks/{id}/resume`,
`PUT/DELETE /api/kiosks/{id}/items/{itemId}/cover` (PNG/JPG/WebP body, max 3 MB).

### Tour guides (hub 1.3+)

Signed in as a person (`Authorization: Bearer <hub token>`); viewers see the kiosks whose `viewerIds` include
them, operators and admins see all:
`GET /api/my/kiosks` → `{user, kiosks:[{id,name,room,enabled,pcId,pcName,pcOnline,items,available,activeItemId,activeTitle,covers,colors,titles}]}`
(`pcId` is the display PC, else the room's first PC), `GET /api/my/kiosks/{id}/config` (same shape as the
screen's config), `POST /api/my/kiosks/{id}/run {itemId}`, covers at `/api/my/kiosks/{id}/cover/{itemId}`.
`GET /api/viewers` (operator+) lists viewer accounts for the kiosk editor. Hub viewers get **403** on
`/api/agents` and `/api/rooms`.

### Linked hubs (hub 1.3+)

A site hub links to the cloud hub with a one-time code: cloud `POST /api/sync/link-code` (admin) →
site `POST /api/sync/join {url, code, prefer: cloud|site}` (admin), or on a new hub
`POST /api/setup/join {code: <setup code>, url, linkCode}`. The site calls the cloud's
`POST /api/sync/accept {code, hubId, name, prefer}` and gets a secret, then keeps a WebSocket open to
`wss://<cloud>/hubsync` (`Authorization: Bearer <secret>`, `X-Hub-Id`). Both sides send JSON frames:

| Frame | Meaning |
|---|---|
| `hello {hubId,name,version,lanUrls}` | Who's there. A site's `lanUrls` are passed to PCs in `hubs` (override with `SC_HUB_LAN_URLS`) |
| `snapshot {ledger, records, files, audit, agents}` | Sent on connect and after every shared change. `ledger` = `{key: {h, t, d}}` (hash, change time, deleted); keys `u:<id>` people (password hash, never tokens), `a:<id>` PCs (key hash), `s:<id>` schedules, `r:<id>` shared rooms, `k:<id>` kiosks, `w` branding. Newer `t` wins; deletions are tombstones. `files` = `{"covers/<file>" or "data/<logo>": sha}` |
| `want {files}` → `file {name, data}` | Fetch cover images and the logo (base64) |
| `online {agents}` | PCs connected to that hub (every 20 s and on change) |

`GET /api/sync`, `DELETE /api/sync/upstream`, `DELETE /api/sync/sites/{hubId}` (admin) show and remove links.
**Schedules** run on the hub the PC is connected to locally: the cloud skips PCs online at a linked site (and
leaves `wake` to the site), a site skips PCs it can't reach. Scheduled commands carry
`dedupe: "sch:<scheduleId>:<yyyyMMddHHmm>"`; the agent (1.5+) acts on each key once (repeat → `ack` with
`duplicate: true`).

The agent keeps one connection per hub. The room kiosk follows the connected hub on the local network
(localhost, private address or `.local`), else the hub it enrolled with, else any other.

### Power (hub 1.2+)

`POST /api/agents/power {action: wake|restart|shutdown, agentIds?}` (admin) acts on every PC, or the ones
listed, and returns `{results:[{agentId,name,ok,message}]}`. Schedules accept `restart` and `shutdown`
actions. PCs refuse power commands unless `allowPower` is on.

### Shared rooms (hub 1.1+)

Phones publish a room's tiles so other phones in the workspace can import them.

| Request | Who | Does |
|---|---|---|
| `GET /api/rooms` | operator+ | `{rooms:[{id,name,kind,tiles,pcs,updatedBy,updatedAt}]}` |
| `GET /api/rooms/{id}` | operator+ | The room including `pack` |
| `PUT /api/rooms/{id}` `{pack}` | operator+ | Publish or update (`{id,name,tiles,pcs,updated}`). Max 512 KB, 200 tiles, 100 PCs |
| `DELETE /api/rooms/{id}` | admin, or whoever published it | Remove it from the hub (phones keep their copies) |

A **pack** is `{v:1, room:{id,name,kind}, pcs:[{ref,name,agentId?,hubAgentId?,host?,port}], tiles:[ShowScene…], from, at}`.
Tile steps refer to PCs by `ref`. Packs never contain pairing tokens, hub tokens or passwords.
Between phones without a hub, the same pack travels as a share code:
`SHOWCONTROL-ROOM:1:` + base64url(deflate(pack JSON)).

## Versioning

Breaking changes bump the WebSocket path (`/ws/v2`). New optional fields and new
event types may be added at any time; clients must ignore unknown ones.
