# Renders the app logo (white download arrow on a rounded blue square) into a multi-size .ico.
# Usage: powershell -ExecutionPolicy Bypass -File windows\tools\make-icon.ps1
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\DSM\Assets\app.ico"),
    [string]$ExtensionIcons = (Join-Path $PSScriptRoot "..\..\extension\icons")
)

Add-Type -AssemblyName System.Drawing
$installed = (New-Object System.Drawing.Text.InstalledFontCollection).Families.Name
$fontName = if ($installed -contains "Segoe Fluent Icons") { "Segoe Fluent Icons" } else { "Segoe MDL2 Assets" }

function New-Logo([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $radius = [Math]::Max(2, [int]($size / 5)); $d = $radius * 2; $e = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($e - $d, 0, $d, $d, 270, 90)
    $path.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $path.AddArc(0, $e - $d, $d, $d, 90, 90); $path.CloseFigure()
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(59, 130, 246)), ([System.Drawing.Color]::FromArgb(29, 78, 216)), 90
    $g.FillPath($fill, $path)
    $font = New-Object System.Drawing.Font $fontName, ([float]($size * 0.6)), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF 0, ([float]($size * 0.03)), $size, $size
    $g.DrawString([string][char]0xE896, $font, [System.Drawing.Brushes]::White, $box, $format)
    $g.Dispose()
    return $bitmap
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-Logo $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

# ICO container: header, one 16-byte directory entry per image, then the PNG payloads.
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($Out))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write($bytes) }
$writer.Close()
Write-Output "Wrote $Out ($((Get-Item $Out).Length) bytes, sizes: $($sizes -join ', '))"

# The browser extension uses the same logo as PNG files.
New-Item -ItemType Directory -Force $ExtensionIcons | Out-Null
foreach ($size in 16, 32, 48, 128) {
    $bitmap = New-Logo $size
    $bitmap.Save((Join-Path $ExtensionIcons "icon$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}
Write-Output "Wrote extension icons to $ExtensionIcons"
