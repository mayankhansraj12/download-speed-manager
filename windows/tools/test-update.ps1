<#
  Watch an update happen, end to end, on this computer:
    1. builds releases 1.0.0 and 1.0.1 (or reuses them with -SkipBuild),
    2. publishes 1.0.1 on a local feed exactly as the release workflow does (latest.json + signature),
    3. installs 1.0.0 silently into a test folder, with its own data folder,
    4. starts it and waits while it finds, verifies, downloads and installs 1.0.1 and restarts itself,
    5. checks the result, then puts everything back.

  Needs the update-signing private key (installed copies only trust releases signed with it), the
  .NET 10 SDK and Node.js. Your real installation's Settings > Apps entry, shortcuts and browser
  registrations are backed up first and restored afterwards; the real uninstaller is never run, and
  a copy of the app that is already running is left alone.

  Usage: powershell -ExecutionPolicy Bypass -File windows\tools\test-update.ps1 [-KeyFile path] [-WorkDir path] [-SkipBuild]
  See docs\LIFECYCLE.md, "Testing updates locally".
#>
param(
    [string]$KeyFile = "$env:USERPROFILE\.dsm-release\update-signing-key.pem",
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "dsm-update-test"),
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$project = Split-Path (Split-Path $PSScriptRoot)
$t = $WorkDir
$install = "$t\install"
$feedPort = 18081
$uninstallKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DownloadSpeedManager'
$hostKeys = 'Google\Chrome', 'Microsoft\Edge', 'Mozilla', 'Chromium', 'BraveSoftware\Brave-Browser' |
    ForEach-Object { "HKCU\Software\$_\NativeMessagingHosts\com.downloadspeedmanager.host" }
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Download Speed Manager.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Download Speed Manager.lnk'
$updateTemp = Join-Path ([IO.Path]::GetTempPath()) "DownloadSpeedManager-Update"

function Log($text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }
function Reg([string]$arguments) { cmd /c "reg $arguments >nul 2>&1"; return $LASTEXITCODE }
function KeyPath($key) { $key -replace '^HKCU\\', 'HKCU:\' }
function BackupName($path) {
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($path))
    "$t\backup\" + ([BitConverter]::ToString($hash, 0, 8) -replace '-', '')
}

