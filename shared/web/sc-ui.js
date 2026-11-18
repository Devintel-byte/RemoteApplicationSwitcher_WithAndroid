/*
 * Show Control UI helpers, shared by the PC dashboard (agent) and the hub. Served at /assets/sc-ui.js.
 * Plain script (no build step): defines a few globals the page scripts use.
 *   $ $$ esc ago initials icon ICON APP_MARK   toast guarded   openDrawer closeDrawer   Theme   Shell
 */
"use strict";

// ---------- DOM and text ----------
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const initials = n => {
  const p = String(n || "").trim().split(/\s+/).filter(Boolean);
  return p.length === 0 ? "SC" : p.length === 1 ? p[0].slice(0, 2).toUpperCase() : (p[0][0] + p[1][0]).toUpperCase();
};
const ago = iso => {
  if (!iso) return "never";
  const s = (Date.now() - new Date(iso)) / 1000;
  if (s < 90) return "just now";
  if (s < 3600) return Math.round(s / 60) + " min ago";
  if (s < 86400) return Math.round(s / 3600) + " h ago";
  return new Date(iso).toLocaleDateString();
};

// ---------- icons (24 px, 1.8 stroke) ----------
const ICON = {
  apps: "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
  back: "M15 18l-6-6 6-6",
  brand: "M12 3l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.4 6.8 19.1l1-5.8L3.5 9.2l5.9-.9z",
  clock: "M12 7v5l3 2M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z",
  close: "M6 6l12 12M18 6 6 18",
  cog: "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z",
  down: "M6 9l6 6 6-6",
  drive: "M3 14h18v5H3zM5 14l2-9h10l2 9M17 16.5h.01",
  edit: "M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4",
  ext: "M14 4h6v6M20 4l-9 9M18 14v6H4V6h6",
  file: "M6 3h8l4 4v14H6zM14 3v4h4",
  folder: "M3 6h6l2 2h10v11H3z",
  home: "M3 11l9-8 9 8M5 10v10h14V10",
  hub: "M12 3v6M12 15v6M3 12h6M15 12h6M12 9a3 3 0 1 1 0 6 3 3 0 0 1 0-6z",
  key: "M15 7a4 4 0 1 1-3.5 6L4 20.5V17h3v-3h3l1.5-1.5A4 4 0 0 1 15 7z",
  kiosk: "M6 3h12v14H6zM9 21h6M12 17v4M9 7h2v2H9zM13 7h2v2h-2zM9 11h2v2H9zM13 11h2v2h-2z",
  link: "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1",
  log: "M5 4h14v16H5zM8 8h8M8 12h8M8 16h5",
  menu: "M4 7h16M4 12h16M4 17h16",
  moon: "M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z",
  out: "M15 4h4v16h-4M10 17l5-5-5-5M15 12H3",
  overview: "M3 5h18v11H3zM8 20h8M12 16v4",
  pcs: "M3 5h18v11H3zM8 20h8M12 16v4",
  people: "M16 19v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1M9 10a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM22 19v-1a4 4 0 0 0-3-3.9M16 4.1a3 3 0 0 1 0 5.8",
  phone: "M8 2h8v20H8zM11 18h2",
  play: "M7 4v16l13-8z",
  plus: "M12 5v14M5 12h14",
  power: "M12 3v9M6.3 7.5a8 8 0 1 0 11.4 0",
  program: "M4 5h16v14H4zM4 9h16M8 13l2 2-2 2",
  refresh: "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7",
  scenes: "M12 3l9 5-9 5-9-5zM3 13l9 5 9-5M3 17l9 5 9-5",
  stop: "M6 6h12v12H6z",
  sun: "M12 4V2M12 22v-2M4 12H2M22 12h-2M5.6 5.6 4.2 4.2M19.8 19.8l-1.4-1.4M5.6 18.4l-1.4 1.4M19.8 4.2l-1.4 1.4M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z",
  test: "M9 3h6M10 3v6l-5 9a2 2 0 0 0 2 3h10a2 2 0 0 0 2-3l-5-9V3",
  tiles: "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
  trash: "M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13",
  up: "M6 15l6-6 6 6",
  upload: "M12 16V4M7 9l5-5 5 5M4 20h16",
  window: "M3 5h18v14H3zM3 9h18",
};
const icon = (name, extra = "") =>
  `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" ${extra}><path d="${ICON[name] || ""}"/></svg>`;
/** The app's mark (monitor with a hub node broadcasting), white on the brand tile: see branding/. */
const APP_MARK = `<svg aria-hidden="true" xmlns="http://www.w3.org/2000/svg" viewBox="40 48 176 168"><rect x="46" y="56" width="164" height="112" rx="22" fill="none" stroke="currentColor" stroke-width="18"/><path d="M100 200h56M128 170v26" fill="none" stroke="currentColor" stroke-width="18" stroke-linecap="round"/><circle cx="128" cy="112" r="15" fill="currentColor"/><path d="M155.85 92.50A34 34 0 0 1 155.85 131.50" fill="none" stroke="currentColor" stroke-width="13" stroke-linecap="round"/><path d="M100.15 131.50A34 34 0 0 1 100.15 92.50" fill="none" stroke="currentColor" stroke-width="13" stroke-linecap="round"/></svg>`;
const appMarkTile = () => `<div class="app-mark">${APP_MARK}</div>`;

