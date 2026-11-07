param([int]$Seconds = 25, [string]$Adb = 'D:/Android/sdk/platform-tools/adb.exe')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location $root
$api = 'http://localhost:8787/api/v1/mirror'
try {
    foreach ($mode in @(@(1280,720,30), @(1280,720,60), @(1920,1080,30), @(1920,1080,60))) {
        $label = "$($mode[1])p$($mode[2])"
        $body = @{width=$mode[0];height=$mode[1];fps=$mode[2];audio=$true} | ConvertTo-Json
        $started = Invoke-RestMethod "$api/start" -Method Post -ContentType application/json -Body $body -TimeoutSec 180
        if (!$started.active) { throw "Mirror $label failed to start" }
        Write-Output "MODE $label"
        & $Adb shell am instrument -w -e mode mirror -e seconds $Seconds com.lantern.tv.test/com.lantern.tv.VerificationRunner |
            Tee-Object ".verification/android-mirror-$label.txt"
        & python tools/smoke/cadence-probe.py --output ".verification/cadence-$label.json" > ".verification/cadence-$label.txt"
        if ($LASTEXITCODE -ne 0) { throw "PTS probe failed for $label" }
        Invoke-RestMethod "$api/stop" -Method Post -TimeoutSec 30 | Out-Null
    }
} finally { Invoke-RestMethod "$api/stop" -Method Post -TimeoutSec 30 | Out-Null }
Write-Output 'Matrix complete. Inspect failures and receiver counters; completion alone is not a pass.'
