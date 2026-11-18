// True when the module at `metaUrl` is the script Node was started with (handles symlinks and Windows casing).
import { realpathSync } from "node:fs";
import { fileURLToPath } from "node:url";

export function isMain(metaUrl) {
  if (!process.argv[1]) return false;
  const normalize = (path) => {
    let real = path;
    try {
      real = realpathSync(path);
    } catch {
      // keep the unresolved path
    }
    return process.platform === "win32" ? real.toLowerCase() : real;
  };
  return normalize(fileURLToPath(metaUrl)) === normalize(process.argv[1]);
}
