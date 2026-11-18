/* Show Control Hub: admin pages (served at /assets/admin.js; helpers in sc-ui.js). */
"use strict";
const ACCENTS = { indigo: "#4F46E5", teal: "#14B8A6", amber: "#F5A524", violet: "#8B5CF6", blue: "#3B82F6", coral: "#F97350" };
const ZONES = ["Africa/Lagos", "Africa/Accra", "Africa/Nairobi", "Africa/Johannesburg", "Africa/Cairo", "Europe/London", "Europe/Paris", "Asia/Dubai", "Asia/Singapore", "America/New_York", "America/Los_Angeles", "UTC"];
const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const ACTIONS = { run_scene: "Run a scene", start_app: "Start an app", stop_app: "Close an app", stop_all: "Close all apps", wake: "Wake the PC", restart: "Restart the PC", shutdown: "Shut down the PC" };
const AUDIT_WORDS = { start_app: "started", stop_app: "closed", restart_app: "restarted", run_scene: "ran scene", stop_all: "closed all apps on", power: "power", wake: "woke", signed_in: "signed in", hub_setup: "set up the hub",
  pc_enrolled: "PC joined", pc_removed: "removed PC", user_added: "added user", user_changed: "changed user", user_removed: "removed user", logo_changed: "changed the logo", branding_changed: "changed branding",
  schedule_added: "added schedule", schedule_changed: "changed schedule", schedule_run: "schedule ran",
  kiosk_run: "visitor chose", kiosk_created: "created kiosk", kiosk_changed: "changed kiosk", kiosk_removed: "removed kiosk", kiosk_new_key: "made a new kiosk link", kiosk_exit: "staff closed kiosk", kiosk_exit_wrong_pin: "wrong kiosk PIN",
  power_all: "whole-site power", hub_linked: "linked a hub", hub_unlinked: "unlinked a hub", room_shared: "shared room", room_updated: "updated shared room", room_unshared: "removed shared room" };

