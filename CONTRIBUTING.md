# Contributing

Thanks for helping. Bug reports, fixes and features are welcome. For anything large, open an issue
first so the approach can be agreed before you spend time on it.

## Set up

| You need | For |
|---|---|
| Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | the Windows app, installer and tests |
| [Node.js](https://nodejs.org/) 20 or newer | the cross-platform engine and web interface |
| Chrome, Edge or Firefox | trying the browser extension |

```powershell
git clone https://github.com/<owner>/<repo>.git
cd <repo>
dotnet run --project windows/DSM.Tests -c Release        # Windows engine, updater and clipboard tests
npm test                                                 # Node engine tests
dotnet run --project windows/DSM                         # run the app from source
powershell -ExecutionPolicy Bypass -File windows\build.ps1   # build the installer into dist\
```

When you run the app from source, give it its own data folder so it can't touch an installed copy:

```powershell
$env:DSM_DATA_DIR = "$env:TEMP\dsm-dev"; dotnet run --project windows/DSM
```

## Project layout

| Folder | What it is |
|---|---|
| `windows/DSM` | the WinForms app: download engine (`Engine/`), integration (`Integration/`), UI (`Ui/`), updates (`Updates/`) |
| `windows/DSM.Setup` | the installer and uninstaller |
| `windows/DSM.Tests` | tests for the engine, updater and clipboard filter (a console app; run it as above) |
| `windows/tools/ReleaseTool` | makes and signs `latest.json` during releases |
| `extension/` | the browser extension (Manifest V3) |
| `src/`, `public/`, `test/` | the Node.js engine, web interface and its tests |
| `.github/workflows` | CI (`ci.yml`) and releases (`release.yml`) |
| `docs/` | design notes and [the release and update lifecycle](docs/LIFECYCLE.md) |

## Making a change

1. Fork the repository and create a branch from `main` (`fix/clipboard-repeat`, `feature/scheduler`).
2. Make the change and add or update a test for it when the behaviour can be tested.
3. Run the tests. CI runs them again on your pull request, and it must pass before merging.
4. Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) if users will notice the change.
5. Open a pull request and describe what changed and how you checked it.

Guidelines:

- Keep the UI design original. Familiar layouts are fine, but don't copy another product's artwork,
  icons, branding or exact look.
- Don't add telemetry. The app talks only to the servers you download from and, for update checks,
  to GitHub.
- New network or file-handling code needs tests. The test servers in `windows/DSM.Tests` simulate
  slow, stalled and connection-capped servers.

## Releases

Maintainers release by tagging `main`. The full process, including how installed copies pick up the
update, is in [docs/LIFECYCLE.md](docs/LIFECYCLE.md).
