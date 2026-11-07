param(
    [string]$FfmpegRoot = 'C:/ffmpeg',
    [switch]$DownloadFfmpegOnSetup
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $root ('.verification/package-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$dest = Join-Path $root 'dist/Lantern'
$zip = Join-Path $root 'dist/Lantern-win-x64.zip'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location $root
try {
    $relay = Join-Path $root 'tools/mediamtx/mediamtx.exe'
    if (!(Test-Path -LiteralPath $relay)) {
        $relayZip = Join-Path $stage 'mediamtx-download.zip'
        $relayUrl = 'https://github.com/bluenviron/mediamtx/releases/download/v1.21.0/mediamtx_v1.21.0_windows_amd64.zip'
        & curl.exe --fail --location --retry 2 --output $relayZip $relayUrl
        if ($LASTEXITCODE -or (Get-FileHash -LiteralPath $relayZip).Hash -ne '8A58A9B8C25EE99A96C23DC0A17F39ACE3072C01D2E148329073C64DDF83493D') {
            throw 'MediaMTX download could not be verified.'
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($relayZip)
        try {
            $entry = $archive.GetEntry('mediamtx.exe')
            if ($null -eq $entry) { throw 'MediaMTX executable missing from archive.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $relay)
        } finally { $archive.Dispose() }
        Remove-Item -LiteralPath $relayZip
    }
    & dotnet publish windows/Lantern.Desktop/Lantern.Desktop.csproj -c Release -r win-x64 --self-contained true -o $stage --nologo -v:q
    if ($LASTEXITCODE) { throw 'Desktop publish failed' }
    & dotnet publish server/Lantern.Server/Lantern.Server.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'server') --nologo -v:q
    if ($LASTEXITCODE) { throw 'Server publish failed' }
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet -CommandType Application).Source
    $notices = Join-Path $stage 'Third-party'
    New-Item -ItemType Directory -Path $notices | Out-Null
    foreach ($file in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        Copy-Item -LiteralPath (Join-Path $dotnetRoot $file) -Destination (Join-Path $notices ".NET-$file")
    }
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    foreach ($notice in @(
        @{ Path='naudio/2.2.1/license.txt'; Name='NAudio-LICENSE.txt' },
        @{ Path='sqlite/3.53.4/LICENSE.txt'; Name='SQLite-LICENSE.txt' }
    )) {
        Copy-Item -LiteralPath (Join-Path $packages $notice.Path) -Destination (Join-Path $notices $notice.Name)
    }
    if (!$DownloadFfmpegOnSetup) {
        $ffdir = Join-Path $stage 'server/ffmpeg'
        New-Item -ItemType Directory -Path $ffdir -Force | Out-Null
        foreach ($name in @('ffmpeg.exe','ffprobe.exe','LICENSE','README.txt')) {
            $source = if ($name.EndsWith('.exe')) { "bin/$name" } else { $name }
            Copy-Item -LiteralPath (Join-Path $FfmpegRoot $source) -Destination $ffdir
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'tools/mediamtx/LICENSE') -Destination (Join-Path $stage 'server/mediamtx/LICENSE.txt')
    foreach ($name in @('Allow-Lantern-LAN.ps1','Setup-Lantern.ps1','Set up Lantern.cmd')) {
        Copy-Item -LiteralPath (Join-Path $root "tools/$name") -Destination $stage
    }
    @'
Lantern - Windows x64
1. Extract the whole ZIP to a folder you can write to, such as Documents\Lantern.
2. Double-click "Set up Lantern". It downloads the streaming tools once if needed.
3. Approve Windows' prompt to allow Lantern on your private home network.
4. Install the Android APK and open Lantern. On the PC, open Devices, click Connect next
   to the receiver, then select Yes on the receiver.
5. On the PC, start Screen Mirror. On Android, open Screen Mirror and select Start receiving.

Next time, open Lantern.Desktop.exe. Run setup again if you move the folder.
Use 720p at 30 FPS if motion is choppy. System sound is included by default.
To share videos, add a folder in the PC app. Some files need time to prepare before playback.
Keep the PC and Android device on the same trusted home network.
'@ | Set-Content -LiteralPath (Join-Path $stage 'START-HERE.txt') -Encoding UTF8
    $required = @('Lantern.Desktop.exe','coreclr.dll','server/Lantern.Server.exe','server/coreclr.dll','server/mediamtx/mediamtx.exe','Set up Lantern.cmd')
    if (!$DownloadFfmpegOnSetup) { $required += @('server/ffmpeg/ffmpeg.exe','server/ffmpeg/ffprobe.exe') }
    foreach ($path in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $path))) { throw "Missing required package file: $path" }
    }
    $manifest = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'BUILD-MANIFEST.json') -Encoding UTF8
    $zipStage = "$stage.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipStage, [IO.Compression.CompressionLevel]::Optimal, $false)
    # Preserve the previous distribution. Both resolved targets must remain inside this checkout.
    $backup = Join-Path $root ('.verification/previous-dist-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($path in @($stage,$dest,$backup)) {
        if (![IO.Path]::GetFullPath($path).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe distribution path' }
    }
    if (Test-Path -LiteralPath $dest) { Move-Item -LiteralPath $dest -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $dest
    Move-Item -LiteralPath $zipStage -Destination $zip -Force
    Write-Output "Published self-contained Windows distribution: $dest"
} finally { Pop-Location }
