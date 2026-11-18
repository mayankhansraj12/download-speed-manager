import { test } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import { createHash, randomBytes } from "node:crypto";
import { mkdtemp, readdir, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Download } from "../src/engine/download.js";
import { isDone, planSegments, splitForHelper, validSegments } from "../src/engine/segments.js";
import { fileNameFrom, sanitizeFileName } from "../src/engine/http-client.js";

const MB = 1024 * 1024;
const sha = (buf) => createHash("sha256").update(buf).digest("hex");
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Local file server. /file honours Range, /norange ignores it, /redirect points at /file.
 * `delayFor(start)` returns a per-64KiB-chunk delay for a request starting at byte `start`.
 */
async function serve(body, { delayFor = () => 0, stallFor = () => false } = {}) {
  const stats = { open: 0, maxOpen: 0, bytes: 0 };
  const server = http.createServer(async (req, res) => {
    if (req.url === "/redirect") {
      res.writeHead(302, { location: "/file" });
      return res.end();
    }
    const ranges = req.url !== "/norange";
    const headers = {
      "content-type": "application/octet-stream",
      "content-disposition": 'attachment; filename="test file.bin"',
      etag: '"v1"',
    };
    let start = 0;
    let end = body.length - 1;
    let status = 200;
    const range = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? "");
    if (ranges) headers["accept-ranges"] = "bytes";
    if (ranges && range) {
      start = Number(range[1]);
      end = range[2] ? Math.min(Number(range[2]), body.length - 1) : body.length - 1;
      status = 206;
      headers["content-range"] = `bytes ${start}-${end}/${body.length}`;
    }
    headers["content-length"] = end - start + 1;
    res.writeHead(status, headers);
    stats.open++;
    stats.maxOpen = Math.max(stats.maxOpen, stats.open);
    res.on("close", () => stats.open--);

    const delay = delayFor(start);
    const stall = stallFor(start);
    for (let i = start; i <= end && !res.destroyed; i += 64 * 1024) {
      if (stall && i > start) {
        // Go silent after the first chunk, like a dead route, until the client gives up.
        await new Promise((resolve) => (res.socket?.destroyed ? resolve() : res.once("close", resolve)));
        break;
      }
      const chunk = body.subarray(i, Math.min(i + 64 * 1024, end + 1));
      stats.bytes += chunk.length;
      if (!res.write(chunk)) {
        await new Promise((resolve) => {
          const done = () => {
            res.off("drain", done);
            res.off("close", done);
            resolve();
          };
          res.on("drain", done);
          res.on("close", done);
        });
      }
      if (delay) await wait(delay);
    }
    res.end();
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  return {
    base,
    stats,
    close: () => new Promise((resolve) => { server.closeAllConnections(); server.close(resolve); }),
  };
}

async function withTemp(fn) {
  const dir = await mkdtemp(join(tmpdir(), "dsm-test-"));
  try {
    await fn(dir);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}

test("multi-connection download is byte-identical and cleans up", async () => {
  const body = randomBytes(8 * MB);
  const srv = await serve(body);
  try {
    await withTemp(async (dir) => {
      const d = new Download({ url: `${srv.base}/file`, dir, connections: 8 });
      await d.start();
      assert.equal(d.status, "completed", d.error);
      assert.equal(d.fileName, "test file.bin");
      assert.equal(sha(await readFile(d.targetPath)), sha(body));
      assert.ok(srv.stats.maxOpen > 1, "expected parallel connections");
      assert.deepEqual(await readdir(dir), ["test file.bin"]);
    });
  } finally {
    await srv.close();
  }
});

test("falls back to a single connection when ranges are unsupported", async () => {
  const body = randomBytes(3 * MB);
  const srv = await serve(body);
  try {
    await withTemp(async (dir) => {
      const d = new Download({ url: `${srv.base}/norange`, dir, connections: 8, suggestedName: "plain.bin" });
      await d.start();
      assert.equal(d.status, "completed", d.error);
      assert.equal(d.resumable, false);
      assert.equal(sha(await readFile(join(dir, "plain.bin"))), sha(body));
    });
  } finally {
    await srv.close();
  }
});

test("follows redirects and avoids overwriting existing files", async () => {
  const body = randomBytes(2 * MB);
  const srv = await serve(body);
  try {
    await withTemp(async (dir) => {
      const first = new Download({ url: `${srv.base}/redirect`, dir });
      const second = new Download({ url: `${srv.base}/redirect`, dir });
      await Promise.all([first.start(), second.start()]);
      assert.deepEqual([first.status, second.status], ["completed", "completed"]);
      assert.deepEqual((await readdir(dir)).sort(), ["test file (1).bin", "test file.bin"]);
      assert.equal(sha(await readFile(second.targetPath)), sha(body));
    });
  } finally {
    await srv.close();
  }
});

test("dynamic segmentation lets fast connections take over a slow segment", async () => {
  const body = randomBytes(4 * MB);
  const srv = await serve(body, { delayFor: (start) => (start === 0 ? 40 : 0) }); // first half is slow
  try {
    await withTemp(async (dir) => {
      const d = new Download({ url: `${srv.base}/file`, dir, connections: 2 });
      const started = Date.now();
      await d.start();
      assert.equal(d.status, "completed", d.error);
      assert.ok(d.segments.length > 2, `expected splits, got ${d.segments.length} segments`);
      assert.ok(Date.now() - started < 32 * 40, "should beat the slow connection alone");
      assert.equal(sha(await readFile(d.targetPath)), sha(body));
    });
  } finally {
    await srv.close();
  }
});

test("a connection that goes silent is taken over instead of waiting for the timeout", async () => {
  const body = randomBytes(4 * MB);
  const srv = await serve(body, { stallFor: (start) => start === 0 }); // first segment dies after 64 KiB
  try {
    await withTemp(async (dir) => {
      const d = new Download({ url: `${srv.base}/file`, dir, connections: 2 });
      const started = Date.now();
      await d.start();
      const seconds = (Date.now() - started) / 1000;
      assert.equal(d.status, "completed", d.error);
      assert.ok(seconds < 12, `took ${seconds}s; the 30s idle timeout should not be needed`);
      assert.equal(sha(await readFile(d.targetPath)), sha(body));
    });
  } finally {
    await srv.close();
  }
});

test("pause and resume continue from the saved segments", async () => {
  const body = randomBytes(8 * MB);
  const srv = await serve(body, { delayFor: () => 25 });
  try {
    await withTemp(async (dir) => {
      const first = new Download({ url: `${srv.base}/file`, dir, connections: 4 });
      const run = first.start();
      while (first.received < 2 * MB) await wait(20);
      await first.pause();
      await run;
      assert.equal(first.status, "paused");
      assert.ok(first.received < body.length);
      assert.ok((await readdir(dir)).includes("test file.bin.part.dsm"));

      const servedBefore = srv.stats.bytes;
      const resumed = new Download({ url: `${srv.base}/file`, dir, connections: 4, fileName: first.fileName });
      await resumed.start();
      assert.equal(resumed.status, "completed", resumed.error);
      assert.ok(srv.stats.bytes - servedBefore < body.length, "resume must not re-download everything");
      assert.equal(sha(await readFile(resumed.targetPath)), sha(body));
      assert.deepEqual(await readdir(dir), ["test file.bin"]);
    });
  } finally {
    await srv.close();
  }
});

test("speed limit holds the transfer near the configured rate", async () => {
  const body = randomBytes(1 * MB);
  const srv = await serve(body);
  try {
    await withTemp(async (dir) => {
      const d = new Download({ url: `${srv.base}/file`, dir, connections: 4, speedLimit: 512 * 1024 });
      const started = Date.now();
      await d.start();
      const seconds = (Date.now() - started) / 1000;
      assert.equal(d.status, "completed", d.error);
      assert.ok(seconds >= 1.4 && seconds < 6, `took ${seconds}s at 512 KiB/s`);
    });
  } finally {
    await srv.close();
  }
});

test("segment planning and splitting", () => {
  const plan = planSegments(10 * MB, 8);
  assert.equal(plan.length, 8);
  assert.ok(validSegments(plan, 10 * MB));
  assert.equal(planSegments(500 * 1024, 8).length, 1);
  assert.deepEqual(planSegments(0, 8), []);

  const KB = 1024;
  // Average-speed segment: the helper takes roughly the second half.
  const even = { start: 0, end: 4 * MB - 1, received: MB, speed: 300 * KB };
  const half = splitForHelper([even], { helperSpeed: 300 * KB });
  assert.equal(even.end + 1, half.piece.start);
  assert.equal(half.piece.end, 4 * MB - 1);
  assert.ok(Math.abs(half.piece.start - 2.5 * MB) < 100 * KB);

  // Slow segment: the fast helper takes almost all of it so both finish together.
  const slow = { start: 0, end: MB - 1, received: MB / 2, speed: 20 * KB };
  const tail = splitForHelper([slow], { helperSpeed: 400 * KB });
  assert.ok(tail.piece.end - tail.piece.start + 1 > 0.9 * (MB / 2));

  // Stalled segment: the helper takes everything and the stalled one counts as finished.
  const stalled = { start: 0, end: MB - 1, received: 1000, speed: 0 };
  const all = splitForHelper([stalled], { helperSpeed: 400 * KB });
  assert.equal(all.piece.start, 1000);
  assert.ok(isDone(stalled));

  // Nearly finished at a good speed: a new connection would not finish sooner.
  const nearlyDone = { start: 0, end: 200 * KB, received: 10 * KB, speed: 400 * KB };
  assert.equal(splitForHelper([nearlyDone], { helperSpeed: 400 * KB }), null);
});

test("file names from headers and URLs are safe", () => {
  assert.equal(fileNameFrom("attachment; filename*=UTF-8''na%C3%AFve%20file.txt", "http://x/y"), "naïve file.txt");
  assert.equal(fileNameFrom('attachment; filename="report.pdf"', "http://x/y"), "report.pdf");
  assert.equal(fileNameFrom(undefined, "http://x/a/b/setup%20v2.exe?token=1"), "setup v2.exe");
  assert.equal(fileNameFrom(undefined, "http://x/"), "download");
  assert.equal(sanitizeFileName('a<b>:c"d.txt'), "a_b__c_d.txt");
  assert.equal(sanitizeFileName("con.txt"), "_con.txt");
});
