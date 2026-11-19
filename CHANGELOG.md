# Changelog

Every release needs a section here before it is tagged: the release workflow publishes the section
for that version as the GitHub Release notes, and refuses to release a version that has none.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/): MAJOR for breaking changes (for example to settings or the
command line), MINOR for new features, PATCH for fixes.

## [Unreleased]

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
