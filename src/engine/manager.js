// Download queue: concurrency limit, global speed limit, persistence across restarts.
import { EventEmitter } from "node:events";
import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { Download } from "./download.js";
import { Throttle } from "./throttle.js";

const ACTIVE = new Set(["connecting", "downloading"]);
const FORWARDED_HEADERS = new Set(["cookie", "referer", "user-agent", "authorization"]);
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, Math.round(Number(n) || lo)));

/** Keep only headers that help a server recognise the browser session, rejecting header injection. */
export function pickHeaders(headers = {}) {
  const out = {};
  for (const [key, value] of Object.entries(headers ?? {})) {
    const name = key.toLowerCase();
    if (FORWARDED_HEADERS.has(name) && typeof value === "string" && value && !/[\r\n]/.test(value)) out[name] = value;
  }
  return out;
}

export class DownloadManager extends EventEmitter {
  #downloads = new Map();
  #saveTimer = null;
  #saving = Promise.resolve();
  #pumping = false;
  #pumpAgain = false;

  constructor({ dataDir, downloadDir, settings = {} }) {
    super();
    this.dataDir = dataDir;
    this.settings = { maxConcurrent: 3, connections: 8, globalSpeedLimit: 0, downloadDir, ...settings };
    this.throttle = new Throttle(this.settings.globalSpeedLimit);
  }

  get file() {
    return join(this.dataDir, "downloads.json");
  }

  async load() {
    await mkdir(this.dataDir, { recursive: true });
    let data = null;
    try {
      data = JSON.parse(await readFile(this.file, "utf8"));
    } catch {
      return; // first run or unreadable list
    }
    if (data.settings) this.updateSettings(data.settings, { persist: false });
    for (const record of data.downloads ?? []) {
      // Anything interrupted comes back paused; the .part.dsm sidecar knows where to resume.
      const status = ["completed", "error", "cancelled"].includes(record.status) ? record.status : "paused";
      this.#track(new Download({ ...record, status }, { globalThrottle: this.throttle }));
    }
  }

  list() {
    return [...this.#downloads.values()].map((d) => d.toJSON());
  }

  get(id) {
    return this.#downloads.get(id) ?? null;
  }

  add({ url, fileName, dir, connections, speedLimit, headers, start = true } = {}) {
    const parsed = new URL(url);
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") throw new Error("Only http and https URLs are supported");
    const download = new Download(
      {
        url: parsed.href,
        suggestedName: fileName,
        dir: dir || this.settings.downloadDir,
        connections: connections ?? this.settings.connections,
        speedLimit,
        headers: pickHeaders(headers),
        status: start ? "queued" : "paused",
      },
      { globalThrottle: this.throttle },
    );
    this.#track(download);
    this.#persist();
    this.#pump();
    return download;
  }

  async pause(id) {
    await this.#require(id).pause();
  }

  resume(id) {
    const download = this.#require(id);
    download.queue();
    this.#pump();
    return download;
  }

  async cancel(id) {
    await this.#require(id).cancel();
  }

  async remove(id, { deleteFile = false } = {}) {
    const download = this.#require(id);
    if (download.status === "completed") {
      if (deleteFile) await rm(download.targetPath, { force: true });
    } else {
      await download.cancel();
    }
    download.removeAllListeners();
    this.#downloads.delete(id);
    this.#persist();
  }

  configure(id, { connections, speedLimit } = {}) {
    const download = this.#require(id);
    if (connections !== undefined) download.setConnections(connections);
    if (speedLimit !== undefined) download.setSpeedLimit(Math.max(0, Number(speedLimit) || 0));
    this.#persist();
    return download;
  }

  updateSettings(patch = {}, { persist = true } = {}) {
    const s = this.settings;
    if (patch.maxConcurrent !== undefined) s.maxConcurrent = clamp(patch.maxConcurrent, 1, 16);
    if (patch.connections !== undefined) s.connections = clamp(patch.connections, 1, 32);
    if (patch.globalSpeedLimit !== undefined) {
      s.globalSpeedLimit = Math.max(0, Number(patch.globalSpeedLimit) || 0);
      this.throttle.setRate(s.globalSpeedLimit);
    }
    if (typeof patch.downloadDir === "string" && patch.downloadDir.trim()) s.downloadDir = patch.downloadDir.trim();
    if (persist) this.#persist();
    this.#pump();
    return { ...s };
  }

  /** Pause everything that is running and write the list to disk. */
  async shutdown() {
    await Promise.all([...this.#downloads.values()].filter((d) => d.isRunning).map((d) => d.pause()));
    await this.flush();
  }

  flush() {
    clearTimeout(this.#saveTimer);
    const data = JSON.stringify(
      { version: 1, settings: this.settings, downloads: [...this.#downloads.values()].map((d) => d.toRecord()) },
      null,
      2,
    );
    const tmp = `${this.file}.tmp`;
    this.#saving = this.#saving
      .then(async () => {
        await mkdir(this.dataDir, { recursive: true });
        await writeFile(tmp, data);
        await rename(tmp, this.file);
      })
      .catch((err) => this.emit("warning", err));
    return this.#saving;
  }

  #require(id) {
    const download = this.#downloads.get(id);
    if (!download) throw Object.assign(new Error(`Unknown download ${id}`), { status: 404 });
    return download;
  }

  #track(download) {
    this.#downloads.set(download.id, download);
    download.on("status", () => {
      this.#persist();
      this.#pump();
      this.emit("change", download);
    });
    download.on("warning", (err) => this.emit("warning", err));
  }

  #persist() {
    clearTimeout(this.#saveTimer);
    this.#saveTimer = setTimeout(() => this.flush(), 300);
    this.#saveTimer.unref?.();
  }

  /** Start queued downloads, oldest first, while slots are free. Re-entrant safe. */
  #pump() {
    if (this.#pumping) {
      this.#pumpAgain = true;
      return;
    }
    this.#pumping = true;
    try {
      do {
        this.#pumpAgain = false;
        const all = [...this.#downloads.values()];
        let running = all.filter((d) => ACTIVE.has(d.status)).length;
        for (const d of all) {
          if (running >= this.settings.maxConcurrent) break;
          if (d.status === "queued" && !d.isRunning) {
            running++;
            d.start();
          }
        }
      } while (this.#pumpAgain);
    } finally {
      this.#pumping = false;
    }
  }
}
