/* Room kiosk and tour guide screen (served at /assets/kiosk.js). */
"use strict";
const $ = s => document.querySelector(s);
const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const LABELS = { teal: ["#0F6B5C", "#1FB59A"], amber: ["#7A4A00", "#E09400"], violet: ["#3B2D8C", "#7F6BF0"], blue: ["#0B3D73", "#2F86E0"], coral: ["#7A2E12", "#E8683A"], red: ["#6E1218", "#D63A44"], gray: ["#2A2F38", "#58606E"] };
const initials = n => { const p = String(n || "").trim().split(/\s+/).filter(Boolean); return p.length === 0 ? "SC" : p.length === 1 ? p[0].slice(0, 2).toUpperCase() : (p[0][0] + p[1][0]).toUpperCase(); };
const ICONS = { back: "M15 18l-6-6 6-6", full: "M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5", exitfull: "M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5", out: "M15 4h4v16h-4M10 8l-4 4 4 4M6 12h10", pc: "M3 5h18v11H3zM8 20h8M12 16v4" };
const icon = n => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${ICONS[n]}"/></svg>`;

/*
 * Two ways in:
 *  - Room screen: the kiosk's own link (#k=…), opened by the room PC. Locked to that one kiosk.
 *  - Tour guide: a person signed in to the hub (a viewer, or an operator/admin). They pick a PC, then one of
 *    the kiosks assigned to them, and use it full screen from their own tablet or phone.
 */
let KEY = new URLSearchParams(location.hash.slice(1)).get("k");
let TOKEN = null;
try {
  if (KEY) localStorage.setItem("kiosk.key", KEY);
  else { TOKEN = localStorage.getItem("hub.token"); if (!TOKEN) KEY = localStorage.getItem("kiosk.key"); }
} catch {}
if (KEY && location.hash.includes("k=")) history.replaceState(null, "", location.pathname);
const MODE = KEY ? "screen" : TOKEN ? "guide" : "none";
if (MODE === "guide") document.documentElement.classList.add("guide");

const S = { cfg: null, sig: "", pending: null, failures: 0, view: "home", pc: null, kioskId: null, list: null, me: null, ws: null };

// Visitors can't select, drag, zoom or open menus.
for (const ev of ["contextmenu", "selectstart", "dragstart", "gesturestart"]) document.addEventListener(ev, e => e.preventDefault());
document.addEventListener("keydown", e => { if ((e.ctrlKey || e.metaKey) && ["+", "-", "=", "0", "p", "s", "o", "f", "l", "n", "t"].includes(e.key.toLowerCase())) e.preventDefault(); });
document.addEventListener("wheel", e => { if (e.ctrlKey) e.preventDefault(); }, { passive: false });

async function call(method, path, body) {
  const headers = body ? { "Content-Type": "application/json" } : {};
  if (MODE === "screen") headers["X-Kiosk-Key"] = KEY || ""; else headers.Authorization = "Bearer " + TOKEN;
  const res = await fetch("/api" + path, { method, headers, body: body ? JSON.stringify(body) : undefined, cache: "no-store" });
  let json = null; try { json = await res.json(); } catch {}
  if (!res.ok) { const err = new Error(json?.error || json?.detail || `Error ${res.status}`); err.status = res.status; throw err; }
  return json;
}

/** Tour guide covers need the sign-in header, so they're fetched once and shown from memory. */
const coverCache = new Map();
async function loadCovers(root = document) {
  if (MODE !== "guide") return;
  for (const img of root.querySelectorAll("img[data-src]")) {
    const src = img.dataset.src; img.removeAttribute("data-src");
    try {
      if (!coverCache.has(src)) coverCache.set(src, fetch(src, { headers: { Authorization: "Bearer " + TOKEN } }).then(r => r.ok ? r.blob() : null).then(b => b ? URL.createObjectURL(b) : null));
      const url = await coverCache.get(src); if (url) img.src = url;
    } catch {}
  }
}
const imgTag = (src, alt = "") => MODE === "guide" ? `<img data-src="${esc(src)}" alt="${esc(alt)}" decoding="async">` : `<img src="${esc(src)}" alt="${esc(alt)}" decoding="async">`;

let toastTimer;
function toast(msg, fault) { const t = $("#toast"); t.textContent = msg; t.className = "toast show" + (fault ? " fault" : ""); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.className = "toast", fault ? 5000 : 2600); }

