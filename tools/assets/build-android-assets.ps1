# Lantern Android brand-asset generator.
#
# Android shows the canonical master as a raster everywhere the logo appears. The one source of truth
# is the extracted square master (windows/Lantern.Desktop/Assets/lantern.png, produced by
# build-lantern-assets.ps1): this script scales and re-frames that raster into the launcher icon, the
# adaptive-icon foreground, the in-app mark and the TV banner. Nothing is re-drawn as a vector, so the
# complete artwork - lamp AND beam - stays byte-true.
#
#   powershell -File tools\assets\build-android-assets.ps1

param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Png = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($Png)) {
    $Png = Join-Path $Repo 'windows\Lantern.Desktop\Assets\lantern.png'
}
if (-not (Test-Path $Png)) { throw "The extracted master PNG is missing (run build-lantern-assets.ps1 first): $Png" }

Add-Type -AssemblyName System.Drawing

$androidRes = Join-Path $Repo 'android\app\src\main\res'

function SavePng([System.Drawing.Bitmap]$bmp, [string]$path) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "png   -> $path"
}

$source = [System.Drawing.Image]::FromFile($Png)
try {
    # The opaque artwork's bounds inside the square master (drives every re-frame below).
    $probe = New-Object System.Drawing.Bitmap($source)
    $minX = $source.Width; $minY = $source.Height; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $probe.Height; $y++) {
        for ($x = 0; $x -lt $probe.Width; $x++) {
            if ($probe.GetPixel($x, $y).A -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $probe.Dispose()

    $artW = $maxX - $minX + 1
    $artH = $maxY - $minY + 1
    $artCx = ($minX + $maxX) / 2.0
    $artCy = ($minY + $maxY) / 2.0

    # 1) Legacy launcher icons: the complete square master, scaled into each density bucket.
    $mipmapSizes = @{ 'mdpi' = 48; 'hdpi' = 72; 'xhdpi' = 96; 'xxhdpi' = 144; 'xxxhdpi' = 192 }
    foreach ($density in $mipmapSizes.Keys) {
        $px = $mipmapSizes[$density]
        $b = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.DrawImage($source, 0, 0, $px, $px)
        $g.Dispose()
        SavePng $b (Join-Path $androidRes "mipmap-$density\ic_launcher.png")
    }

    # 2) Adaptive-icon foreground: the artwork centred in the safe-zone circle, per density.
    #    The safe zone is a 66dp circle (33dp radius) in the 108dp canvas; 30dp leaves a margin so
    #    the complete mark - beam tip included - stays clear of every mask shape.
    $safeRadius = 30.0
    $rad = [Math]::Sqrt([Math]::Pow($artW / 2.0, 2) + [Math]::Pow($artH / 2.0, 2))
    $iconScale = $safeRadius / $rad
    $densityScale = @{ 'mdpi' = 1.0; 'hdpi' = 1.5; 'xhdpi' = 2.0; 'xxhdpi' = 3.0; 'xxxhdpi' = 4.0 }
    foreach ($density in $densityScale.Keys) {
        $d = $densityScale[$density]
        $canvas = [int](108 * $d)
        $b = New-Object System.Drawing.Bitmap($canvas, $canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $sc = $iconScale * $d
        $dstX = (54.0 * $d) - ($artCx * $sc)
        $dstY = (54.0 * $d) - ($artCy * $sc)
        $g.DrawImage($source, [float]$dstX, [float]$dstY, [float]($source.Width * $sc), [float]($source.Height * $sc))
        $g.Dispose()
        SavePng $b (Join-Path $androidRes "mipmap-$density\ic_launcher_foreground.png")
    }

    # 3) In-app mark: the square master itself, at native size (density-independent).
    $nodpi = Join-Path $androidRes 'drawable-nodpi'
    New-Item -ItemType Directory -Path $nodpi -Force | Out-Null
    Copy-Item $Png (Join-Path $nodpi 'ic_lantern.png') -Force
    Write-Host "png   -> $(Join-Path $nodpi 'ic_lantern.png')"

    # 4) Android TV banner (320x180dp at xhdpi = 640x360): the Lantern background with the artwork
    #    centred, beam included.
    $bannerW = 640; $bannerH = 360
    $banner = New-Object System.Drawing.Bitmap($bannerW, $bannerH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($banner)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point($bannerW, $bannerH)),
        [System.Drawing.Color]::FromArgb(255, 0x0B, 0x10, 0x20), [System.Drawing.Color]::FromArgb(255, 0x72, 0x60, 0xF3))
    $g.FillRectangle($bg, 0, 0, $bannerW, $bannerH)
    $bg.Dispose()
    $bannerScale = [Math]::Min(500.0 / $artW, 300.0 / $artH)
    $dstX = ($bannerW / 2.0) - ($artCx * $bannerScale)
    $dstY = ($bannerH / 2.0) - ($artCy * $bannerScale)
    $g.DrawImage($source, [float]$dstX, [float]$dstY, [float]($source.Width * $bannerScale), [float]($source.Height * $bannerScale))
    $g.Dispose()
    SavePng $banner (Join-Path $androidRes 'drawable-xhdpi\tv_banner.png')

    # Remove the old vector re-draws: the canonical logo is a raster now.
    foreach ($stale in @(
        (Join-Path $androidRes 'drawable\ic_lantern.xml'),
        (Join-Path $androidRes 'drawable\ic_launcher_foreground.xml'),
        (Join-Path $androidRes 'drawable\tv_banner.xml'),
        (Join-Path $androidRes 'mipmap\ic_launcher.xml')
    )) {
        if (Test-Path $stale) { Remove-Item $stale -Force; Write-Host "rm    -> $stale" }
    }
}
finally {
    $source.Dispose()
}
