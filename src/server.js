#!/usr/bin/env node
// Local web UI + JSON API for Download Speed Manager. Listens on 127.0.0.1 only.
import http from "node:http";
import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, extname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { DownloadManager } from "./engine/manager.js";
import { isMain } from "./is-main.js";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const PUBLIC_DIR = join(ROOT, "public");
const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
};
const EXTENSION_ORIGIN = /^(chrome-extension|moz-extension|safari-web-extension):\/\/[\w.-]+$/;

const httpError = (status, message) => Object.assign(new Error(message), { status });

function send(res, status, body) {
  if (body === undefined) {
    res.writeHead(status);
    return res.end();
  }
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" });
  res.end(JSON.stringify(body));
}

function readJson(req) {
  return new Promise((resolve, reject) => {
    if (!/^application\/json\b/i.test(req.headers["content-type"] ?? "")) {
      return reject(httpError(415, "Send JSON with Content-Type: application/json"));
    }
    const chunks = [];
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > 1_000_000) {
        reject(httpError(413, "Request body too large"));
        req.destroy();
      } else chunks.push(chunk);
    });
    req.on("end", () => {
      try {
        resolve(chunks.length ? JSON.parse(Buffer.concat(chunks).toString("utf8")) : {});
      } catch {
        reject(httpError(400, "Invalid JSON"));
      }
    });
    req.on("error", reject);
  });
}

/** Map the browser-extension / UI payload onto manager.add() options. */
function toAddOptions(body) {
  if (!body || typeof body.url !== "string") throw httpError(400, "Missing url");
  return {
    url: body.url,
    fileName: typeof body.fileName === "string" ? body.fileName : undefined,
    dir: typeof body.dir === "string" ? body.dir : undefined,
    connections: body.connections,
    speedLimit: body.speedLimit,
    start: body.start !== false,
    headers: { ...(body.headers ?? {}), cookie: body.cookies, referer: body.referrer, "user-agent": body.userAgent },
  };
}

