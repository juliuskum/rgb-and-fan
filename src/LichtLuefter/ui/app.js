"use strict";

// ------------------------------------------------------------------ Zustand & Brücke

const S = { settings: null, fan: null, light: null, safety: null, autostart: false, conflicts: [] };
const ui = { tab: "light", editProfile: null, curveKey: "fanCpu", dragging: null };
const $ = (id) => document.getElementById(id);
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

const bridge = window.chrome && window.chrome.webview;
function send(type, payload = {}) { if (bridge) bridge.postMessage({ type, ...payload }); }

if (bridge) {
  bridge.addEventListener("message", (e) => {
    const { type, data } = e.data;
    switch (type) {
      case "state":
        S.settings = data.settings; S.autostart = data.autostart; S.safety = data.safety;
        if (!ui.editProfile) ui.editProfile = S.settings.fans.activeProfile !== "bios" ? S.settings.fans.activeProfile : "balanced";
        renderAll();
        break;
      case "fan": { const first = !S.fan; S.fan = data; renderInstrument(); renderFanLive(); if (first) { renderChart(); renderChannels(); } break; }
      case "light": S.light = data; renderLightStatus(); break;
      case "toast": toast(data.text); break;
      case "conflicts": S.conflicts = data; renderConflicts(); break;
    }
  });
}

// ------------------------------------------------------------------ Texte

const EFFECTS = [
  ["static", "Statisch"], ["breathing", "Atmen"], ["colorCycle", "Farbwechsel"], ["rainbow", "Regenbogen"],
  ["temperature", "Temperatur"], ["fire", "Feuer"], ["off", "Aus"],
];
const SWATCHES = ["#3FA9F5", "#FFFFFF", "#FF3B30", "#FF8A00", "#FFD60A", "#34C759", "#00C7BE", "#8E6CF0", "#FF2D92"];
const SPEEDS = [[0, "Langsam"], [1, "Mittel"], [2, "Schnell"]];
const SOURCES = [["cpu", "CPU"], ["gpu", "GPU"], ["max", "Höherer Wert"]];
const CURVES = [["fanCpu", "Lüfter nach CPU"], ["fanGpu", "Lüfter nach GPU"], ["pump", "Pumpe nach CPU"]];
const ROLE_TEXT = { fan: "Lüfter", pump: "Pumpe", off: "nicht geregelt" };
const PROFILE_HINT = {
  bios: "Die App greift nicht ein. Alle Anschlüsse folgen den Kurven aus dem BIOS.",
  quiet: "Leise: Lüfter bleiben lange auf ihrem Minimum (CPU_FAN1 20 %, SYS_FAN6 35 %), die Pumpe auf 70 %. Erst bei hoher Last wird es lauter.",
  balanced: "Ausgewogen: Lüfter steigen gleichmäßig mit der Temperatur, die Pumpe läuft mit 75 bis 100 %.",
  performance: "Leistung: Lüfter reagieren früh und kräftig, die Pumpe läuft immer mit 100 %.",
};

// ------------------------------------------------------------------ Tabs

for (const btn of document.querySelectorAll('[role="tab"]')) {
  btn.addEventListener("click", () => selectTab(btn.id.replace("tabbtn-", "")));
  btn.addEventListener("keydown", (e) => {
    const tabs = [...document.querySelectorAll('[role="tab"]')];
    const i = tabs.indexOf(btn);
    if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
      const n = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
      n.focus(); selectTab(n.id.replace("tabbtn-", ""));
    }
  });
}
function selectTab(name) {
  ui.tab = name;
  for (const b of document.querySelectorAll('[role="tab"]')) {
    const on = b.id === "tabbtn-" + name;
    b.setAttribute("aria-selected", on); b.tabIndex = on ? 0 : -1;
    $(b.getAttribute("aria-controls")).hidden = !on;
  }
  if (name === "fans") renderChart();
}

// ------------------------------------------------------------------ Hilfen

function el(tag, attrs = {}, ...kids) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") n.className = v;
    else if (k === "text") n.textContent = v;
    else if (k.startsWith("on")) n.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) n.setAttribute(k, v === true ? "" : v);
  }
  for (const k of kids) if (k != null) n.append(k);
  return n;
}

function radioGroup(container, items, current, onPick, cls) {
  container.replaceChildren(...items.map(([value, label]) => {
    const b = el("button", { type: "button", role: "radio", "aria-checked": String(value === current), class: cls || "", tabindex: value === current ? 0 : -1 });
    b.textContent = label;
    b.addEventListener("click", () => onPick(value));
    b.addEventListener("keydown", (e) => {
      const i = items.findIndex(x => x[0] === value);
      if (["ArrowRight", "ArrowDown", "ArrowLeft", "ArrowUp"].includes(e.key)) {
        e.preventDefault();
        const d = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : -1;
        const n = items[(i + d + items.length) % items.length][0];
        onPick(n);
        requestAnimationFrame(() => container.querySelector('[aria-checked="true"]')?.focus());
      }
    });
    return b;
  }));
}

