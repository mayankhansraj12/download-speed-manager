# Download Speed Manager for Windows

A native Windows download manager (C# / .NET 10 WinForms) that works the way classic Windows download
managers do: a main window with categories, a progress window per download, a notification-area icon,
link catching from the clipboard and the browser, and a command line compatible with IDM's switches.
The design and artwork are original; no IDM code, images or branding are used.

For an overview, screenshots and downloads, see the [main README](../README.md). This page is the
detailed reference for the Windows app.

## Themes

The dark theme is the default. *Options → General → Theme* switches to Light or follows Windows (System).

### Colours

Status colours follow the convention Windows uses for progress, the same green, yellow and red that
taskbar buttons and File Explorer copy dialogs show:

| Colour | Meaning | Where |
|---|---|---|
| Green | Running fine, or done | Downloading and Complete status, progress bars and segment maps, *Resume* and *Start download* / *Open* buttons, "Receiving data", resume supported, browser connected, live speed |
| Yellow | Paused, waiting or limited | Paused, Queued and Connecting status, Unfinished, *Pause* / *Pause all*, waiting to retry, "can't resume", an active speed limit |
| Red | Failed or destructive | Error and Cancelled status, *Delete* and *Stop*, error messages, browser not connected |
| Blue | Brand and selection | Logo, *Add URL*, the selected category, *OK* buttons |

The app's taskbar button and every progress window's taskbar button show progress in the same colours:
green while downloading, yellow when paused, and red after an error.

## Install

1. Download **`DownloadSpeedManager-Setup-<version>.exe`** from the repository's **Releases** page and run it.
   To build it yourself, run `powershell -ExecutionPolicy Bypass -File windows\build.ps1`. The installer is
   then in `dist\`, but a copy you build has no update source.
   - The wizard (Welcome → Options → Install → Finish) installs for your Windows account only, without
     administrator rights, to `%LOCALAPPDATA%\Programs\Download Speed Manager`. You can choose another folder.
   - It adds a Start menu shortcut (a desktop shortcut is optional) and an entry in **Settings → Apps** with
     the icon, version, size and an **Uninstall** button.
   - Running a newer setup updates in place: the app is closed cleanly first (downloads pause and resume
     later), and your settings and download list are kept.
   - Uninstalling removes the app, its shortcuts, start-with-Windows entry and browser registration.
     Downloaded files are never touched; your settings are deleted only if you tick the box.
   - Requires the free [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). If it's
     missing, Windows offers the download when setup starts.
   - Silent use for scripts: `Setup.exe /S [/D=folder] [/desktop | /nodesktop] [/run]`, and
     `"%LOCALAPPDATA%\Programs\Download Speed Manager\Uninstall.exe" /uninstall /S [/removedata]`.
     Without `/desktop` or `/nodesktop`, a silent install keeps the desktop shortcut as it is.
2. Prefer not to install? `DownloadSpeedManager-<version>-portable.zip` runs from any folder. It tells you
   about new versions but can't install them itself.
3. Add the browser extension (see below).

## Browser extension

The extension in `extension\` (also installed next to the app) connects Chrome, Edge, Brave and Firefox
to the app. Like IDM's integration, it talks to the app through native messaging: the browser starts
`DownloadSpeedManager.exe` as a small host, which hands everything to the running app and starts the app
if needed. No local web server is involved.

| Feature | What it does |
|---|---|
| Catch downloads | Downloads you start in the browser go to the app's *New download* dialog, with cookies and referrer carried over. File types and a minimum size are configurable. Links with no visible type, which is common on file hosts, are caught too. If the app isn't installed, the browser downloads as usual. |
| <kbd>Alt</kbd>+click | Hold Alt while clicking a link to let the browser download it. |
| Download video | A button over videos that come from a single downloadable file, like *Save video as*. Streamed or protected video (HLS/DASH/DRM) is not offered. |
| Right-click menu | *Download with Download Speed Manager* (links), *Download this video or audio…*, and *Download all links…* (page or selection). |
| Link picker | Lists every link on a page, filtered by type (All files, Video, Audio, Documents, Archives, Software, Images), with files pre-selected. *Download N* queues them in the app in one go, without a dialog for each. |
| Toolbar popup | Connection status: green when connected, yellow when paused, red when the app isn't found. Also on/off switches, video and audio found on the page, the link picker, and settings. |

**Install it:** open `chrome://extensions` (or `edge://extensions`), turn on **Developer mode**, choose
**Load unpacked** and select the `extension` folder inside the install folder. The extension's ID is fixed
(`glhkcjbcocbmlopkkbepbchghfkmplob`), so the app recognises it. Firefox:
`about:debugging#/runtime/this-firefox` → **Load Temporary Add-on** → `extension\manifest.json`.

## What it does

| Feature | How |
|---|---|
| Multi-connection downloads (1–32 per file) | Byte-range requests over real HTTP/1.1 connections, written to a pre-sized sparse `.part` file |
| Finishes fast, no "stuck at 99%" | Speed-aware takeover: an idle connection takes over the tail of the segment that would finish last, cut so both finish together |
| Survives bad connections | Silent connections are reconnected after 6 s; servers that refuse extra connections (HTTP 429/503) get fewer, re-tested every 10 s |
| Pause / resume, even after a restart or crash | Progress is kept in a `.part.dsm` file next to the download; ETag/Last-Modified guard against changed files |
| Queue | Set how many downloads run at once; *Download later* adds paused |
| Speed limits | Overall (toolbar, tray) and per download (progress window) |
| Categories | Video, Audio, Documents, Archives, Software, Images; optionally saved into matching subfolders |
| Browser capture | Extension → native-messaging host → running app, which shows the *New download* dialog with cookies and referrer carried over |
| Clipboard catching | Copy a link to a downloadable file and the *New download* dialog appears |
| Notification area | Keeps running when the window is closed; tray menu for add, pause all, speed limit, exit |
| Start with Windows | Optional, per user |
| IPv4 first | Like IDM; IPv6 is used automatically when IPv4 fails |
| Dark and light themes | Windows 11-style colours with a blue accent; dark title bars, menus, lists and dialogs |

## Updates

Installed copies keep themselves up to date from this repository's GitHub Releases. They check 20
seconds after starting and then every 6 hours, or at once with **Help → Check for updates**. When a
newer version is out, a bar offers **Install update**. The app checks the release's signature and the
installer's SHA-256, installs it silently, restarts, and continues the downloads that were running.
**Options → Updates** turns automatic checks off, or lets updates install without asking when nothing is
downloading. Nothing besides these requests is sent anywhere.

## Command line

The app accepts the switches IDM documents, so existing scripts keep working:

```
DownloadSpeedManager.exe /d <url> [/p <folder>] [/f <file name>] [/n] [/a]
DownloadSpeedManager.exe /s        start everything waiting in the queue
DownloadSpeedManager.exe --tray    start hidden in the notification area
```

`/n` adds without asking, and `/a` adds without starting. If the app is already running, the command is handed to it.

## Project layout

```
windows/
  DSM/                    the app
    Engine/               downloads: HTTP, segments and takeover, throttle, queue (no UI code)
    Ui/                   main window, dialogs, progress window, tray (Shell)
    Integration/          single instance, native-messaging host, browser registration,
                          clipboard watcher, start with Windows, command line
    Updates/              update check, signature and checksum verification, the trusted public key
    Platform/             paths, JSON, Win32 calls (sparse files, Downloads folder)
  DSM.Tests/              engine, updater and clipboard tests (dotnet run --project windows/DSM.Tests)
  DSM.Setup/              the installer: wizard, per-user install, Settings > Apps entry, uninstaller
  tools/make-icon.ps1     renders Assets/app.ico
  tools/ReleaseTool/      makes and signs latest.json for releases (keygen, manifest, sign, verify)
  tools/test-update.ps1   acts out a whole update on this computer, end to end
  build.ps1               icon → tests → publish app → embed it in Setup.exe → zips and checksums in dist\
```

## Release audit

The published build was checked with a binary analysis tool:

- Hardening: ASLR (with high-entropy VA), DEP/NX and Control Flow Guard are all enabled. The executable has no Authenticode signature yet, so SmartScreen may warn on first install. (Updates are protected separately, by the release signature.)
- Release exposure: no credential markers, debug companions or source maps were found. The only embedded PDB path is Microsoft's own `apphost.pdb` build path from the .NET launcher stub. As with every .NET single-file app, the bundled assembly can be extracted, so treat it as public.

## Data locations

- Settings and download list: `%APPDATA%\DownloadSpeedManager` (the list includes browser cookies for unfinished downloads, so they can resume)
- Browser integration manifests: `%LOCALAPPDATA%\DownloadSpeedManager\native-messaging`
- Crash details, if any: `%APPDATA%\DownloadSpeedManager\errors.log`
- A downloaded update, until the next one replaces it: `%TEMP%\DownloadSpeedManager-Update`
