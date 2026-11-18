import { test } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import { createHash, randomBytes } from "node:crypto";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startServer } from "../src/server.js";

const sha = (buf) => createHash("sha256").update(buf).digest("hex");
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Raw HTTP call so Host and Origin can be set explicitly. */
function call(port, method, path, { headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : JSON.stringify(body);
    const req = http.request(
      {
        host: "127.0.0.1",
        port,
        method,
        path,
        headers: { host: `127.0.0.1:${port}`, ...(payload ? { "content-type": "application/json" } : {}), ...headers },
      },
      (res) => {
        const chunks = [];
        res.on("data", (c) => chunks.push(c));
        res.on("end", () => {
          const text = Buffer.concat(chunks).toString("utf8");
          resolve({ status: res.statusCode, json: text ? JSON.parse(text) : null });
        });
      },
    );
    req.on("error", reject);
    req.end(payload);
  });
}

test("API accepts the UI/extension, refuses websites, and completes a download", async () => {
  const body = randomBytes(2 * 1024 * 1024);
  const files = http.createServer((req, res) => {
    res.writeHead(200, { "content-length": body.length });
    res.end(body);
  });
  await new Promise((resolve) => files.listen(0, "127.0.0.1", resolve));
  const fileUrl = `http://127.0.0.1:${files.address().port}/payload.bin`;

  const dir = await mkdtemp(join(tmpdir(), "dsm-api-"));
  const app = await startServer({ port: 0, dataDir: join(dir, "data"), downloadDir: join(dir, "downloads") });
  try {
    const foreign = await call(app.port, "POST", "/api/downloads", {
      headers: { origin: "https://evil.example" },
      body: { url: fileUrl },
    });
    assert.equal(foreign.status, 403);

    const rebinding = await call(app.port, "GET", "/api/downloads", { headers: { host: `evil.example:${app.port}` } });
    assert.equal(rebinding.status, 403);

    const badUrl = await call(app.port, "POST", "/api/downloads", { body: { url: "not a url" } });
    assert.equal(badUrl.status, 400);

    const created = await call(app.port, "POST", "/api/downloads", {
      headers: { origin: "chrome-extension://abcdefghijklmnopabcdefghijklmnop" },
      body: { url: fileUrl, cookies: "session=1", referrer: "https://example.com/page" },
    });
    assert.equal(created.status, 201);
    assert.equal(created.json.headers, undefined, "cookies must never be echoed by the API");

    let entry;
    for (let i = 0; i < 200; i++) {
      entry = (await call(app.port, "GET", "/api/downloads")).json.find((d) => d.id === created.json.id);
      if (entry.status === "completed" || entry.status === "error") break;
      await wait(25);
    }
    assert.equal(entry.status, "completed", entry.error);
    assert.equal(sha(await readFile(join(dir, "downloads", "payload.bin"))), sha(body));

    const page = await call(app.port, "GET", "/api/ping");
    assert.equal(page.json.app, "download-speed-manager");
  } finally {
    await app.close();
    files.closeAllConnections();
    await new Promise((resolve) => files.close(resolve));
    await rm(dir, { recursive: true, force: true });
  }
});
