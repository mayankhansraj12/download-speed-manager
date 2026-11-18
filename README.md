# Download Speed Manager

A multi-connection download manager for Windows, macOS and Linux, built with Node.js and no runtime dependencies.

- **Segmented downloads**: up to 32 parallel connections per file, each writing at its own offset in a pre-allocated file.
- **Speed-aware dynamic segmentation**: every connection's speed is measured. When a connection goes idle, it takes over the tail of whichever segment will finish last, cut so that both finish together (down to 32 KB pieces). This avoids the classic "stuck at 99%" tail. A connection that stops delivering data for 6 s is reconnected.
- **Pause / resume**: survives app restarts and crashes. Progress lives in a small `.part.dsm` file next to the download, and `If-Range` validators protect against files that changed on the server.
- **Speed limits**: global and per-download token-bucket limits, applied as TCP backpressure.
- **Adapts to servers**: if a server rejects extra connections (HTTP 429/503), it drops to fewer connections instead of failing, and re-tests one more connection every 10 s. Servers without range support fall back to a single connection.
- **IPv4 first**, like IDM, with automatic IPv6 fallback.
- **Queue**: a configurable number of simultaneous downloads.
- **Browser capture**: an extension hands Chrome, Edge and Firefox downloads (with cookies and referrer) to the app.
- **Web UI** with live progress maps, plus a **CLI**.

How it is designed, and what was learned by statically inspecting Internet Download Manager with REA,
is in [docs/rea-findings.md](docs/rea-findings.md). No IDM code or assets are used.

## Windows desktop app

A native Windows version (C# / .NET 10) with a main window, a progress window per download, a
notification-area icon, clipboard and browser catching, an installer, automatic updates, and
IDM-compatible command-line switches. It is in [`windows/`](windows/README.md).

**Install it** from the **Releases** page: download `DownloadSpeedManager-Setup-<version>.exe` and run it.
Installed copies update themselves from new releases. To build it yourself, run
`powershell -ExecutionPolicy Bypass -File windows\build.ps1`; the installer is written to `dist\`.

## Releases, updates and contributing

- [docs/LIFECYCLE.md](docs/LIFECYCLE.md) explains how a change becomes a release, and how installed copies
  find, verify and install it. It also covers signing keys, code signing, and what to do when a release
  goes wrong.
- [CONTRIBUTING.md](CONTRIBUTING.md) explains how to set up, test and send a change.
- [SECURITY.md](SECURITY.md) explains how to report a vulnerability privately.
- [CHANGELOG.md](CHANGELOG.md) lists what changed in each version.
- Licensed under the [MIT License](LICENSE).

The rest of this file covers the cross-platform Node.js version.

## Run it

Needs Node.js 20 or newer.

```powershell
npm start
```

Open <http://127.0.0.1:9614>, paste a link and press **Download**. Files go to your `Downloads` folder by
default; change it in the UI. Press `Ctrl+C` in the terminal to stop: running downloads are paused and
can be resumed next time.

| Environment variable | Default | Purpose |
|---|---|---|
| `DSM_PORT` | `9614` | Port of the local UI/API (also update `APP` in `extension/background.js`) |
| `DSM_DOWNLOAD_DIR` | `~/Downloads` | Initial download folder |
| `DSM_DATA_DIR` | `./data` | Download list and settings |
| `DSM_IP_FAMILY` | `ipv4` | Set to `auto` to use the operating system's IPv6/IPv4 order |

### Benchmark

Same 100 MB file (Vultr Bangalore test file), same machine, back to back, 16 connections:

| Run | Time | Average |
|---|---|---|
| IDM | 27.1 s, 26.1 s | 3.8 MB/s |
| DSM 0.1.0 (halving splits) | 33.0 s | 3.0 MB/s, with an 11 s tail at 99% |
| **DSM with speed-aware takeover** | **21.6 s, 21.4 s** | **4.7 MB/s** |

All outputs had identical SHA-256 hashes.

## Command line

```powershell
node src/cli.js <url> [-c 16] [-o D:\Downloads] [-n name.zip] [-l 2m]
```

`-l` sets a speed limit (`500k`, `2m`, …). Press `Ctrl+C` to pause, and run the same command again to resume.

## Browser extension

1. Start the app (`npm start`).
2. **Chrome / Edge**: open `chrome://extensions` (or `edge://extensions`), turn on *Developer mode*, choose
   *Load unpacked* and select the `extension` folder.
   **Firefox**: open `about:debugging#/runtime/this-firefox`, choose *Load Temporary Add-on* and pick
   `extension/manifest.json`.
3. Downloads you start in the browser now go to Download Speed Manager. If the app isn't running, the
   browser downloads normally. The extension also adds a *Download video* button over videos, a
   right-click menu, a link picker for downloading many links at once, and a toolbar popup with on/off
   switches. It prefers the Windows app through native messaging and falls back to this local server.
   Details are in [windows/README.md](windows/README.md#browser-extension).

## Security notes

- The server listens on `127.0.0.1` only. It rejects requests whose `Host` isn't localhost (DNS rebinding)
  and changes from any web-page origin, so websites can't add downloads or read your list.
  Browser extensions and local tools are allowed.
- Cookies handed over by the browser are stored in `data/downloads.json` so interrupted downloads can
  resume. They are never returned by the API, and they are dropped when a download redirects to another site.

## Tests

```powershell
npm test
```

The tests run against a local HTTP server. They cover byte-exact multi-connection downloads, the fallback
without range support, redirects, file-name collisions, dynamic segmentation, pause/resume, speed limiting,
and the API's Host/Origin protections.

## Not included (yet)

FTP, HLS/DASH stream grabbing, scheduling, checksum verification, and a system tray icon.