function screen(title, text, extra = "") {
  $("#app").innerHTML = `<div class="center"><div><h2>${esc(title)}</h2><p>${text}</p>${extra}</div></div>`;
}

// ---------------- room screen + kiosk grid (both modes) ----------------

async function refresh() {
  if (MODE === "none") { screen("Show Control kiosks", "Sign in with the account your operator gave you to use your kiosks.", `<a class="signin" href="/?next=kiosk">Sign in</a>`); return; }
  if (MODE === "guide" && S.view !== "kiosk") return refreshGuide();
  try {
    const cfg = await call("GET", MODE === "screen" ? "/kiosk/config" : `/my/kiosks/${encodeURIComponent(S.kioskId)}/config`);
    S.failures = 0; $("#banner").classList.remove("show");
    if (MODE === "screen") { try { localStorage.setItem("kiosk.cache", JSON.stringify(cfg)); } catch {} }
    apply(cfg);
  } catch (e) {
    if (MODE === "guide" && (e.status === 401 || e.status === 404)) { if (e.status === 401) return signedOut(); toast(e.message, true); return go("home"); }
    if (e.status === 401) { S.cfg = null; S.sig = ""; screen("This screen's link has changed", "An operator made a new link for this kiosk. It updates by itself on the room PC; on other devices, open the new link."); return; }
    if (++S.failures >= 2) $("#banner").classList.add("show");
    if (!S.cfg && MODE === "screen") { try { const c = JSON.parse(localStorage.getItem("kiosk.cache")); if (c) apply(c); else screen("Connecting…", "Waiting for the hub."); } catch { screen("Connecting…", "Waiting for the hub."); } }
  }
}

function apply(cfg) {
  const ws = cfg.workspace || {};
  document.documentElement.dataset.accent = ws.accent || "indigo";
  document.title = `${cfg.kiosk.room || cfg.kiosk.name} · ${ws.name || "Show Control"}`;
  const sig = JSON.stringify(cfg);
  if (sig === S.sig) return;
  const shape = c => JSON.stringify({ k: c.kiosk, w: c.workspace, i: c.items.map(x => [x.id, x.title, x.subtitle, x.color, x.cover]) });
  const structural = !S.cfg || shape(S.cfg) !== shape(cfg);
  S.cfg = cfg; S.sig = sig;
  if (structural) render(); else update();
}

function layout(n) {
  const set = S.cfg.kiosk.columns;
  const portrait = innerHeight > innerWidth * 1.1;
  let cols = set > 0 ? set : n <= 1 ? 1 : n === 2 ? 2 : n <= 3 ? 3 : n === 4 ? 2 : n <= 6 ? 3 : 4;
  if (portrait && !(set > 0)) cols = n <= 1 ? 1 : n === 3 ? 1 : 2; // upright screens: wide tiles stacked
  const rows = Math.max(1, Math.ceil(n / cols));
  const g = $("#grid");
  g.style.setProperty("--cols", cols);
  // Fill the screen when it fits; scroll (with tiles at least 220 px tall) when there are many.
  g.style.setProperty("--row", rows <= 3 ? "1fr" : "minmax(220px, calc((100% - 2 * var(--gap)) / 3))");
}

function render() {
  const { kiosk, workspace: ws, items } = S.cfg;
  if (MODE === "screen" && (!kiosk.enabled || kiosk.paused)) {
    screen(kiosk.paused ? "Paused by staff" : "This screen is resting", "It comes back when an operator resumes it from the hub.");
    return;
  }
  if (MODE === "guide" && !kiosk.enabled) { screen("This kiosk is turned off", "An operator can turn it back on from the hub.", guideBar()); bindGuideBar(); return; }
  $("#app").innerHTML = `
    ${MODE === "guide" ? guideBar() : ""}
    <header>
      <div class="logo" id="logo" ${kiosk.hasExitPin && MODE === "screen" ? 'aria-label="Staff: press and hold to exit"' : ""}>${ws.logoUrl ? `<img src="${esc(ws.logoUrl)}" alt="">` : esc(initials(ws.name))}<span class="hold"></span></div>
      <div class="brand"><b>${esc(ws.name || "")}</b><h1>${esc(kiosk.room || kiosk.name)}</h1></div>
      <div class="clock"><b id="time"></b><span id="date"></span></div>
    </header>
    <p class="welcome">${esc(kiosk.welcome || "")}</p>
    ${items.length ? `<div class="grid" id="grid" role="list">${items.map(tileHtml).join("")}</div>`
      : `<div class="center"><div><h2>Nothing to show yet</h2><p>Add items to this kiosk on the hub.</p></div></div>`}`;
  if (items.length) {
    layout(items.length);
    document.querySelectorAll(".tile").forEach(b => b.addEventListener("click", () => choose(b.dataset.id)));
  }
  tick(); if (MODE === "screen") bindExit(kiosk.hasExitPin); else bindGuideBar();
  loadCovers();
}

