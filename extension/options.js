// Settings page: file types to catch, minimum size, and the local server port.
const $ = (id) => document.getElementById(id);
const FIELDS = ["fileTypes", "minSizeKb", "localPort"];

function fill(settings) {
  $("fileTypes").value = settings.fileTypes;
  $("minSizeKb").value = settings.minSizeKb;
  $("localPort").value = settings.localPort;
}

/** Show a result next to the buttons. For a problem, mark the field it is about and move to it. */
function say(text, tone, field) {
  $("saved").textContent = text;
  $("saved").className = tone;
  for (const id of FIELDS) $(id).removeAttribute("aria-invalid");
  if (field) {
    $(field).setAttribute("aria-invalid", "true");
    $(field).focus();
  }
}

$("save").addEventListener("click", async () => {
  const types = [...DSM.fileTypes($("fileTypes").value)].join(" ");
  const port = Number($("localPort").value);
  if (!types) return say("List at least one file type.", "error", "fileTypes");
  if (!Number.isInteger(port) || port < 1 || port > 65535) return say("The port must be a number from 1 to 65535.", "error", "localPort");
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

// Once a field is edited again, an earlier "Saved." or error no longer describes it.
for (const id of FIELDS) $(id).addEventListener("input", () => $("saved").textContent && say("", ""));

DSM.settings().then(fill);
