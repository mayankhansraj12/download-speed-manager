// Download Speed Manager content script:
//  - a "Download video" button over videos that come from a single downloadable file
//  - Alt+click on a link lets the browser download it instead of the app (like IDM)
(() => {
  if (window.__dsmContentScript) return;
  window.__dsmContentScript = true;

  document.addEventListener(
    "click",
    (event) => {
      if (event.altKey && event.target instanceof Element && event.target.closest("a[href]")) {
        chrome.runtime.sendMessage({ type: "bypass" }).catch(() => {});
      }
    },
    true,
  );

  let enabled = true;
  chrome.storage.local.get({ videoButton: true }).then((s) => (enabled = s.videoButton));
  chrome.storage.onChanged.addListener((changes) => {
    if (changes.videoButton) enabled = changes.videoButton.newValue;
    if (!enabled) hide();
  });

  // The button lives in a closed shadow root so page styles can't change it (and it can't change the page).
  const host = document.createElement("dsm-video-download");
  const shadow = host.attachShadow({ mode: "closed" });
  shadow.innerHTML = `
    <style>
      button {
        all: initial; display: inline-flex; align-items: center; gap: 8px; cursor: pointer;
        padding: 7px 12px 7px 10px; border-radius: 8px; font: 600 13px/1 "Segoe UI", system-ui, sans-serif;
        color: #f0f0f0; background: rgba(32, 32, 32, 0.92); border: 1px solid rgba(255, 255, 255, 0.14);
        box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35); transition: background 120ms ease;
      }
      button:hover { background: #15803d; }
      button:focus-visible { outline: 2px solid #4c91ff; outline-offset: 2px; }
      svg { width: 16px; height: 16px; fill: none; stroke: #4ade80; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
      button:hover svg { stroke: #fff; }
      button[data-tone="ok"] { background: #15803d; }
      button[data-tone="warn"] { background: #a16207; }
      button[data-tone="error"] { background: #b91c1c; }
    </style>
    <button type="button" title="Download with Download Speed Manager">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v11M7 10l5 5 5-5M5 20h14"/></svg>
      <span>Download video</span>
    </button>`;
  const button = shadow.querySelector("button");
  const label = shadow.querySelector("span");
  let current = null; // the video the button belongs to
  let currentUrl = null;
  let hideTimer = 0;
  let resetTimer = 0;

  const inside = (r, x, y) => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;

  async function downloadableUrl(video) {
    if (DSM.isHttp(video.currentSrc)) return video.currentSrc;
    // blob:/streamed players: use a whole media file this tab loaded, if there was one.
    const reply = await chrome.runtime.sendMessage({ type: "media" }).catch(() => null);
    const media = reply?.media ?? [];
    return (media.find((m) => m.type.startsWith(video instanceof HTMLAudioElement ? "audio" : "video")) ?? media[0])?.url ?? null;
  }

  function place() {
    if (!current?.isConnected) return hide();
    const r = current.getBoundingClientRect();
    host.style.cssText = `all: initial; position: fixed; z-index: 2147483647; top: ${Math.max(6, r.top + 12)}px; left: ${Math.max(6, Math.min(window.innerWidth - 190, r.right - 182))}px;`;
    if (!host.isConnected) document.documentElement.appendChild(host);
  }

  function hide() {
    host.remove();
    current = null;
    currentUrl = null;
  }

  async function show(video) {
    if (video === current) return place();
    const url = await downloadableUrl(video);
    if (!url) return; // streamed in pieces or protected: nothing to save as one file
    current = video;
    currentUrl = url;
    label.textContent = "Download video";
    button.removeAttribute("data-tone");
    place();
  }

  let lastMove = 0;
  document.addEventListener(
    "mousemove",
    (event) => {
      if (!enabled || Date.now() - lastMove < 120) return;
      lastMove = Date.now();
      const video = [...document.getElementsByTagName("video")].find((v) => {
        const r = v.getBoundingClientRect();
        return r.width >= 240 && r.height >= 135 && inside(r, event.clientX, event.clientY);
      });
      if (video) {
        clearTimeout(hideTimer);
        show(video);
      } else if (current && !host.matches(":hover")) {
        clearTimeout(hideTimer);
        hideTimer = setTimeout(hide, 1500);
      }
    },
    { passive: true },
  );
  window.addEventListener("scroll", () => current && place(), { passive: true, capture: true });
  window.addEventListener("resize", () => current && place(), { passive: true });

  function fileName(url) {
    const fromUrl = DSM.nameFromUrl(url);
    if (DSM.extensionOf(fromUrl)) return fromUrl;
    const title = (document.title || "video").replace(/[\\/:*?"<>|]+/g, " ").trim().slice(0, 120);
    return `${title || "video"}.mp4`;
  }

  function flash(text, tone) {
    label.textContent = text;
    button.dataset.tone = tone;
    clearTimeout(resetTimer);
    resetTimer = setTimeout(() => {
      label.textContent = "Download video";
      button.removeAttribute("data-tone");
    }, 2500);
  }

  button.addEventListener("click", async (event) => {
    event.preventDefault();
    event.stopPropagation();
    if (!currentUrl) return;
    const reply = await chrome.runtime
      .sendMessage({ type: "download", url: currentUrl, referrer: location.href, fileName: fileName(currentUrl) })
      .catch((err) => ({ ok: false, error: String(err) }));
    if (reply?.ok) flash("Sent to Download Speed Manager", "ok");
    else flash(reply?.error?.includes("isn't running") ? "Start Download Speed Manager first" : "Couldn't send the video", "error");
  });
})();