function tileHtml(it) {
  const [c1, c2] = LABELS[it.color] || LABELS.amber;
  const active = S.cfg.activeItemId === it.id;
  return `<button class="tile${active ? " active" : ""}${it.available ? "" : " off"}${S.pending === it.id ? " pending" : ""}" data-id="${esc(it.id)}" role="listitem" aria-label="${esc(it.title)}${it.available ? "" : ", unavailable"}${active ? ", now playing" : ""}">
    <div class="art${it.cover ? "" : " plain"}" style="--c1:${c1};--c2:${c2}">${it.cover ? imgTag(it.cover) : `<i>${esc(initials(it.title))}</i>`}</div>
    ${active ? `<span class="badge"><span class="eq"><i></i><i></i><i></i></span>Now playing</span>` : it.available ? "" : `<span class="badge">Unavailable</span>`}
    <div class="label"><h2>${esc(it.title)}</h2>${it.subtitle ? `<p>${esc(it.subtitle)}</p>` : ""}</div>
    <div class="busy"><div class="spin"></div></div>
  </button>`;
}

/** Status-only change (now playing, availability): patch tiles in place so images don't reload. */
function update() {
  if (!$("#grid")) { render(); return; }
  S.cfg.items.forEach(it => {
    const el = document.querySelector(`.tile[data-id="${CSS.escape(it.id)}"]`); if (!el) return;
    const tmp = document.createElement("div"); tmp.innerHTML = tileHtml(it);
    const fresh = tmp.firstElementChild;
    el.className = fresh.className;
    el.setAttribute("aria-label", fresh.getAttribute("aria-label"));
    el.querySelector(".badge")?.remove();
    const badge = fresh.querySelector(".badge"); if (badge) el.insertBefore(badge, el.querySelector(".label"));
  });
}

async function choose(id) {
  const it = S.cfg.items.find(x => x.id === id);
  if (!it || S.pending) return;
  if (!it.available) { toast(`${it.title} isn't available right now`, true); return; }
  S.pending = id; update();
  try {
    const r = await call("POST", MODE === "screen" ? "/kiosk/run" : `/my/kiosks/${encodeURIComponent(S.kioskId)}/run`, { itemId: id });
    if (!r.ok) toast(r.message || "That didn't work. Please try again.", true);
    else S.cfg.activeItemId = id;
  } catch (e) {
    toast(e.status === 401 ? (MODE === "screen" ? "This screen needs a new link." : "Please sign in again.") : "Can't reach the show system. Please try again.", true);
  } finally {
    S.pending = null; update(); setTimeout(refresh, 400);
  }
}

function tick() {
  const t = $("#time"), d = $("#date"); if (!t) return;
  const now = new Date();
  t.textContent = now.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  d.textContent = now.toLocaleDateString([], { weekday: "long", day: "numeric", month: "long" });
}

// Staff exit (room screen): press and hold the logo for 3 seconds, then enter the PIN.
function bindExit(enabled) {
  const logo = $("#logo"); if (!logo || !enabled) return;
  let timer = null;
  const start = e => { e.preventDefault(); logo.classList.add("holding"); timer = setTimeout(() => { logo.classList.remove("holding"); pinPad(); }, 3000); };
  const stop = () => { clearTimeout(timer); logo.classList.remove("holding"); };
  logo.addEventListener("pointerdown", start);
  ["pointerup", "pointerleave", "pointercancel"].forEach(ev => logo.addEventListener(ev, stop));
}