let toastTimer;
function toast(text) {
  const t = $("toast");
  t.textContent = text; t.classList.add("show");
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove("show"), 2600);
}

function hexToRgb(h) { h = (h || "#000000").replace("#", ""); return [0, 2, 4].map(i => parseInt(h.slice(i, i + 2), 16) || 0); }
function rgbToHex([r, g, b]) { return "#" + [r, g, b].map(v => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, "0")).join(""); }
function hsv(h, s, v) {
  h = ((h % 360) + 360) % 360; const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
  return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
}
function toHsv([r, g, b]) {
  r /= 255; g /= 255; b /= 255; const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
  let h = d === 0 ? 0 : mx === r ? 60 * (((g - b) / d) % 6) : mx === g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
  if (h < 0) h += 360; return [h, mx === 0 ? 0 : d / mx, mx];
}
// Gleiche Logik wie LightingService.LerpHue: von kalt nach heiß mit abnehmendem Farbton.
function lerpHue(a, b, f) {
  let [ha, sa, va] = toHsv(hexToRgb(a)); let [hb, sb, vb] = toHsv(hexToRgb(b));
  if (hb > ha) hb -= 360;
  return hsv(ha + (hb - ha) * f, sa + (sb - sa) * f, va + (vb - va) * f);
}
function thermal(t) {
  if (t == null) return "var(--line)";
  return t < 60 ? "var(--cool)" : t < 80 ? "var(--warm)" : "var(--hot)";
}
function fmt(n, d = 0) { return n == null ? "–" : Number(n).toFixed(d); }

// ------------------------------------------------------------------ Render: alles

function renderAll() {
  if (!S.settings) return;
  renderLight();
  renderFans();
  renderSettings();
  renderInstrument();
}

// ------------------------------------------------------------------ Licht

let lightTimer;
function lightChanged(extra = {}) {
  renderLight();
  clearTimeout(lightTimer);
  lightTimer = setTimeout(() => send("light.set", { profile: S.settings.light.current, ...extra }), 70);
}

function renderLight() {
  const L = S.settings.light, cur = L.current;
  $("lightEnabled").checked = L.enabled;

  const fx = $("effects");
  fx.replaceChildren(...EFFECTS.map(([id, label]) => {
    const b = el("button", { type: "button", role: "radio", class: "effect", "aria-checked": String(cur.effect === id), tabindex: cur.effect === id ? 0 : -1 },
      el("span", { class: "chip chip-" + id }), el("span", { text: label }));
    b.addEventListener("click", () => { cur.effect = id; if (!L.enabled && id !== "off") { L.enabled = true; lightChanged({ enabled: true }); } else lightChanged(); });
    return b;
  }));

  const needsColor = ["static", "breathing"].includes(cur.effect);
  const animated = ["breathing", "colorCycle", "rainbow", "fire"].includes(cur.effect);
  $("grpColor").hidden = !needsColor;
  $("grpTemp").hidden = cur.effect !== "temperature";
  $("grpSpeed").hidden = !animated;
  $("grpBright").hidden = cur.effect === "off";

  // Farben
  const sw = $("swatches");
  const sel = cur.color.toUpperCase();
  const kids = SWATCHES.map(c => {
    const b = el("button", { type: "button", role: "radio", class: "swatch", "aria-checked": String(sel === c), "aria-label": "Farbe " + c, title: c });
    b.style.background = c;
    b.addEventListener("click", () => { cur.color = c; lightChanged(); });
    return b;
  });
  const custom = el("input", { type: "color", value: cur.color.toLowerCase(), "aria-label": "Eigene Farbe" });
  custom.addEventListener("input", () => { cur.color = custom.value.toUpperCase(); lightChanged(); });
  kids.push(el("label", { class: "custom-color" }, custom, el("span", { text: SWATCHES.includes(sel) ? "Eigene Farbe" : sel })));
  sw.replaceChildren(...kids);
  sw.setAttribute("role", "radiogroup");

  // Temperatur
  radioGroup($("tempSource"), SOURCES, cur.tempSource, v => { cur.tempSource = v; lightChanged(); });
  $("coldC").value = cur.coldC; $("hotC").value = cur.hotC;
  $("coldColor").value = cur.coldColor.toLowerCase(); $("hotColor").value = cur.hotColor.toLowerCase();
  const stops = [0, .25, .5, .75, 1].map(f => rgbToHex(lerpHue(cur.coldColor, cur.hotColor, f)));
  $("tempGradient").style.background = `linear-gradient(90deg, ${stops.join(", ")})`;
  updateTempMarker();

  // Helligkeit & Tempo
  $("brightness").value = cur.brightness;
  $("brightness").style.setProperty("--p", cur.brightness + "%");
  $("brightnessOut").textContent = cur.brightness + " %";
  radioGroup($("speed"), SPEEDS, cur.speed, v => { cur.speed = v; lightChanged(); });

  // Profile
  const list = $("lightProfiles");
  list.replaceChildren(...L.saved.map(p => {
    const dot = el("span", { class: "dot" });
    dot.style.background = p.effect === "rainbow" || p.effect === "colorCycle"
      ? "conic-gradient(#ff4b4b, #ffd24b, #4bff88, #4bc8ff, #9a6bff, #ff4b4b)"
      : p.effect === "temperature" ? `linear-gradient(90deg, ${p.coldColor}, ${p.hotColor})`
      : p.effect === "fire" ? "linear-gradient(#ffae1a, #ff4a00)" : p.effect === "off" ? "var(--line)" : p.color;
    const li = el("li", { class: p.name === cur.name ? "current" : "" },
      el("button", { type: "button", onclick: () => send("light.loadProfile", { name: p.name }) }, dot, el("span", { text: p.name })),
      el("button", { type: "button", class: "del", "aria-label": `Profil ${p.name} löschen`, title: "Löschen", onclick: () => send("light.deleteProfile", { name: p.name }) }, "×"));
    return li;
  }));
  if (L.saved.length === 0) list.replaceChildren(el("li", { class: "hint", text: "Noch keine Profile gespeichert." }));

  updateLedVars();
}