if (-not (Test-Path $KeyFile)) { throw "No signing key at $KeyFile. Pass -KeyFile (see docs\LIFECYCLE.md, 'Key management')." }
if (-not $SkipBuild) { Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force "$t\backup", "$t\feed", "$t\data" | Out-Null
Remove-Item -LiteralPath $install -Recurse -Force -ErrorAction SilentlyContinue

# 0. Back up what a test install changes.
$backups = @{}
foreach ($key in @($uninstallKey) + $hostKeys) {
    if (Test-Path (KeyPath $key)) {
        $file = (BackupName $key) + ".reg"
        if ((Reg "export `"$key`" `"$file`" /y") -ne 0) { throw "Backing up $key failed" }
        $backups[$key] = $file
    }
}
foreach ($link in $startMenu, $desktop) { if (Test-Path $link) { Copy-Item $link (BackupName $link) } }
Log "Backed up $($backups.Count) registry keys and the shortcuts"

$server = $null
try {
    # 1. Two releases: the one people have installed, and the update.
    if (-not $SkipBuild) {
        foreach ($v in "1.0.0", "1.0.1") {
            & "$project\windows\build.ps1" -Version $v -OutDir "$t\v$v" -SkipTests | Out-Null
            Log "Built $v"
        }
    }

    # 2. The feed, made exactly as the release workflow makes it, signed with the real update key.
    Copy-Item "$t\v1.0.1\DownloadSpeedManager-Setup-1.0.1.exe" "$t\feed\" -Force
    $tool = "$project\windows\tools\ReleaseTool\ReleaseTool.csproj"
    dotnet run --project $tool -c Release -- manifest --version 1.0.1 --setup "$t\feed\DownloadSpeedManager-Setup-1.0.1.exe" --base-url "http://127.0.0.1:$feedPort" --out "$t\feed\latest.json"
    dotnet run --project $tool -c Release -- sign --file "$t\feed\latest.json" --key-file "$KeyFile" --out "$t\feed\latest.json.sig"
    dotnet run --project $tool -c Release -- verify --file "$t\feed\latest.json" --signature "$t\feed\latest.json.sig" --public-key-file "$project\windows\DSM\Updates\update-public-key.txt"
    if ($LASTEXITCODE -ne 0) { throw "The key in $KeyFile doesn't match the public key built into the app." }

    # A tiny static file server standing in for GitHub Releases.
    $serve = @'
import http from "node:http";
import { readFile } from "node:fs/promises";
import { join, basename } from "node:path";
const [dir, port] = process.argv.slice(2);
http.createServer(async (req, res) => {
  try {
    const body = await readFile(join(dir, basename(decodeURIComponent(new URL(req.url, "http://x").pathname))));
    res.writeHead(200, { "content-length": body.length }).end(body);
  } catch { res.writeHead(404).end(); }
}).listen(Number(port), "127.0.0.1");
'@
    [IO.File]::WriteAllText("$t\serve.mjs", $serve, (New-Object Text.UTF8Encoding $false))
    $server = Start-Process node -ArgumentList "`"$t\serve.mjs`"", "`"$t\feed`"", "$feedPort" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    Log "Feed is up on http://127.0.0.1:$feedPort"

    # 3. Isolated app data: updates install automatically; nothing else touches the system.
    $utf8 = New-Object Text.UTF8Encoding $false
    $settings = [ordered]@{ checkForUpdates = $true; autoInstallUpdates = $true; browserIntegration = $false
        monitorClipboard = $false; launchOnStartup = $false; showProgressWindow = $false; downloadFolder = "$t\downloads" }
    [IO.File]::WriteAllText("$t\data\settings.json", ($settings | ConvertTo-Json), $utf8)
    $paused = [ordered]@{ id = [guid]::NewGuid().ToString("N"); url = "http://127.0.0.1:9/files/keep-me.zip"; fileName = "keep-me.zip"
        folder = "$t\downloads"; category = "Archives"; status = "Paused"; size = 1048576; received = 524288; connections = 8
        createdAt = (Get-Date).ToString("o"); headers = @{} }
    [IO.File]::WriteAllText("$t\data\downloads.json", (ConvertTo-Json @($paused) -Depth 4), $utf8)
    $env:DSM_DATA_DIR = "$t\data"
    $env:DSM_UPDATE_FEED = "http://127.0.0.1:$feedPort"

    # 4. Install 1.0.0 silently, the way a first install lands.
    $setup = Start-Process "$t\v1.0.0\DownloadSpeedManager-Setup-1.0.0.exe" -ArgumentList "/S", "`"/D=$install`"" -PassThru -Wait
    $version = (Get-Item "$install\DownloadSpeedManager.exe").VersionInfo.ProductVersion
    Log "Installed $version (setup exit code $($setup.ExitCode))"

    # 5. Run it and wait: the first update check is 20 s after start.
    $started = Get-Date
    $first = Start-Process "$install\DownloadSpeedManager.exe" -ArgumentList "--tray" -PassThru
    Log "Started 1.0.0 as PID $($first.Id); waiting for it to update itself"
    $updated = $null
    $sawExit = $false
    while (((Get-Date) - $started).TotalSeconds -lt 150) {
        Start-Sleep -Seconds 2
        if ($first.HasExited -and -not $sawExit) { $sawExit = $true; Log "1.0.0 exited so the installer could replace it" }
        $now = Get-Process DownloadSpeedManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$install\*" -and $_.Id -ne $first.Id }
        if ($now -and (Get-Item "$install\DownloadSpeedManager.exe").VersionInfo.ProductVersion -like "1.0.1*") { $updated = $now | Select-Object -First 1; break }
    }
    if (-not $updated) { throw "The app did not update within 150 s. Check $t\data\errors.log." }
    Log ("Running {0} as PID {1}, {2:N0} s after the first start" -f (Get-Item "$install\DownloadSpeedManager.exe").VersionInfo.ProductVersion, $updated.Id, ((Get-Date) - $started).TotalSeconds)
    Start-Sleep -Seconds 4

    # 6. Check the result.
    $list = Get-Content "$t\data\downloads.json" -Raw | ConvertFrom-Json
    $downloaded = "$updateTemp\DownloadSpeedManager-Setup-1.0.1.exe"
    $published = (Get-Content "$t\feed\latest.json" -Raw | ConvertFrom-Json).setup.sha256
    [pscustomobject]@{
        "exe version"                = (Get-Item "$install\DownloadSpeedManager.exe").VersionInfo.ProductVersion
        "Settings > Apps version"    = (Get-ItemProperty (KeyPath $uninstallKey)).DisplayVersion
        "extension files installed"  = Test-Path "$install\extension\icons\icon128.png"
        "download list kept"         = [bool]($list | Where-Object fileName -eq "keep-me.zip")
        "resume file consumed"       = -not (Test-Path "$t\data\resume-after-update.json")
        "installer hash = published" = (Test-Path $downloaded) -and ((Get-FileHash $downloaded).Hash -eq $published)
        "old process gone"           = $first.HasExited
        "errors.log"                 = $(if (Test-Path "$t\data\errors.log") { Get-Content "$t\data\errors.log" -Raw } else { "none" })
    } | Format-List
}
finally {
    # 7. Clean up, and put the real installation's entries back exactly as they were.
    if (Test-Path "$install\DownloadSpeedManager.exe") {
        $exit = Start-Process "$install\DownloadSpeedManager.exe" -ArgumentList "--exit" -PassThru
        $exit.WaitForExit(10000) | Out-Null
    }
    Start-Sleep -Seconds 3
    Get-Process DownloadSpeedManager, DownloadSpeedManagerSetup -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "$install\*" -or $_.Path -like "$updateTemp\*" } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:DSM_DATA_DIR, Env:DSM_UPDATE_FEED -ErrorAction SilentlyContinue

    foreach ($key in @($uninstallKey) + $hostKeys) {
        if ($backups.ContainsKey($key)) {
            Reg "delete `"$key`" /f" | Out-Null
            if ((Reg "import `"$($backups[$key])`"") -ne 0) { Write-Warning "Restoring $key failed; the backup is $($backups[$key])" }
        }
        elseif (Test-Path (KeyPath $key)) { Reg "delete `"$key`" /f" | Out-Null }
    }
    foreach ($link in $startMenu, $desktop) {
        $saved = BackupName $link
        if (Test-Path $saved) { Copy-Item $saved $link -Force } elseif (Test-Path $link) { Remove-Item $link -Force }
    }
    Start-Sleep -Seconds 1
    Remove-Item -LiteralPath $install -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $updateTemp -Recurse -Force -ErrorAction SilentlyContinue
    Log "Cleaned up; your own installation's entries are as they were."
}
