// Link picker: every link on a page (or in the selection), filtered by type, sent to the app in one go.
const $ = (id) => document.getElementById(id);
const FILTERS = ["Files", "Video", "Audio", "Documents", "Archives", "Software", "Images", "Everything"];
let links = []; // { url, name, category }
let filter = "Files";
const checked = new Set();
let source = { title: "", url: "" };

/** Runs inside the page (all frames): links and media sources, optionally only within the selection. */
function collectLinks(selectionOnly) {
  const selection = window.getSelection();
  const inSelection = (node) => !selectionOnly || (selection && selection.rangeCount > 0 && selection.containsNode(node, true));
  const found = [];
  for (const a of document.querySelectorAll("a[href]")) {
    if (inSelection(a)) found.push({ url: a.href, text: (a.textContent || a.title || "").trim().slice(0, 150) });
  }
  for (const m of document.querySelectorAll("video[src], audio[src], video source[src], audio source[src]")) {
    if (inSelection(m)) found.push({ url: m.src, text: "" });
  }
  return { title: document.title, url: location.href, links: found };
}

function visible() {
  const query = $("search").value.trim().toLowerCase();
  return links.filter((link) => {
    const inFilter = filter === "Everything" || (filter === "Files" ? link.category !== "Other" : link.category === filter);
    return inFilter && (!query || link.name.toLowerCase().includes(query) || link.url.toLowerCase().includes(query));
  });
}

function renderFilters() {
  const box = $("filters");
  box.replaceChildren();
  for (const name of FILTERS) {
    const count = name === "Everything" ? links.length
      : name === "Files" ? links.filter((l) => l.category !== "Other").length
      : links.filter((l) => l.category === name).length;
    if (count === 0 && !["Files", "Everything"].includes(name)) continue;
    const chip = document.createElement("button");
    chip.setAttribute("aria-pressed", String(name === filter));
    chip.append(name === "Files" ? "All files" : name);
    const n = document.createElement("span");
    n.className = "n";
    n.textContent = count;
    chip.append(n);
    chip.addEventListener("click", () => {
      filter = name;
      renderFilters();
      renderRows();
    });
    box.append(chip);
  }
}

function renderRows() {
  const rows = visible();
  const body = $("rows");
  body.replaceChildren();
  for (const link of rows) {
    const tr = document.createElement("tr");
    const pick = document.createElement("td");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = checked.has(link.url);
    box.setAttribute("aria-label", `Select ${link.name}`);
    box.addEventListener("change", () => {
      if (box.checked) checked.add(link.url);
      else checked.delete(link.url);
      updateFooter();
    });
    pick.append(box);
    const name = document.createElement("td");
    name.textContent = link.name;
    name.title = link.name;
    const type = document.createElement("td");
    const tag = document.createElement("span");
    tag.className = "tag";
    tag.textContent = link.category === "Other" ? "Page/other" : link.category;
    type.append(tag);
    const url = document.createElement("td");
    url.className = "url";
    url.textContent = link.url;
    url.title = link.url;
    tr.append(pick, name, type, url);
    tr.addEventListener("click", (event) => {
      if (event.target !== box) box.click();
    });
    body.append(tr);
  }
  $("empty").hidden = rows.length > 0;
  updateFooter();
}

function selectedVisible() {
  return visible().filter((link) => checked.has(link.url));
}

function updateFooter() {
  const rows = visible();
  const picked = selectedVisible().length;
  $("count").textContent = `${picked} of ${rows.length} selected`;
  $("all").checked = rows.length > 0 && picked === rows.length;
  $("all").indeterminate = picked > 0 && picked < rows.length;
  $("download").disabled = picked === 0;
  $("download").textContent = picked ? `Download ${picked}` : "Download";
}

$("all").addEventListener("change", () => {
  for (const link of visible()) {
    if ($("all").checked) checked.add(link.url);
    else checked.delete(link.url);
  }
  renderRows();
});
$("search").addEventListener("input", renderRows);

$("download").addEventListener("click", async () => {
  const items = selectedVisible().map((link) => ({ url: link.url, fileName: DSM.extensionOf(link.name) ? link.name : undefined }));
  $("download").disabled = true;
  const result = $("result");
  result.className = "muted";
  result.textContent = "Sending…";
  const reply = await chrome.runtime.sendMessage({ type: "batch", items, referrer: source.url, later: $("later").checked });
  result.className = reply.ok ? "ok" : "error";
  result.textContent = reply.ok ? `Sent ${reply.sent} to Download Speed Manager.` : reply.error;
  updateFooter();
});

(async () => {
  const params = new URLSearchParams(location.search);
  const tabId = Number(params.get("tab"));
  const selectionOnly = params.get("selection") === "1";
  try {
    const results = await chrome.scripting.executeScript({ target: { tabId, allFrames: true }, func: collectLinks, args: [selectionOnly] });
    const seen = new Set();
    for (const frame of results) {
      if (!frame.result) continue;
      if (frame.frameId === 0) source = { title: frame.result.title, url: frame.result.url };
      for (const found of frame.result.links) {
        const url = found.url.split("#")[0];
        if (!DSM.isHttp(url) || seen.has(url)) continue;
        seen.add(url);
        const fromUrl = DSM.nameFromUrl(url);
        const name = fromUrl || found.text || url;
        const category = DSM.categoryOf(fromUrl);
        links.push({ url, name, category });
        if (category !== "Other") checked.add(url); // files are picked by default, pages are not
      }
    }
    $("source").textContent = `${links.length} links ${selectionOnly ? "in the selection on" : "on"} “${source.title || source.url}”`;
  } catch (err) {
    $("source").className = "error";
    $("source").textContent = `Couldn't read that page: ${err.message}`;
  }
  renderFilters();
  renderRows();
})();