$("lightEnabled").addEventListener("change", (e) => { S.settings.light.enabled = e.target.checked; lightChanged({ enabled: e.target.checked }); });
$("brightness").addEventListener("input", (e) => { S.settings.light.current.brightness = +e.target.value; lightChanged(); });
$("coldC").addEventListener("change", (e) => { const c = S.settings.light.current; c.coldC = Math.min(+e.target.value, c.hotC - 5); lightChanged(); });
$("hotC").addEventListener("change", (e) => { const c = S.settings.light.current; c.hotC = Math.max(+e.target.value, c.coldC + 5); lightChanged(); });
$("coldColor").addEventListener("input", (e) => { S.settings.light.current.coldColor = e.target.value.toUpperCase(); lightChanged(); });
$("hotColor").addEventListener("input", (e) => { S.settings.light.current.hotColor = e.target.value.toUpperCase(); lightChanged(); });
$("saveProfileForm").addEventListener("submit", (e) => {
  e.preventDefault();
  const n = $("profileName").value.trim();
  if (!n) { $("profileName").focus(); toast("Bitte einen Namen eingeben."); return; }
  send("light.saveProfile", { name: n }); $("profileName").value = "";
});

function currentTempFor(src) {
  const c = S.fan?.cpuTemp, g = S.fan?.gpuTemp;
  return src === "cpu" ? c : src === "gpu" ? g : (c == null && g == null ? null : Math.max(c ?? 0, g ?? 0));
}
function updateTempMarker() {
  if (!S.settings) return;
  const cur = S.settings.light.current, t = currentTempFor(cur.tempSource);
  const f = t == null ? 0 : Math.max(0, Math.min(1, (t - cur.coldC) / Math.max(1, cur.hotC - cur.coldC)));
  $("tempMarker").style.left = `calc(${(f * 100).toFixed(1)}% - 1px)`;
  $("tempMarker").title = t == null ? "" : `${fmt(t)} °C`;
}

function renderLightStatus() {
  const L = S.light;
  if (!L) return;
  $("lightConn").textContent = L.connected ? (L.message || "") : (L.message || "Verbinde mit OpenRGB …") + " Startet OpenRGB gerade, verbindet sich die App von selbst.";
  $("lightConn").classList.toggle("warn", !L.connected);
  $("devices").replaceChildren(...(L.devices || []).map(d => el("tr", {}, el("td", { text: d.name }), el("td", { text: d.kind }), el("td", { text: d.mode }))));
  $("openRgbState").textContent = L.connected ? "SDK-Server verbunden." : "SDK-Server nicht erreichbar. In OpenRGB unter „SDK Server“ auf „Start Server“ klicken oder hier neu starten.";
}

// ------------------------------------------------------------------ Instrument (Ring, Temperaturen, Anschlüsse)

