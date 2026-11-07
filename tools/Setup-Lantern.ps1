param([switch]$SkipFirewall, [switch]$SkipLaunch)
$ErrorActionPreference = 'Stop'

$desktop = Join-Path $PSScriptRoot 'Lantern.Desktop.exe'
$server = Join-Path $PSScriptRoot 'server'
$ffmpeg = Join-Path $server 'ffmpeg'
if (!(Test-Path -LiteralPath $desktop)) {
    throw 'Extract the full Lantern ZIP before running setup.'
}

if (!(Test-Path -LiteralPath (Join-Path $ffmpeg 'ffmpeg.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $ffmpeg 'ffprobe.exe'))) {
    Write-Host 'Downloading the streaming tools (about 110 MB, first run only)…'
    $name = 'ffmpeg-2026-09-17-git-7070fe638e-essentials_build'
    $url = "https://github.com/GyanD/codexffmpeg/releases/download/2026-09-17-git-7070fe638e/$name.zip"
    $expectedHash = 'B13DF71593C3F56DDFB20A8C38B1ACBCC4DA9AE5DC9E9D929260AAD5BE626F02'
    $stage = Join-Path $server ('.ffmpeg-setup-' + [Guid]::NewGuid().ToString('N'))
    $archivePath = Join-Path $stage 'download.zip'
    $payload = Join-Path $stage 'ffmpeg'
    New-Item -ItemType Directory -Path $payload | Out-Null
    try {
        & "$env:SystemRoot\System32\curl.exe" --fail --location --retry 2 --connect-timeout 15 --max-time 600 --progress-bar --output $archivePath $url
        if ($LASTEXITCODE -ne 0) { throw 'Download failed. Check your Internet connection and run setup again.' }
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
            throw 'The streaming download could not be verified. Run setup again.'
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($file in @('bin/ffmpeg.exe', 'bin/ffprobe.exe', 'LICENSE', 'README.txt')) {
                $entry = $archive.GetEntry("$name/$file")
                if ($null -eq $entry) { throw 'The verified download is missing a required streaming file.' }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $payload ([IO.Path]::GetFileName($file))))
            }
        } finally { $archive.Dispose() }

        if (!(Test-Path -LiteralPath $ffmpeg)) { [IO.Directory]::Move($payload, $ffmpeg) }
        else {
            foreach ($file in @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE', 'README.txt')) {
                $target = Join-Path $ffmpeg $file
                if (!(Test-Path -LiteralPath $target)) { [IO.File]::Move((Join-Path $payload $file), $target) }
            }
        }
    } finally {
        $parent = (Resolve-Path -LiteralPath $server).Path
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $parent -and
            [IO.Path]::GetFileName($resolvedStage) -match '^\.ffmpeg-setup-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}

if (!$SkipFirewall) {
    Write-Host 'Windows will ask to allow Lantern on your private local network.'
    $firewall = Join-Path $PSScriptRoot 'Allow-Lantern-LAN.ps1'
    try {
        $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$firewall`""
        if ($process.ExitCode -ne 0) { Write-Warning 'Network setup failed. Run setup again if your receiver cannot find the PC.' }
    } catch {
        Write-Warning 'Network setup was skipped. Run setup again if your receiver cannot find the PC.'
    }
}

if (!$SkipLaunch) {
    Write-Host 'Opening Lantern…'
    Start-Process -FilePath $desktop -WorkingDirectory $PSScriptRoot
}
