/* Show Control Agent: the PC dashboard (served at /assets/dashboard.js; helpers in sc-ui.js). */
"use strict";
const COLORS = { teal: "#4FD1B5", amber: "#FFB020", violet: "#9B8CFF", blue: "#4DA3FF", coral: "#FF8A5B", red: "#FF6B6B", gray: "#8A909C" };
const ICONS = ["play", "film", "videocam", "camera", "music", "light", "sparkle", "gamepad", "refresh", "timer", "door", "power", "stop", "star", "wave"];
const STEP_TYPES = {
  start: { label: "Start an app", hint: "Launch one of this PC's apps" },
  stop: { label: "Close an app", hint: "Polite close, then force" },
  restart: { label: "Restart an app", hint: "Close and launch again" },
  stopAll: { label: "Close all apps", hint: "Every app on this PC" },
  wait: { label: "Wait", hint: "Pause before the next step" },
  waitForPort: { label: "Wait for a port", hint: "Until a local TCP port opens" },
  osc: { label: "Send OSC", hint: "Resolume, TouchDesigner, Unity…" },
  udp: { label: "Send UDP text", hint: "Plain text to a port" },
  http: { label: "Web request", hint: "Call any HTTP API" },
  obs: { label: "OBS command", hint: "Switch scene, stream, record" },
  wake: { label: "Wake a PC", hint: "Wake-on-LAN by MAC address" },
};
const OBS_REQUESTS = ["SetCurrentProgramScene", "SetCurrentPreviewScene", "TriggerStudioModeTransition", "StartStream", "StopStream", "ToggleStream", "StartRecord", "StopRecord", "ToggleRecord", "PauseRecord", "ResumeRecord", "ToggleInputMute", "SetInputMute", "SetSceneItemEnabled", "TriggerMediaInputAction", "StartVirtualCam", "StopVirtualCam", "SaveReplayBuffer"];

// ---------- state ----------
const S = { token: null, local: false, data: null, page: "overview", ws: null, live: {}, sceneRuns: {}, logTimer: null };
const PAGES = [
  ["overview", "Overview", "overview"], ["apps", "Apps", "apps"], ["scenes", "Scenes", "scenes"], ["phones", "Phones", "phone"],
  ["settings", "Settings", "cog"], ["hub", "Hub", "hub"], ["log", "Activity log", "log"],
];
/** Pages shared by every PC and phone: they live on the hub (on this network when there is one), opened here with a way back. */
const SITE_PAGES = [["kiosks", "Kiosks", "kiosk"], ["rooms", "Shared rooms", "tiles"], ["schedules", "Schedules", "clock"], ["people", "People", "people"], ["branding", "Branding", "brand"], ["activity", "Hub activity", "log"], ["sync", "Linked hubs", "link"]];
const siteHref = page => `${S.data.hub.siteUrl}/?back=${encodeURIComponent(location.origin + "/#" + S.page)}#${page}`;

Theme.init("sc.theme");