const blades = $("blades");
(function buildBlades() {
  const n = 7, parts = [];
  for (let i = 0; i < n; i++) {
    const a = (i * 360) / n;
    parts.push(`<path transform="rotate(${a})" d="M 0 -20 C 26 -30 58 -46 80 -26 C 70 -8 40 2 16 -6 Z"/>`);
  }
  blades.innerHTML = parts.join("");
})();

function updateLedVars() {
  const cur = S.settings?.light.current;
  if (!cur) return;
  const base = cur.effect === "temperature" ? rgbToHex(tempColor()) : cur.color;
  const [r, g, b] = hexToRgb(base);
  document.documentElement.style.setProperty("--led", base);
  document.documentElement.style.setProperty("--led-soft", `rgba(${r}, ${g}, ${b}, .16)`);
}
// Wie im Lichtdienst: Die angezeigte Temperatur folgt dem Messwert mit 6 s Zeitkonstante (sanfter Übergang).
let tempShown = null, tempShownAt = 0;
function stepTempShown(now) {
  const t = S.settings ? currentTempFor(S.settings.light.current.tempSource) : null;
  if (t == null) return;
  const dt = (now - tempShownAt) / 1000; tempShownAt = now;
  tempShown = tempShown == null || dt > 5 || reduceMotion ? t : tempShown + (t - tempShown) * (1 - Math.exp(-dt / 6));
}
function tempColor() {
  const cur = S.settings.light.current, t = tempShown ?? currentTempFor(cur.tempSource);
  const f = t == null ? 0 : Math.max(0, Math.min(1, (t - cur.coldC) / Math.max(1, cur.hotC - cur.coldC)));
  return lerpHue(cur.coldColor, cur.hotColor, f);
}

let angle = 0, last = performance.now(), fire = 0.5;
function frame(now) {
  const dt = Math.min(0.1, (now - last) / 1000); last = now;
  const halo = $("halo"), glow = $("haloGlow");
  if (S.settings) {
    const L = S.settings.light, cur = L.current, t = now / 1000;
    const bright = cur.brightness / 100, sp = cur.speed;
    const effect = L.enabled ? cur.effect : "off";
    let fill = null, opacity = 1, rotate = 0;
    if (effect === "off") { fill = "var(--line)"; opacity = .5; }
    else if (effect === "static") { fill = cur.color; opacity = .25 + .75 * bright; }
    else if (effect === "temperature") { stepTempShown(now); fill = rgbToHex(tempColor()); opacity = .25 + .75 * bright; }
    else if (effect === "breathing") {
      const p = [7, 4, 2][sp]; const k = reduceMotion ? 1 : Math.pow((1 - Math.cos(2 * Math.PI * t / p)) / 2, 1.6);
      fill = cur.color; opacity = (.12 + .88 * k) * (.25 + .75 * bright);
    }
    else if (effect === "colorCycle") { fill = rgbToHex(hsv(reduceMotion ? 200 : t * [6, 15, 36][sp], 1, 1)); opacity = .25 + .75 * bright; }
    else if (effect === "rainbow") {
      fill = "conic-gradient(#ff3b30, #ffd60a, #34c759, #00c7be, #3fa9f5, #8e6cf0, #ff2d92, #ff3b30)";
      rotate = reduceMotion ? 0 : (t * [30, 70, 140][sp]) % 360; opacity = .25 + .75 * bright;
    }
    else if (effect === "fire") {
      fire += (Math.random() - fire) * (reduceMotion ? 0 : .18 + .1 * sp);
      fill = rgbToHex(fire < .5 ? mix([120, 8, 0], [255, 70, 0], fire * 2) : mix([255, 70, 0], [255, 170, 20], (fire - .5) * 2));
      opacity = (.55 + .45 * fire) * (.25 + .75 * bright);
    }
    for (const n of [halo, glow]) {
      n.style.background = fill;
      n.style.transform = rotate ? `rotate(${rotate}deg)` : "";
    }
    halo.style.opacity = opacity;
    glow.style.opacity = opacity * .85;
  }
  // Flügel drehen proportional zur gemessenen Drehzahl der geregelten Lüfter.
  if (!reduceMotion && S.fan) {
    const rpms = S.fan.headers.filter(h => h.role !== "pump" && h.rpm > 0).map(h => h.rpm);
    const rpm = rpms.length ? rpms.reduce((a, b) => a + b, 0) / rpms.length : 0;
    angle = (angle + dt * (rpm / 1000) * 360 * 0.9) % 360;
    blades.style.transform = `rotate(${angle}deg)`;
  }
  requestAnimationFrame(frame);
}
function mix(a, b, f) { return a.map((v, i) => v + (b[i] - v) * f); }
requestAnimationFrame(frame);

