// Download Speed Manager extension: background worker.
// Works like IDM's browser integration: downloads, links and videos are handed to a native-messaging
// host that relays them to the running Windows app. The cross-platform Node.js app's local API is a
// fallback. If neither answers, or a hand-off fails, the browser simply keeps its download.
if (typeof importScripts === "function") importScripts("shared.js"); // Chrome/Edge service worker

const HOST = "com.downloadspeedmanager.host";
const browserFallback = new Set(); // URLs deliberately given back to the browser
let bypassUntil = 0; // Alt+click: let the browser handle downloads until this time
let appCache = { at: 0, value: null };

// ---- Talking to the app -----------------------------------------------------------------

async function nativeSend(message) {
  try {
    return (await chrome.runtime.sendNativeMessage(HOST, message)) ?? null;
  } catch {
    return null; // host not registered, or the app could not start
  }
}

async function localFetch(path, init = {}) {
  const { localPort } = await DSM.settings();
  const { timeout = 2000, ...rest } = init;
  return fetch(`http://127.0.0.1:${localPort}${path}`, { ...rest, signal: AbortSignal.timeout(timeout) });
}

/** "native" (Windows app), "local" (Node.js app) or null. Cached briefly so a burst of downloads pings once. */
async function availableApp(fresh = false) {
  if (!fresh && Date.now() - appCache.at < 5000) return appCache.value;
  let value = null;
  if ((await nativeSend({ type: "ping" }))?.ok) value = "native";
  else {
    try {
      if ((await localFetch("/api/ping", { timeout: 800 })).ok) value = "local";
    } catch {
      // not running
    }
  }
  appCache = { at: Date.now(), value };
  return value;
}

async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

/** One download. The Windows app shows its "New download" dialog unless `silent`. */
async function sendDownload({ url, referrer, fileName, silent = false }) {
  const target = await availableApp();
  if (!target) throw new Error("Download Speed Manager isn't running or isn't installed.");
  const payload = { url, referrer, fileName, cookies: await cookieHeader(url), userAgent: navigator.userAgent };
  if (target === "native") {
    const reply = await nativeSend({ type: "download", silent, ...payload });
    if (!reply?.ok) throw new Error(reply?.error ?? "The app did not accept the download.");
    return;
  }
  const res = await localFetch("/api/downloads", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
    timeout: 5000,
  });
  if (!res.ok) throw new Error(`The app refused the download (HTTP ${res.status}).`);
}

/** Many links at once (link picker): queued in the app without a dialog for each. */
async function sendBatch(items, referrer, later = false) {
  const target = await availableApp();
  if (!target) throw new Error("Download Speed Manager isn't running or isn't installed.");
  const withCookies = await Promise.all(
    items.map(async (i) => ({ url: i.url, fileName: i.fileName, cookies: await cookieHeader(i.url) })),
  );
  if (target === "native") {
    const reply = await nativeSend({ type: "batch", referrer, later, userAgent: navigator.userAgent, items: withCookies });
    if (!reply?.ok) throw new Error(reply?.error ?? "The app did not accept the links.");
    return reply.sent ?? items.length;
  }
  let sent = 0;
  for (const item of withCookies) {
    const res = await localFetch("/api/downloads", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ...item, referrer, userAgent: navigator.userAgent, start: !later }),
    }).catch(() => null);
    if (res?.ok) sent++;
  }
  if (!sent) throw new Error("The app did not accept the links.");
  return sent;
}

// ---- Catching downloads the browser starts ----------------------------------------------

/**
 * Take a download when its type is in the list, or when it has no recognisable type (most
 * file-host links). Leave known types the user didn't list, and files below the minimum size.
 */
function shouldCatch(item, settings) {
  if (!settings.enabled || Date.now() < bypassUntil) return false;
  const name = DSM.baseName(item.filename) || DSM.nameFromUrl(item.finalUrl || item.url);
  const ext = DSM.extensionOf(name);
  if (ext && !DSM.fileTypes(settings.fileTypes).has(ext)) return false;
  if (settings.minSizeKb > 0 && item.totalBytes > 0 && item.totalBytes < settings.minSizeKb * 1024) return false;
  return true;
}

async function handleDownload(item) {
  const url = item.finalUrl || item.url;
  if (!DSM.isHttp(url) || item.byExtensionId === chrome.runtime.id) return;
  if (browserFallback.delete(url)) return;
  if (!shouldCatch(item, await DSM.settings())) return;
  if (!(await availableApp())) return; // no app: the browser keeps it
  try {
    await hideDownloadBubble();
    await chrome.downloads.cancel(item.id);
    await chrome.downloads.erase({ id: item.id });
    await sendDownload({ url, referrer: item.referrer, fileName: DSM.baseName(item.filename) || undefined });
  } catch (err) {
    console.warn("Hand-off to Download Speed Manager failed; downloading in the browser.", err);
    giveBackToBrowser(url);
  }
}