const S = { token: null, user: null, ws: null, page: "pcs", agents: [], timers: [], live: null };
// Opened from a PC's own dashboard ("?back=http://localhost:47800/"): show a way back to it.
try { const b = new URLSearchParams(location.search).get("back"); if (b && /^https?:\/\/(localhost|127\.0\.0\.1|\[::1\]|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(b)) sessionStorage.setItem("hub.back", b); } catch {}
const backUrl = (() => { try { return sessionStorage.getItem("hub.back"); } catch { return null; } })();
let saved = {}; try { saved = { token: localStorage.getItem("hub.token") }; } catch {}
Theme.init("hub.theme");
function applyBrand(ws) { S.workspace = ws; document.documentElement.dataset.accent = ws.accent || "indigo"; document.title = `${ws.name} · Show Control Hub`; }
const logoHtml = (ws, cls = "logo") => `<div class="${cls}">${ws.logoUrl ? `<img src="${esc(ws.logoUrl)}" alt="">` : esc(initials(ws.name))}</div>`;

async function api(method, path, body, raw) {
  const headers = {};
  if (S.token) headers.Authorization = "Bearer " + S.token;
  let payload;
  if (raw) { headers["Content-Type"] = raw.type; payload = raw; }
  else if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const res = await fetch("/api" + path, { method, headers, body: payload });
  let json = null; try { json = await res.json(); } catch {}
  if (res.status === 401 && path !== "/login") { signOutLocal(); throw new Error("Please sign in again"); }
  if (!res.ok) throw new Error(json?.error || json?.detail || `Request failed (${res.status})`);
  return json;
}
function signOutLocal() { S.token = null; S.user = null; try { localStorage.removeItem("hub.token"); } catch {} boot(); }
// ---------- boot / sign-in ----------
async function boot() {
  S.timers.forEach(clearInterval); S.timers = [];
  const pub = await fetch("/api/public/workspace").then(r => r.json()).catch(() => null);
  if (pub) applyBrand(pub.workspace);
  if (pub?.setupNeeded) return renderSetup();
  S.token = saved.token || S.token; saved.token = null;
  if (S.token) {
    const me = await api("GET", "/me").catch(() => null);
    if (me) { if (toKiosks(me.user)) return; S.user = me.user; applyBrand(me.workspace); window.addEventListener("hashchange", route); return route(); }
  }
  renderLogin(pub?.workspace || { name: "Show Control" });
}
function renderLogin(ws) {
  $("#app").innerHTML = `<div class="center"><form id="f">
    ${logoHtml(ws)}<h1>Sign in to ${esc(ws.name)}</h1>
    <label class="field">Username<input class="input" id="u" autocomplete="username" required></label>
    <label class="field">Password<input class="input" type="password" id="p" autocomplete="current-password" required></label>
    <button class="btn primary" type="submit">Sign in</button></form></div>`;
  $("#f").onsubmit = async e => { e.preventDefault(); const r = await guarded(() => api("POST", "/login", { username: $("#u").value, password: $("#p").value, device: "Hub dashboard" })); if (r) signedIn(r); };
}
function renderSetup() {
  $("#app").innerHTML = `<div class="center"><form id="f">
    ${logoHtml({ name: "Show Control" })}<h1>Set up your hub</h1>
    <p class="muted" style="margin:0">Create the first admin account. The setup code is printed in the hub's console and saved in <span class="mono">data/setup-code.txt</span>.</p>
    <label class="field">Setup code<input class="input mono" id="c" required autocomplete="off" style="letter-spacing:.12em;text-transform:uppercase"></label>
    <label class="field">Company or workspace name<input class="input" id="w" value="Moving Surface" required maxlength="60"></label>
    <div class="grid2"><label class="field">Your name<input class="input" id="d" required maxlength="60"></label><label class="field">Username<input class="input" id="u" required autocomplete="username"></label></div>
    <label class="field">Password <span class="hint">At least 8 characters</span><input class="input" type="password" id="p" required minlength="8" autocomplete="new-password"></label>
    <button class="btn primary" type="submit">Create admin account</button>
    <p class="muted" style="margin:6px 0 0;text-align:center">Already have a hub (e.g. on your server)? <a href="#" id="joinLink">Join an existing hub</a> instead, and use the same accounts here.</p></form></div>`;
  $("#joinLink").onclick = e => { e.preventDefault(); renderJoin(); };
  $("#f").onsubmit = async e => {
    e.preventDefault();
    const r = await guarded(() => api("POST", "/setup", { code: $("#c").value.trim().toUpperCase(), workspaceName: $("#w").value, displayName: $("#d").value, username: $("#u").value, password: $("#p").value }));
    if (r) signedIn(r);
  };
}
function renderJoin() {
  $("#app").innerHTML = `<div class="center"><form id="f">
    ${logoHtml({ name: "Show Control" })}<h1>Join an existing hub</h1>
    <p class="muted" style="margin:0">This hub becomes the venue's <b>site hub</b>: it takes the other hub's people, PCs, kiosks and settings, and keeps them in sync both ways. On the other hub: <b>Linked hubs → Make a link code</b>.</p>
    <label class="field">Setup code <span class="hint">From this hub's console or data/setup-code.txt</span><input class="input mono" id="c" required autocomplete="off" style="letter-spacing:.12em;text-transform:uppercase"></label>
    <label class="field">The other hub's address<input class="input mono" id="url" required placeholder="https://hub.movingsurface.com.ng" autocomplete="off"></label>
    <label class="field">Link code<input class="input mono" id="lc" required autocomplete="off" style="letter-spacing:.12em;text-transform:uppercase"></label>
    <button class="btn primary" type="submit" id="go">Join</button>
    <p class="muted" style="margin:6px 0 0;text-align:center"><a href="#" id="back">Set up a new hub instead</a></p></form></div>`;
  $("#back").onclick = e => { e.preventDefault(); renderSetup(); };
  $("#f").onsubmit = async e => {
    e.preventDefault(); $("#go").disabled = true; $("#go").textContent = "Joining…";
    const r = await guarded(() => api("POST", "/setup/join", { code: $("#c").value.trim().toUpperCase(), url: $("#url").value, linkCode: $("#lc").value.trim().toUpperCase() }), "Joined. Sign in with your account from the other hub");
    if (r) boot(); else { $("#go").disabled = false; $("#go").textContent = "Join"; }
  };
}
function signedIn(r) { S.token = r.token; S.user = r.user; applyBrand(r.workspace); try { localStorage.setItem("hub.token", r.token); } catch {} if (toKiosks(r.user)) return; window.addEventListener("hashchange", route); route(); }
/** Viewers (tour guides) only use their kiosks; anyone signing in from the kiosk page goes back there. */
function toKiosks(user) {
  const next = new URLSearchParams(location.search).get("next");
  if (user.role === "viewer" || next === "kiosk") { location.replace("/kiosk"); return true; }
  return false;
}

// ---------- shell ----------
const PAGES = [["pcs", "PCs", "pcs", "operator"], ["kiosks", "Kiosks", "kiosk", "operator"], ["rooms", "Shared rooms", "tiles", "operator"], ["schedules", "Schedules", "clock", "operator"], ["people", "People", "people", "admin"], ["branding", "Branding", "brand", "admin"], ["activity", "Activity", "log", "admin"], ["sync", "Linked hubs", "link", "admin"], ["account", "Your account", "key", "viewer"]];
const RANK = { viewer: 0, operator: 1, admin: 2 };
const can = role => RANK[S.user?.role] >= RANK[role];
function route() {
  closeDrawer(true);
  const p = location.hash.slice(1);
  const allowed = PAGES.filter(x => can(x[3]));
  S.page = allowed.some(x => x[0] === p) ? p : allowed[0][0];
  S.timers.forEach(clearInterval); S.timers = [];
  shell();
}
function shell() {
  const ws = S.workspace;
  const brand = `${logoHtml(ws)}<div><b title="${esc(ws.name)}">${esc(ws.name)}</b><span>Show Control Hub</span></div>`;
  const nav = (backUrl ? Shell.navItem({ label: "This PC", ic: "home", href: backUrl }) : "")
    + PAGES.filter(x => can(x[3])).map(([id, label, ic]) => Shell.navItem({ id, label, ic, current: S.page === id })).join("");
  const foot = `<div class="who"><b>${esc(S.user.displayName)}</b><span class="muted">${esc(ROLE_LABEL[S.user.role] || S.user.role)}</span></div>
    ${Shell.navItem({ label: "Tour guide view", ic: "kiosk", href: "/kiosk" })}${Theme.button("theme")}
    <button class="nav" id="out">${icon("out")}<span>Sign out</span></button>`;
  $("#app").innerHTML = Shell.html({ brand, nav, foot });
  Shell.bind();
  $$(".nav[data-page]").forEach(b => b.onclick = () => location.hash = b.dataset.page);
  $("#theme").onclick = () => { Theme.toggle(); shell(); };
  $("#out").onclick = async () => { await api("POST", "/logout").catch(() => {}); signOutLocal(); };
  ({ pcs: pcsPage, kiosks: kiosksPage, rooms: roomsPage, schedules: schedulesPage, people: peoplePage, branding: brandingPage, activity: activityPage, sync: syncPage, account: accountPage })[S.page]();
}

// ---------- PCs ----------
async function loadAgents() { const r = await api("GET", "/agents"); S.agents = r.agents; return r.agents; }
async function pcsPage() {
  const main = $("#main");
  const render = () => {
    const a = S.agents, online = a.filter(x => x.online).length, crashed = a.reduce((n, x) => n + (x.online ? x.crashed : 0), 0);
    main.innerHTML = `<div class="page-head"><div><h1>PCs</h1><p>Every show PC connected to ${esc(S.workspace.name)}. Phones signed in to the hub can reach these from anywhere.</p></div>
      ${can("admin") ? `<div class="actions"><button class="btn" data-power="wake">${icon("power")}Turn on all</button><button class="btn" data-power="restart">${icon("refresh")}Restart all</button><button class="btn danger" data-power="shutdown">${icon("power")}Shut down all</button><button class="btn primary" id="addPc">${icon("plus")}Add a PC</button></div>` : ""}</div>
      <div class="stats" style="--n:3;margin-bottom:var(--s-6)"><div class="stat"><span>Online now</span><b>${online} <small>of ${a.length}</small></b></div><div class="stat"><span>Apps running</span><b>${a.reduce((n, x) => n + (x.online ? x.running : 0), 0)}</b></div><div class="stat"><span>Crashed apps</span><b style="color:${crashed ? "var(--fault)" : "inherit"}">${crashed}</b></div></div>
      <div class="rack">${a.length ? a.map(x => `<div class="rack-row clickable" data-id="${esc(x.agentId)}" tabindex="0" role="button" aria-label="Open ${esc(x.name)}">
        <span class="led ${x.online ? (x.crashed ? "crashed" : "online") : ""}"></span>
        <div style="min-width:0"><div class="name">${esc(x.name)}${x.online && x.crashed ? `<span class="tag fault">${x.crashed} crashed</span>` : ""}</div>
          <div class="sub">${x.online ? `${x.running} of ${x.apps} apps running` : `Offline, last seen ${ago(x.lastSeen)}`}${x.hostname ? ` · <span class="mono">${esc(x.hostname)}</span>` : ""}</div></div>
        <span class="state">${x.version ? "Agent " + esc(x.version) : ""}</span>
        <div class="ops">${!x.online && x.canWake && can("operator") ? `<button class="btn small" data-wake="${esc(x.agentId)}">${icon("power")}Wake</button>` : ""}</div>
      </div>`).join("") : `<div class="empty"><b>No PCs yet</b>${can("admin") ? "Choose Add a PC, then enter the code in that PC's dashboard under Hub." : "Ask an admin to connect your show PCs."}</div>`}</div>`;
    $("#addPc")?.addEventListener("click", enrollDialog);
    main.querySelectorAll("[data-power]").forEach(b => b.onclick = () => powerAll(b.dataset.power));
    main.querySelectorAll(".rack-row.clickable").forEach(r => { r.onclick = e => { if (!e.target.closest("button")) openPc(r.dataset.id); }; r.onkeydown = e => { if (e.key === "Enter") openPc(r.dataset.id); }; });
    main.querySelectorAll("[data-wake]").forEach(b => b.onclick = async () => guarded(() => api("POST", `/agents/${b.dataset.wake}/wake`), "Wake signal sent"));
  };
  await guarded(loadAgents); render();
  S.timers.push(setInterval(async () => { if (!$("#drawer")) { await loadAgents().catch(() => {}); if (S.page === "pcs" && !$("#drawer")) render(); } }, 5000));
}
async function enrollDialog() {
  const r = await guarded(() => api("POST", "/agents/enroll-code")); if (!r) return;
  openDrawer("Add a PC", `<div class="codebox"><span class="muted">Enrollment code</span><span class="code">${esc(r.code)}</span><span class="muted">Works once, until ${new Date(r.expiresAt).toLocaleTimeString()}.</span></div>
    <ol style="margin:0;padding-left:20px;display:flex;flex-direction:column;gap:8px">
      <li>On the show PC, double-click the Show Control icon in the tray to open its dashboard.</li>
      <li>Open <b>Hub</b> and enter this address: <span class="mono">${esc(location.origin)}</span></li>
      <li>Enter the code above and choose <b>Connect to hub</b>. The PC appears here within a few seconds.</li></ol>`,
    `<span class="spacer"></span><button class="btn primary" data-close>Done</button>`);
}

// Control one PC through the relay. No live view here: watching a PC's screen is in the Android app only.
function openPc(id) {
  const pc = S.agents.find(a => a.agentId === id); if (!pc) return;
  openDrawer(pc.name, `${pc.online ? "" : `<p class="notice">This PC is offline.</p>`}
    <fieldset><legend>Scenes</legend><div id="pcScenes" class="muted">${pc.online ? "Loading…" : "—"}</div></fieldset>
    <fieldset><legend>Apps</legend><div id="pcApps" class="muted">${pc.online ? "Loading…" : "—"}</div></fieldset>
    ${can("admin") ? `<fieldset><legend>Manage</legend><div class="grid2"><label class="field">Name<input class="input" id="pcName" value="${esc(pc.name)}" maxlength="60"></label><div class="field" style="justify-content:flex-end"><button class="btn" id="pcRename">Rename</button></div></div>
      <p class="muted" style="margin:0">Agent ${esc(pc.version || "?")} · joined ${new Date(pc.enrolledAt).toLocaleDateString()} · <span class="mono">${esc(pc.agentId)}</span></p></fieldset>` : ""}`,
    `${can("admin") ? `<button class="btn danger" id="pcRemove">${icon("trash")}Remove from hub</button>` : ""}<span class="spacer"></span>${!pc.online && pc.canWake && can("operator") ? `<button class="btn" id="pcWake">${icon("power")}Wake</button>` : ""}<button class="btn primary" data-close>Close</button>`,
    () => { try { S.live?.close(); } catch {} S.live = null; });
  $("#pcRename")?.addEventListener("click", async () => { if (await guarded(() => api("PUT", `/agents/${id}`, { name: $("#pcName").value }), "Renamed")) { await loadAgents(); } });
  $("#pcRemove")?.addEventListener("click", async () => { if (!confirm(`Remove ${pc.name} from the hub? Phones lose remote access until it's enrolled again.`)) return; if (await guarded(() => api("DELETE", `/agents/${id}`), "Removed")) { closeDrawer(); pcsPage(); } });
  $("#pcWake")?.addEventListener("click", () => guarded(() => api("POST", `/agents/${id}/wake`), "Wake signal sent"));
  if (!pc.online) return;
  const ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws/agent/${encodeURIComponent(id)}`);
  S.live = ws;
  const st = { apps: [], scenes: [], runs: {} };
  const send = m => { if (ws.readyState === 1) ws.send(JSON.stringify(m)); };
  const renderLive = () => {
    const ctl = can("operator");
    const sc = $("#pcScenes"), ap = $("#pcApps"); if (!sc) return;
    sc.innerHTML = st.scenes.length ? st.scenes.map(s => { const r = st.runs[s.id]; return `<div class="mini-row"><div class="grow"><b>${esc(s.name)}</b>${s.group ? ` <span class="tag">${esc(s.group)}</span>` : ""}<div class="muted" style="font-size:13px">${r ? `Step ${r.step} of ${r.total}: ${esc(r.label)}${r.state !== "running" ? ` (${r.state})` : ""}` : `${s.steps} step${s.steps === 1 ? "" : "s"}`}</div></div>${ctl ? `<button class="btn small" data-scene="${esc(s.id)}" data-confirm="${s.confirm}" data-name="${esc(s.name)}">${icon("play")}Run</button>` : ""}</div>`; }).join("") : `<span class="muted">No scenes on this PC.</span>`;
    ap.innerHTML = st.apps.length ? st.apps.map(a => `<div class="mini-row"><span class="led ${a.state}"></span><div class="grow"><b>${esc(a.name)}</b><div class="muted" style="font-size:13px">${esc(a.state)}${a.message ? ", " + esc(a.message) : ""}</div></div>${ctl ? (a.state === "running" ? `<button class="btn small" data-stop="${esc(a.id)}">${icon("stop")}Close</button>` : `<button class="btn small" data-start="${esc(a.id)}" ${a.state === "starting" || a.state === "stopping" ? "disabled" : ""}>${icon("play")}${a.state === "crashed" ? "Restart" : "Start"}</button>`) : ""}</div>`).join("") : `<span class="muted">No apps on this PC.</span>`;
    sc.querySelectorAll("[data-scene]").forEach(b => b.onclick = () => { if (b.dataset.confirm === "true" && !confirm(`Run ${b.dataset.name}?`)) return; send({ type: "run_scene", sceneId: b.dataset.scene }); });
    ap.querySelectorAll("[data-start]").forEach(b => b.onclick = () => send({ type: "start_app", appId: b.dataset.start }));
    ap.querySelectorAll("[data-stop]").forEach(b => b.onclick = () => send({ type: "stop_app", appId: b.dataset.stop }));
  };
  ws.onopen = () => send({ type: "hello", id: "h", token: S.token, clientName: "Hub dashboard" });
  ws.onmessage = e => {
    if (typeof e.data !== "string") return;
    const m = JSON.parse(e.data);
    if (m.type === "welcome" || m.type === "state") {
      st.apps = m.apps; st.scenes = m.scenes; renderLive();
    }
    else if (m.type === "app_status") { const a = st.apps.find(x => x.id === m.appId); if (a) Object.assign(a, { state: m.state, message: m.message }); renderLive(); }
    else if (m.type === "scene_progress") { st.runs[m.sceneId] = m; renderLive(); if (m.state === "failed") toast(`Scene stopped: ${m.message || m.label}`, "fault"); }
    else if (m.type === "config_changed") send({ type: "get_state" });
    else if (m.type === "error" && m.code !== "busy") toast(m.message, "fault");
  };
  ws.onclose = () => { if (S.live === ws && $("#pcApps")) toast(`Disconnected from ${pc.name}`, "fault"); };
}

// ---------- shared rooms (tiles published from phones) ----------
async function roomsPage() {
  const main = $("#main");
  const r = await guarded(() => api("GET", "/rooms")); if (!r) return;
  main.innerHTML = `<div class="page-head"><div><h1>Shared rooms</h1><p>Rooms and their tiles published from the Show Control app (open a room, tap Share, then Publish). Everyone signed in to ${esc(S.workspace.name)} can import them on their phone from Rooms → Import. Pairing keys are never shared: a phone asks for a PC's PIN when it doesn't know it yet.</p></div></div>
    <div class="rack">${r.rooms.length ? r.rooms.map(x => `<div class="rack-row" style="grid-template-columns:auto minmax(0,1fr) auto">
      ${icon("tiles", 'style="width:20px;height:20px;color:var(--muted)"')}
      <div style="min-width:0"><div class="name">${esc(x.name)}${x.kind ? ` <span class="tag">${esc(x.kind.toLowerCase())}</span>` : ""}</div>
        <div class="sub">${x.tiles} tile${x.tiles === 1 ? "" : "s"} · ${x.pcs} PC${x.pcs === 1 ? "" : "s"} · published by ${esc(x.updatedBy || "?")} ${ago(x.updatedAt)}</div></div>
      <div class="ops">${can("operator") ? `<button class="btn small danger" data-del="${esc(x.id)}" data-name="${esc(x.name)}">${icon("trash")}Remove</button>` : ""}</div>
    </div>`).join("") : `<div class="empty"><b>Nothing shared yet</b>In the app, open a room, tap Share and choose Publish to this hub.</div>`}</div>`;
  main.querySelectorAll("[data-del]").forEach(b => b.onclick = async () => {
    if (!confirm(`Remove ${b.dataset.name} from the hub? Phones that already imported it keep their copy.`)) return;
    if (await guarded(() => api("DELETE", `/rooms/${encodeURIComponent(b.dataset.del)}`), "Removed")) roomsPage();
  });
}

// ---------- kiosks: visitor touch screens for each room ----------
const KCOLORS = { teal: ["#0F6B5C", "#1FB59A"], amber: ["#7A4A00", "#E09400"], violet: ["#3B2D8C", "#7F6BF0"], blue: ["#0B3D73", "#2F86E0"], coral: ["#7A2E12", "#E8683A"], red: ["#6E1218", "#D63A44"], gray: ["#2A2F38", "#58606E"] };
const newId = () => "i" + Math.random().toString(36).slice(2, 10);
const kioskUrl = k => location.origin + k.path;
/** Cover images need the sign-in header, so they're fetched and shown as blobs. */
async function loadAuthedImages(root = document) {
  for (const img of root.querySelectorAll("img[data-auth-src]")) {
    const src = img.dataset.authSrc; img.removeAttribute("data-auth-src");
    try { const r = await fetch(src, { headers: { Authorization: "Bearer " + S.token } }); if (r.ok) img.src = URL.createObjectURL(await r.blob()); } catch {}
  }
}
function kioskState(k) {
  if (!k.enabled) return ["Off", ""];
  if (k.paused) return ["Paused by staff", "warn"];
  if (!k.displayAgentId) return ["Link only", ""];
  return k.displayOnline ? ["Live on " + (S.agents.find(a => a.agentId === k.displayAgentId)?.name || "PC"), "ok"] : ["Display PC offline", "fault"];
}
async function kiosksPage() {
  const main = $("#main");
  const [r] = await Promise.all([guarded(() => api("GET", "/kiosks")), guarded(loadAgents)]); if (!r) return;
  main.innerHTML = `<div class="page-head"><div><h1>Kiosks</h1><p>Touch screens for visitors in each room. Choose what they can switch between, with a cover for each, and the PC in the room that shows it. In kiosk mode the screen is locked full screen: visitors can't leave it.</p></div>
    <div class="actions"><button class="btn primary" id="addKiosk">${icon("plus")}New kiosk</button></div></div>
    ${r.kiosks.length ? `<div class="kgrid">${r.kiosks.map(k => { const [st, cls] = kioskState(k); return `<button class="kcard" data-id="${esc(k.id)}">
      <div class="kmosaic">${k.items.slice(0, 4).map(it => it.cover ? `<img data-auth-src="${esc(it.cover)}" alt="">` : `<span style="background:linear-gradient(140deg,${KCOLORS[it.color]?.[0] || "#333"},${KCOLORS[it.color]?.[1] || "#555"})">${esc(initials(it.title))}</span>`).join("") || `<span class="none">No items yet</span>`}</div>
      <div class="kmeta"><b>${esc(k.name)}</b><span class="muted">${esc(k.room || "")}${k.room ? " · " : ""}${k.items.length} item${k.items.length === 1 ? "" : "s"}${k.locked ? " · kiosk mode" : " · window"}${k.viewerIds?.length ? ` · ${k.viewerIds.length} guide${k.viewerIds.length === 1 ? "" : "s"}` : ""}</span>
      <span class="kstate ${cls}">${esc(st)}</span></div></button>`; }).join("")}</div>`
      : `<div class="rack"><div class="empty"><b>No kiosks yet</b>Choose <b>New kiosk</b> to set up a visitor screen for a room.</div></div>`}`;
  $("#addKiosk").onclick = () => editKiosk(null);
  main.querySelectorAll(".kcard").forEach(c => c.onclick = async () => { const k = await guarded(() => api("GET", `/kiosks/${c.dataset.id}`)); if (k) editKiosk(k); });
  loadAuthedImages(main);
}

function editKiosk(k0) {
  const isNew = !k0;
  const k = k0 ? structuredClone(k0) : { name: "", room: "", welcome: "Tap a screen to choose what plays in this room.", agentIds: [], displayAgentId: null, locked: true, enabled: true, stopPrevious: false, columns: 0, hasExitPin: false, items: [], viewerIds: [] };
  k.viewerIds = k.viewerIds || [];
  const covers = {}; // itemId -> File to upload, or null to remove
  let editingItem = null;
  const agentName = id => S.agents.find(a => a.agentId === id)?.name || "Removed PC";
  const roomPcs = () => k.agentIds.length ? S.agents.filter(a => k.agentIds.includes(a.agentId)) : S.agents;

  const itemsHtml = () => k.items.length ? k.items.map((it, i) => `<div class="kitem">
      <div class="kthumb" style="background:linear-gradient(140deg,${KCOLORS[it.color]?.[0]},${KCOLORS[it.color]?.[1]})">${covers[it.id] instanceof File ? `<img src="${URL.createObjectURL(covers[it.id])}" alt="">` : covers[it.id] === null || !it.cover ? esc(initials(it.title)) : `<img data-auth-src="${esc(it.cover)}" alt="">`}</div>
      <div class="grow" style="min-width:0"><b>${esc(it.title)}</b><div class="muted" style="font-size:13px">${it.action === "start_app" ? "Starts app" : "Runs scene"} ${esc(it.targetName || it.targetId)} on ${esc(agentName(it.agentId))}</div></div>
      <div class="ops"><button class="btn icon ghost small" data-up="${i}" ${i === 0 ? "disabled" : ""} aria-label="Move up">${icon("up")}</button><button class="btn icon ghost small" data-down="${i}" ${i === k.items.length - 1 ? "disabled" : ""} aria-label="Move down">${icon("down")}</button><button class="btn small" data-edit="${i}">Edit</button><button class="btn icon ghost small" data-del="${i}" aria-label="Remove ${esc(it.title)}">${icon("trash")}</button></div></div>`).join("")
    : `<p class="muted" style="margin:0">No items yet. Each item is a scene or an app on one of the room's PCs, shown as a cover on the screen.</p>`;

  openDrawer(isNew ? "New kiosk" : `Kiosk: ${k.name}`, `
    ${isNew ? "" : `<div class="codebox"><span class="muted">Screen link${k.displayAgentId ? " (the display PC opens it by itself)" : ""}</span><span class="mono klink">${esc(kioskUrl(k))}</span>
      <div style="display:flex;gap:8px;flex-wrap:wrap"><button class="btn small" id="kCopy">Copy link</button><a class="btn small" href="${esc(kioskUrl(k))}" target="_blank" rel="noopener">Open screen</a><button class="btn small" id="kKey">New link</button>${k.paused ? `<button class="btn small primary" id="kResume">Resume kiosk</button>` : ""}</div>
      ${k.paused ? `<span class="hint" style="color:var(--warn)">Paused: staff closed it with the exit PIN.</span>` : ""}</div>`}
    <fieldset><legend>Room</legend>
      <div class="grid2"><label class="field">Kiosk name<input class="input" id="kName" value="${esc(k.name)}" maxlength="60" placeholder="e.g. Main hall screen"></label>
        <label class="field">Room shown on screen<input class="input" id="kRoom" value="${esc(k.room)}" maxlength="60" list="kRooms" placeholder="e.g. Main hall"><datalist id="kRooms"></datalist></label></div>
      <label class="field">Welcome line<input class="input" id="kWelcome" value="${esc(k.welcome)}" maxlength="200"></label>
      <div class="field">PCs in this room <span class="hint">Items are picked from these</span><div class="chips" id="kPcs">${S.agents.map(a => `<button type="button" class="chip" data-a="${esc(a.agentId)}" aria-pressed="${k.agentIds.includes(a.agentId)}">${esc(a.name)}</button>`).join("") || `<span class="muted">No PCs on the hub yet</span>`}</div></div>
    </fieldset>
    <fieldset><legend>Items</legend><div id="kItems">${itemsHtml()}</div><div id="kItemEditor"></div>
      <div><button class="btn" id="kAdd">${icon("plus")}Add item</button></div></fieldset>
    <fieldset><legend>Tour guides</legend>
      <div class="field">Viewers who can use this kiosk <span class="hint">They sign in to the hub on a tablet or phone, pick a PC, then this kiosk, and switch items full screen. Operators and admins can always use it.</span>
        <div class="chips" id="kViewers"><span class="muted">Loading…</span></div></div>
    </fieldset>
    <fieldset><legend>Screen</legend>
      <label class="field">Display PC <span class="hint">The room PC that shows this kiosk (it must run the Show Control Agent)</span><select class="input" id="kDisplay"><option value="">None: I'll open the link on a device myself</option>${S.agents.map(a => `<option value="${esc(a.agentId)}" ${a.agentId === k.displayAgentId ? "selected" : ""}>${esc(a.name)}${a.online ? "" : " (offline)"}</option>`).join("")}</select></label>
      <label class="switch"><input type="checkbox" id="kLocked" ${k.locked ? "checked" : ""}><span>Kiosk mode<small>Locked full screen: no address bar, tabs or way out for visitors, and it reopens if closed. Off: a normal window.</small></span></label>
      <label class="switch"><input type="checkbox" id="kStop" ${k.stopPrevious ? "checked" : ""}><span>Close the previous app when a visitor switches<small>For items that start apps. Scenes can do their own switching.</small></span></label>
      <label class="switch"><input type="checkbox" id="kOn" ${k.enabled ? "checked" : ""}><span>Screen is on<small>Off closes it on the display PC and shows a resting page anywhere else.</small></span></label>
      <div class="grid2"><label class="field">Tiles per row<select class="input" id="kCols">${[[0, "Automatic"], [1, "1"], [2, "2"], [3, "3"], [4, "4"], [5, "5"]].map(([v, l]) => `<option value="${v}" ${k.columns === v ? "selected" : ""}>${l}</option>`).join("")}</select></label>
        <label class="field">Staff exit PIN <span class="hint">${k.hasExitPin ? "Set. Type a new one to change it" : "4–8 digits. Hold the logo 3 s to use it"}</span><input class="input mono" id="kPin" inputmode="numeric" maxlength="8" placeholder="${k.hasExitPin ? "••••" : "none"}" autocomplete="off"></label></div>
      ${k.hasExitPin ? `<label class="switch"><input type="checkbox" id="kNoPin"><span>Remove the exit PIN</span></label>` : ""}
    </fieldset>`,
    `${isNew ? "" : `<button class="btn danger" id="kDel">${icon("trash")}Remove</button>`}<span class="spacer"></span><button class="btn ghost" data-close>Cancel</button><button class="btn primary" id="kSave">${isNew ? "Create kiosk" : "Save changes"}</button>`);
  $("#drawer").classList.add("wide");
  api("GET", "/viewers").then(r => {
    const box = $("#kViewers"); if (!box) return;
    box.innerHTML = r.viewers.length ? r.viewers.map(v => `<button type="button" class="chip" data-v="${esc(v.id)}" aria-pressed="${k.viewerIds.includes(v.id)}">${esc(v.displayName)}</button>`).join("")
      : `<span class="muted">No viewer accounts yet. Add one in People with the role <b>Viewer (tour guide)</b>.</span>`;
    box.querySelectorAll("[data-v]").forEach(b => b.onclick = () => {
      const on = b.getAttribute("aria-pressed") !== "true"; b.setAttribute("aria-pressed", on);
      k.viewerIds = on ? [...k.viewerIds, b.dataset.v] : k.viewerIds.filter(x => x !== b.dataset.v);
    });
  }).catch(() => { const box = $("#kViewers"); if (box) box.innerHTML = `<span class="muted">Couldn't load viewers.</span>`; });
  api("GET", "/rooms").then(r => { $("#kRooms").innerHTML = r.rooms.map(x => `<option value="${esc(x.name)}">`).join(""); }).catch(() => {});

  const drawItems = () => {
    $("#kItems").innerHTML = itemsHtml(); loadAuthedImages($("#kItems"));
    $("#kItems").querySelectorAll("[data-up]").forEach(b => b.onclick = () => { const i = +b.dataset.up; [k.items[i - 1], k.items[i]] = [k.items[i], k.items[i - 1]]; drawItems(); });
    $("#kItems").querySelectorAll("[data-down]").forEach(b => b.onclick = () => { const i = +b.dataset.down; [k.items[i + 1], k.items[i]] = [k.items[i], k.items[i + 1]]; drawItems(); });
    $("#kItems").querySelectorAll("[data-del]").forEach(b => b.onclick = () => { k.items.splice(+b.dataset.del, 1); drawItems(); });
    $("#kItems").querySelectorAll("[data-edit]").forEach(b => b.onclick = () => itemEditor(+b.dataset.edit));
  };
  drawItems();

  // Item editor (inline): PC → scene/app → title, colour, cover.
  const itemEditor = (index) => {
    const isNewItem = index === null;
    const it = isNewItem ? { id: newId(), title: "", subtitle: "", color: "amber", action: "run_scene", agentId: roomPcs()[0]?.agentId || "", targetId: "", targetName: "" } : structuredClone(k.items[index]);
    editingItem = it;
    const box = $("#kItemEditor"); $("#kAdd").hidden = true;
    let pendingCover = covers[it.id];
    box.innerHTML = `<div class="kedit">
      <div class="grid2"><label class="field">PC<select class="input" id="iPc">${roomPcs().map(a => `<option value="${esc(a.agentId)}" ${a.agentId === it.agentId ? "selected" : ""}>${esc(a.name)}${a.online ? "" : " (offline)"}</option>`).join("")}</select></label>
        <label class="field">When tapped<select class="input" id="iAct"><option value="run_scene" ${it.action === "run_scene" ? "selected" : ""}>Run a scene</option><option value="start_app" ${it.action === "start_app" ? "selected" : ""}>Start an app</option></select></label></div>
      <div id="iTarget"></div>
      <div class="grid2"><label class="field">Title on the screen<input class="input" id="iTitle" value="${esc(it.title)}" maxlength="60" placeholder="Uses the scene or app name"></label>
        <label class="field">Small text under it <span class="hint">Optional</span><input class="input" id="iSub" value="${esc(it.subtitle)}" maxlength="80" placeholder="e.g. 4 min · projection"></label></div>
      <div class="field">Colour <span class="hint">Used when there's no cover</span><div class="chips" id="iColor">${Object.keys(KCOLORS).map(c => `<button type="button" class="swatch" data-c="${c}" aria-pressed="${it.color === c}" aria-label="${c}" style="background:linear-gradient(140deg,${KCOLORS[c][0]},${KCOLORS[c][1]})"></button>`).join("")}</div></div>
      <div class="field">Cover image <span class="hint">PNG, JPG or WebP, up to 3 MB. Landscape (16:9) looks best</span>
        <div style="display:flex;gap:12px;align-items:center;flex-wrap:wrap"><div class="kthumb big" id="iPrev"></div>
          <label class="btn small">${icon("upload")}Choose image<input type="file" id="iFile" accept="image/png,image/jpeg,image/webp" hidden></label>
          <button type="button" class="btn small ghost" id="iNoCover">Remove cover</button></div></div>
      <div style="display:flex;gap:10px;justify-content:flex-end"><button type="button" class="btn ghost" id="iCancel">Cancel</button><button type="button" class="btn primary" id="iDone">${isNewItem ? "Add to kiosk" : "Done"}</button></div></div>`;
    const preview = () => {
      const p = $("#iPrev"); const [c1, c2] = KCOLORS[it.color];
      p.style.background = `linear-gradient(140deg,${c1},${c2})`;
      if (pendingCover instanceof File) p.innerHTML = `<img src="${URL.createObjectURL(pendingCover)}" alt="">`;
      else if (pendingCover !== null && it.cover) { p.innerHTML = `<img data-auth-src="${esc(it.cover)}" alt="">`; loadAuthedImages(p); }
      else p.textContent = initials($("#iTitle").value || it.targetName || "?");
    };
    preview();
    let targets = null;
    const renderTarget = async refetch => {
      const t = $("#iTarget"), act = $("#iAct").value;
      if (refetch || !targets) { t.innerHTML = `<p class="muted" style="margin:0">Asking the PC for its ${act === "run_scene" ? "scenes" : "apps"}…</p>`; targets = await pcTargets($("#iPc").value); }
      const list = targets ? (act === "run_scene" ? targets.scenes : targets.apps) : null;
      t.innerHTML = list
        ? (list.length ? `<label class="field">${act === "run_scene" ? "Scene" : "App"}<select class="input" id="iTgt">${list.map(x => `<option value="${esc(x.id)}" data-name="${esc(x.name)}" ${x.id === it.targetId ? "selected" : ""}>${esc(x.name)}</option>`).join("")}</select></label>`
          : `<p class="muted" style="margin:0">This PC has no ${act === "run_scene" ? "scenes" : "apps"} yet. Add them on its dashboard.</p>`)
        : `<label class="field">${act === "run_scene" ? "Scene" : "App"} id <span class="hint">The PC is offline, so type the id from its dashboard</span><input class="input mono" id="iTgt" value="${esc(it.targetId)}"></label>`;
    };
    renderTarget(true);
    $("#iPc").onchange = () => renderTarget(true);
    $("#iAct").onchange = () => renderTarget(false);
    $("#iTitle").oninput = preview;
    $("#iColor").querySelectorAll(".swatch").forEach(b => b.onclick = () => { it.color = b.dataset.c; $("#iColor").querySelectorAll(".swatch").forEach(x => x.setAttribute("aria-pressed", x === b)); preview(); });
    $("#iFile").onchange = e => { const f = e.target.files[0]; if (!f) return; if (f.size > 3 * 1024 * 1024) { toast("Keep cover images under 3 MB", "fault"); return; } pendingCover = f; preview(); };
    $("#iNoCover").onclick = () => { pendingCover = null; preview(); };
    const closeEditor = () => { editingItem = null; box.innerHTML = ""; $("#kAdd").hidden = false; };
    $("#iCancel").onclick = closeEditor;
    $("#iDone").onclick = () => {
      const tgt = $("#iTgt");
      if (!tgt || !tgt.value) { toast("Choose a scene or app", "fault"); return; }
      it.agentId = $("#iPc").value; it.action = $("#iAct").value; it.targetId = tgt.value;
      it.targetName = tgt.selectedOptions?.[0]?.dataset.name || tgt.value;
      it.title = $("#iTitle").value.trim() || it.targetName; it.subtitle = $("#iSub").value.trim();
      if (pendingCover !== undefined) covers[it.id] = pendingCover;
      if (isNewItem) k.items.push(it); else k.items[index] = it;
      closeEditor(); drawItems();
    };
  };
  $("#kAdd").onclick = () => { if (!S.agents.length) { toast("Add a PC to the hub first", "fault"); return; } itemEditor(null); };
  $("#kPcs")?.querySelectorAll(".chip").forEach(b => b.onclick = () => {
    const on = b.getAttribute("aria-pressed") !== "true"; b.setAttribute("aria-pressed", on);
    k.agentIds = [...$("#kPcs").querySelectorAll('[aria-pressed="true"]')].map(x => x.dataset.a);
  });

  $("#kCopy")?.addEventListener("click", async () => { try { await navigator.clipboard.writeText(kioskUrl(k)); toast("Link copied"); } catch { toast("Copy the link from the box", "fault"); } });
  $("#kKey")?.addEventListener("click", async () => {
    if (!confirm("Make a new link? Screens using the old link stop working (the display PC updates by itself).")) return;
    const r = await guarded(() => api("POST", `/kiosks/${k.id}/key`), "New link ready"); if (r) { closeDrawer(true); editKiosk(r); }
  });
  $("#kResume")?.addEventListener("click", async () => { const r = await guarded(() => api("POST", `/kiosks/${k.id}/resume`), "Kiosk resumed"); if (r) { closeDrawer(true); editKiosk(r); kiosksPage(); } });
  $("#kDel")?.addEventListener("click", async () => { if (confirm(`Remove ${k.name}? Its screen closes.`) && await guarded(() => api("DELETE", `/kiosks/${k.id}`), "Removed")) { closeDrawer(); kiosksPage(); } });
  $("#kSave").onclick = async () => {
    if (editingItem) { toast("Finish the item you're editing first (Add / Done)", "fault"); return; }
    const pin = $("#kPin").value.trim();
    const body = { name: $("#kName").value, room: $("#kRoom").value, welcome: $("#kWelcome").value, agentIds: k.agentIds,
      displayAgentId: $("#kDisplay").value || null, locked: $("#kLocked").checked, stopPrevious: $("#kStop").checked, enabled: $("#kOn").checked,
      columns: Number($("#kCols").value), viewerIds: k.viewerIds,
      items: k.items.map(({ id, title, subtitle, color, action, agentId, targetId, targetName }) => ({ id, title, subtitle, color, action, agentId, targetId, targetName })) };
    if (pin) body.exitPin = pin; else if ($("#kNoPin")?.checked) body.exitPin = "";
    const saved = await guarded(() => isNew ? api("POST", "/kiosks", body) : api("PUT", `/kiosks/${k.id}`, body));
    if (!saved) return;
    for (const [itemId, file] of Object.entries(covers)) {
      if (!saved.items.some(i => i.id === itemId)) continue;
      if (file === null) await guarded(() => api("DELETE", `/kiosks/${saved.id}/items/${itemId}/cover`));
      else await guarded(() => api("PUT", `/kiosks/${saved.id}/items/${itemId}/cover`, undefined, file));
    }
    toast(isNew ? "Kiosk created" : "Saved");
    closeDrawer(true); kiosksPage();
    if (isNew) { const full = await guarded(() => api("GET", `/kiosks/${saved.id}`)); if (full) editKiosk(full); }
  };
}

// ---------- whole-site power (admin) ----------
async function powerAll(action) {
  const words = { wake: ["Turn on all PCs?", "Every PC that's off gets a Wake-on-LAN signal, sent by an online PC at its venue."], restart: ["Restart all PCs?", "Every online PC restarts in 5 seconds. Running shows stop. PCs without \"Allow restart and shut down\" are skipped."], shutdown: ["Shut down all PCs?", "Every online PC shuts down in 5 seconds. Running shows stop. Turn them on again with Turn on all (Wake-on-LAN)."] }[action];
  if (!confirm(`${words[0]}\n\n${words[1]}`)) return;
  const r = await guarded(() => api("POST", "/agents/power", { action })); if (!r) return;
  const ok = r.results.filter(x => x.ok), bad = r.results.filter(x => !x.ok);
  openDrawer(words[0].replace("?", ""), `<p style="margin:0"><b>${ok.length} of ${r.results.length}</b> PCs ${action === "wake" ? "were sent a wake signal" : action === "restart" ? "are restarting" : "are shutting down"}.</p>
    <div class="rack">${r.results.map(x => `<div class="rack-row" style="grid-template-columns:auto minmax(0,1fr)"><span class="led ${x.ok ? "online" : "crashed"}"></span><div><div class="name">${esc(x.name)}</div><div class="sub">${x.ok ? "Done" : esc(x.message || "Failed")}</div></div></div>`).join("")}</div>
    ${bad.some(x => /turned off|Allow restart/i.test(x.message || "")) ? `<p class="muted" style="margin:0">To allow it on a PC: its dashboard → Settings → <i>Allow phones to restart and shut down this PC</i>.</p>` : ""}`,
    `<span class="spacer"></span><button class="btn primary" data-close>Close</button>`);
}

// ---------- schedules ----------
async function schedulesPage() {
  const main = $("#main");
  const [r] = await Promise.all([guarded(() => api("GET", "/schedules")), guarded(loadAgents)]);
  if (!r) return;
  const name = id => S.agents.find(a => a.agentId === id)?.name || "Removed PC";
  const dayText = d => !d.length || d.length === 7 ? "Every day" : d.join(",") === "1,2,3,4,5" ? "Weekdays" : d.join(",") === "0,6" ? "Weekends" : d.map(x => DAY_NAMES[x]).join(" ");
  main.innerHTML = `<div class="page-head"><div><h1>Schedules</h1><p>Things the hub does on its own: open the installation every morning, close it at night. Times are in ${esc(r.timeZone)}.</p></div>
    ${can("admin") ? `<div class="actions"><button class="btn primary" id="addSch">${icon("plus")}New schedule</button></div>` : ""}</div>
    <div class="rack">${r.schedules.length ? r.schedules.map(s => `<div class="rack-row">
      <span class="led ${s.enabled ? "online" : ""}" title="${s.enabled ? "On" : "Off"}"></span>
      <div style="min-width:0"><div class="name">${esc(s.name)}${!s.enabled ? `<span class="tag">Off</span>` : ""}</div>
        <div class="sub">${esc(ACTIONS[s.action] || s.action)}${s.targetName ? ` "${esc(s.targetName)}"` : ""} on ${esc(name(s.agentId))}</div>
        <div class="sub">${s.lastRun ? `Last ran ${ago(s.lastRun)}: ${esc(s.lastResult || "")}` : "Hasn't run yet"}</div></div>
      <span class="state"><b style="font:600 20px var(--display);color:var(--text)">${esc(s.time)}</b><br>${dayText(s.days)}</span>
      <div class="ops">${can("admin") ? `<button class="btn small" data-run="${esc(s.id)}">${icon("play")}Run now</button><button class="btn icon ghost" data-edit="${esc(s.id)}" aria-label="Edit ${esc(s.name)}">${icon("edit")}</button>` : ""}</div>
    </div>`).join("") : `<div class="empty"><b>No schedules yet</b>${can("admin") ? "For example: run “Main show” on the lobby PC at 08:00 on weekdays." : ""}</div>`}</div>`;
  $("#addSch")?.addEventListener("click", () => editSchedule(null));
  main.querySelectorAll("[data-edit]").forEach(b => b.onclick = () => editSchedule(r.schedules.find(s => s.id === b.dataset.edit)));
  main.querySelectorAll("[data-run]").forEach(b => b.onclick = async () => { const x = await guarded(() => api("POST", `/schedules/${b.dataset.run}/run`)); if (x) { toast(x.message, x.ok ? "" : "fault"); schedulesPage(); } });
}
async function pcTargets(agentId) {
  // Ask the PC (through the relay) for its scenes and apps.
  return new Promise(resolve => {
    const ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws/agent/${encodeURIComponent(agentId)}`);
    const done = v => { try { ws.close(); } catch {} resolve(v); };
    const t = setTimeout(() => done(null), 6000);
    ws.onopen = () => ws.send(JSON.stringify({ type: "hello", id: "h", token: S.token, clientName: "Hub dashboard" }));
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.type === "welcome") { clearTimeout(t); done({ scenes: m.scenes, apps: m.apps }); } };
    ws.onclose = () => { clearTimeout(t); resolve(null); };
  });
}
function editSchedule(s) {
  const isNew = !s;
  const x = s ? structuredClone(s) : { name: "", enabled: true, agentId: S.agents[0]?.agentId || "", action: "run_scene", targetId: "", targetName: "", time: "08:00", days: [1, 2, 3, 4, 5] };
  openDrawer(isNew ? "New schedule" : `Edit ${x.name}`, `
    <label class="field">Name<input class="input" id="sName" value="${esc(x.name)}" maxlength="60" placeholder="e.g. Open the installation"></label>
    <div class="grid2"><label class="field">PC<select class="input" id="sPc">${S.agents.map(a => `<option value="${esc(a.agentId)}" ${a.agentId === x.agentId ? "selected" : ""}>${esc(a.name)}${a.online ? "" : " (offline)"}</option>`).join("")}</select></label>
      <label class="field">Do this<select class="input" id="sAct">${Object.entries(ACTIONS).map(([k, v]) => `<option value="${k}" ${k === x.action ? "selected" : ""}>${v}</option>`).join("")}</select></label></div>
    <div id="sTarget"></div>
    <div class="grid2"><label class="field">At<input class="input" type="time" id="sTime" value="${esc(x.time)}" required></label><div></div></div>
    <div class="field">On these days<div class="days" id="sDays">${DAY_NAMES.map((d, i) => `<button type="button" data-d="${i}" aria-pressed="${x.days.includes(i)}" aria-label="${d}">${d[0]}</button>`).join("")}</div><span class="hint">None selected means every day.</span></div>
    <label class="switch"><input type="checkbox" id="sOn" ${x.enabled ? "checked" : ""}><span>Schedule is on</span></label>`,
    `${isNew ? "" : `<button class="btn danger" id="sDel">${icon("trash")}Remove</button>`}<span class="spacer"></span><button class="btn ghost" data-close>Cancel</button><button class="btn primary" id="sSave">${isNew ? "Create schedule" : "Save changes"}</button>`);
  let targets = null;
  const renderTarget = async (refetch) => {
    const act = $("#sAct").value, box = $("#sTarget");
    if (act === "stop_all" || act === "wake" || act === "restart" || act === "shutdown") {
      box.innerHTML = act === "wake" ? `<p class="muted" style="margin:0">Another online PC sends the Wake-on-LAN signal.</p>`
        : act === "stop_all" ? "" : `<p class="muted" style="margin:0">The PC needs <i>Allow phones to restart and shut down this PC</i> on in its dashboard's Settings, otherwise it refuses and the schedule shows why.</p>`;
      return;
    }
    if (refetch || !targets) { box.innerHTML = `<p class="muted" style="margin:0">Asking the PC for its ${act === "run_scene" ? "scenes" : "apps"}…</p>`; targets = await pcTargets($("#sPc").value); }
    const list = targets ? (act === "run_scene" ? targets.scenes : targets.apps) : null;
    box.innerHTML = list
      ? `<label class="field">${act === "run_scene" ? "Scene" : "App"}<select class="input" id="sTgt">${list.map(t => `<option value="${esc(t.id)}" data-name="${esc(t.name)}" ${t.id === x.targetId ? "selected" : ""}>${esc(t.name)}</option>`).join("")}</select></label>`
      : `<label class="field">${act === "run_scene" ? "Scene" : "App"} id <span class="hint">The PC is offline, so type the id from its dashboard</span><input class="input mono" id="sTgt" value="${esc(x.targetId || "")}"></label>`;
  };
  renderTarget(true);
  $("#sPc").onchange = () => renderTarget(true);
  $("#sAct").onchange = () => renderTarget(false);
  $("#sDays").querySelectorAll("button").forEach(b => b.onclick = () => b.setAttribute("aria-pressed", b.getAttribute("aria-pressed") !== "true"));
  $("#sSave").onclick = async () => {
    const tgt = $("#sTgt");
    const body = { ...x, name: $("#sName").value, agentId: $("#sPc").value, action: $("#sAct").value, time: $("#sTime").value, enabled: $("#sOn").checked,
      days: [...$("#sDays").querySelectorAll('[aria-pressed="true"]')].map(b => Number(b.dataset.d)),
      targetId: tgt ? tgt.value : null, targetName: tgt?.selectedOptions?.[0]?.dataset.name || (tgt ? tgt.value : null) };
    if (await guarded(() => isNew ? api("POST", "/schedules", body) : api("PUT", `/schedules/${x.id}`, body), isNew ? "Schedule created" : "Saved")) { closeDrawer(); schedulesPage(); }
  };
  $("#sDel")?.addEventListener("click", async () => { if (confirm(`Remove ${x.name}?`) && await guarded(() => api("DELETE", `/schedules/${x.id}`), "Removed")) { closeDrawer(); schedulesPage(); } });
}

// ---------- people ----------
async function peoplePage() {
  const r = await guarded(() => api("GET", "/users")); if (!r) return;
  $("#main").innerHTML = `<div class="page-head"><div><h1>People</h1><p>Admins manage the hub. Operators run shows from the app and here. Viewers are tour guides: they only see the kiosks assigned to them (Kiosks → Tour guides) and switch what plays.</p></div>
    <div class="actions"><button class="btn primary" id="addUser">${icon("plus")}Add person</button></div></div>
    <div class="rack">${r.users.map(u => `<div class="rack-row">
      ${icon("people", 'style="width:20px;height:20px;color:var(--muted)"')}
      <div style="min-width:0"><div class="name">${esc(u.displayName)}${u.id === S.user.id ? `<span class="tag">You</span>` : ""}</div><div class="sub"><span class="mono">${esc(u.username)}</span>${u.devices.length ? ` · ${u.devices.length} signed-in ${u.devices.length === 1 ? "device" : "devices"}, last active ${ago(u.devices[0].lastUsed)}` : " · not signed in anywhere"}${u.role === "viewer" ? ` · ${u.kiosks?.length ? "Kiosks: " + esc(u.kiosks.join(", ")) : "no kiosks assigned yet"}` : ""}</div></div>
      <span class="tag ${u.role === "admin" ? "ok" : ""}">${esc(ROLE_LABEL[u.role] || u.role)}</span>
      <div class="ops"><button class="btn icon ghost" data-edit="${esc(u.id)}" aria-label="Edit ${esc(u.displayName)}">${icon("edit")}</button></div></div>`).join("")}</div>`;
  $("#addUser").onclick = () => editUser(null);
  document.querySelectorAll("[data-edit]").forEach(b => b.onclick = () => editUser(r.users.find(u => u.id === b.dataset.edit)));
}
const ROLE_LABEL = { admin: "Admin", operator: "Operator", viewer: "Viewer (tour guide)" };
function editUser(u) {
  const isNew = !u;
  const roleSel = `<label class="field">Role<select class="input" id="uRole">${["operator", "viewer", "admin"].map(r => `<option value="${r}" ${(u?.role || "operator") === r ? "selected" : ""}>${ROLE_LABEL[r]}</option>`).join("")}</select><span class="hint">Viewers only see their assigned kiosks.</span></label>`;
  openDrawer(isNew ? "Add person" : `Edit ${u.displayName}`, `
    <div class="grid2"><label class="field">Name<input class="input" id="uName" value="${esc(u?.displayName || "")}" maxlength="60"></label>
    ${isNew ? `<label class="field">Username<input class="input" id="uUser" autocomplete="off"></label>` : `<label class="field">Username<input class="input mono" value="${esc(u.username)}" disabled></label>`}</div>
    ${roleSel}
    <label class="field">${isNew ? "Password" : "New password"} <span class="hint">${isNew ? "At least 8 characters. Share it with them privately." : "Leave empty to keep it. Setting one signs them out everywhere."}</span><input class="input" type="password" id="uPw" autocomplete="new-password"></label>
    ${!isNew && u.devices.length ? `<fieldset><legend>Signed in on</legend>${u.devices.map(d => `<div class="mini-row"><div class="grow">${esc(d.device)}</div><span class="muted">${ago(d.lastUsed)}</span></div>`).join("")}<div><button class="btn small" id="uOut">Sign out everywhere</button></div></fieldset>` : ""}`,
    `${!isNew && u.id !== S.user.id ? `<button class="btn danger" id="uDel">${icon("trash")}Remove</button>` : ""}<span class="spacer"></span><button class="btn ghost" data-close>Cancel</button><button class="btn primary" id="uSave">${isNew ? "Add person" : "Save changes"}</button>`);
  $("#uSave").onclick = async () => {
    const ok = isNew
      ? await guarded(() => api("POST", "/users", { displayName: $("#uName").value, username: $("#uUser").value, role: $("#uRole").value, password: $("#uPw").value }), "Added")
      : await guarded(() => api("PUT", `/users/${u.id}`, { displayName: $("#uName").value, role: $("#uRole").value, password: $("#uPw").value || null }), "Saved");
    if (ok) { closeDrawer(); peoplePage(); }
  };
  $("#uOut")?.addEventListener("click", async () => { if (await guarded(() => api("DELETE", `/users/${u.id}/sessions`), "Signed out everywhere")) { closeDrawer(); peoplePage(); } });
  $("#uDel")?.addEventListener("click", async () => { if (confirm(`Remove ${u.displayName}? They'll lose access straight away.`) && await guarded(() => api("DELETE", `/users/${u.id}`), "Removed")) { closeDrawer(); peoplePage(); } });
}

// ---------- branding ----------
function brandingPage() {
  const ws = S.workspace; let accent = ws.accent;
  $("#main").innerHTML = `<div class="page-head"><div><h1>Branding</h1><p>Phones signed in to this hub show your name, logo and accent colour, and so does this dashboard.</p></div></div>
    <div class="preview"><form id="bf" style="display:flex;flex-direction:column;gap:28px">
      <fieldset><legend>Name</legend><label class="field">Company or client name<input class="input" id="bName" value="${esc(ws.name)}" maxlength="60"></label></fieldset>
      <fieldset><legend>Logo</legend>
        <div style="display:flex;gap:16px;align-items:center">${logoHtml(ws, "logo")}<div style="display:flex;gap:8px;flex-wrap:wrap">
          <label class="btn small" for="bFile" style="cursor:pointer">Upload image</label><input type="file" id="bFile" accept="image/png,image/jpeg,image/webp,image/svg+xml" hidden>
          ${ws.logoUrl ? `<button class="btn small ghost" type="button" id="bRm">Remove</button>` : ""}</div></div>
        <span class="muted" style="font-size:13px">Square PNG or SVG works best, up to 1 MB. Transparent backgrounds look right in both themes.</span></fieldset>
      <fieldset><legend>Accent colour</legend><div class="accents" id="bAcc">${Object.entries(ACCENTS).map(([k, v]) => `<button type="button" data-a="${k}" aria-label="${k}" aria-pressed="${k === accent}" style="background:${v}"></button>`).join("")}</div></fieldset>
      <fieldset><legend>Time zone</legend><label class="field">Used for schedules<input class="input" id="bTz" list="zones" value="${esc(ws.timeZone)}"><datalist id="zones">${ZONES.map(z => `<option value="${z}">`).join("")}</datalist></label></fieldset>
      <div><button class="btn primary" type="submit">Save branding</button></div>
    </form>
    <div><div class="muted" style="font-size:13px;margin-bottom:10px">On the phone</div><div class="phone" aria-hidden="true">
      <div class="hdr">${logoHtml(ws, "plogo")}<div><div class="pname" id="pvName">${esc(ws.name)}</div><div class="psub">Show control</div></div></div>
      <div class="ptitle">Rooms</div>
      <div class="pcard"><b>Event Hall</b><span>Production · 4 PCs</span></div>
      <div class="pcard"><b>Arrival Hall</b><span>Installation · 3 PCs</span></div>
      <div class="pbtn">Add room</div></div></div></div>`;
  $("#bName").oninput = e => $("#pvName").textContent = e.target.value;
  $("#bAcc").querySelectorAll("button").forEach(b => b.onclick = () => { accent = b.dataset.a; document.documentElement.dataset.accent = accent; $("#bAcc").querySelectorAll("button").forEach(x => x.setAttribute("aria-pressed", x === b)); });
  $("#bFile").onchange = async e => { const f = e.target.files[0]; if (!f) return; if (f.size > 1048576) return toast("Keep the logo under 1 MB", "fault"); const r = await guarded(() => api("PUT", "/workspace/logo", undefined, f), "Logo updated"); if (r) { applyBrand(r); shell(); } };
  $("#bRm")?.addEventListener("click", async () => { const r = await guarded(() => api("DELETE", "/workspace/logo"), "Logo removed"); if (r) { applyBrand(r); shell(); } });
  $("#bf").onsubmit = async e => { e.preventDefault(); const r = await guarded(() => api("PUT", "/workspace", { name: $("#bName").value, accent, timeZone: $("#bTz").value.trim() }), "Branding saved"); if (r) { applyBrand(r); shell(); } else document.documentElement.dataset.accent = S.workspace.accent; };
}

// ---------- activity ----------
async function activityPage() {
  const [r] = await Promise.all([guarded(() => api("GET", "/audit?limit=300")), loadAgents().catch(() => {})]); if (!r) return;
  const pcName = id => id ? (S.agents.find(a => a.agentId === id)?.name || "removed PC") : "";
  $("#main").innerHTML = `<div class="page-head"><div><h1>Activity</h1><p>Who did what, newest first. Commands sent through the hub, sign-ins, schedules and changes to the hub.</p></div></div>
    <div class="rack">${r.entries.length ? `<table class="table"><thead><tr><th>When</th><th>Who</th><th>What</th><th class="hide-m">Details</th></tr></thead><tbody>${r.entries.map(e => `<tr>
      <td class="muted" style="white-space:nowrap" title="${esc(new Date(e.at).toLocaleString())}">${ago(e.at)}</td><td>${esc(e.user)}</td>
      <td>${esc(AUDIT_WORDS[e.action] || e.action)} ${e.agentId ? `<b>${esc(pcName(e.agentId))}</b>` : ""}</td><td class="muted hide-m mono">${esc(e.detail || "")}</td></tr>`).join("")}</tbody></table>` : `<div class="empty"><b>Nothing yet</b>Activity appears here as people use the hub.</div>`}</div>`;
}

// ---------- linked hubs ----------
async function syncPage() {
  const r = await guarded(() => api("GET", "/sync")); if (!r) return;
  const up = r.upstream, sites = r.sites;
  const state = (on, last) => `<span class="tag ${on ? "ok" : ""}">${on ? "Connected" : "Offline"}</span>${!on && last ? ` <span class="muted" style="font-size:13px">last seen ${ago(last)}</span>` : ""}`;
  $("#main").innerHTML = `<div class="page-head"><div><h1>Linked hubs</h1><p>Run one hub on a PC at the venue (local access, works without internet) and one on your server (remote access). Linked, they share people, PCs, kiosks, schedules, shared rooms, branding and activity, both ways. PCs connect to both; each schedule runs once.</p></div></div>
    ${up ? `<div class="rack"><div class="rack-row">${icon("link", 'style="width:20px;height:20px;color:var(--muted)"')}
        <div style="min-width:0"><div class="name">Linked to ${esc(up.name || up.url)}</div><div class="sub"><span class="mono">${esc(up.url)}</span> · linked ${ago(up.linkedAt)}${up.lastSync ? ` · last sync ${ago(up.lastSync)}` : ""}</div></div>
        <span>${state(up.online)}</span><div class="ops"><button class="btn small danger" id="unlink">Unlink</button></div></div></div>
        <p class="muted">This is a <b>site hub</b>. Its PCs also connect to the hub above, so phones and the kiosk keep working from outside the venue. If the internet drops, everything here keeps working locally and catches up when it's back.</p>`
    : sites.length ? "" : `<fieldset style="max-width:620px"><legend>Link this hub to another hub</legend>
        <p class="muted" style="margin:0">On the other hub (usually your server, e.g. <span class="mono">https://hub.movingsurface.com.ng</span>): <b>Linked hubs → Make a link code</b>. Then enter it here.</p>
        <label class="field">The other hub's address<input class="input mono" id="lUrl" placeholder="https://hub.movingsurface.com.ng" autocomplete="off"></label>
        <label class="field">Link code<input class="input mono" id="lCode" autocomplete="off" style="letter-spacing:.12em;text-transform:uppercase"></label>
        <label class="field">Where both hubs have something set up, keep <span class="hint">Anything only one hub has is kept and copied to the other.</span>
          <select class="input" id="lPref"><option value="cloud" selected>The other hub's people, kiosks, schedules and branding (recommended)</option><option value="site">This hub's people, kiosks, schedules and branding</option></select></label>
        <div><button class="btn primary" id="lGo">${icon("link")}Link hubs</button></div></fieldset>`}
    ${up ? "" : `<div class="page-head" style="margin-top:24px"><div><h2 style="margin:0;font-size:18px">Site hubs linked here</h2><p>Hubs at your venues that keep in sync with this one.</p></div>
        <div class="actions"><button class="btn" id="mkCode">${icon("plus")}Make a link code</button></div></div>
      <div class="rack">${sites.length ? sites.map(x => `<div class="rack-row">${icon("pcs", 'style="width:20px;height:20px;color:var(--muted)"')}
        <div style="min-width:0"><div class="name">${esc(x.name)}</div><div class="sub">${x.lanUrls.length ? `<span class="mono">${esc(x.lanUrls.join(", "))}</span> · ` : ""}linked ${ago(x.linkedAt)}${x.online ? ` · ${x.pcsOnline} PC${x.pcsOnline === 1 ? "" : "s"} connected there` : ""}</div></div>
        <span>${state(x.online, x.lastSeen)}</span><div class="ops"><button class="btn icon ghost" data-rm="${esc(x.hubId)}" aria-label="Remove ${esc(x.name)}">${icon("trash")}</button></div></div>`).join("")
        : `<div class="empty"><b>No site hubs yet</b>Install the hub on a PC at the venue (ShowControlHub-…-windows.zip → Install-Hub.cmd), then link it with a code from here.</div>`}</div>`}
    <p class="muted" style="font-size:13px;margin-top:20px">This hub: <span class="mono">${esc(r.hubId)}</span>${r.lanUrls.length ? ` · on this network at <span class="mono">${esc(r.lanUrls.join(", "))}</span>` : ""}</p>`;
  $("#unlink")?.addEventListener("click", async () => { if (confirm("Unlink? Both hubs keep what they have now but stop syncing. PCs keep connecting to both until you remove them from one.") && await guarded(() => api("DELETE", "/sync/upstream"), "Unlinked")) syncPage(); });
  $("#lGo")?.addEventListener("click", async () => {
    const b = $("#lGo"); b.disabled = true; b.textContent = "Linking…";
    const ok = await guarded(() => api("POST", "/sync/join", { url: $("#lUrl").value, code: $("#lCode").value.trim().toUpperCase(), prefer: $("#lPref").value }), "Linked. Syncing now");
    if (ok) { const me = await api("GET", "/me").catch(() => null); if (me) { S.user = me.user; applyBrand(me.workspace); } shell(); } else { b.disabled = false; b.innerHTML = `${icon("link")}Link hubs`; }
  });
  $("#mkCode")?.addEventListener("click", async () => {
    const c = await guarded(() => api("POST", "/sync/link-code")); if (!c) return;
    openDrawer("Link a site hub", `<div class="codebox"><span class="muted">Link code</span><span class="code">${esc(c.code)}</span><span class="muted">Works once, for ${c.expiresInMinutes} minutes.</span></div>
      <ol class="muted" style="margin:0;padding-left:20px;line-height:1.7"><li>On the venue PC, open its hub (<span class="mono">http://localhost:47900</span>).</li><li>New hub: choose <b>Join an existing hub</b>. Already set up: <b>Linked hubs</b>.</li><li>Enter this hub's address <span class="mono">${esc(location.origin)}</span> and the code.</li></ol>`,
      `<span class="spacer"></span><button class="btn primary" data-close>Done</button>`);
  });
  document.querySelectorAll("[data-rm]").forEach(b => b.onclick = async () => { if (confirm("Remove this site hub? It stops syncing with this one.") && await guarded(() => api("DELETE", `/sync/sites/${b.dataset.rm}`), "Removed")) syncPage(); });
  S.timers.push(setInterval(() => { if (S.page === "sync" && !$("#drawer.open") && !document.activeElement?.matches("input")) syncPage(); }, 10000));
}

// ---------- account ----------
function accountPage() {
  $("#main").innerHTML = `<div class="page-head"><div><h1>Your account</h1><p>Signed in as <span class="mono">${esc(S.user.username)}</span> (${esc(S.user.role)}).</p></div></div>
    <form id="pf" style="display:flex;flex-direction:column;gap:14px;max-width:420px">
      <label class="field">Current password<input class="input" type="password" id="pc" autocomplete="current-password" required></label>
      <label class="field">New password <span class="hint">At least 8 characters</span><input class="input" type="password" id="pn" autocomplete="new-password" minlength="8" required></label>
      <div><button class="btn primary" type="submit">Change password</button></div></form>`;
  $("#pf").onsubmit = async e => { e.preventDefault(); if (await guarded(() => api("PUT", "/me/password", { current: $("#pc").value, new: $("#pn").value }), "Password changed")) $("#pf").reset(); };
}

boot();