function renderInstrument() {
  const F = S.fan;
  if (!F) return;
  for (const [id, t, raw] of [["tCpu", F.cpuTemp, F.cpuTempRaw], ["tGpu", F.gpuTemp, F.gpuTempRaw]]) {
    const n = $(id);
    n.querySelector(".num").textContent = fmt(t);
    n.title = raw == null ? "" : id === "tCpu"
      ? `Durchschnitt aller Kerne, über einige Sekunden geglättet. Danach richten sich Lüfter und Licht. Heißester Kern gerade: ${fmt(raw)} °C`
      : `Über einige Sekunden geglättet. Gerade gemessen: ${fmt(raw)} °C`;
    const bar = n.querySelector(".temp-bar span");
    bar.style.width = t == null ? "0" : Math.max(0, Math.min(100, (t - 20) / 80 * 100)) + "%";
    bar.style.background = thermal(t);
  }

  const shown = F.headers.filter(h => h.role !== "off" || (h.rpm ?? 0) > 0);
  $("headerList").replaceChildren(...shown.map(h => {
    const meter = el("span", { class: "meter" }, el("span"));
    meter.firstChild.style.width = Math.max(0, Math.min(100, h.duty ?? 0)) + "%";
    return el("li", { class: "hdr" + (h.controlled ? " controlled" : "") },
      el("span", { class: "hdr-name" }, h.label, el("span", { class: "hdr-role", text: ROLE_TEXT[h.role] ?? "" })),
      el("span", { class: "hdr-rpm" }, fmt(h.rpm), el("small", { text: " U/min" })),
      el("span", { class: "hdr-bar" }, meter, el("output", { text: fmt(h.duty) + " %" })));
  }));
  const empty = F.headers.filter(h => h.role === "off" && !((h.rpm ?? 0) > 0)).map(h => h.label);
  $("emptyHeaders").textContent = empty.length ? `Ohne Drehzahl und nicht geregelt: ${empty.join(", ")}` : "";

  // Wer regelt gerade?
  const line = $("controlLine");
  line.className = "control-line" + (F.mode === "fault" ? " fault" : F.emergency ? " emergency" : "");
  const prof = S.settings?.fans.profiles.find(p => p.id === F.profile)?.name ?? "";
  const ctl = F.headers.filter(h => h.controlled).map(h => h.label);
  let html;
  const sm = S.safety || { pumpMin: 70, fanMin: 30 };
  switch (F.mode) {
    case "starting": html = "Sensoren starten …"; break;
    case "unsupported": html = esc(F.message); break;
    case "suspended": html = "Ruhezustand: BIOS-Kurve aktiv."; break;
    case "fault": html = `<strong>Störung.</strong> ${esc(F.message)}`; break;
    case "active":
      html = F.emergency ? `<strong>${esc(F.message)}</strong>`
        : `<strong>${esc(prof)}</strong> regelt ${esc(joinDe(ctl))}. Mindestwerte: ${esc(F.headers.filter(h => h.controlled).map(h => `${h.label} ${h.minPercent} %`).join(", "))}. Beim Beenden übernimmt wieder das BIOS.`;
      break;
    default:
      html = F.message ? esc(F.message) : "Alle Lüfter folgen der <strong>BIOS-Kurve</strong>.";
  }
  line.innerHTML = html;
  if (F.mode === "fault") line.append(el("div", {}, el("button", { class: "btn", type: "button", onclick: () => send("fan.retry"), text: "Erneut versuchen" })));

  updateTempMarker();
  if (S.settings?.light.current.effect === "temperature") updateLedVars();
  $("hwInfo").textContent = [F.boardName, F.cpuName, F.gpuName].filter(Boolean).join(", ");
}
function esc(s) { return String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])); }
function joinDe(a) { return a.length <= 1 ? (a[0] ?? "keinen Anschluss") : a.slice(0, -1).join(", ") + " und " + a[a.length - 1]; }

// ------------------------------------------------------------------ Lüfter

function renderFans() {
  const Fs = S.settings.fans;
  const items = [["bios", "BIOS-Kurve"], ...Fs.profiles.map(p => [p.id, p.name])];
  radioGroup($("fanProfiles"), items, Fs.activeProfile, (id) => {
    Fs.activeProfile = id; if (id !== "bios") ui.editProfile = id;
    send("fan.setProfile", { id }); renderFans();
  });
  $("fanProfileHint").textContent = PROFILE_HINT[Fs.activeProfile] ?? "";

  const sel = $("editProfile");
  sel.replaceChildren(...Fs.profiles.map(p => el("option", { value: p.id, text: p.name, selected: p.id === ui.editProfile })));
  radioGroup($("curveKey"), CURVES, ui.curveKey, (k) => { ui.curveKey = k; renderFans(); });
  renderChannels();
  renderChart();
}
$("editProfile").addEventListener("change", (e) => { ui.editProfile = e.target.value; renderChart(); });
$("resetCurves").addEventListener("click", () => send("fan.resetProfile", { id: ui.editProfile }));

