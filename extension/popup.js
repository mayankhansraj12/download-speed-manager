// Toolbar popup: connection status, switches, media found on the page, and the link picker.
const $ = (id) => document.getElementById(id);

(async () => {
  const pinned = Number(new URLSearchParams(location.search).get("tab")); // set when opened as a page (tests)
  const [tab] = pinned ? [await chrome.tabs.get(pinned)] : await chrome.tabs.query({ active: true, currentWindow: true });
  const status = await chrome.runtime.sendMessage({ type: "status", tabId: tab?.id });
  const { settings } = status;

  // Green: the app will take downloads. Yellow: paused. Red: no app, the browser downloads as usual.
  const pill = $("status");
  const hint = $("hint");
  if (!status.app) {
    pill.textContent = "App not found";
    pill.className = "pill error";
    hint.textContent = "Install or start Download Speed Manager. Until then the browser downloads as usual.";
    hint.hidden = false;
  } else if (!settings.enabled) {
    pill.textContent = "Paused: the browser downloads";
    pill.className = "pill warn";
  } else {
    pill.textContent = status.app === "native" ? "Connected to Download Speed Manager" : "Connected to the local server";
    pill.className = "pill ok";
  }

  for (const key of ["enabled", "videoButton"]) {
    $(key).checked = settings[key];
    $(key).addEventListener("change", async () => {
      await chrome.storage.local.set({ [key]: $(key).checked });
      if (key === "enabled" && status.app) {
        pill.textContent = $(key).checked ? "Connected to Download Speed Manager" : "Paused: the browser downloads";
        pill.className = $(key).checked ? "pill ok" : "pill warn";
      }
    });
  }

  const list = $("media");
  if (!status.media.length) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = "None found yet. Videos that are downloadable as one file appear here once they start playing.";
    list.append(empty);
  }
  for (const media of status.media) {
    const item = document.createElement("li");
    const info = document.createElement("span");
    info.className = "info";
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = DSM.nameFromUrl(media.url) || media.url;
    name.title = media.url;
    const meta = document.createElement("span");
    meta.className = "meta";
    meta.textContent = [media.type, DSM.formatBytes(media.size)].filter(Boolean).join(" · ");
    info.append(name, meta);
    const send = document.createElement("button");
    send.className = "go";
    send.textContent = "Download";
    send.addEventListener("click", async () => {
      send.disabled = true;
      const reply = await chrome.runtime.sendMessage({ type: "download", url: media.url, referrer: tab?.url });
      send.textContent = reply.ok ? "Sent" : "Failed";
      meta.textContent = reply.ok ? "Sent to Download Speed Manager" : reply.error;
      meta.className = `meta ${reply.ok ? "ok" : "error"}`;
    });
    item.append(info, send);
    list.append(item);
  }

  const canPick = DSM.isHttp(tab?.url);
  $("links").disabled = !canPick;
  if (!canPick) $("links").title = "Open a web page first";
  $("links").addEventListener("click", async () => {
    await chrome.runtime.sendMessage({ type: "pick-links", tabId: tab.id });
    window.close();
  });
  $("options").addEventListener("click", (event) => {
    event.preventDefault();
    chrome.runtime.openOptionsPage();
  });
})();