// ---------- api ----------
async function api(method, path, body) {
  const headers = { "X-ShowControl": "1" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (S.token && !S.local) headers.Authorization = "Bearer " + S.token;
  const res = await fetch("/api/admin" + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  let json = null; try { json = await res.json(); } catch {}
  if (res.status === 401 && !S.local && path !== "/login") { S.token = null; renderLogin(); throw new Error("Sign in again"); }
  if (!res.ok) throw new Error(json?.error || json?.detail || `Request failed (${res.status})`);
  return json;
}
// ---------- boot ----------
async function boot() {
  try {
    const s = await api("GET", "/session");
    if (s.token) { S.token = s.token; S.local = s.local; await refresh(); connectWs(); route(); }
    else renderLogin(s.remoteEnabled);
  } catch (e) {
    $("#app").innerHTML = `<div class="center"><div class="panel">${appMarkTile()}<h1>Can't open the dashboard</h1><p class="muted">${esc(e.message)}</p></div></div>`;
  }
}
function renderLogin(enabled = true) {
  $("#app").innerHTML = `<div class="center"><form id="loginForm">
    ${appMarkTile()}
    <h1>Sign in to this PC</h1>
    <p class="muted" style="margin:0">${enabled ? "Enter the remote access password set in this PC's dashboard." : "Remote access is turned off. Open the dashboard on the PC itself (double-click the tray icon), then turn on remote access in Settings."}</p>
    ${enabled ? `<label class="field">Password<input class="input" type="password" id="pw" autocomplete="current-password" required></label>
    <button class="btn primary" type="submit">Sign in</button>` : ""}
  </form></div>`;
  $("#loginForm").onsubmit = async e => {
    e.preventDefault();
    const r = await guarded(() => api("POST", "/login", { password: $("#pw").value }));
    if (r?.token) { S.token = r.token; S.local = false; await refresh(); connectWs(); route(); }
  };
}
async function refresh() { S.data = await api("GET", "/state"); }

// ---------- live websocket ----------
function connectWs() {
  const ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`);
  S.ws = ws;
  ws.onopen = () => ws.send(JSON.stringify({ type: "hello", id: "dash", token: S.token, clientName: "Dashboard" }));
  ws.onmessage = e => { if (typeof e.data === "string") onLive(JSON.parse(e.data)); };
  ws.onclose = () => { setLink(false); setTimeout(() => { if (S.token && S.ws === ws) connectWs(); }, 2000); };
}
function wsSend(msg) { if (S.ws?.readyState === 1) S.ws.send(JSON.stringify(msg)); else toast("Reconnecting to the agent…", "fault"); }
function setLink(ok) { const el = $("#linkState"); if (el) el.innerHTML = `<span class="led ${ok ? "running" : "connecting"}"></span>${ok ? "Live" : "Reconnecting…"}`; }
function onLive(m) {
  switch (m.type) {
    case "welcome": case "state":
      setLink(true);
      m.apps.forEach(a => S.live[a.id] = a);
      rerenderLive(); break;
    case "app_status": {
      const a = S.live[m.appId] || {}; Object.assign(a, { state: m.state, pid: m.pid, since: m.since, message: m.message }); S.live[m.appId] = a;
      rerenderLive(); break;
    }
    case "scene_progress":
      S.sceneRuns[m.sceneId] = m;
      if (m.state === "failed") toast(`Scene stopped at "${m.label}": ${m.message || "failed"}`, "fault");
      if (m.state !== "running") setTimeout(() => { if (S.sceneRuns[m.sceneId] === m) { delete S.sceneRuns[m.sceneId]; rerenderLive(); } }, 4000);
      rerenderLive(); break;
    case "stats": if (S.data) { S.data.stats = m; if (S.page === "overview") renderMeters(); } break;
    case "config_changed": refresh().then(() => { if (!$("#drawer")) rerender(); }); break;
    case "error":
      if (m.code === "unauthorized") { boot(); }
      else if (m.code !== "busy") toast(m.message, "fault");
      break;
  }
}
function appState(id) { return S.live[id]?.state || S.data?.apps.find(a => a.def.id === id)?.state || "stopped"; }
// ---------- routing & shell ----------
function route() {
  closeDrawer(true);
  $(".modal")?.remove();
  const p = location.hash.replace("#", "");
  S.page = PAGES.some(x => x[0] === p) || p === "site" ? p : "overview";
  rerender();
}
window.addEventListener("hashchange", route);
function rerender() {
  if (!S.data) return;
  const d = S.data;
  const counts = { apps: d.apps.length, scenes: d.scenes.length, phones: d.clients.length };
  const brand = `${appMarkTile()}<div><b>Show Control</b><span>Agent ${esc(d.agent.version)} · ${esc(d.agent.name)}</span></div>`;
  const nav = PAGES.map(([id, label, ic]) => Shell.navItem({ id, label, ic, current: S.page === id, count: counts[id] })).join("")
    + `<div class="rail-sect eyebrow">Site · on the hub</div>`
    + SITE_PAGES.map(([id, label, ic]) => d.hub.siteUrl
      ? Shell.navItem({ label, ic, href: siteHref(id), title: `Opens on ${d.hub.siteUrl}` })
      : Shell.navItem({ id: "site", label, ic, current: S.page === "site" && id === "kiosks" })).join("");
  const foot = `<div class="link-state" id="linkState"><span class="led connecting"></span>Connecting…</div>${Theme.button()}`;
  $("#app").innerHTML = Shell.html({ brand, nav, foot });
  $("#main").innerHTML = renderPage();
  Shell.bind();
  $$("button.nav[data-page]").forEach(b => b.onclick = () => { location.hash = b.dataset.page; });
  $("#themeBtn").onclick = () => { Theme.toggle(); rerender(); };
  setLink(S.ws?.readyState === 1);
  bindPage();
}
function rerenderLive() {
  if (!S.data) return;
  if (S.page === "overview") { const el = $("#signals"); if (el) el.innerHTML = signalsHtml(); renderMeters(); }
  if (S.page === "apps") { const el = $("#appRack"); if (el) { el.innerHTML = appRowsHtml(); bindAppRows(); } }
  if (S.page === "scenes") { const el = $("#sceneRack"); if (el) { el.innerHTML = sceneRowsHtml(); bindSceneRows(); } }
}
function renderPage() {
  switch (S.page) {
    case "apps": return appsPage();
    case "scenes": return scenesPage();
    case "phones": return phonesPage();
    case "settings": return settingsPage();
    case "hub": return hubPage();
    case "log": return logPage();
    case "site": return sitePage();
    default: return overviewPage();
  }
}
function bindPage() {
  clearInterval(S.logTimer);
  ({ overview: bindOverview, apps: bindApps, scenes: bindScenes, phones: bindPhones, settings: bindSettings, hub: bindHub, log: bindLog })[S.page]?.();
}

// ---------- overview ----------
// The PC's screen is only streamed to the Android app (it costs CPU/GPU here), so the overview is facts and health.
function overviewPage() {
  const d = S.data;
  return `<div class="page-head"><div><span class="eyebrow">This PC</span><h1>${esc(d.agent.name)}</h1><p>${esc(d.agent.hostname)} · Show Control Agent ${esc(d.agent.version)}</p></div>
    <div class="actions"><a class="btn" href="#phones">${icon("phone")}Pair a phone</a><a class="btn primary" href="#apps">${icon("plus")}Add app</a></div></div>
  <div class="stats" id="meters" style="--n:4">${metersHtml()}</div>
  <div class="overview">
    <section class="card pad"><h2 class="section-title">Connect a phone</h2>
      <dl class="facts">
        <div><dt>Pairing PIN</dt><dd><span class="pin num">${esc(d.pin.slice(0, 3))} ${esc(d.pin.slice(3))}</span></dd></div>
        <div><dt>Address</dt><dd class="mono">${d.agent.ips.map(esc).join("<br>") || "No network"} <span class="muted">port ${d.settings.port}</span></dd></div>
        <div><dt>Phones</dt><dd>${d.clients.length} paired, ${d.connections.length} connected now</dd></div>
        <div><dt>Hub</dt><dd>${hubBadge()}</dd></div>
      </dl></section>
    <section class="card pad"><h2 class="section-title">Apps</h2><div class="signal-strip" id="signals" aria-label="App status">${signalsHtml()}</div></section>
  </div>`;
}
function hubBadge() {
  const h = S.data.hub;
  if (!h.enabled) return `<a href="#hub">Not connected</a>`;
  const label = { connected: "Connected", connecting: "Connecting…", error: "Problem" }[h.state] || h.state;
  return `<span class="tag ${h.state === "connected" ? "" : "warn"}"><span class="led ${h.state === "connected" ? "running" : "connecting"}"></span>${label}${h.workspace ? " to " + esc(h.workspace) : ""}</span>`;
}
function signalsHtml() {
  const apps = S.data.apps;
  if (!apps.length) return `<a href="#apps" class="btn small">${icon("plus")}Add your first app</a>`;
  return apps.map(a => { const st = appState(a.def.id); return `<span class="signal"><span class="led ${st}"></span>${esc(a.def.name)}${st === "running" ? "" : `<span class="muted">${st}</span>`}</span>`; }).join("");
}
function metersHtml() {
  const s = S.data.stats || {}; const ram = s.ramTotalMb ? s.ramUsedMb / s.ramTotalMb : 0;
  const up = s.uptimeSec || 0, h = Math.floor(up / 3600), m = Math.floor(up % 3600 / 60);
  const running = S.data.apps.filter(a => appState(a.def.id) === "running").length;
  return `<div class="stat"><span>CPU</span><b>${Math.round(s.cpu || 0)}%</b><div class="bar"><i style="width:${Math.min(100, s.cpu || 0)}%"></i></div></div>
    <div class="stat"><span>Memory</span><b>${((s.ramUsedMb || 0) / 1024).toFixed(1)} <small>of ${((s.ramTotalMb || 0) / 1024).toFixed(0)} GB</small></b><div class="bar"><i style="width:${ram * 100}%"></i></div></div>
    <div class="stat"><span>Up for</span><b>${h >= 24 ? Math.floor(h / 24) + "d " + (h % 24) + "h" : h + "h " + m + "m"}</b></div>
    <div class="stat"><span>Apps running</span><b>${running} <small>of ${S.data.apps.length}</small></b></div>`;
}
function renderMeters() { const el = $("#meters"); if (el) el.innerHTML = metersHtml(); }
function bindOverview() {}

// ---------- apps ----------
function appsPage() {
  return `<div class="page-head"><div><h1>Apps</h1><p>Programs this PC can start and close when a phone, a scene or the hub asks. Phones only ever see these.</p></div>
    <div class="actions"><button class="btn primary" id="addApp">${icon("plus")}Add app</button></div></div>
    <div class="rack" id="appRack">${appRowsHtml()}</div>`;
}
function appRowsHtml() {
  const apps = S.data.apps;
  if (!apps.length) return `<div class="empty"><b>No apps yet</b>Add TouchDesigner, Resolume, a Unity build or anything else this PC runs in a show.</div>`;
  return apps.map((a, i) => {
    const st = appState(a.def.id), live = S.live[a.def.id] || a;
    const busy = st === "starting" || st === "stopping";
    return `<div class="rack-row" data-id="${esc(a.def.id)}">
      <span class="led ${st}" title="${st}"></span>
      <div style="min-width:0">
        <div class="name">${a.def.color ? `<span class="swatch" style="background:${COLORS[a.def.color] || "#888"}"></span>` : ""}${esc(a.def.name)}
          ${a.def.startWithAgent ? `<span class="tag">Starts with agent</span>` : ""}${a.def.watchdog ? `<span class="tag">Watchdog</span>` : ""}${!a.exists ? `<span class="tag fault">Program not found</span>` : ""}</div>
        <div class="sub mono" title="${esc(a.def.path)}">${esc(a.def.path)}${a.def.arguments ? " " + esc(a.def.arguments) : ""}</div>
      </div>
      <span class="state ${st}">${st}${live.message ? ", " + esc(live.message) : ""}</span>
      <div class="ops">
        ${st === "running" ? `<button class="btn small" data-act="stop">${icon("stop")}Close</button>` : `<button class="btn small" data-act="start" ${busy ? "disabled" : ""}>${icon("play")}${st === "crashed" ? "Restart" : "Start"}</button>`}
        <button class="btn icon ghost" data-act="up" aria-label="Move ${esc(a.def.name)} up" ${i === 0 ? "disabled" : ""}>${icon("up")}</button>
        <button class="btn icon ghost" data-act="edit" aria-label="Edit ${esc(a.def.name)}">${icon("edit")}</button>
      </div>
    </div>`;
  }).join("");
}
function bindAppRows() {
  document.querySelectorAll("#appRack .rack-row").forEach(row => {
    const id = row.dataset.id;
    row.querySelectorAll("[data-act]").forEach(b => b.onclick = async () => {
      const act = b.dataset.act;
      if (act === "start") wsSend({ type: "start_app", appId: id });
      if (act === "stop") wsSend({ type: "stop_app", appId: id });
      if (act === "edit") openAppEditor(S.data.apps.find(a => a.def.id === id).def);
      if (act === "up") {
        const ids = S.data.apps.map(a => a.def.id); const i = ids.indexOf(id); [ids[i - 1], ids[i]] = [ids[i], ids[i - 1]];
        await guarded(() => api("PUT", "/order", { apps: ids })); await refresh(); rerender();
      }
    });
  });
}
function bindApps() { $("#addApp").onclick = () => openAppEditor(null); bindAppRows(); }

function openAppEditor(def) {
  const isNew = !def;
  const a = def ? structuredClone(def) : { name: "", path: "", arguments: "", workingDirectory: "", processName: "", windowStyle: "normal", color: "teal", watchdog: false, startWithAgent: false, gracefulTimeoutSec: 10, readyDelaySec: 3, startTimeoutSec: 60 };
  openDrawer(isNew ? "Add app" : `Edit ${a.name}`, `
    <div class="program" id="programBox"></div>
    <fieldset><legend>Basics</legend>
      <label class="field">Name on the phone<input class="input" id="f_name" value="${esc(a.name)}" maxlength="60" placeholder="e.g. Resolume Arena"></label>
      <div class="field"><label for="f_args">Arguments <span class="hint">Optional. Usually the project file to open</span></label>
        <div class="row"><input class="input mono grow" id="f_args" value="${esc(a.arguments || "")}" placeholder='"D:\\Shows\\Lobby\\mapping.toe"'><button class="btn small" type="button" id="pickProject" style="min-height:40px">${icon("file")}Add a file</button></div></div>
      <div class="field"><label for="f_wd">Start in folder <span class="hint">Leave empty to use the program's own folder</span></label>
        <div class="row"><input class="input mono grow" id="f_wd" value="${esc(a.workingDirectory || "")}"><button class="btn small" type="button" id="pickWd" style="min-height:40px">${icon("folder")}Choose</button></div></div>
      <div class="field">Colour label<div class="colors" id="f_color">${Object.entries(COLORS).map(([k, v]) => `<button type="button" data-c="${k}" aria-label="${k}" aria-pressed="${a.color === k}" style="background:${v}"></button>`).join("")}</div></div>
    </fieldset>
    <fieldset><legend>Behaviour</legend>
      <label class="switch"><input type="checkbox" id="f_start" ${a.startWithAgent ? "checked" : ""}><span>Launch when the agent starts<small>At Windows sign-in, after the startup delay in Settings</small></span></label>
      <label class="switch"><input type="checkbox" id="f_watch" ${a.watchdog ? "checked" : ""}><span>Restart it if it crashes<small>Up to 5 times in 10 minutes</small></span></label>
      <label class="field">Window<select class="input" id="f_win">${["normal", "maximized", "minimized"].map(w => `<option ${a.windowStyle === w ? "selected" : ""} value="${w}">${w[0].toUpperCase() + w.slice(1)}</option>`).join("")}</select></label>
    </fieldset>
    <details class="adv"><summary>Advanced</summary><fieldset>
      <label class="field">Process name <span class="hint">As shown in Task Manager → Details, without .exe. Needed when a launcher starts a different program.</span><input class="input mono" id="f_proc" value="${esc(a.processName || "")}" placeholder="worked out from the program"></label>
      <div class="grid3">
        <label class="field">Close timeout (s)<input class="input" type="number" min="1" max="120" id="f_grace" value="${a.gracefulTimeoutSec}"></label>
        <label class="field">Ready after (s)<input class="input" type="number" min="0" max="120" id="f_ready" value="${a.readyDelaySec}"></label>
        <label class="field">Start timeout (s)<input class="input" type="number" min="5" max="600" id="f_stt" value="${a.startTimeoutSec}"></label>
      </div>
      ${a.id ? `<p class="muted mono" style="margin:0">id: ${esc(a.id)}</p>` : ""}
    </fieldset></details>`,
    `${isNew ? "" : `<button class="btn danger" id="delApp">${icon("trash")}Remove</button>`}<span class="spacer"></span>
     <button class="btn ghost" data-close>Cancel</button><button class="btn primary" id="saveApp">${isNew ? "Add app" : "Save changes"}</button>`);

  const renderProgram = (info) => {
    const box = $("#programBox");
    box.innerHTML = `<div><div style="font-weight:600;margin-bottom:4px">Program</div>
      <div class="path ${a.path ? "" : "muted"}">${esc(a.path || "Choose the .exe, shortcut or batch file to launch.")}</div>
      ${info ? `<div class="muted" style="font-size:13px;margin-top:6px">${info}</div>` : ""}</div>
      <div class="actions">
        ${S.data.canPick ? `<button class="btn small primary" type="button" id="pickNative">${icon("folder")}Choose on this PC</button>` : ""}
        <button class="btn small ${S.data.canPick ? "" : "primary"}" type="button" id="pickBrowse">${icon("folder")}Browse folders</button>
        <button class="btn small" type="button" id="pickRunning">${icon("window")}Pick a running app</button>
        <button class="btn small ghost" type="button" id="pickType">Type a path</button>
      </div>`;
    $("#pickNative")?.addEventListener("click", async () => { const r = await guarded(() => api("POST", "/pick", { kind: "file", initial: a.path })); if (r?.path) useProgram(r.path); });
    $("#pickBrowse").onclick = () => fileBrowser("file", a.path, p => useProgram(p));
    $("#pickRunning").onclick = () => runningPicker(p => useProgram(p));
    $("#pickType").onclick = () => { const p = prompt("Full path to the program", a.path || ""); if (p) useProgram(p); };
  };
  const useProgram = async (path) => {
    const info = await guarded(() => api("POST", "/inspect", { path }));
    if (!info) return;
    a.path = info.path;
    if (!$("#f_name").value.trim()) $("#f_name").value = info.suggestedName;
    $("#f_proc").placeholder = info.processName;
    if (!$("#f_wd").value && info.workingDirectory && /obs64/i.test(info.processName)) $("#f_wd").value = info.workingDirectory; // OBS needs its own folder
    const notes = [];
    if (!info.exists) notes.push("This file doesn't exist on this PC.");
    if (info.originalPath !== info.path) notes.push("The shortcut points here.");
    if (info.running) notes.push("It's running now.");
    if (info.alreadyAdded) notes.push(`Already added as "${esc(info.alreadyAdded)}".`);
    renderProgram(notes.join(" "));
  };
  renderProgram();
  $("#f_color").querySelectorAll("button").forEach(b => b.onclick = () => { a.color = b.dataset.c; $("#f_color").querySelectorAll("button").forEach(x => x.setAttribute("aria-pressed", x === b)); });
  $("#pickWd").onclick = async () => {
    if (S.data.canPick) { const r = await guarded(() => api("POST", "/pick", { kind: "folder", initial: $("#f_wd").value || a.path })); if (r?.path) $("#f_wd").value = r.path; }
    else fileBrowser("folder", $("#f_wd").value || a.path, p => $("#f_wd").value = p);
  };
  $("#pickProject").onclick = async () => {
    const add = p => { const v = $("#f_args").value.trim(); $("#f_args").value = (v ? v + " " : "") + `"${p}"`; };
    if (S.data.canPick) { const r = await guarded(() => api("POST", "/pick", { kind: "project", initial: a.path })); if (r?.path) add(r.path); }
    else fileBrowser("any", a.path, add);
  };
  $("#saveApp").onclick = async () => {
    const body = { ...a, name: $("#f_name").value, arguments: $("#f_args").value, workingDirectory: $("#f_wd").value, processName: $("#f_proc").value,
      windowStyle: $("#f_win").value, startWithAgent: $("#f_start").checked, watchdog: $("#f_watch").checked,
      gracefulTimeoutSec: Number($("#f_grace").value), readyDelaySec: Number($("#f_ready").value), startTimeoutSec: Number($("#f_stt").value) };
    const r = await guarded(() => isNew ? api("POST", "/apps", body) : api("PUT", "/apps/" + encodeURIComponent(a.id), body), isNew ? `Added ${body.name}` : "Saved");
    if (r) { closeDrawer(); await refresh(); rerender(); }
  };
  $("#delApp")?.addEventListener("click", async () => {
    if (!confirm(`Remove ${a.name}? Phones will no longer see it.`)) return;
    if (await guarded(() => api("DELETE", "/apps/" + encodeURIComponent(a.id)), `Removed ${a.name}`)) { closeDrawer(); await refresh(); rerender(); }
  });
}

// ---------- scenes ----------
function scenesPage() {
  return `<div class="page-head"><div><h1>Scenes</h1><p>Step-by-step sequences on this PC: launch apps, wait, send OSC cues, drive OBS. Each one shows up as a tile on the phone.</p></div>
    <div class="actions"><button class="btn primary" id="addScene">${icon("plus")}New scene</button></div></div>
    <div class="rack" id="sceneRack">${sceneRowsHtml()}</div>`;
}
function sceneRowsHtml() {
  const scenes = S.data.scenes;
  if (!scenes.length) return `<div class="empty"><b>No scenes yet</b>A scene can start every app for a show with one tap, or fire a single cue.</div>`;
  return scenes.map((s, i) => {
    const run = S.sceneRuns[s.id];
    const isStartup = S.data.settings.startupSceneId === s.id;
    return `<div class="rack-row" data-id="${esc(s.id)}">
      <span class="swatch" style="background:${COLORS[s.color] || "#888"};width:14px;height:14px"></span>
      <div style="min-width:0"><div class="name">${esc(s.name)}${s.group ? `<span class="tag">${esc(s.group)}</span>` : ""}${s.confirm ? `<span class="tag">Asks first</span>` : ""}${isStartup ? `<span class="tag">Runs at startup</span>` : ""}</div>
        <div class="sub">${run ? `Step ${run.step} of ${run.total}: ${esc(run.label)}${run.state === "failed" ? " (stopped)" : run.state === "done" ? " (done)" : ""}` : s.steps.map(stepSummary).map(esc).join(" → ")}</div></div>
      <span class="state">${s.steps.length} step${s.steps.length === 1 ? "" : "s"}</span>
      <div class="ops">
        <button class="btn small" data-act="run" ${run?.state === "running" ? "disabled" : ""}>${icon("play")}Run</button>
        <button class="btn icon ghost" data-act="up" aria-label="Move ${esc(s.name)} up" ${i === 0 ? "disabled" : ""}>${icon("up")}</button>
        <button class="btn icon ghost" data-act="edit" aria-label="Edit ${esc(s.name)}">${icon("edit")}</button>
      </div></div>`;
  }).join("");
}
function stepSummary(st) {
  const app = id => S.data.apps.find(a => a.def.id === id)?.def.name || id || "?";
  switch (String(st.action).toLowerCase()) {
    case "start": return "Start " + app(st.appId);
    case "stop": return "Close " + app(st.appId);
    case "restart": return "Restart " + app(st.appId);
    case "stopall": return "Close all apps";
    case "wait": return `Wait ${st.seconds ?? "?"} s`;
    case "waitforport": return `Wait for port ${st.port ?? "?"}`;
    case "osc": return `OSC ${st.address || ""}`;
    case "udp": return `UDP "${st.text || ""}"`;
    case "http": return `${(st.method || "GET").toUpperCase()} ${st.url || ""}`;
    case "obs": return `OBS ${st.request || ""}`;
    case "wake": return `Wake ${st.mac || ""}`;
    default: return st.action;
  }
}
function bindSceneRows() {
  document.querySelectorAll("#sceneRack .rack-row").forEach(row => {
    const id = row.dataset.id;
    row.querySelectorAll("[data-act]").forEach(b => b.onclick = async () => {
      const act = b.dataset.act;
      if (act === "run") wsSend({ type: "run_scene", sceneId: id });
      if (act === "edit") openSceneEditor(S.data.scenes.find(s => s.id === id));
      if (act === "up") {
        const ids = S.data.scenes.map(s => s.id); const i = ids.indexOf(id); [ids[i - 1], ids[i]] = [ids[i], ids[i - 1]];
        await guarded(() => api("PUT", "/order", { scenes: ids })); await refresh(); rerender();
      }
    });
  });
}
function bindScenes() { $("#addScene").onclick = () => openSceneEditor(null); bindSceneRows(); }

const stepKey = a => Object.keys(STEP_TYPES).find(k => k.toLowerCase() === String(a).toLowerCase()) || a;
function openSceneEditor(def) {
  const isNew = !def;
  const sc = def ? structuredClone(def) : { name: "", group: "", color: "amber", icon: "play", confirm: false, steps: [] };
  const groups = [...new Set(S.data.scenes.map(s => s.group).filter(Boolean))];
  let openStep = null;
  openDrawer(isNew ? "New scene" : `Edit ${sc.name}`, `
    <fieldset><legend>Tile</legend>
      <div class="grid2">
        <label class="field">Name<input class="input" id="s_name" value="${esc(sc.name)}" maxlength="60" placeholder="e.g. Start show"></label>
        <label class="field">Section on the phone <span class="hint">e.g. Cues</span><input class="input" id="s_group" list="groupList" value="${esc(sc.group || "")}"><datalist id="groupList">${groups.map(g => `<option value="${esc(g)}">`).join("")}</datalist></label>
      </div>
      <div class="grid2">
        <div class="field">Colour label<div class="colors" id="s_color">${Object.entries(COLORS).map(([k, v]) => `<button type="button" data-c="${k}" aria-label="${k}" aria-pressed="${sc.color === k}" style="background:${v}"></button>`).join("")}</div></div>
        <label class="field">Icon on the phone<select class="input" id="s_icon">${ICONS.map(i => `<option ${sc.icon === i ? "selected" : ""}>${i}</option>`).join("")}</select></label>
      </div>
      <label class="switch"><input type="checkbox" id="s_confirm" ${sc.confirm ? "checked" : ""}><span>Ask before running<small>The phone asks "Run this scene?" first</small></span></label>
    </fieldset>
    <fieldset><legend>Steps</legend>
      <ol class="steps" id="stepList"></ol>
      <div id="stepAdd"></div>
    </fieldset>`,
    `${isNew ? "" : `<button class="btn danger" id="delScene">${icon("trash")}Remove</button>`}<span class="spacer"></span>
     <button class="btn ghost" data-close>Cancel</button><button class="btn primary" id="saveScene">${isNew ? "Create scene" : "Save changes"}</button>`);

  const readOpenStep = () => {
    if (openStep === null) return true;
    const st = sc.steps[openStep]; if (!st) return true;
    const body = $("#stepList .card-body"); if (!body) return true;
    for (const el of body.querySelectorAll("[data-k]")) {
      const k = el.dataset.k;
      if (el.type === "checkbox") st[k] = el.checked;
      else if (el.type === "number") st[k] = el.value === "" ? undefined : Number(el.value);
      else if (k === "args") { st.argsText = el.value; delete st.args; }
      else if (k === "data") { try { st.data = el.value.trim() ? JSON.parse(el.value) : {}; } catch { toast(`Step ${openStep + 1}: the OBS data isn't valid JSON`, "fault"); return false; } }
      else if (k === "label") st.label = el.value.trim() || undefined;
      else st[k] = el.value;
    }
    return true;
  };
  const stepFields = (st) => {
    const appSel = () => `<label class="field">App<select class="input" data-k="appId">${S.data.apps.map(a => `<option value="${esc(a.def.id)}" ${a.def.id === st.appId ? "selected" : ""}>${esc(a.def.name)}</option>`).join("") || `<option value="">Add an app first</option>`}</select></label>`;
    const num = (k, label, extra = "") => `<label class="field">${label}<input class="input" type="number" data-k="${k}" value="${st[k] ?? ""}" ${extra}></label>`;
    const text = (k, label, ph = "", mono = true) => `<label class="field">${label}<input class="input ${mono ? "mono" : ""}" data-k="${k}" value="${esc(st[k] ?? "")}" placeholder="${esc(ph)}"></label>`;
    const waitSw = `<label class="switch"><input type="checkbox" data-k="waitForRunning" ${st.waitForRunning !== false ? "checked" : ""}><span>Wait until it's running before the next step</span></label>`;
    const label = text("label", "Label on the phone <span class=\"hint\">Optional</span>", stepSummary(st), false);
    switch (stepKey(st.action)) {
      case "start": return appSel() + waitSw + num("timeoutSec", "Give up after (s)", 'min="1" max="600"') + label;
      case "restart": return appSel() + waitSw + label;
      case "stop": return appSel() + label;
      case "stopAll": return `<p class="muted" style="margin:0">Closes every app registered on this PC.</p>` + label;
      case "wait": return num("seconds", "Seconds", 'min="0.1" max="3600" step="0.1"') + label;
      case "waitForPort": return `<div class="grid2">${num("port", "Port", 'min="1" max="65535"')}${num("timeoutSec", "Give up after (s)", 'min="1" max="600"')}</div><p class="muted" style="margin:0">Handy for OBS (4455) or an app's web server.</p>` + label;
      case "osc": return `<div class="grid2">${text("host", "Host", "127.0.0.1")}${num("port", "Port", 'min="1" max="65535"')}</div>` + text("address", "Address", "/composition/layers/1/clips/1/connect")
        + `<label class="field">Arguments <span class="hint">Separate with commas: 1, 0.5, "intro", true</span><input class="input mono" data-k="args" value="${esc(st.argsText ?? formatArgs(st.args))}"></label>` + label;
      case "udp": return `<div class="grid2">${text("host", "Host", "127.0.0.1")}${num("port", "Port", 'min="1" max="65535"')}</div>` + text("text", "Text", "reset") + label;
      case "http": return `<div class="row"><label class="field" style="width:120px">Method<select class="input" data-k="method">${["GET", "POST", "PUT", "DELETE"].map(m => `<option ${String(st.method || "GET").toUpperCase() === m ? "selected" : ""}>${m}</option>`).join("")}</select></label><div class="grow">${text("url", "Address", "http://192.168.1.50/api/scene")}</div></div>`
        + `<label class="field">Body <span class="hint">Optional. JSON is sent as application/json</span><textarea class="input" data-k="body">${esc(st.body || "")}</textarea></label>` + label;
      case "obs": return `<label class="field">Request<input class="input mono" data-k="request" list="obsList" value="${esc(st.request || "")}"><datalist id="obsList">${OBS_REQUESTS.map(r => `<option value="${r}">`).join("")}</datalist></label>`
        + `<label class="field">Data <span class="hint">JSON, e.g. {"sceneName": "Live"}. Use {} if the request needs none.</span><textarea class="input" data-k="data">${esc(JSON.stringify(st.data || {}, null, 1))}</textarea></label>`
        + `<p class="muted" style="margin:0">Uses the OBS connection in Settings.</p>` + label;
      case "wake": return text("mac", "MAC address", "AA:BB:CC:DD:EE:FF") + label;
      default: return label;
    }
  };
  const renderSteps = () => {
    const list = $("#stepList");
    list.innerHTML = sc.steps.map((st, i) => `<li class="step">
      <div class="rail-n"><span class="n">${i + 1}</span><span class="wire"></span></div>
      <div class="card">
        <div class="card-head"><b>${esc(STEP_TYPES[stepKey(st.action)]?.label || st.action)}</b><span class="desc">${esc(stepSummary(st))}</span>
          <button class="btn icon ghost" data-i="${i}" data-op="test" aria-label="Test step ${i + 1} now" title="Test this step now">${icon("test")}</button>
          <button class="btn icon ghost" data-i="${i}" data-op="up" aria-label="Move step ${i + 1} up" ${i === 0 ? "disabled" : ""}>${icon("up")}</button>
          <button class="btn icon ghost" data-i="${i}" data-op="down" aria-label="Move step ${i + 1} down" ${i === sc.steps.length - 1 ? "disabled" : ""}>${icon("down")}</button>
          ${openStep === i ? `<button class="btn small" data-i="${i}" data-op="toggle" aria-expanded="true">Done</button>` : `<button class="btn icon ghost" data-i="${i}" data-op="toggle" aria-label="Edit step ${i + 1}" aria-expanded="false">${icon("edit")}</button>`}
          <button class="btn icon ghost" data-i="${i}" data-op="del" aria-label="Remove step ${i + 1}">${icon("trash")}</button>
        </div>
        ${openStep === i ? `<div class="card-body">${stepFields(st)}</div>` : ""}
      </div></li>`).join("") || `<p class="muted" style="margin:0">No steps yet. Choose the first one below.</p>`;
    list.querySelectorAll("[data-op]").forEach(b => b.onclick = async () => {
      const i = Number(b.dataset.i), op = b.dataset.op;
      if (!readOpenStep()) return;
      if (op === "up") { [sc.steps[i - 1], sc.steps[i]] = [sc.steps[i], sc.steps[i - 1]]; if (openStep === i) openStep = i - 1; else if (openStep === i - 1) openStep = i; }
      if (op === "down") { [sc.steps[i + 1], sc.steps[i]] = [sc.steps[i], sc.steps[i + 1]]; if (openStep === i) openStep = i + 1; else if (openStep === i + 1) openStep = i; }
      if (op === "del") { sc.steps.splice(i, 1); openStep = null; }
      if (op === "toggle") openStep = openStep === i ? null : i;
      if (op === "test") { const r = await guarded(() => api("POST", "/test-step", sc.steps[i])); if (r) toast(`Step ${i + 1}: ${r.message}`, r.ok ? "" : "fault"); }
      renderSteps();
    });
    $("#stepAdd").innerHTML = `<div class="muted" style="font-size:13px;margin:4px 0 8px">Add a step</div><div class="step-menu">${Object.entries(STEP_TYPES).map(([k, t]) => `<button type="button" data-add="${k}"><b>${t.label}</b><span>${t.hint}</span></button>`).join("")}</div>`;
    $("#stepAdd").querySelectorAll("[data-add]").forEach(b => b.onclick = () => {
      if (!readOpenStep()) return;
      const k = b.dataset.add;
      const firstApp = S.data.apps[0]?.def.id;
      const defaults = { start: { appId: firstApp, waitForRunning: true, timeoutSec: 60 }, stop: { appId: firstApp }, restart: { appId: firstApp, waitForRunning: true, timeoutSec: 60 },
        wait: { seconds: 2 }, waitForPort: { port: 4455, timeoutSec: 30 }, osc: { host: "127.0.0.1", port: 7000, address: "/", args: [] }, udp: { host: "127.0.0.1", port: 9000, text: "" },
        http: { method: "GET", url: "http://" }, obs: { request: "SetCurrentProgramScene", data: { sceneName: "" } }, wake: { mac: "" } };
      sc.steps.push({ action: k, ...(defaults[k] || {}) });
      openStep = sc.steps.length - 1;
      renderSteps();
      $("#stepList .card-body")?.querySelector("input,select,textarea")?.focus();
    });
  };
  renderSteps();
  $("#s_color").querySelectorAll("button").forEach(b => b.onclick = () => { sc.color = b.dataset.c; $("#s_color").querySelectorAll("button").forEach(x => x.setAttribute("aria-pressed", x === b)); });
  $("#saveScene").onclick = async () => {
    if (!readOpenStep()) return;
    const body = { ...sc, name: $("#s_name").value, group: $("#s_group").value, icon: $("#s_icon").value, confirm: $("#s_confirm").checked };
    const r = await guarded(() => isNew ? api("POST", "/scenes", body) : api("PUT", "/scenes/" + encodeURIComponent(sc.id), body), isNew ? `Created ${body.name}` : "Saved");
    if (r) { closeDrawer(); await refresh(); rerender(); }
  };
  $("#delScene")?.addEventListener("click", async () => {
    if (!confirm(`Remove ${sc.name}?`)) return;
    if (await guarded(() => api("DELETE", "/scenes/" + encodeURIComponent(sc.id)), `Removed ${sc.name}`)) { closeDrawer(); await refresh(); rerender(); }
  });
}
function parseArgs(text) {
  const out = [];
  for (const raw of text.match(/("[^"]*"|[^,]+)/g) || []) {
    const v = raw.trim();
    if (!v) continue;
    if (v.startsWith('"') && v.endsWith('"') && v.length >= 2) out.push(v.slice(1, -1));
    else if (v === "true") out.push(true);
    else if (v === "false") out.push(false);
    else if (!isNaN(Number(v))) out.push(Number(v));
    else out.push(v);
  }
  return out;
}
function formatArgs(args) { return (args || []).map(a => typeof a === "string" ? `"${a}"` : String(a)).join(", "); }

// ---------- phones ----------
function phonesPage() {
  const d = S.data;
  return `<div class="page-head"><div><h1>Phones</h1><p>Phones pair once with the PIN. Set a phone to <b>Watch only</b> to hand it to a client: it sees the live view and status but has no controls. Remove a phone to take its access away straight away.</p></div></div>
  <div class="pin-hero">
    <div><div class="muted" style="font-size:13px">Pairing PIN</div><div class="pin" style="font-size:48px;margin:6px 0 8px">${esc(d.pin.slice(0, 3))} ${esc(d.pin.slice(3))}</div>
    <div class="muted">In the app, open a room, tap <b>+</b>, pick this PC and enter the PIN.</div></div>
    <button class="btn" id="newPin">${icon("refresh")}New PIN</button>
  </div>
  <section class="block"><h2>Paired</h2><div class="rack">${d.clients.length ? d.clients.map(c => `<div class="rack-row" style="grid-template-columns:auto minmax(0,1fr) auto">
    ${icon("phone", 'style="width:20px;height:20px;color:var(--muted)"')}<div><div class="name">${esc(c.name)}</div><div class="sub">Paired ${new Date(c.pairedAt).toLocaleString()}</div></div>
    <div class="ops"><select class="input small" data-role="${esc(c.id)}" data-name="${esc(c.name)}" aria-label="What ${esc(c.name)} can do"><option value="operator" ${c.role !== "viewer" ? "selected" : ""}>Can control</option><option value="viewer" ${c.role === "viewer" ? "selected" : ""}>Watch only</option></select><button class="btn small danger" data-remove="${esc(c.id)}" data-name="${esc(c.name)}">Remove</button></div></div>`).join("") : `<div class="empty"><b>No phones paired yet</b>Use the PIN above from the Show Control app.</div>`}</div></section>
  <section class="block"><h2>Connected now</h2><div class="rack">${d.connections.length ? d.connections.map(c => `<div class="rack-row" style="grid-template-columns:auto minmax(0,1fr) auto">
    <span class="led running"></span><div><div class="name">${esc(c.name)}</div><div class="sub mono">${esc(c.remote)}</div></div><span class="tag">${c.viaHub ? "Through the hub, " : ""}${esc(c.role)}</span></div>`).join("") : `<div class="empty">Nobody is connected right now.</div>`}</div></section>`;
}
function bindPhones() {
  $("#newPin").onclick = async () => { if (!confirm("Make a new PIN? Phones already paired keep working.")) return; if (await guarded(() => api("POST", "/pin"), "New PIN ready")) { await refresh(); rerender(); } };
  document.querySelectorAll("[data-role]").forEach(sel => sel.onchange = async () => {
    const ok = await guarded(() => api("PUT", "/clients/" + sel.dataset.role, { role: sel.value }), sel.value === "viewer" ? `${sel.dataset.name} can now only watch` : `${sel.dataset.name} can control again`);
    await refresh(); rerender();
  });
  document.querySelectorAll("[data-remove]").forEach(b => b.onclick = async () => {
    if (!confirm(`Remove ${b.dataset.name}? It will need the PIN to pair again.`)) return;
    if (await guarded(() => api("DELETE", "/clients/" + b.dataset.remove), `Removed ${b.dataset.name}`)) { await refresh(); rerender(); }
  });
}

// ---------- settings ----------
function settingsPage() {
  const s = S.data.settings, d = S.data;
  const startupApps = d.apps.filter(a => a.def.startWithAgent);
  return `<div class="page-head"><div><h1>Settings</h1><p>How this PC behaves as part of a show.</p></div></div>
  <form id="settingsForm" style="display:flex;flex-direction:column;gap:32px;max-width:760px">
    <fieldset><legend>This PC</legend>
      <label class="field">Name shown on phones<input class="input" id="st_name" value="${esc(s.name)}" maxlength="60"></label>
      <label class="switch"><input type="checkbox" id="st_power" ${s.allowPower ? "checked" : ""}><span>Allow phones to restart and shut down this PC</span></label>
      ${d.startWithWindows !== null ? `<label class="switch"><input type="checkbox" id="st_win" ${d.startWithWindows ? "checked" : ""}><span>Start the agent when Windows starts<small>Pair this with automatic sign-in on unattended show PCs</small></span></label>` : ""}
    </fieldset>
    <fieldset><legend>Startup</legend>
      <div class="grid2">
        <label class="field">Wait after the agent starts (s)<input class="input" type="number" min="0" max="600" id="st_delay" value="${s.startupDelaySec}"></label>
        <label class="field">Then run this scene<select class="input" id="st_scene"><option value="">Nothing</option>${d.scenes.map(x => `<option value="${esc(x.id)}" ${x.id === s.startupSceneId ? "selected" : ""}>${esc(x.name)}</option>`).join("")}</select></label>
      </div>
      <p class="muted" style="margin:0">${startupApps.length ? "Apps launched first: " + startupApps.map(a => esc(a.def.name)).join(", ") + "." : "No apps launch at startup yet. Turn on <i>Launch when the agent starts</i> when editing an app."}</p>
    </fieldset>
    <fieldset><legend>OBS connection</legend>
      <div class="grid3">
        <label class="field">Host<input class="input mono" id="st_obsHost" value="${esc(s.obsHost)}"></label>
        <label class="field">Port<input class="input" type="number" id="st_obsPort" value="${s.obsPort}"></label>
        <label class="field">Password<input class="input" type="password" id="st_obsPw" placeholder="${s.obsHasPassword ? "unchanged" : "none"}" autocomplete="new-password"></label>
      </div>
      <div><button class="btn small" type="button" id="testObs">Test connection</button></div>
    </fieldset>
    <fieldset><legend>Opening this dashboard from another computer</legend>
      <label class="switch"><input type="checkbox" id="st_remote" ${s.remoteAdminEnabled ? "checked" : ""}><span>Allow it, with a password<small>At http://${esc(d.agent.ips[0] || "this-pc")}:${s.port}/</small></span></label>
      <label class="field">${s.remoteAdminHasPassword ? "New password" : "Password"} <span class="hint">At least 8 characters${s.remoteAdminHasPassword ? ". Leave empty to keep the current one" : ""}</span><input class="input" type="password" id="st_remotePw" autocomplete="new-password"></label>
    </fieldset>
    <fieldset><legend>Network</legend>
      <div class="grid2">
        <label class="field">Control port<input class="input" type="number" id="st_port" value="${s.port}"></label>
        <label class="field">Discovery port<input class="input" type="number" id="st_dport" value="${s.discoveryPort}"></label>
      </div>
      <p class="muted" style="margin:0">Changing ports restarts the agent, and paired phones need the new port.</p>
    </fieldset>
    <div style="display:flex;gap:10px;flex-wrap:wrap"><button class="btn primary" type="submit">Save settings</button>
      ${d.startWithWindows !== null ? `<button class="btn" type="button" id="restartAgent">${icon("refresh")}Restart agent</button>` : ""}</div>
    <p class="muted mono" style="margin:0">Saved in ${esc(d.configPath)}</p>
  </form>`;
}
function bindSettings() {
  $("#settingsForm").onsubmit = async e => {
    e.preventDefault();
    const body = { name: $("#st_name").value, allowPower: $("#st_power").checked, startupDelaySec: Number($("#st_delay").value), startupSceneId: $("#st_scene").value,
      obsHost: $("#st_obsHost").value, obsPort: Number($("#st_obsPort").value), remoteAdminEnabled: $("#st_remote").checked,
      port: Number($("#st_port").value), discoveryPort: Number($("#st_dport").value) };
    if ($("#st_obsPw").value) body.obsPassword = $("#st_obsPw").value;
    if ($("#st_remotePw").value) body.remoteAdminPassword = $("#st_remotePw").value;
    const r = await guarded(() => api("PUT", "/settings", body), "Settings saved");
    if (!r) return;
    if ($("#st_win")) await guarded(() => api("PUT", "/start-with-windows", { enabled: $("#st_win").checked }));
    if (r.restartNeeded && confirm("The new ports take effect after a restart. Restart the agent now?")) {
      await guarded(() => api("POST", "/restart"));
      setTimeout(() => { location.href = `${location.protocol}//${location.hostname}:${body.port}/#settings`; }, 2500);
      return;
    }
    await refresh(); rerender();
  };
  $("#testObs").onclick = async () => { const r = await guarded(() => api("POST", "/test-obs")); if (r) toast(r.message, r.ok ? "" : "fault"); };
  $("#restartAgent")?.addEventListener("click", async () => { if (confirm("Restart the agent? Phones reconnect by themselves.")) { await guarded(() => api("POST", "/restart")); setTimeout(() => location.reload(), 3000); } });
}

// ---------- hub ----------
function hubPage() {
  const h = S.data.hub;
  return `<div class="page-head"><div><h1>Hub</h1><p>Connect this PC to your Show Control hub so phones can reach it from anywhere, under your accounts and branding.</p></div></div>
  ${h.enabled ? `<div class="rack">${(h.hubs || []).map(x => `<div class="rack-row" style="grid-template-columns:auto minmax(0,1fr) auto">
      <span class="led ${x.state === "connected" ? "running" : x.state === "error" ? "crashed" : "connecting"}"></span>
      <div><div class="name">${x.primary ? esc(h.workspace || "Hub") : x.kind === "site" ? "Site hub (this network)" : x.kind === "cloud" ? "Cloud hub" : "Linked hub"}
          <span class="tag">${x.primary ? "enrolled here" : x.auto ? "linked automatically" : "added here"}</span>${x.lan ? ` <span class="tag">local</span>` : ""}</div>
        <div class="sub mono">${esc(x.url)}</div>
        ${x.lastError && x.state !== "connected" ? `<div class="state crashed">${esc(x.lastError)}</div>` : ""}</div>
      <div class="ops"><span class="tag">${x.sessions} remote now</span>${x.primary ? `<button class="btn small" id="hubRe">${icon("refresh")}Reconnect</button><button class="btn small danger" id="hubOff">Disconnect</button>` : x.auto ? "" : `<button class="btn small" data-also-rm="${esc(x.url)}">Remove</button>`}</div>
    </div>`).join("")}
    ${h.kiosk ? `<div class="rack-row" style="grid-template-columns:auto minmax(0,1fr)">
      <span class="led ${h.kiosk.running ? "running" : h.kiosk.closedHere ? "stopped" : "connecting"}"></span>
      <div><div class="name">Room kiosk: ${esc(h.kiosk.name || "")}</div><div class="sub">${h.kiosk.running ? `On screen ${h.kiosk.locked ? "in kiosk mode (locked full screen)" : "in a window"}` : h.kiosk.closedHere ? "Closed from the tray menu on this PC" : "Opening…"}. Set up on the hub under Kiosks.</div></div>
    </div>` : ""}</div>`
  : `<form id="hubForm" style="display:flex;flex-direction:column;gap:16px;max-width:620px">
      <label class="field">Hub address<input class="input mono" id="h_url" placeholder="https://hub.yourcompany.com" required></label>
      <label class="field">Enrollment code <span class="hint">On the hub, open PCs and choose Add a PC. Codes work once and expire after 15 minutes.</span><input class="input mono" id="h_code" required autocomplete="off" style="letter-spacing:.15em;text-transform:uppercase"></label>
      <div><button class="btn primary" type="submit">Connect to hub</button></div>
    </form>`}
  ${h.enabled ? `<form id="alsoForm" style="display:flex;flex-direction:column;gap:10px;max-width:620px;margin-top:22px">
      <label class="field">Also connect to a linked hub <span class="hint">Linked hubs find each other by themselves. Add one here only if this PC can't hear about it (no code needed: it uses the same key).</span>
        <div style="display:flex;gap:8px"><input class="input mono" id="also_url" placeholder="http://192.168.1.10:47900" style="flex:1"><button class="btn" type="submit">${icon("plus")}Add</button></div></label>
    </form>
    <p class="muted" style="font-size:13px">The room kiosk opens from a hub on this network when there is one, so it keeps working if the internet drops.</p>` : ""}`;
}
function bindHub() {
  $("#hubForm")?.addEventListener("submit", async e => {
    e.preventDefault();
    if (await guarded(() => api("POST", "/hub/enroll", { url: $("#h_url").value, code: $("#h_code").value }), "Connected to the hub")) { await refresh(); rerender(); }
  });
  $("#hubOff")?.addEventListener("click", async () => { if (!confirm("Disconnect from the hub? Remote phones lose access to this PC.")) return; if (await guarded(() => api("POST", "/hub/disconnect"), "Disconnected")) { await refresh(); rerender(); } });
  $("#alsoForm")?.addEventListener("submit", async e => {
    e.preventDefault();
    if (await guarded(() => api("POST", "/hub/also", { url: $("#also_url").value }), "Added")) { await refresh(); rerender(); }
  });
  document.querySelectorAll("[data-also-rm]").forEach(b => b.onclick = async () => { if (await guarded(() => api("POST", "/hub/also/remove", { url: b.dataset.alsoRm }), "Removed")) { await refresh(); rerender(); } });
  $("#hubRe")?.addEventListener("click", async () => { await guarded(() => api("POST", "/hub/reconnect")); setTimeout(async () => { await refresh(); rerender(); }, 1500); });
}

// Kiosks, shared rooms, schedules, people… are kept by the hub so every PC and phone sees the same ones.
function sitePage() {
  return `<div class="page-head"><div><h1>Site pages</h1><p>Kiosks, shared rooms, schedules, people, branding and activity are shared by all your PCs and phones, so they live on your hub. Connect this PC to a hub and they open right here.</p></div></div>
    <div class="rack"><div class="empty" style="text-align:left"><b>Two ways to have a hub</b>
      <ol style="margin:10px 0 0;padding-left:20px;line-height:1.8"><li><b>On this network</b> (works without internet): install <span class="mono">ShowControlHub</span> on one PC at the venue. It runs in the background from Windows start-up at <span class="mono">http://&lt;that PC&gt;:47900</span>.</li>
      <li><b>On your server</b> for access from anywhere, e.g. <span class="mono">https://hub.movingsurface.com.ng</span>. Link the two under <b>Linked hubs</b> and they stay in sync.</li></ol>
      <div style="margin-top:14px"><a class="btn primary" href="#hub">${icon("hub")}Connect this PC to a hub</a></div></div></div>`;
}

// ---------- log ----------
function logPage() {
  return `<div class="page-head"><div><h1>Activity log</h1><p>What the agent did and who asked. Full daily logs are in <span class="mono">${esc(S.data.logFolder)}</span>.</p></div></div><div class="log" id="log" tabindex="0" aria-label="Log lines"></div>`;
}
function bindLog() {
  const load = async () => {
    const r = await api("GET", "/logs").catch(() => null); if (!r) return;
    const el = $("#log"); if (!el) return;
    const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 30;
    el.innerHTML = r.lines.map(l => { const lv = l.split(" ")[1]; return `<div class="${esc(lv)}">${esc(l)}</div>`; }).join("");
    if (atBottom) el.scrollTop = el.scrollHeight;
  };
  load(); S.logTimer = setInterval(load, 2000);
}

// ---------- modals (the side drawer is in sc-ui.js) ----------
document.addEventListener("keydown", e => { if (e.key === "Escape") $(".modal")?.remove(); }, true);

function modal(title, inner, footer = "") {
  $(".modal")?.remove();
  const m = document.createElement("div"); m.className = "modal"; m.setAttribute("role", "dialog"); m.setAttribute("aria-modal", "true"); m.setAttribute("aria-label", title);
  m.innerHTML = `<div class="scrim" style="position:absolute"></div><div class="box" style="position:relative"><header><h2>${esc(title)}</h2><button class="btn icon ghost" data-x aria-label="Close">${icon("close")}</button></header>${inner}${footer ? `<footer>${footer}</footer>` : ""}</div>`;
  document.body.appendChild(m);
  m.querySelector("[data-x]").onclick = () => m.remove();
  m.querySelector(".scrim").onclick = () => m.remove();
  return m;
}
async function fileBrowser(mode, start, onPick) {
  const title = mode === "folder" ? "Choose a folder" : mode === "file" ? "Choose a program" : "Choose a file";
  const startDir = start && /[\\/]/.test(start) ? start.replace(/[\\/][^\\/]*$/, "") : "";
  const m = modal(title, `<div class="crumb"><button class="btn icon small" id="fbUp" aria-label="Up one folder">${icon("back")}</button><input class="input mono" id="fbPath" style="flex:1" placeholder="This PC" aria-label="Folder"><button class="btn small" id="fbGo">Go</button></div>
    <div class="list" id="fbList" style="min-height:300px"></div>`,
    `${mode === "file" ? `<label class="switch" style="font-size:13px"><input type="checkbox" id="fbAll"><span>Show all files</span></label>` : ""}<span style="flex:1"></span>${mode === "folder" ? `<button class="btn primary" id="fbUse">Use this folder</button>` : ""}`);
  let cur = startDir, parent = null;
  const load = async (path) => {
    const fmode = mode === "file" && m.querySelector("#fbAll")?.checked ? "any" : mode;
    const r = await guarded(() => api("GET", `/fs?path=${encodeURIComponent(path || "")}&mode=${fmode}`));
    if (!r) return;
    cur = r.path || ""; parent = r.parent;
    m.querySelector("#fbPath").value = cur;
    const list = m.querySelector("#fbList");
    list.innerHTML = (r.error ? `<p class="notice fault" style="margin:8px">${esc(r.error)}</p>` : "") + (r.entries.length ? r.entries.map(e => `<button class="item ${e.kind}" data-path="${esc(e.path)}" data-kind="${e.kind}">${icon(e.kind === "drive" || e.kind === "place" ? "drive" : e.kind === "dir" ? "folder" : e.kind === "program" ? "program" : "file")}<span>${esc(e.name)}</span>${e.size ? `<span class="meta">${e.size > 1048576 ? (e.size / 1048576).toFixed(1) + " MB" : Math.ceil(e.size / 1024) + " KB"}</span>` : ""}</button>`).join("") : `<p class="muted" style="padding:12px">Nothing here${mode === "file" ? " that can be launched. Tick Show all files to see everything" : ""}.</p>`);
    list.querySelectorAll(".item").forEach(b => b.onclick = () => {
      const k = b.dataset.kind;
      if (k === "dir" || k === "drive" || k === "place") load(b.dataset.path);
      else { m.remove(); onPick(b.dataset.path); }
    });
  };
  m.querySelector("#fbUp").onclick = () => load(parent || "");
  m.querySelector("#fbGo").onclick = () => load(m.querySelector("#fbPath").value);
  m.querySelector("#fbPath").onkeydown = e => { if (e.key === "Enter") load(e.target.value); };
  m.querySelector("#fbAll")?.addEventListener("change", () => load(cur));
  m.querySelector("#fbUse")?.addEventListener("click", () => { if (cur) { m.remove(); onPick(cur); } });
  load(cur);
}
async function runningPicker(onPick) {
  const m = modal("Pick a running app", `<p class="muted" style="margin:12px 18px 0">Start the program the normal way, then pick its window here.</p><div class="list" id="rpList" style="min-height:240px"><p class="muted" style="padding:12px">Looking…</p></div>`);
  const r = await guarded(() => api("GET", "/processes"));
  const list = m.querySelector("#rpList");
  if (!r) return;
  list.innerHTML = r.processes.length ? r.processes.map(p => `<button class="item program" data-path="${esc(p.path || "")}" ${p.path ? "" : "disabled"}>${icon("window")}<span><b>${esc(p.title)}</b><br><span class="mono muted">${esc(p.path || p.name + ": path hidden. Run the agent as administrator to see it.")}</span></span></button>`).join("")
    : `<p class="muted" style="padding:12px">No programs with a window are running.</p>`;
  list.querySelectorAll(".item").forEach(b => b.onclick = () => { if (b.dataset.path) { m.remove(); onPick(b.dataset.path); } });
}

boot();
