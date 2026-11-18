// Minimal HTTP(S) client: redirects, idle timeouts, probing and file-name detection.
import dns from "node:dns";
import http from "node:http";
import https from "node:https";
import { posix } from "node:path";

// Try IPv4 addresses first; it is often the faster, more reliable route. IPv6 stays
// available as a fallback through Node's happy-eyeballs connect. DSM_IP_FAMILY=auto keeps OS order.
const PREFER_IPV4 = process.env.DSM_IP_FAMILY !== "auto";
const lookupIPv4First = (hostname, options, callback) =>
  dns.lookup(hostname, { ...options, order: "ipv4first", verbatim: false }, callback);

export const USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DownloadSpeedManager/0.1";

// Keep-alive pools let segment workers reuse sockets when a segment is split or retried.
const agents = {
  "http:": new http.Agent({ keepAlive: true, maxSockets: 256 }),
  "https:": new https.Agent({ keepAlive: true, maxSockets: 256 }),
};

// Never forward these to a different origin after a redirect.
const CREDENTIAL_HEADERS = ["cookie", "authorization"];

export class HttpError extends Error {
  constructor(status, url) {
    super(`HTTP ${status} from ${new URL(url).host}`);
    this.status = status;
    this.retryable = status === 408 || status === 429 || status >= 500;
  }
}

export function isRetryable(err) {
  if (err?.name === "AbortError") return false;
  if (err instanceof HttpError) return err.retryable;
  return err?.retryable ?? true; // socket resets, DNS hiccups and timeouts are worth retrying
}

const lowerKeys = (headers) =>
  Object.fromEntries(
    Object.entries(headers ?? {})
      .filter(([, value]) => value !== undefined && value !== null && value !== "")
      .map(([key, value]) => [key.toLowerCase(), String(value)]),
  );

/**
 * Send a request and follow redirects. Resolves with the final IncomingMessage
 * (body not consumed); `res.finalUrl` holds the URL that produced it.
 */
export function request(url, { method = "GET", headers = {}, maxRedirects = 10, timeoutMs = 30_000, signal } = {}) {
  return new Promise((resolve, reject) => {
    const firstOrigin = new URL(url).origin;

    const visit = (current, redirectsLeft, hdrs) => {
      const target = new URL(current);
      const lib = target.protocol === "https:" ? https : target.protocol === "http:" ? http : null;
      if (!lib) return reject(new Error(`Unsupported protocol: ${target.protocol}`));

      const req = lib.request(target, {
        method,
        // identity encoding keeps byte offsets identical to the file on the server
        headers: { "user-agent": USER_AGENT, "accept-encoding": "identity", ...hdrs },
        agent: agents[target.protocol],
        lookup: PREFER_IPV4 ? lookupIPv4First : undefined,
        signal,
      });
      req.setTimeout(timeoutMs, () =>
        req.destroy(Object.assign(new Error(`No data for ${Math.round(timeoutMs / 1000)}s`), { retryable: true })),
      );
      req.once("error", reject);
      req.once("response", (res) => {
        const status = res.statusCode ?? 0;
        const location = res.headers.location;
        if ([301, 302, 303, 307, 308].includes(status) && location) {
          res.resume();
          if (redirectsLeft <= 0) return reject(new Error("Too many redirects"));
          const next = new URL(location, target);
          const nextHeaders = { ...hdrs };
          if (next.origin !== firstOrigin) for (const name of CREDENTIAL_HEADERS) delete nextHeaders[name];
          return visit(next.href, redirectsLeft - 1, nextHeaders);
        }
        res.finalUrl = target.href;
        resolve(res);
      });
      req.end();
    };

    visit(url, maxRedirects, lowerKeys(headers));
  });
}

/** Discover size, range support, validators and a file name without downloading the body. */
export async function probe(url, { headers = {}, signal, timeoutMs } = {}) {
  const res = await request(url, { headers: { ...headers, range: "bytes=0-0" }, signal, timeoutMs });
  const status = res.statusCode ?? 0;
  const h = res.headers;
  res.destroy(); // a server that ignores Range would otherwise stream the whole file here
  if (status >= 400 && status !== 416) throw new HttpError(status, res.finalUrl);

  let size = null;
  let resumable = false;
  const total = /bytes\s+(?:\d+-\d+|\*)\/(\d+)/i.exec(h["content-range"] ?? "");
  if ((status === 206 || status === 416) && total) {
    size = Number(total[1]);
    resumable = true;
  } else if (status === 200 && h["content-length"] !== undefined) {
    size = Number(h["content-length"]);
  }

  return {
    finalUrl: res.finalUrl,
    size,
    resumable,
    etag: h.etag ?? null,
    lastModified: h["last-modified"] ?? null,
    contentType: h["content-type"] ?? null,
    fileName: fileNameFrom(h["content-disposition"], res.finalUrl),
  };
}

/** Pick a file name from Content-Disposition (RFC 6266 / 5987) or the URL path. */
export function fileNameFrom(disposition, url) {
  let name = null;
  if (disposition) {
    const extended = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(disposition);
    const plain = /filename\s*=\s*(?:"([^"]*)"|([^;]+))/i.exec(disposition);
    if (extended) {
      try {
        name = decodeURIComponent(extended[2].trim().replace(/^"|"$/g, ""));
      } catch {
        name = null;
      }
    }
    if (!name && plain) name = (plain[1] ?? plain[2]).trim();
  }
  if (!name && url) {
    const base = posix.basename(new URL(url).pathname);
    try {
      name = decodeURIComponent(base);
    } catch {
      name = base;
    }
  }
  return sanitizeFileName(name);
}

/** Make a name safe for Windows and POSIX file systems. */
export function sanitizeFileName(name) {
  let clean = String(name ?? "")
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_")
    .trim()
    .replace(/[. ]+$/, "");
  if (!clean) clean = "download";
  if (/^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$/i.test(clean)) clean = `_${clean}`;
  return clean.slice(0, 200);
}
