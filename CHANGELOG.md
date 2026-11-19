# Changelog

Every release needs a section here before it is tagged: the release workflow publishes the section
for that version as the GitHub Release notes, and refuses to release a version that has none.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/): MAJOR for breaking changes (for example to settings or the
command line), MINOR for new features, PATCH for fixes.

## [Unreleased]

## [1.2.0] - 2026-11-19

### Changed

- A polished look across the app, installer, browser extension and web interface: clearer selection,
  hover and focus states, rounded controls, brand-blue buttons with readable contrast, and consistent
  spacing in the dark and light themes.
- Main window: a themed search box (Ctrl+F to search, Esc to clear), readable selected rows, full dates,
  long names shortened in the middle so the extension stays visible, and helpful messages when a list is
  empty.
- Setup: full keyboard support (Enter, Esc and Alt shortcuts), clearer notices, and layouts that scale
  properly on high-DPI screens.
- Browser extension: restyled status and update banner, a link picker that keeps its header and
  Download bar in view, and clearer empty states and errors.
- Web interface: no sideways scrolling on phones, keyboard focus is kept after actions, and states are
  colour-coded the same way as in the app.

### Fixed

- Pressing Enter on the first page of setup closed it instead of moving on.
- A double-click on Download in the web interface added the download twice.
- In the extension, a long file name pushed the popup's Download buttons out of view, and the
  "Download video" button could spill past the edge of the video.

## [1.1.0] - 2026-11-19

### Added

- Updates in the browser extension: the toolbar icon shows when a new version is out, the popup has a
  one-click **Update** button and a **Check for updates** link, and the extension reloads itself after the
  app has installed its new files.
- **Options → Updates**: choose how often to check (every hour, 6 hours or day) and see when the last
  check ran.
- **Check for updates** and **Install update** in the notification-area menu, and `--update` on the
  command line.

## [1.0.0] - 2026-11-19

### Added

- Windows app: multi-connection downloads (1–32 connections per file) with dynamic segment takeover,
  stall recovery, connection-cap detection (HTTP 429/503), pause and resume, and per-download and
  overall speed limits.
- Main window with categories, colour-coded states, a progress window per download, a notification-area
  icon, taskbar progress, and dark, light and system themes.
- Installer with Start menu and desktop shortcuts, an entry in Settings → Apps and an uninstaller;
  a portable zip for running without installing.
- Browser extension for Chrome, Edge and Firefox: catches downloads, adds a "Download video" button
  over single-file videos, and picks links from a page. It talks to the app through native messaging.
- Clipboard catching that ignores links restored to the clipboard by dictation tools such as Wispr Flow,
  and content that other apps mark as private.
- Automatic updates from GitHub Releases. Update information is signed (ECDSA P-256) and the installer is
  checked against its published SHA-256 before it runs. Downloads continue after the update.
- Node.js engine and web interface for other platforms.
