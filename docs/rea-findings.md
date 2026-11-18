# REA findings: how Internet Download Manager is put together

Target: the locally installed Internet Download Manager (`C:\Program Files (x86)\Internet Download Manager`).
Method: static, read-only REA inspections of shipped artifacts. Nothing was executed, decompiled or
patched, and licensing/registration code was out of scope. The goal was to learn the *architecture and
principles* so this project could implement its own design. No IDM code, assets or branding are reused.

The raw REA evidence files are kept out of the repository because they contain machine-specific paths;
the IDs in the Evidence column refer to them.

## Findings ledger

| # | Finding | Type | Evidence |
|---|---------|------|----------|
| 1 | `IDMan.exe` (32-bit) does its networking over **raw Winsock**: delay-loaded `WS2_32` ordinals resolve to `socket`, `connect`, `send`, `recv`, `select`, `__WSAFDIsSet`, `ioctlsocket`, `gethostbyname`, `bind`, `listen`, `accept`, `WSAStartup`/`WSACleanup`. | observed | `ev_babf98846d0b3c8f…` (imports) |
| 2 | It does **not** import WinHTTP, and uses WinINet only for URL helpers (`InternetCrackUrl`, `InternetCanonicalizeUrl`, `InternetCombineUrl`) and the cookie store (`InternetGetCookie`/`InternetSetCookie`). | observed | `ev_babf98846d0b3c8f…` |
| 3 | So IDM ships **its own HTTP/FTP client** on top of sockets, multiplexed with `select()` over non-blocking sockets rather than one thread per connection. | inferred from 1–2 | — |
| 4 | File output uses `SetFilePointer` + `SetEndOfFile` + `WriteFile`, consistent with **pre-allocating the file and writing each segment at its own offset**. | inferred | `ev_babf98846d0b3c8f…` |
| 5 | TLS comes from bundled OpenSSL (`libssl.dll`, `libcrypto.dll` on disk). They are absent from the static import table, so they are loaded at runtime. | observed (files) / inferred (loading) | directory listing, imports |
| 6 | Browser hand-off uses **Chrome/Firefox native messaging**: host `com.tonec.idm`, `type: stdio`, launched as `IDMMsgHost.exe`. | observed | `IDMMsgHost.json`, `IDMMsgHostMoz.json` |
| 7 | `IDMMsgHost.exe` is a small relay: `ReadFile`/`WriteFile` (stdio framing) plus a thread and `CoCreateInstance`. It most likely forwards captured links to the main app over COM (`idmantypeinfo.tlb` ships alongside). | observed imports / inferred role | `ev_411a3b917fd6f88b…` |
| 8 | The browser extension (MV3, v6.43.1) runs a service worker plus a content script injected into **all frames at `document_start`**. Its permissions include `downloads`, `downloads.shelf`, `webRequest`, `declarativeNetRequest`, `cookies` and `nativeMessaging`. | observed (manifest) | `idm-chrome-extension-summary.json` |
| 9 | The REA semantic graph of the extension shows port-style messaging tokens (`postMessage` ×37, `runtime` ×14, `onMessage`, `onDisconnect`), consistent with a long-lived native-messaging port. | observed tokens / inferred pattern | `ev_527d7ec02b4f61b6…` (partial coverage) |
| 10 | The main app also uses window messages (`RegisterWindowMessage`, `FindWindow`, `SendMessage`, `PostMessage`), shared memory (`CreateFileMapping`/`MapViewOfFile`), a tray icon (`Shell_NotifyIcon`) and `DeviceIoControl`. The last matches the bundled WFP/TDI network drivers used for traffic monitoring. | observed imports / inferred purpose | `ev_babf98846d0b3c8f…` |

## Unknowns (deliberately not pursued)

- The exact segmentation, connection-scheduling and speed-limiting algorithms. Import tables can't
  show them, and recovering them would mean decompiling proprietary code.
- Whether the listening socket (`bind`/`listen`/`accept`) serves FTP active mode, local integration, or both.
- The extension's complete capture rules. REA coverage was partial (1 source not examined, 2 limited).

## Design decisions this project made from the findings

| IDM principle (observed/inferred) | Download Speed Manager's own implementation |
|---|---|
| Own HTTP stack, many concurrent connections | Node `http`/`https` with keep-alive agents; many ranged requests in one event loop (no thread per connection) |
| Pre-allocated file, offset writes | `.part` file truncated to full size, positional `write()` per segment |
| Segmented download that keeps all connections busy | Speed-aware dynamic segmentation: an idle connection takes over the tail of the segment expected to finish last, cut so both finish together; silent connections are recycled after 6 s |
| Extension with a content script in every frame plus `downloads`, `webRequest` and `cookies` permissions (finding 8) | Own extension with the same capabilities: catches downloads, carries cookies, a content script for a *Download video* button and Alt+click bypass, `webRequest` to spot whole media files, a link picker, and a toolbar popup |
| IPv4-only name resolution (`gethostbyname`, finding 1) | IPv4 addresses are tried first, with IPv6 as a fallback (`DSM_IP_FAMILY=auto` restores OS order) |
| Resume | JSON sidecar (`.part.dsm`) storing segment progress and validators (ETag / Last-Modified) |
| Browser capture via native messaging | Windows app: the same architecture. A native-messaging host (`com.downloadspeedmanager.host`, the app's own exe) relays to the running app over a per-user named pipe and starts the app if needed. Node app: a localhost HTTP API (Host/Origin checked) |
| Speed limiting | Token-bucket limiter, global and per-download, applied as stream backpressure |
