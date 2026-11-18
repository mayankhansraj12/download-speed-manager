<#
  Builds a Windows release into dist\ (or -OutDir):
    DownloadSpeedManager-Setup-<version>.exe        installer: wizard, Start menu, Settings > Apps, uninstaller, updates
    DownloadSpeedManager-<version>-portable.zip     the app, for running without installing (no automatic updates)
    DownloadSpeedManager-extension-<version>.zip    the browser extension, for "Load unpacked" or a store upload
    SHA256SUMS.txt                                  checksums of the files above

  Usage:
    powershell -ExecutionPolicy Bypass -File windows\build.ps1 [-Version 1.2.3] [-Repository owner/repo] [-SkipTests]

  -Repository bakes the GitHub repository the app checks for updates into the build. The release
  workflow passes its own repository; local builds leave it out and have no update source.
  latest.json and its signature are made by the release workflow (tools\ReleaseTool), not here.
#>
param(
    [string]$Version = "1.0.0",
    [string]$Repository = "",
    [string]$OutDir = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = if ($OutDir) { $OutDir } else { Join-Path (Split-Path $root) "dist" }
$stage = Join-Path ([IO.Path]::GetTempPath()) "dsm-build-$([guid]::NewGuid().ToString('N'))"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
if ($Repository -and $Repository -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { throw "Repository must look like owner/repo (got '$Repository')." }

# Optional Authenticode signing: when DSM_SIGN_SCRIPT names a script,
# it is called with each exe to sign, before anything is zipped or checksummed.
function Invoke-Sign([string]$Path) {
    if (-not $env:DSM_SIGN_SCRIPT) { return }
    & $env:DSM_SIGN_SCRIPT $Path
    if (-not $?) { throw "Signing $Path failed" }
    Write-Host "Signed $(Split-Path $Path -Leaf)"
}

# Zip a folder with standard forward-slash entry names. (Compress-Archive in Windows PowerShell 5.1
# writes backslashes, which other unzip tools and the browser extension stores don't accept.)
function New-Zip([string]$Source, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $base = (Resolve-Path $Source).Path.TrimEnd('\')
    $zip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
            $name = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

if (-not (Test-Path "$root\DSM\Assets\app.ico")) { & "$root\tools\make-icon.ps1" }

if (-not $SkipTests) {
    dotnet run --project "$root\DSM.Tests\DSM.Tests.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

New-Item -ItemType Directory -Force $stage, $dist | Out-Null
# Tidy earlier release formats; skip anything in use (for example a copy of the app that is running).
$keep = '^(DownloadSpeedManager-.*\.(exe|zip)|SHA256SUMS\.txt|latest\.json(\.sig)?)$'
Get-ChildItem $dist -Force | Where-Object { $_.Name -notmatch $keep } | ForEach-Object {
    $item = $_
    try { Remove-Item $item.FullName -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "Left $($item.Name) in dist because it is in use." }
}

try {
    # 1. The app: one framework-dependent exe plus the browser extension.
    dotnet publish "$root\DSM\DSM.csproj" -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version "-p:UpdateRepository=$Repository" `
        -o "$stage\app" -nologo
    if ($LASTEXITCODE -ne 0) { throw "App publish failed" }
    Invoke-Sign "$stage\app\DownloadSpeedManager.exe"

    # The extension carries the same version as the app, so a bug report names one number.
    $manifest = "$stage\app\extension\manifest.json"
    $json = [IO.File]::ReadAllText($manifest)
    $json = ([regex]'"version"\s*:\s*"[^"]*"').Replace($json, "`"version`": `"$Version`"", 1)
    [IO.File]::WriteAllText($manifest, $json, (New-Object Text.UTF8Encoding $false))

    New-Zip "$stage\app" "$stage\payload.zip"

    # 2. The installer, with the app embedded in it.
    dotnet publish "$root\DSM.Setup\DSM.Setup.csproj" -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version "-p:PayloadZip=$stage\payload.zip" `
        -o "$stage\setup" -nologo
    if ($LASTEXITCODE -ne 0) { throw "Setup publish failed" }
    Invoke-Sign "$stage\setup\DownloadSpeedManagerSetup.exe"

    $files = @(
        "DownloadSpeedManager-Setup-$Version.exe",
        "DownloadSpeedManager-$Version-portable.zip",
        "DownloadSpeedManager-extension-$Version.zip"
    )
    Copy-Item "$stage\setup\DownloadSpeedManagerSetup.exe" (Join-Path $dist $files[0]) -Force
    # 3. Portable zip for people who prefer not to install.
    Copy-Item "$stage\payload.zip" (Join-Path $dist $files[1]) -Force
    # 4. The extension on its own (manifest.json at the root of the zip).
    New-Zip "$stage\app\extension" (Join-Path $dist $files[2])

    # 5. Checksums, in the format `sha256sum -c` understands.
    $sums = foreach ($name in $files) {
        "{0}  {1}" -f (Get-FileHash (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLowerInvariant(), $name
    }
    [IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS.txt"), (($sums -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))

    foreach ($name in $files) {
        $file = Get-Item (Join-Path $dist $name)
        Write-Host ("Built {0} ({1:N0} KB)" -f $file.Name, ($file.Length / 1KB))
    }
    if ($Repository) { Write-Host "Update source: https://github.com/$Repository/releases" }
    else { Write-Host "No update source (local build). Pass -Repository owner/repo to enable updates." }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
