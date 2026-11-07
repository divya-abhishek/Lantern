# Lantern brand-asset generator.
#
# The one source of truth is the supplied square master artwork, lantern_logo_square.svg: a 462 x 462
# square whose artwork is embedded directly. It is canonical. This script never redraws, re-frames,
# re-renders or crops it - it extracts it - and everything the Windows apps show comes from that one
# file:
#
#   * windows/Lantern.Desktop/Assets/lantern.svg   (and the dist copy) - the master itself
#   * windows/Lantern.Desktop/Assets/lantern.png   - the master's artwork, byte for byte
#   * windows/Lantern.Desktop/Assets/lantern.ico   - 16/24/32/48/64/128/256 window + taskbar icon,
#                                                    every size resampled from that same artwork
#
# There is no second mark anywhere in the Windows UI: the sidebar shows this asset, the icon is cut
# from it, and no page draws its own copy.
#
#   powershell -File tools\assets\build-lantern-assets.ps1

param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# --- the square master --------------------------------------------------------------------------
#
# 462 x 462, artwork embedded: copied as-is wherever an SVG is wanted, extracted wherever a raster
# is wanted. Nothing about the drawing is interpreted, so nothing about it can drift.

$master = Join-Path $Repo 'lantern_logo_square.svg'
if (-not (Test-Path $master)) { throw "The canonical square logo is missing: $master" }

$svg = Get-Content $master -Raw
if ($svg -notmatch '<svg[^>]*width="462"[^>]*height="462"') {
    throw "The canonical logo is expected to be a 462 x 462 square SVG: $master"
}

$png = Join-Path $Repo 'windows\Lantern.Desktop\Assets\lantern.png'
$ico = Join-Path $Repo 'windows\Lantern.Desktop\Assets\lantern.ico'
$svgTargets = @(
    (Join-Path $Repo 'windows\Lantern.Desktop\Assets\lantern.svg'),
    (Join-Path $Repo 'dist\Lantern\Assets\lantern.svg')
)

foreach ($target in $svgTargets) {
    $folder = Split-Path $target -Parent
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    Copy-Item $master $target -Force
    Write-Host "svg   -> $target"
}

$match = [regex]::Match($svg, 'base64,([A-Za-z0-9+/=]+)')
if (-not $match.Success) { throw "The canonical logo carries no embedded artwork: $master" }

$artwork = [Convert]::FromBase64String($match.Groups[1].Value)
[System.IO.File]::WriteAllBytes($png, $artwork)
Write-Host "png   -> $png ($([math]::Round($artwork.Length / 1KB)) KB, extracted verbatim)"

# --- the icon: the same artwork at every size Windows asks for ----------------------------------
#
# Windows' standard sizes, all resampled from that one artwork: nothing is redrawn for 16 px, and no
# size gets a mark of its own.

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$source = [System.Drawing.Image]::FromFile($png)

try {
    $entries = @()

    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($source, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
        }
        finally {
            $graphics.Dispose()
        }

        $frame = New-Object System.IO.MemoryStream
        $bitmap.Save($frame, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()

        $entries += [pscustomobject]@{ Size = $size; Bytes = $frame.ToArray() }
        $frame.Dispose()
    }
}
finally {
    $source.Dispose()
}

# ICONDIR, one ICONDIRENTRY per size and then the images: PNG entries, which Vista and later read
# straight out of the file (the same shape this icon already had, so the shell sees no difference).
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([uint16]0)                     # reserved
$writer.Write([uint16]1)                     # 1 = icon
$writer.Write([uint16]$entries.Count)

$offset = 6 + (16 * $entries.Count)
foreach ($entry in $entries) {
    $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }   # 0 means 256 in an icon dir
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)                   # palette size
    $writer.Write([byte]0)                   # reserved
    $writer.Write([uint16]1)                 # colour planes
    $writer.Write([uint16]32)                # bits per pixel
    $writer.Write([uint32]$entry.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}

foreach ($entry in $entries) { $writer.Write($entry.Bytes) }

$writer.Flush()
[System.IO.File]::WriteAllBytes($ico, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "ico   -> $ico ($($sizes -join '/'))"

# The Android launcher/adaptive/banner/in-app assets are generated from the same extracted master
# by tools\assets\build-android-assets.ps1 (raster, never re-drawn vectors). Run it after this script.
