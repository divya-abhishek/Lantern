#Requires -RunAsAdministrator
# Run once from the final Lantern distribution folder. Safe to rerun after moving it.
param([int]$ApiPort = 8787)
$ErrorActionPreference = 'Stop'
$server = Join-Path $PSScriptRoot 'server/Lantern.Server.exe'
$relay = Join-Path $PSScriptRoot 'server/mediamtx/mediamtx.exe'
foreach ($file in @($server, $relay)) { if (!(Test-Path -LiteralPath $file)) { throw "Missing packaged dependency: $file" } }
$rules = @(
    @{ Name='Lantern-API'; Program=$server; Protocol='TCP'; LocalPort=$ApiPort },
    @{ Name='Lantern-Discovery'; Program=$server; Protocol='UDP'; LocalPort=@(8788,5353) },
    @{ Name='Lantern-RTSP'; Program=$relay; Protocol='TCP'; LocalPort='Any' }
)
foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
    if ($existing) { $existing | Remove-NetFirewallRule }
    New-NetFirewallRule @rule -DisplayName $rule.Name -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
Write-Output 'Lantern is allowed on Private local networks. No Public-network or Internet rule was added.'
