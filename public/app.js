// Download Speed Manager UI: live list over Server-Sent Events, actions over the JSON API.
const $ = (selector) => document.querySelector(selector);
const list = $("#downloads");
const rows = new Map();
let lastDownloads = [];
let settingsDirty = false;

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

async function api(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: body ? { "content-type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    const detail = await res.json().catch(() => ({}));
    throw new Error(detail.error ?? `Request failed (${res.status})`);
  }
  return res.status === 204 ? null : res.json();
}

function showFormError(message) {
  const el = $("#form-error");
  el.textContent = message;
  el.hidden = !message;
}

// ---- Add a download -------------------------------------------------------
$("#add-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  showFormError("");
  try {
    await api("POST", "/api/downloads", {
      url: $("#add-url").value.trim(),
      connections: Number($("#add-connections").value),
    });
    $("#add-url").value = "";
  } catch (err) {
    showFormError(err.message);
  }
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
  if (!button) return;
  const row = button.closest(".download");
  const { id } = row.dataset;
  const { action } = button.dataset;
  const download = row.downloadData;
  button.disabled = true;
  try {
    if (action === "remove") {
      if (download.status !== "completed" && !confirm("Remove this download and delete its partial file?")) return;
      await api("DELETE", `/api/downloads/${id}`);
    } else if (action === "cancel") {
      if (!confirm("Cancel this download and delete the partial file?")) return;
      await api("POST", `/api/downloads/${id}/cancel`);
    } else {
      await api("POST", `/api/downloads/${id}/${action}`);
    }
  } catch (err) {
    const el = row.querySelector(".row-error");
    el.textContent = err.message;
    el.hidden = false;
  } finally {
    button.disabled = false;
  }
});

// ---- Rendering ---------------------------------------------------------------
function renderRow(d) {
  let row = rows.get(d.id);
  if (!row) {
    row = $("#row-template").content.firstElementChild.cloneNode(true);
    row.dataset.id = d.id;
    rows.set(d.id, row);
  }
  row.downloadData = d;

  const name = row.querySelector(".name");
  name.textContent = d.fileName ?? d.url;
  name.title = d.url;
  const status = row.querySelector(".status");
  status.textContent = d.status;
  status.dataset.status = d.status;

  const pct = d.size ? Math.floor((d.received / d.size) * 100) : null;
  row.querySelector(".sizes").textContent = d.size
    ? `${formatBytes(d.received)} of ${formatBytes(d.size)} (${pct}%)`
    : formatBytes(d.received);
  const active = d.status === "downloading";
  row.querySelector(".speed").textContent = active ? `${formatBytes(d.speed)}/s` : "";
  row.querySelector(".eta").textContent = active ? formatEta(d.eta) : "";
  row.querySelector(".conns").textContent = active
    ? d.resumable
      ? `${d.activeConnections}/${d.connections} connections`
      : "1 connection (server can't resume)"
    : "";

  const show = (action, visible) => {
    row.querySelector(`[data-action="${action}"]`).hidden = !visible;
  };
  show("pause", ["queued", "connecting", "downloading"].includes(d.status));
  show("resume", ["paused", "error", "cancelled"].includes(d.status));
  show("cancel", !["completed", "cancelled"].includes(d.status));

  const error = row.querySelector(".row-error");
  error.hidden = !d.error;
  error.textContent = d.error ?? "";
  return row;
}

/** Progress map: one bar per file, filled where each segment has data, with thin gaps at segment starts. */
function drawMap(canvas, d, css) {
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  if (!width) return;
  const ratio = window.devicePixelRatio || 1;
  if (canvas.width !== Math.round(width * ratio)) {
    canvas.width = Math.round(width * ratio);
    canvas.height = Math.round(height * ratio);
  }
  const ctx = canvas.getContext("2d");
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = css.getPropertyValue(d.status === "completed" ? "--fill-done" : "--fill").trim();
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
  for (const [id, row] of rows) {
    if (!seen.has(id)) {
      row.remove();
      rows.delete(id);
    }
  }
  const css = getComputedStyle(document.documentElement);
  for (const d of sorted) drawMap(rows.get(d.id).querySelector(".map"), d, css);

  $("#empty").hidden = downloads.length > 0;
  const active = downloads.filter((d) => d.status === "downloading");
  $("#total-speed").textContent = `${formatBytes(active.reduce((sum, d) => sum + d.speed, 0))}/s`;
  $("#total-active").textContent = `${active.length} active`;
}

window.addEventListener("resize", () => render(lastDownloads));

// ---- Live updates --------------------------------------------------------------
const source = new EventSource("/api/events");
source.onopen = () => {
  $("#connection-state").textContent = "Live";
};
source.onmessage = (event) => {
  const data = JSON.parse(event.data);
  render(data.downloads);
  applySettings(data.settings);
};
source.onerror = () => {
  $("#connection-state").textContent = "Reconnecting… is the app still running?";
};