function renderChannels() {
  const chans = S.settings.fans.channels;
  const live = S.fan?.headers ?? [];
  const rows = (live.length ? live.map(h => h.index) : chans.map(c => c.index)).map(idx => {
    const c = chans.find(x => x.index === idx) || { index: idx, label: live.find(h => h.index === idx)?.label ?? "Kanal " + idx, role: "off" };
    const h = live.find(x => x.index === idx);
    const opts = idx === 1 ? [["pump", "Pumpe"], ["off", "Nicht regeln"]] : [["fan", "Lüfter"], ["pump", "Pumpe"], ["off", "Nicht regeln"]];
    const s = el("select", { "aria-label": `Rolle für ${c.label}` }, ...opts.map(([v, t]) => el("option", { value: v, text: t, selected: c.role === v })));
    s.addEventListener("change", () => send("fan.setChannel", { index: idx, role: s.value }));
    const min = h && c.role !== "off" ? `min. ${h.minPercent} %` : "";
    return el("tr", {}, el("td", { text: c.label }), el("td", { class: "rpm", "data-idx": idx, text: h ? `${fmt(h.rpm)} U/min` : "" }),
      el("td", { class: "rpm", text: min }), el("td", {}, s));
  });
  $("channels").replaceChildren(...rows);
}

function renderFanLive() {
  for (const td of document.querySelectorAll(".channels td.rpm")) {
    const h = S.fan.headers.find(x => x.index === +td.dataset.idx);
    if (h) td.textContent = `${fmt(h.rpm)} U/min`;
  }
  if (ui.tab === "fans" && !ui.dragging) renderChartNow();
}

// ------------------------------------------------------------------ Kurveneditor

const CH = { x0: 46, x1: 626, y0: 14, y1: 262, t0: 20, t1: 100 };
const sx = (t) => CH.x0 + (t - CH.t0) / (CH.t1 - CH.t0) * (CH.x1 - CH.x0);
const sy = (p) => CH.y1 - p / 100 * (CH.y1 - CH.y0);
const svgNS = "http://www.w3.org/2000/svg";
function sv(tag, attrs = {}, text) {
  const n = document.createElementNS(svgNS, tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (typeof v === "string" && v.startsWith("var(")) n.style.setProperty(k, v);
    else n.setAttribute(k, v);
  }
  if (text != null) n.textContent = text;
  return n;
}
function editProfile() { return S.settings.fans.profiles.find(p => p.id === ui.editProfile) || S.settings.fans.profiles[0]; }
function floorFor(key) { return key === "pump" ? (S.safety?.pumpMin ?? 70) : (S.safety?.fanMin ?? 30); }
function evalCurve(pts, t) {
  const s = [...pts].sort((a, b) => a.t - b.t);
  if (!s.length) return 100;
  if (t <= s[0].t) return s[0].p;
  if (t >= s[s.length - 1].t) return s[s.length - 1].p;
  for (let i = 1; i < s.length; i++) if (t <= s[i].t) { const a = s[i - 1], b = s[i]; return a.p + (b.p - a.p) * (t - a.t) / Math.max(.001, b.t - a.t); }
  return s[s.length - 1].p;
}
function pathFor(pts) {
  const s = [...pts].sort((a, b) => a.t - b.t);
  if (!s.length) return "";
  const d = [`M ${CH.x0} ${sy(s[0].p)}`, ...s.map(p => `L ${sx(p.t)} ${sy(p.p)}`), `L ${CH.x1} ${sy(s[s.length - 1].p)}`];
  return d.join(" ");
}

