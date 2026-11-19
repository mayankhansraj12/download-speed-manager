// Download Speed Manager UI: live list over Server-Sent Events, actions over the JSON API.
const $ = (selector) => document.querySelector(selector);
const list = $("#downloads");
const addUrl = $("#add-url");
const rows = new Map();
let lastDownloads = [];
let settingsDirty = false;

// Same colours as the desktop app: green = running or done, yellow = paused or waiting, red = failed.
const TONE = {
  downloading: "ok",
  completed: "ok",
  queued: "warn",
  connecting: "warn",
  paused: "warn",
  error: "danger",
  cancelled: "danger",
};

const UNITS = ["B", "KB", "MB", "GB", "TB"];
function formatBytes(bytes) {
  if (bytes === null || bytes === undefined) return "?";
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit && value < 100 ? 1 : 0)} ${UNITS[unit]}`;
}

function formatEta(seconds) {
  if (seconds === null || seconds === undefined) return "";
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h) return `${h}h ${m}m left`;
  return m ? `${m}m ${s}s left` : `${s}s left`;
}

/** Until the server has named the file, show the last part of the link rather than the whole URL. */
function displayName(d) {
  if (d.fileName) return d.fileName;
  try {
    const url = new URL(d.url);
    const last = url.pathname.split("/").filter(Boolean).pop();
    if (!last) return url.host;
    try {
      return decodeURIComponent(last);
    } catch {
      return last;
    }
  } catch {
    return d.url;
  }
}

/** Only touch text that changed, so live updates don't wipe a selection or re-announce content. */
function setText(el, text) {
  if (el.textContent !== text) el.textContent = text;
}

async function api(method, path, body) {
  let res;
  try {
    res = await fetch(path, {
      method,
      headers: body ? { "content-type": "application/json" } : {},
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new Error("Couldn't reach Download Speed Manager. Is the app still running?");
  }
  if (!res.ok) {
    const detail = await res.json().catch(() => ({}));
    throw new Error(detail.error ?? `Request failed (${res.status})`);
  }
  return res.status === 204 ? null : res.json();
}

/** Show a message under the add form ("" hides it); `invalid` marks the link field as the cause. */
function showFormError(message, { invalid = false } = {}) {
  const el = $("#form-error");
  el.textContent = message;
  el.hidden = !message;
  if (message && invalid) {
    addUrl.setAttribute("aria-invalid", "true");
    addUrl.setAttribute("aria-describedby", "form-error");
  } else {
    addUrl.removeAttribute("aria-invalid");
    addUrl.removeAttribute("aria-describedby");
  }
}

// ---- Add a download -------------------------------------------------------
const addButton = $("#add-form button[type=submit]");
let adding = false;
$("#add-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (adding) return; // a double click must not add the same link twice
  adding = true;
  addButton.setAttribute("aria-disabled", "true");
  showFormError("");
  try {
    await api("POST", "/api/downloads", {
      url: addUrl.value.trim(),
      connections: Number($("#add-connections").value),
    });
    addUrl.value = "";
  } catch (err) {
    showFormError(err.message, { invalid: true });
  } finally {
    adding = false;
    addButton.removeAttribute("aria-disabled");
  }
});
addUrl.addEventListener("input", () => {
  if (addUrl.hasAttribute("aria-invalid")) showFormError("");
});

// ---- Settings (debounced; live snapshots never overwrite unsaved edits) ---
let settingsTimer;
function saveSettings() {
  settingsDirty = true;
  clearTimeout(settingsTimer);
  settingsTimer = setTimeout(async () => {
    try {
      await api("PUT", "/api/settings", {
        globalSpeedLimit: Math.max(0, Number($("#set-limit").value) || 0) * 1024,
        maxConcurrent: Number($("#set-concurrent").value) || 1,
        downloadDir: $("#set-dir").value,
      });
    } catch (err) {
      showFormError(err.message);
    } finally {
      settingsDirty = false;
    }
  }, 500);
}
for (const id of ["#set-limit", "#set-concurrent", "#set-dir"]) $(id).addEventListener("input", saveSettings);

function applySettings(settings) {
  if (settingsDirty || !settings) return;
  const fields = [
    ["#set-limit", Math.round(settings.globalSpeedLimit / 1024)],
    ["#set-concurrent", settings.maxConcurrent],
    ["#set-dir", settings.downloadDir],
  ];
  for (const [id, value] of fields) {
    if (document.activeElement !== $(id)) $(id).value = value;
  }
}

// ---- Row actions -----------------------------------------------------------
list.addEventListener("click", async (event) => {
  const button = event.target.closest("button[data-action]");
  // aria-disabled (not disabled) marks a request in flight, so keyboard focus stays on the button.
  if (!button || button.getAttribute("aria-disabled") === "true") return;
  const row = button.closest(".download");
  const { id } = row.dataset;
  const { action } = button.dataset;
  const download = row.downloadData;
  const name = displayName(download);
  row.actionError = null;
  showRowError(row, download);
  button.setAttribute("aria-disabled", "true");
  try {
    if (action === "remove") {
      if (download.status !== "completed" && !confirm(`Remove this download and delete its partial file?\n\n${name}`)) return;
      await api("DELETE", `/api/downloads/${id}`);
    } else if (action === "cancel") {
      if (!confirm(`Cancel this download and delete the partial file?\n\n${name}`)) return;
      await api("POST", `/api/downloads/${id}/cancel`);
    } else {
      await api("POST", `/api/downloads/${id}/${action}`);
    }
  } catch (err) {
    // Kept until the download's state changes, so the next live update doesn't hide it at once.
    row.actionError = { message: err.message, status: download.status };
    showRowError(row, download);
  } finally {
    button.removeAttribute("aria-disabled");
  }
});

function showRowError(row, d) {
  const stale = row.actionError && row.actionError.status !== d.status;
  if (stale) row.actionError = null;
  const message = d.error ?? row.actionError?.message ?? "";
  const el = row.querySelector(".row-error");
  setText(el, message);
  el.hidden = !message;
}

// ---- Rendering ---------------------------------------------------------------
function createRow(d) {
  const row = $("#row-template").content.firstElementChild.cloneNode(true);
  row.dataset.id = d.id;
  // Name the progress bar and describe each button with the file name ("Pause, ubuntu.iso").
  const name = row.querySelector(".name");
  name.id = `name-${d.id}`;
  row.querySelector(".map").setAttribute("aria-labelledby", name.id);
  for (const button of row.querySelectorAll(".actions button")) button.setAttribute("aria-describedby", name.id);
  return row;
}

function renderRow(d) {
  let row = rows.get(d.id);
  if (!row) {
    row = createRow(d);
    rows.set(d.id, row);
  }
  row.downloadData = d;
  row.dataset.tone = TONE[d.status] ?? "";

  const label = displayName(d);
  const name = row.querySelector(".name");
  setText(name, label);
  name.title = label === d.url ? d.url : `${label}\n${d.url}`;
  const status = row.querySelector(".status");
  setText(status, d.status);
  status.dataset.status = d.status;

  const pct = d.size ? Math.floor((d.received / d.size) * 100) : null;
  const sizes = d.size ? `${formatBytes(d.received)} of ${formatBytes(d.size)} (${pct}%)` : formatBytes(d.received);
  setText(row.querySelector(".sizes"), sizes);
  const map = row.querySelector(".map");
  const value = d.status === "completed" ? 100 : pct;
  if (value === null) map.removeAttribute("aria-valuenow");
  else map.setAttribute("aria-valuenow", String(value));
  map.setAttribute("aria-valuetext", sizes);

  const active = d.status === "downloading";
  setText(row.querySelector(".speed"), active ? `${formatBytes(d.speed)}/s` : "");
  setText(row.querySelector(".eta"), active ? formatEta(d.eta) : "");
  setText(
    row.querySelector(".conns"),
    active ? (d.resumable ? `${d.activeConnections}/${d.connections} connections` : "1 connection (server can't resume)") : "",
  );

  // When the pressed button goes away (Pause turns into Resume), keep keyboard focus in the row.
  const focused = row.contains(document.activeElement) ? document.activeElement : null;
  const show = (action, visible) => {
    row.querySelector(`[data-action="${action}"]`).hidden = !visible;
  };
  show("pause", ["queued", "connecting", "downloading"].includes(d.status));
  show("resume", ["paused", "error", "cancelled"].includes(d.status));
  show("cancel", !["completed", "cancelled"].includes(d.status));
  if (focused?.hidden) row.querySelector(".actions button:not([hidden])")?.focus();

  showRowError(row, d);
  return row;
}

/** Progress map: one bar per file, filled where each segment has data, with thin gaps at segment starts. */
function drawMap(canvas, d, css) {
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  if (!width) return;
  const ratio = window.devicePixelRatio || 1;
  if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
    canvas.width = Math.round(width * ratio);
    canvas.height = Math.round(height * ratio);
  }
  const ctx = canvas.getContext("2d");
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = css.getPropertyValue(`--${TONE[d.status] ?? "muted"}`).trim();
  if (d.status === "completed") return ctx.fillRect(0, 0, width, height);
  if (!d.size) return;
  if (!d.segments.length) return ctx.fillRect(0, 0, (d.received / d.size) * width, height);

  for (const s of d.segments) {
    if (s.received) ctx.fillRect((s.start / d.size) * width, 0, Math.max(1, (s.received / d.size) * width), height);
  }
  ctx.fillStyle = css.getPropertyValue("--panel").trim();
  for (const s of d.segments) if (s.start > 0) ctx.fillRect((s.start / d.size) * width, 0, 1, height);
}

function render(downloads) {
  lastDownloads = downloads;
  const sorted = [...downloads].sort((a, b) => b.createdAt - a.createdAt);
  const seen = new Set();
  sorted.forEach((d, index) => {
    const row = renderRow(d);
    seen.add(d.id);
    if (list.children[index] !== row) list.insertBefore(row, list.children[index] ?? null);
  });
  let refocus = null;
  for (const [id, row] of rows) {
    if (!seen.has(id)) {
      // A removed row took keyboard focus with it: move to a neighbour, not the top of the page.
      if (row.contains(document.activeElement)) refocus = row.nextElementSibling ?? row.previousElementSibling ?? addUrl;
      row.remove();
      rows.delete(id);
    }
  }
  if (refocus) {
    const target = refocus.isConnected ? refocus.querySelector(".actions button:not([hidden])") : null;
    (target ?? addUrl).focus();
  }
  const css = getComputedStyle(document.documentElement);
  for (const d of sorted) drawMap(rows.get(d.id).querySelector(".map"), d, css);

  $("#empty").hidden = downloads.length > 0;
  const active = downloads.filter((d) => d.status === "downloading");
  setText($("#total-speed"), `${formatBytes(active.reduce((sum, d) => sum + d.speed, 0))}/s`);
  setText($("#total-active"), `${active.length} active`);
}

window.addEventListener("resize", () => render(lastDownloads));
// Progress maps are painted with theme colours; repaint when the system theme flips.
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => render(lastDownloads));

// ---- Live updates --------------------------------------------------------------
function setConnection(state, text) {
  document.body.dataset.connection = state;
  setText($("#connection-state"), text);
}

const source = new EventSource("/api/events");
source.onopen = () => setConnection("live", "Live");
source.onmessage = (event) => {
  const data = JSON.parse(event.data);
  render(data.downloads);
  applySettings(data.settings);
};
source.onerror = () => setConnection("offline", "Reconnecting… is the app still running?");
