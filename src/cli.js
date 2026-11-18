#!/usr/bin/env node
// Command-line downloads with the same engine. Re-running the same command resumes.
import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { Download } from "./engine/download.js";
import { probe, sanitizeFileName } from "./engine/http-client.js";
import { isMain } from "./is-main.js";

const USAGE = `Usage: node src/cli.js <url> [options]

Options:
  -c, --connections <n>  parallel connections (1-32, default 8)
  -o, --out <dir>        output directory (default: current directory)
  -n, --name <file>      file name (default: from the server)
  -l, --limit <rate>     speed limit per second, e.g. 500k, 2m (default: unlimited)
  -h, --help             show this help`;

export function parseSize(text) {
  const match = /^(\d+(?:\.\d+)?)\s*([kmg]?)i?b?$/i.exec(String(text).trim());
  if (!match) throw new Error(`Invalid size: ${text}`);
  return Math.round(Number(match[1]) * 1024 ** ["", "k", "m", "g"].indexOf(match[2].toLowerCase()));
}

export function formatBytes(bytes) {
  if (bytes === null || bytes === undefined) return "?";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit ? 1 : 0)} ${units[unit]}`;
}

const formatEta = (seconds) =>
  seconds === null ? "--:--" : `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, "0")}`;

async function main() {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: {
      connections: { type: "string", short: "c", default: "8" },
      out: { type: "string", short: "o", default: "." },
      name: { type: "string", short: "n" },
      limit: { type: "string", short: "l", default: "0" },
      help: { type: "boolean", short: "h", default: false },
    },
  });
  if (values.help || positionals.length !== 1) {
    console.log(USAGE);
    process.exit(values.help ? 0 : 1);
  }

  const url = positionals[0];
  const dir = resolve(values.out);
  const info = await probe(url);
  const name = values.name ? sanitizeFileName(values.name) : info.fileName;

  // Resume when an unfinished download of the same URL left its progress file behind.
  let resumeName;
  try {
    const saved = JSON.parse(await readFile(join(dir, `${name}.part.dsm`), "utf8"));
    if (saved.url === url) resumeName = name;
  } catch {
    resumeName = undefined;
  }

  const download = new Download({
    url,
    dir,
    fileName: resumeName,
    suggestedName: name,
    connections: Number(values.connections),
    speedLimit: parseSize(values.limit),
  });

  const started = Date.now();
  download.on("status", (status) => {
    if (status === "downloading") {
      const n = download.connections;
      const mode = download.resumable ? `${n} connection${n === 1 ? "" : "s"}` : "single connection (no range support)";
      console.log(`${resumeName ? "Resuming" : "Downloading"} ${download.fileName} (${formatBytes(download.size)}), ${mode}`);
    }
  });
  download.on("progress", (d) => {
    const pct = d.size ? ((d.received / d.size) * 100).toFixed(1).padStart(5) : "  ?  ";
    const { activeConnections, eta } = d.toJSON();
    process.stdout.write(
      `\r${pct}%  ${formatBytes(d.received)} / ${formatBytes(d.size)}  ${formatBytes(d.speed)}/s  ETA ${formatEta(eta)}  [${activeConnections} conn]   `,
    );
  });

  let interrupted = false;
  process.on("SIGINT", () => {
    if (interrupted) process.exit(130);
    interrupted = true;
    process.stdout.write("\nPausing… ");
    download.pause();
  });

  await download.start();
  process.stdout.write("\n");
  if (download.status === "completed") {
    const seconds = (Date.now() - started) / 1000;
    console.log(`Saved ${download.targetPath} in ${seconds.toFixed(1)}s (${formatBytes(download.received / Math.max(seconds, 0.001))}/s average)`);
  } else if (download.status === "paused") {
    console.log("progress saved. Run the same command again to resume.");
    process.exit(130);
  } else {
    console.error(`Download failed: ${download.error}`);
    process.exit(1);
  }
}

if (isMain(import.meta.url)) {
  main().catch((err) => {
    console.error(err.message);
    process.exit(1);
  });
}