function pinPad() {
  let pin = "";
  const m = document.createElement("div"); m.className = "modal";
  m.innerHTML = `<div class="pad" role="dialog" aria-modal="true" aria-label="Staff exit">
    <h3>Staff exit</h3><p class="sub">Enter the kiosk PIN</p><div class="dots" id="dots"></div>
    <div class="keys">${[1, 2, 3, 4, 5, 6, 7, 8, 9].map(n => `<button data-k="${n}">${n}</button>`).join("")}<button class="muted" data-k="cancel">Cancel</button><button data-k="0">0</button><button class="ok" data-k="ok">Exit</button></div>
    <p class="err" id="perr"></p></div>`;
  document.body.appendChild(m);
  const draw = () => { $("#dots").innerHTML = Array.from({ length: Math.max(4, pin.length) }, (_, i) => `<i class="${i < pin.length ? "on" : ""}"></i>`).join(""); };
  draw();
  const close = () => m.remove();
  const idle = setTimeout(close, 30000);
  m.querySelectorAll("[data-k]").forEach(b => b.onclick = async () => {
    const k = b.dataset.k;
    if (k === "cancel") { clearTimeout(idle); close(); return; }
    if (k === "ok") {
      try {
        await call("POST", "/kiosk/exit", { pin });
        clearTimeout(idle); close();
        screen("Kiosk closed", "Resume it from the hub (Kiosks → Resume). On the room PC, the kiosk window closes in a moment.");
      } catch (e) { pin = ""; draw(); $("#perr").textContent = e.message; }
      return;
    }
    if (pin.length < 8) { pin += k; draw(); $("#perr").textContent = ""; }
  });
}

// ---------------- tour guide: your PCs → your kiosks → full screen ----------------

function signedOut() {
  try { localStorage.removeItem("hub.token"); } catch {}
  screen("Please sign in again", "Your sign-in has expired.", `<a class="signin" href="/?next=kiosk">Sign in</a>`);
}

function guideBar() {
  const fs = !!document.fullscreenElement;
  return `<div class="guide-bar"><button class="iconbtn" id="gBack" aria-label="Back to kiosks">${icon("back")}<span class="lbl">Kiosks</span></button>
    ${document.fullscreenEnabled ? `<button class="iconbtn round" id="gFull" aria-label="${fs ? "Leave full screen" : "Full screen"}">${icon(fs ? "exitfull" : "full")}</button>` : ""}</div>`;
}
function bindGuideBar() {
  $("#gBack")?.addEventListener("click", () => { if (document.fullscreenElement) document.exitFullscreen().catch(() => {}); go(S.list && pcGroups().length > 1 ? "pc" : "home", S.pc); });
  $("#gFull")?.addEventListener("click", () => { if (document.fullscreenElement) document.exitFullscreen().catch(() => {}); else document.documentElement.requestFullscreen().catch(() => {}); });
}
document.addEventListener("fullscreenchange", () => { const b = $("#gFull"); if (b) { const fs = !!document.fullscreenElement; b.innerHTML = icon(fs ? "exitfull" : "full"); b.setAttribute("aria-label", fs ? "Leave full screen" : "Full screen"); } });

function pcGroups() {
  const groups = new Map();
  for (const k of S.list || []) {
    const key = k.pcId || "_any";
    if (!groups.has(key)) groups.set(key, { id: key, name: k.pcName || "Any screen", online: k.pcId ? k.pcOnline : true, kiosks: [] });
    groups.get(key).kiosks.push(k);
  }
  return [...groups.values()];
}

function go(view, pc = null, kioskId = null) {
  S.view = view; S.pc = pc; S.kioskId = kioskId; S.cfg = null; S.sig = "";
  history.replaceState(null, "", location.pathname + (view === "kiosk" ? `#guide-k=${kioskId}` : view === "pc" ? `#guide-pc=${pc}` : ""));
  if (view === "kiosk") { $("#app").innerHTML = ""; refresh(); }
  else renderGuide();
}

async function refreshGuide() {
  try {
    const [r, me] = await Promise.all([call("GET", "/my/kiosks"), S.me ? Promise.resolve(S.me) : call("GET", "/me")]);
    S.me = me; S.failures = 0; $("#banner").classList.remove("show");
    document.documentElement.dataset.accent = me.workspace?.accent || "indigo";
    const sig = JSON.stringify(r.kiosks);
    if (sig === JSON.stringify(S.list) && $("#guideRoot")) return;
    S.list = r.kiosks;
    renderGuide();
  } catch (e) {
    if (e.status === 401) return signedOut();
    if (++S.failures >= 2) $("#banner").classList.add("show");
    if (!S.list) screen("Connecting…", "Waiting for the hub.");
  }
}