function giveBackToBrowser(url) {
  browserFallback.add(url);
  chrome.downloads.download({ url });
}

/** Briefly hide the browser's download bubble so a handed-off download doesn't flash in it. */
async function hideDownloadBubble() {
  if (!chrome.downloads.setUiOptions) return; // Chrome/Edge 115+ only
  try {
    await chrome.downloads.setUiOptions({ enabled: false });
    setTimeout(() => chrome.downloads.setUiOptions({ enabled: true }).catch(() => {}), 1500);
  } catch {
    // permission missing or unsupported
  }
}

if (chrome.downloads.onDeterminingFilename) {
  // Chrome/Edge: decide once the server has named the file (better type information).
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    suggest(); // keep the browser's own name; we only decide who downloads it
    handleDownload(item);
  });
} else {
  chrome.downloads.onCreated.addListener(handleDownload); // Firefox
}

// ---- Video and audio files on pages ---------------------------------------------------------

const header = (headers, name) => headers?.find((h) => h.name.toLowerCase() === name)?.value;

function sizeFrom(headers, statusCode) {
  const total = /\/(\d+)\s*$/.exec(header(headers, "content-range") ?? "");
  if (total) return Number(total[1]);
  const length = header(headers, "content-length");
  return statusCode === 200 && length ? Number(length) : null;
}

// Streaming pieces (HLS/DASH segments and playlists) aren't one downloadable file.
const STREAM_PIECE = /\.(m3u8|mpd|ts|m4s)(\?|$)|[?&](range|bytestart|byteend)=/i;

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0 || details.method !== "GET" || details.statusCode >= 400) return;
    const type = (header(details.responseHeaders, "content-type") ?? "").toLowerCase();
    if (!/^(video|audio)\//.test(type) || /mpegurl|dash/.test(type) || STREAM_PIECE.test(details.url)) return;
    const size = sizeFrom(details.responseHeaders, details.statusCode);
    if (size !== null && size < 512 * 1024) return; // ignore short sounds and clips
    rememberMedia(details.tabId, { url: details.url, type: type.split(";")[0], size });
  },
  { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "other"] },
  ["responseHeaders"],
);

async function rememberMedia(tabId, entry) {
  const key = `media:${tabId}`;
  const list = (await chrome.storage.session.get(key))[key] ?? [];
  await chrome.storage.session.set({ [key]: [entry, ...list.filter((m) => m.url !== entry.url)].slice(0, 12) });
}

async function mediaFor(tabId) {
  if (tabId === undefined || tabId < 0) return [];
  const key = `media:${tabId}`;
  return (await chrome.storage.session.get(key))[key] ?? [];
}

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === "loading" && change.url) chrome.storage.session.remove(`media:${tabId}`);
});
chrome.tabs.onRemoved.addListener((tabId) => chrome.storage.session.remove(`media:${tabId}`));

// ---- Right-click menu, toolbar badge, messages ------------------------------------------------

function openLinkPicker(tabId, selectionOnly) {
  const query = new URLSearchParams({ tab: String(tabId ?? "") });
  if (selectionOnly) query.set("selection", "1");
  chrome.tabs.create({ url: chrome.runtime.getURL(`links.html?${query}`) });
}

// ---- Updates -------------------------------------------------------------------------------
// The Windows app updates itself from GitHub Releases, and the extension's files with it (they
// live in the app's folder). Every 30 minutes the extension reloads itself if newer files are on
// disk, and every few hours it asks the app whether an update is waiting, to show it on the badge.

const UPDATE_ALARM = "dsm-updates";
const RELOAD_ALARM = "dsm-reload-check";
const UPDATE_CHECK_MS = 6 * 60 * 60 * 1000;

/** An app update replaced the files on disk: reload so the browser runs the new version. */
async function reloadIfUpdated() {
  try {
    const response = await fetch(chrome.runtime.getURL("manifest.json"), { cache: "no-store" });
    const { version } = await response.json();
    if (version && version !== chrome.runtime.getManifest().version) chrome.runtime.reload();
  } catch {
    // store-installed copies can't change on disk; nothing to do
  }
}

/** Ask the app (through the native host) whether a newer version is out, and remember the answer. */
async function checkForUpdate() {
  const reply = await nativeSend({ type: "update-status" });
  const updateStatus = reply?.state ? { ...reply, checkedAt: Date.now() } : null;
  await chrome.storage.local.set({ updateStatus });
  await updateBadge();
  return updateStatus;
}