export async function startServer({
  port = Number(process.env.DSM_PORT) || 9614,
  host = "127.0.0.1",
  dataDir = process.env.DSM_DATA_DIR ?? join(ROOT, "data"),
  downloadDir = process.env.DSM_DOWNLOAD_DIR ?? join(homedir(), "Downloads"),
} = {}) {
  const manager = new DownloadManager({ dataDir, downloadDir });
  await manager.load();
  manager.on("warning", (err) => console.warn("[dsm]", err.message));

  const clients = new Set();
  let actualPort = port;
  const allowedHosts = () => new Set([`127.0.0.1:${actualPort}`, `localhost:${actualPort}`]);
  const originAllowed = (origin) =>
    !origin || // non-browser clients such as the CLI or curl
    origin === `http://127.0.0.1:${actualPort}` ||
    origin === `http://localhost:${actualPort}` ||
    EXTENSION_ORIGIN.test(origin);

  const snapshot = () => JSON.stringify({ downloads: manager.list(), settings: manager.settings });
  let lastPayload = "";
  const pushTimer = setInterval(() => {
    if (!clients.size) return;
    const payload = snapshot();
    if (payload === lastPayload) return;
    lastPayload = payload;
    for (const client of clients) client.write(`data: ${payload}\n\n`);
  }, 500);
  pushTimer.unref();

  async function serveStatic(pathname, res) {
    const file = pathname === "/" ? "index.html" : pathname.slice(1);
    if (!/^[\w.-]+$/.test(file)) return send(res, 404, { error: "Not found" });
    try {
      const body = await readFile(join(PUBLIC_DIR, file));
      res.writeHead(200, {
        "content-type": MIME[extname(file)] ?? "application/octet-stream",
        "cache-control": "no-cache",
        "x-content-type-options": "nosniff",
        "content-security-policy": "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'",
      });
      res.end(body);
    } catch {
      send(res, 404, { error: "Not found" });
    }
  }

  async function handle(req, res) {
    // Host check blocks DNS-rebinding; Origin check blocks websites from driving the API.
    if (!allowedHosts().has(req.headers.host ?? "")) return send(res, 403, { error: "Unexpected Host header" });
    if (!["GET", "HEAD"].includes(req.method) && !originAllowed(req.headers.origin)) {
      return send(res, 403, { error: "Origin not allowed" });
    }
    const { pathname, searchParams } = new URL(req.url, `http://${req.headers.host}`);

    if (req.method === "GET" && pathname === "/api/ping") return send(res, 200, { app: "download-speed-manager", version: "0.1.0" });
    if (req.method === "GET" && pathname === "/api/downloads") return send(res, 200, manager.list());
    if (req.method === "POST" && pathname === "/api/downloads") {
      return send(res, 201, manager.add(toAddOptions(await readJson(req))).toJSON());
    }
    if (req.method === "GET" && pathname === "/api/settings") return send(res, 200, manager.settings);
    if (req.method === "PUT" && pathname === "/api/settings") return send(res, 200, manager.updateSettings(await readJson(req)));
    if (req.method === "GET" && pathname === "/api/events") {
      res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-store", connection: "keep-alive" });
      res.write(`retry: 2000\ndata: ${snapshot()}\n\n`);
      clients.add(res);
      req.on("close", () => clients.delete(res));
      return;
    }

    const match = /^\/api\/downloads\/([\w-]+)(?:\/(pause|resume|cancel))?$/.exec(pathname);
    if (match) {
      const [, id, action] = match;
      if (req.method === "POST" && action === "pause") {
        await manager.pause(id);
        return send(res, 200, manager.get(id).toJSON());
      }
      if (req.method === "POST" && action === "resume") return send(res, 200, manager.resume(id).toJSON());
      if (req.method === "POST" && action === "cancel") {
        await manager.cancel(id);
        return send(res, 200, manager.get(id).toJSON());
      }
      if (req.method === "PATCH" && !action) return send(res, 200, manager.configure(id, await readJson(req)).toJSON());
      if (req.method === "DELETE" && !action) {
        await manager.remove(id, { deleteFile: searchParams.get("deleteFile") === "1" });
        return send(res, 204);
      }
    }

    if (req.method === "GET" || req.method === "HEAD") return serveStatic(pathname, res);
    return send(res, 404, { error: "Not found" });
  }

  const server = http.createServer((req, res) => {
    handle(req, res).catch((err) => {
      const status = err.status ?? (err.code === "ERR_INVALID_URL" ? 400 : 500);
      if (status === 500) console.error("[dsm]", err);
      if (!res.headersSent) send(res, status, { error: err.message });
      else res.end();
    });
  });

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, host, () => {
      actualPort = server.address().port;
      resolve();
    });
  });

  let closing = null;
  const close = () =>
    (closing ??= (async () => {
      clearInterval(pushTimer);
      for (const client of clients) client.end();
      await new Promise((resolve) => server.close(resolve));
      await manager.shutdown();
    })());

  return { server, manager, port: actualPort, url: `http://${host}:${actualPort}`, close };
}

// Run directly: `node src/server.js`
if (isMain(import.meta.url)) {
  try {
    const app = await startServer();
    console.log(`Download Speed Manager is running at ${app.url}`);
    console.log(`Saving downloads to ${app.manager.settings.downloadDir}. Press Ctrl+C to stop.`);
    const stop = async () => {
      console.log("\nPausing downloads and saving state…");
      await app.close();
      process.exit(0);
    };
    process.on("SIGINT", stop);
    process.on("SIGTERM", stop);
  } catch (err) {
    if (err.code === "EADDRINUSE") {
      console.error("Port already in use. Is Download Speed Manager already running? Set DSM_PORT to pick another port.");
    } else console.error(err);
    process.exit(1);
  }
}