function renderGuide() {
  if (!S.list || !S.me) { refreshGuide(); return; }
  const ws = S.me.workspace || {}, groups = pcGroups();
  document.title = `Your kiosks · ${ws.name || "Show Control"}`;
  // One PC: go straight to its kiosks.
  if (S.view === "home" && groups.length === 1) { S.view = "pc"; S.pc = groups[0].id; }
  const head = (title, back) => `<header class="top" id="guideRoot">
      ${back ? `<button class="iconbtn" id="gHome" aria-label="All PCs">${icon("back")}<span class="lbl">All PCs</span></button>` : `<div class="logo">${ws.logoUrl ? `<img src="${esc(ws.logoUrl)}" alt="">` : esc(initials(ws.name))}</div>`}
      <div class="brand"><b>${esc(ws.name || "")}</b><h1>${esc(title)}</h1></div>
      <span style="flex:1"></span><button class="iconbtn" id="gOut" aria-label="Sign out">${icon("out")}<span class="lbl">Sign out</span></button></header>`;
  if (S.view === "home") {
    $("#app").innerHTML = `${head("Your kiosks", false)}
      <p class="hello">Hello, ${esc(S.me.user.displayName)}</p><p class="welcome">Choose a PC to see the kiosks you can run on it.</p>
      ${groups.length ? `<div class="list">${groups.map(g => `<button class="card" data-pc="${esc(g.id)}">
          <div class="pcicon">${icon("pc")}</div>
          <div class="meta"><b>${esc(g.name)}</b><span>${esc([...new Set(g.kiosks.map(k => k.room).filter(Boolean))].join(", ") || "")}</span>
          <span>${g.kiosks.length} kiosk${g.kiosks.length === 1 ? "" : "s"}</span><span class="state ${g.online ? "on" : "off"}">${g.id === "_any" ? "Any device" : g.online ? "Online" : "Offline"}</span></div></button>`).join("")}</div>`
        : `<div class="center"><div><h2>No kiosks yet</h2><p>Ask an operator to assign kiosks to your account (Hub → Kiosks → Viewers).</p></div></div>`}`;
    document.querySelectorAll("[data-pc]").forEach(b => b.onclick = () => go("pc", b.dataset.pc));
  } else {
    const g = groups.find(x => x.id === S.pc);
    if (!g) { go("home"); return; }
    $("#app").innerHTML = `${head(g.name, groups.length > 1)}
      <p class="welcome">Tap a kiosk to open it full screen.</p>
      <div class="list">${g.kiosks.map(k => `<button class="card" data-k="${esc(k.id)}" ${k.enabled ? "" : 'style="opacity:.5"'}>
        <div class="mosaic">${k.covers.map((c, i) => c ? imgTag(c) : `<span style="background:linear-gradient(140deg,${(LABELS[k.colors[i]] || LABELS.amber)[0]},${(LABELS[k.colors[i]] || LABELS.amber)[1]})">${esc(initials(k.titles[i]))}</span>`).join("") || `<span>${esc(initials(k.name))}</span>`}</div>
        <div class="meta"><b>${esc(k.name)}</b><span>${esc(k.room || "")}${k.room ? " · " : ""}${k.items} item${k.items === 1 ? "" : "s"}${k.available < k.items ? `, ${k.items - k.available} unavailable` : ""}</span>
        ${k.activeTitle ? `<span class="state on">Now playing: ${esc(k.activeTitle)}</span>` : k.enabled ? "" : `<span class="state off">Turned off</span>`}</div></button>`).join("")}</div>`;
    $("#gHome")?.addEventListener("click", () => go("home"));
    document.querySelectorAll("[data-k]").forEach(b => b.onclick = () => {
      // Full screen needs the tap itself, so ask for it here, then open the kiosk.
      document.documentElement.requestFullscreen?.().catch(() => {});
      go("kiosk", S.pc, b.dataset.k);
    });
  }
  $("#gOut")?.addEventListener("click", async () => { try { await call("POST", "/logout"); } catch {} signedOut(); location.href = "/"; });
  loadCovers();
}

// ---------------- start ----------------
if (MODE === "guide") {
  const m = location.hash.match(/^#guide-(k|pc)=(.+)$/);
  if (m?.[1] === "k") { S.view = "kiosk"; S.kioskId = decodeURIComponent(m[2]); }
  else if (m?.[1] === "pc") { S.view = "pc"; S.pc = decodeURIComponent(m[2]); }
}
addEventListener("resize", () => { if ($("#grid") && S.cfg) layout(S.cfg.items.length); });
refresh();
setInterval(refresh, 3000);
setInterval(tick, 10000);