async function updateBadge() {
  const { enabled } = await DSM.settings();
  const { updateStatus } = await chrome.storage.local.get("updateStatus");
  const update = updateStatus?.state === "available" ? updateStatus : null;
  if (!enabled) {
    await chrome.action.setBadgeText({ text: "off" });
    await chrome.action.setBadgeBackgroundColor({ color: "#a16207" }); // yellow: paused
  } else {
    await chrome.action.setBadgeText({ text: update ? "↑" : "" });
    await chrome.action.setBadgeBackgroundColor({ color: "#2563eb" }); // blue: an update is waiting
  }
  await chrome.action.setTitle({
    title: update ? `Download Speed Manager: version ${update.latest} is available` : "Download Speed Manager",
  });
}

chrome.alarms.get(UPDATE_ALARM, (alarm) => {
  if (!alarm) chrome.alarms.create(UPDATE_ALARM, { delayInMinutes: 1, periodInMinutes: 30 });
});

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === RELOAD_ALARM) {
    // Just after the Update button: watch for the new files every minute, for ten minutes.
    const { reloadWatchUntil = 0 } = await chrome.storage.local.get("reloadWatchUntil");
    if (Date.now() > reloadWatchUntil) chrome.alarms.clear(RELOAD_ALARM);
    await reloadIfUpdated();
    return;
  }
  if (alarm.name !== UPDATE_ALARM) return;
  await reloadIfUpdated();
  const { updateStatus } = await chrome.storage.local.get("updateStatus");
  if (!updateStatus || Date.now() - updateStatus.checkedAt > UPDATE_CHECK_MS) await checkForUpdate();
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "dsm-link", title: "Download with Download Speed Manager", contexts: ["link"] });
    chrome.contextMenus.create({ id: "dsm-media", title: "Download this video or audio with Download Speed Manager", contexts: ["video", "audio"] });
    chrome.contextMenus.create({ id: "dsm-all", title: "Download all links with Download Speed Manager…", contexts: ["page", "selection"] });
  });
  updateBadge();
});
chrome.runtime.onStartup.addListener(updateBadge);
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.enabled) updateBadge();
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "dsm-all") {
    openLinkPicker(tab?.id, Boolean(info.selectionText));
    return;
  }
  let url = info.linkUrl || (DSM.isHttp(info.srcUrl) ? info.srcUrl : null);
  if (!url && info.menuItemId === "dsm-media") url = (await mediaFor(tab?.id))[0]?.url; // streamed player: use the file it loaded
  if (!url) return;
  try {
    await sendDownload({ url, referrer: info.pageUrl });
  } catch {
    giveBackToBrowser(url);
  }
});

const fromExtensionPage = (sender) => sender.url?.startsWith(chrome.runtime.getURL(""));

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  (async () => {
    switch (message?.type) {
      case "status":
        return { ok: true, app: await availableApp(true), settings: await DSM.settings(), media: await mediaFor(message.tabId) };
      case "media":
        return { ok: true, media: await mediaFor(sender.tab?.id ?? message.tabId) };
      case "bypass":
        bypassUntil = Date.now() + 4000;
        return { ok: true };
      case "download":
        if (!DSM.isHttp(message.url)) throw new Error("Only http and https links can be downloaded.");
        await sendDownload({ url: message.url, referrer: message.referrer ?? sender.tab?.url, fileName: message.fileName });
        return { ok: true };
      case "batch":
        if (!fromExtensionPage(sender)) throw new Error("Not allowed.");
        return { ok: true, sent: await sendBatch(message.items.filter((i) => DSM.isHttp(i.url)), message.referrer, message.later) };
      case "pick-links":
        openLinkPicker(message.tabId, false);
        return { ok: true };
      case "update-status": {
        if (!fromExtensionPage(sender)) throw new Error("Not allowed.");
        const { updateStatus } = await chrome.storage.local.get("updateStatus");
        const stale = !updateStatus || Date.now() - updateStatus.checkedAt > UPDATE_CHECK_MS;
        return { ok: true, status: message.fresh || stale ? await checkForUpdate() : updateStatus };
      }
      case "install-update": {
        if (!fromExtensionPage(sender)) throw new Error("Not allowed.");
        const reply = await nativeSend({ type: "install-update" });
        if (!reply?.ok) throw new Error(reply?.error ?? "Download Speed Manager couldn't be reached.");
        // The app takes it from here; reload as soon as it has put the new files in place.
        await chrome.storage.local.set({ updateStatus: null, reloadWatchUntil: Date.now() + 10 * 60 * 1000 });
        await updateBadge();
        chrome.alarms.create(RELOAD_ALARM, { delayInMinutes: 1, periodInMinutes: 1 });
        return { ok: true };
      }
      default:
        throw new Error("Unknown message.");
    }
  })().then(sendResponse, (err) => sendResponse({ ok: false, error: String(err?.message ?? err) }));
  return true; // answer asynchronously
});
