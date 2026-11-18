// Settings page: file types to catch, minimum size, and the local server port.
const $ = (id) => document.getElementById(id);

function fill(settings) {
  $("fileTypes").value = settings.fileTypes;
  $("minSizeKb").value = settings.minSizeKb;
  $("localPort").value = settings.localPort;
}

function say(text, tone) {
  $("saved").textContent = text;
  $("saved").className = tone;
}

$("save").addEventListener("click", async () => {
  const types = [...DSM.fileTypes($("fileTypes").value)].join(" ");
  const port = Number($("localPort").value);
  if (!types) return say("List at least one file type.", "error");
  if (!Number.isInteger(port) || port < 1 || port > 65535) return say("The port must be a number from 1 to 65535.", "error");
  await chrome.storage.local.set({
    fileTypes: types,
    minSizeKb: Math.max(0, Number($("minSizeKb").value) || 0),
    localPort: port,
  });
  fill(await DSM.settings());
  say("Saved.", "ok");
});

$("reset").addEventListener("click", async () => {
  await chrome.storage.local.remove(["fileTypes", "minSizeKb", "localPort"]);
  fill(await DSM.settings());
  say("Defaults restored.", "ok");
});

DSM.settings().then(fill);