// ---------- feedback ----------
function toast(msg, kind = "") {
  let host = $("#toasts");
  if (!host) { host = document.createElement("div"); host.id = "toasts"; host.className = "toasts"; host.setAttribute("role", "status"); host.setAttribute("aria-live", "polite"); document.body.append(host); }
  const el = document.createElement("div");
  el.className = "toast " + kind; el.textContent = msg;
  host.append(el);
  setTimeout(() => el.remove(), kind === "fault" ? 6000 : 3200);
}
/** Runs an async action; shows its error as a toast. Returns the result (or true), or null if it failed. */
async function guarded(fn, okMessage) {
  try { const r = await fn(); if (okMessage) toast(okMessage); return r ?? true; }
  catch (e) { toast(e.message, "fault"); return null; }
}

// ---------- side drawer ----------
let _drawerFocus = null, _drawerOnClose = null;
function openDrawer(title, body, footer, onClose, opts = {}) {
  closeDrawer(true);
  _drawerFocus = document.activeElement; _drawerOnClose = onClose || null;
  const scrim = document.createElement("div"); scrim.className = "scrim"; scrim.id = "scrim";
  const dr = document.createElement("aside");
  dr.className = "drawer closed" + (opts.wide ? " wide" : ""); dr.id = "drawer";
  dr.setAttribute("role", "dialog"); dr.setAttribute("aria-modal", "true"); dr.setAttribute("aria-label", title);
  dr.innerHTML = `<header><h2>${esc(title)}</h2><button class="btn icon ghost" data-close aria-label="Close">${icon("close")}</button></header>
    <div class="body">${body}</div>${footer ? `<footer>${footer}</footer>` : ""}`;
  document.body.append(scrim, dr);
  requestAnimationFrame(() => dr.classList.remove("closed"));
  scrim.onclick = () => closeDrawer();
  $$("[data-close]", dr).forEach(b => b.onclick = () => closeDrawer());
  setTimeout(() => dr.querySelector(".body input:not([disabled]), .body select, .body textarea")?.focus(), 80);
  return dr;
}
function closeDrawer(instant) {
  const dr = $("#drawer"); if (!dr) return;
  $("#scrim")?.remove();
  const done = _drawerOnClose; _drawerOnClose = null; done?.();
  if (instant) dr.remove(); else { dr.classList.add("closed"); setTimeout(() => dr.remove(), 230); }
  _drawerFocus?.focus?.();
}
document.addEventListener("keydown", e => {
  if (e.key !== "Escape") return;
  if ($(".shell.menu-open")) Shell.closeMenu(); else closeDrawer();
});

// ---------- theme ----------
const Theme = {
  key: "sc.theme",
  init(key) { this.key = key || this.key; let t = null; try { t = localStorage.getItem(this.key); } catch {} this.apply(t); },
  apply(t) {
    const theme = t || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
    document.documentElement.dataset.theme = theme;
    try { localStorage.setItem(this.key, theme); } catch {}
  },
  get dark() { return document.documentElement.dataset.theme !== "light"; },
  toggle() { this.apply(this.dark ? "light" : "dark"); },
  button(id = "themeBtn") {
    return `<button class="nav" id="${id}">${icon(this.dark ? "sun" : "moon")}${this.dark ? "Light mode" : "Dark mode"}</button>`;
  },
};

// ---------- app shell: side rail on wide screens, top bar + slide-in menu on phones and tablets ----------
const Shell = {
  /**
   * brand: HTML for the brand block · nav: HTML of the nav items · foot: HTML at the bottom of the rail.
   * Returns the whole shell; the page goes in <main id="main">.
   */
  html({ brand, nav, foot = "" }) {
    return `<div class="shell" id="shell">
      <header class="topbar"><div class="brand">${brand}</div>
        <button class="btn icon ghost" id="menuBtn" aria-label="Open menu" aria-controls="rail" aria-expanded="false">${icon("menu")}</button></header>
      <div class="rail-scrim" id="railScrim"></div>
      <nav class="rail" id="rail" aria-label="Sections"><div class="brand">${brand}</div>${nav}<div class="rail-foot">${foot}</div></nav>
      <main id="main"></main>
    </div>`;
  },
  bind() {
    $("#menuBtn")?.addEventListener("click", () => Shell.openMenu());
    $("#railScrim")?.addEventListener("click", () => Shell.closeMenu());
    $$("#rail .nav").forEach(n => n.addEventListener("click", () => Shell.closeMenu()));
  },
  openMenu() { $("#shell")?.classList.add("menu-open"); $("#menuBtn")?.setAttribute("aria-expanded", "true"); $("#rail .nav")?.focus(); },
  closeMenu() { $("#shell")?.classList.remove("menu-open"); $("#menuBtn")?.setAttribute("aria-expanded", "false"); },
  navItem({ id, label, ic, current, count, href, title }) {
    const inner = `${icon(ic)}<span>${esc(label)}</span>${count !== undefined ? `<span class="count">${count}</span>` : ""}`;
    return href
      ? `<a class="nav" href="${esc(href)}" ${title ? `title="${esc(title)}"` : ""}>${inner}</a>`
      : `<button class="nav" data-page="${esc(id)}" ${current ? 'aria-current="page"' : ""}>${inner}</button>`;
  },
};