function renderChart() {
  if (!S.settings) return;
  const svg = $("chart"), prof = editProfile(), key = ui.curveKey, floor = floorFor(key);
  $("pumpHint").hidden = key !== "pump";
  svg.replaceChildren();
  const defs = sv("defs");
  const pat = sv("pattern", { id: "hatch", width: 8, height: 8, patternUnits: "userSpaceOnUse", patternTransform: "rotate(45)" });
  pat.append(sv("rect", { width: 8, height: 8, fill: "var(--surface)" }), sv("line", { x1: 0, y1: 0, x2: 0, y2: 8, stroke: "var(--line)", "stroke-width": 3 }));
  defs.append(pat); svg.append(defs);

  const grid = sv("g", { class: "grid" }), axis = sv("g", { class: "axis" });
  for (let p = 0; p <= 100; p += 20) {
    grid.append(sv("line", { x1: CH.x0, x2: CH.x1, y1: sy(p), y2: sy(p) }));
    axis.append(sv("text", { x: CH.x0 - 8, y: sy(p) + 4, "text-anchor": "end" }, p + " %"));
  }
  for (let t = 20; t <= 100; t += 10) {
    grid.append(sv("line", { x1: sx(t), x2: sx(t), y1: CH.y0, y2: CH.y1, "stroke-opacity": .5 }));
    axis.append(sv("text", { x: sx(t), y: CH.y1 + 18, "text-anchor": "middle" }, t + " °C"));
  }
  svg.append(grid);
  svg.append(sv("rect", { class: "locked", x: CH.x0, y: sy(floor), width: CH.x1 - CH.x0, height: CH.y1 - sy(floor) }));
  svg.append(sv("text", { class: "locked-label", x: CH.x1 - 6, y: sy(floor) + 15, "text-anchor": "end" },
    key === "pump" ? `Gesperrt: Pumpe nie unter ${floor} %` : `Gesperrt: kein Lüfter unter ${floor} %`));
  // Anschlüsse mit höherem gemessenem Minimum: gestrichelte Linie (darunter wird für diesen Anschluss angehoben).
  if (key !== "pump" && S.fan) {
    for (const h of S.fan.headers.filter(h => h.role === "fan" && h.minPercent > floor)) {
      svg.append(sv("line", { x1: CH.x0, x2: CH.x1, y1: sy(h.minPercent), y2: sy(h.minPercent), class: "curve-other" }));
      svg.append(sv("text", { class: "locked-label", x: CH.x0 + 6, y: sy(h.minPercent) - 6 }, `${h.label} läuft nie unter ${h.minPercent} %`));
    }
  }
  svg.append(axis);

  for (const [k] of CURVES) if (k !== key) svg.append(sv("path", { class: "curve-other", d: pathFor(prof[k]) }));
  svg.append(sv("path", { class: "curve", d: pathFor(prof[key]), id: "curvePath" }));
  svg.append(sv("g", { class: "now", id: "nowMarker" }));

  const pts = prof[key];
  pts.forEach((p, i) => {
    const c = sv("circle", { class: "pt", cx: sx(p.t), cy: sy(p.p), r: 8, tabindex: 0, role: "slider",
      "aria-label": `Punkt ${i + 1}: ${p.t} °C, ${p.p} %`, "aria-valuenow": p.p, "aria-valuemin": floor, "aria-valuemax": 100 });
    c.addEventListener("pointerdown", (e) => { c.setPointerCapture(e.pointerId); ui.dragging = { i, el: c }; c.classList.add("drag"); });
    c.addEventListener("pointermove", (e) => {
      if (!ui.dragging || ui.dragging.el !== c) return;
      const m = svg.getScreenCTM().inverse(), pt = new DOMPoint(e.clientX, e.clientY).matrixTransform(m);
      movePoint(i, CH.t0 + (pt.x - CH.x0) / (CH.x1 - CH.x0) * (CH.t1 - CH.t0), (CH.y1 - pt.y) / (CH.y1 - CH.y0) * 100);
    });
    c.addEventListener("pointerup", () => { ui.dragging = null; c.classList.remove("drag"); commitCurve(); renderChart(); });
    c.addEventListener("keydown", (e) => {
      const step = e.shiftKey ? 5 : 1, q = pts[i];
      const map = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] };
      if (!map[e.key]) return;
      e.preventDefault();
      movePoint(i, q.t + map[e.key][0], q.p + map[e.key][1]);
      clearTimeout(ui.keyTimer); ui.keyTimer = setTimeout(commitCurve, 400);
      renderChart();
      requestAnimationFrame(() => svg.querySelectorAll(".pt")[i]?.focus());
    });
    svg.append(c);
  });
  renderChartNow();
}

function movePoint(i, t, p) {
  const prof = editProfile(), pts = prof[ui.curveKey], floor = floorFor(ui.curveKey);
  const lo = i > 0 ? pts[i - 1].t + 1 : 20, hi = i < pts.length - 1 ? pts[i + 1].t - 1 : 100;
  pts[i].t = Math.round(Math.max(lo, Math.min(hi, t)));
  pts[i].p = Math.round(Math.max(floor, Math.min(100, p)));
  const c = ui.dragging?.el;
  if (c) { c.setAttribute("cx", sx(pts[i].t)); c.setAttribute("cy", sy(pts[i].p)); }
  $("curvePath").setAttribute("d", pathFor(pts));
  const label = $("chart").querySelector(".pt-label") || $("chart").appendChild(sv("text", { class: "pt-label" }));
  label.setAttribute("x", sx(pts[i].t) + 12); label.setAttribute("y", sy(pts[i].p) - 12);
  label.textContent = `${pts[i].t} °C, ${pts[i].p} %`;
}
function commitCurve() { send("fan.updateProfile", { profile: editProfile() }); }

function renderChartNow() {
  const g = $("nowMarker");
  if (!g || !S.fan) return;
  const key = ui.curveKey, t = key === "fanGpu" ? S.fan.gpuTemp : S.fan.cpuTemp;
  g.replaceChildren();
  if (t == null) return;
  const tt = Math.max(20, Math.min(100, t));
  const p = Math.max(floorFor(key), evalCurve(editProfile()[key], t));
  g.append(sv("line", { x1: sx(tt), x2: sx(tt), y1: CH.y0, y2: CH.y1 }), sv("circle", { cx: sx(tt), cy: sy(p), r: 5 }));
  const right = sx(tt) > CH.x1 - 120;
  g.append(sv("text", { x: sx(tt) + (right ? -10 : 10), y: CH.y0 + 14, "text-anchor": right ? "end" : "start" },
    `jetzt ${fmt(t)} °C, Kurve ${fmt(p)} %`));
}

// ------------------------------------------------------------------ Einstellungen

function renderSettings() {
  $("autostart").checked = S.autostart;
  $("startOpenRgb").checked = S.settings.general.startOpenRgb;
  const s = S.safety;
  if (s) $("rules").replaceChildren(
    el("li", { text: `Die Pumpe (PUMP_FAN1) läuft nie unter ${s.pumpMin} %. Fällt ihre Drehzahl unter 1000 U/min, übernimmt sofort das BIOS und du bekommst eine Meldung.` }),
    el("li", { text: `Jeder Lüfteranschluss hat ein gemessenes Minimum: CPU_FAN1 20 % (läuft stabil bis 15 %), SYS_FAN6 35 % (bleibt bei 25 % stehen). Nicht gemessene Anschlüsse laufen nie unter 40 %. Bleibt ein vorher drehender Lüfter trotzdem stehen, übernimmt das BIOS.` }),
    el("li", { text: `Ab ${s.cpuEmergency} °C CPU oder ${s.gpuEmergency} °C GPU (geglättet) laufen alle geregelten Anschlüsse auf 100 %, unabhängig vom Profil. Bei einem einzelnen Messwert ab ${s.cpuEmergencyRaw} °C CPU oder ${s.gpuEmergencyRaw} °C GPU sofort.` }),
    el("li", { text: "Temperaturen werden über einige Sekunden geglättet. Die CPU meldet als „Package“ immer den gerade heißesten Kern, und der springt beim Hochtakten innerhalb von Sekundenbruchteilen um 20 °C und mehr. Das ist normales Verhalten der CPU, für die Wasserkühlung aber ohne Bedeutung." }),
    el("li", { text: "Fällt ein Temperatursensor länger als 5 Sekunden aus oder bestätigt der Lüfter-Controller einen Befehl nicht, übernimmt das BIOS." }),
    el("li", { text: "Beim Beenden, Abmelden und im Ruhezustand gehen alle Anschlüsse zurück auf die BIOS-Kurven. Stürzt die App ab oder hängt sie, erledigt das ein eigener Wächterprozess." }),
    el("li", { text: "BIOS, Treiber und OpenRGB-Einstellungen werden nicht verändert. Das Mainboard bekommt nur Hardware-Modi, nie Direct." }),
  );
}
$("autostart").addEventListener("change", (e) => send("autostart.set", { enabled: e.target.checked }));
$("startOpenRgb").addEventListener("change", (e) => send("general.set", { startOpenRgb: e.target.checked }));
$("restartOpenRgb").addEventListener("click", () => send("openrgb.restart"));
$("openBackups").addEventListener("click", (e) => { e.preventDefault(); send("open.folder", { which: "backups" }); });
$("openLogs").addEventListener("click", () => send("open.folder", { which: "logs" }));
$("exitApp").addEventListener("click", () => send("app.exit"));

function renderConflicts() {
  const c = S.conflicts || [];
  $("conflictGroup").hidden = c.length === 0;
  $("conflicts").textContent = c.length ? `Läuft gerade: ${c.join(", ")}. Diese Programme greifen ebenfalls auf Licht oder Lüfter zu. Bitte beenden, damit nur Licht & Lüfter die Hardware steuert.` : "";
}

selectTab("light");
send("ready");
