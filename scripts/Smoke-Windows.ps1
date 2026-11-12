# Intended for an ephemeral elevated GitHub Windows runner, not a production machine.
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true') { throw '此脚本只用于临时 GitHub Actions runner。真实机器请按 README 手动验收。' }
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'artifacts\ziyin-windows-client-win-x64'
$base = 'http://127.0.0.1:17643'
$headers = $null
$trustedCert = $null
try {
    Write-Host 'Smoke phase: install/upgrade service'
    & "$package\scripts\Install.ps1" -DisableAutoUpdate
    # Simulate a legacy admin-owned key with an explicit Users read grant; upgrade must remove it.
    $keyForUpgrade = "$env:ProgramData\ZiYin\service\api-key.json"
    for ($i=0; $i -lt 30 -and -not (Test-Path $keyForUpgrade); $i++) { Start-Sleep -Seconds 1 }
    $legacyAcl = Get-Acl $keyForUpgrade
    $legacyAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Read', 'Allow'))
    Set-Acl -LiteralPath $keyForUpgrade -AclObject $legacyAcl
    Write-Host 'Smoke phase: install/upgrade service'
    & "$package\scripts\Install.ps1" -DisableAutoUpdate
    $service = Get-CimInstance Win32_Service -Filter "Name='ZiYinRemote'"
    if ($service.StartMode -ne 'Auto' -or $service.StartName -ne 'LocalSystem') { throw 'Service identity/startup mismatch' }
    $keyPath = "$env:ProgramData\ZiYin\service\api-key.json"
    for ($i=0; $i -lt 30 -and -not (Test-Path $keyPath); $i++) { Start-Sleep -Seconds 1 }
    $key = (Get-Content $keyPath -Raw | ConvertFrom-Json).ApiKey
    # Neither the key nor request headers are emitted to logs.
    $headers = @{Authorization="Bearer $key"; 'X-ZiYin-Request'='1'}
    $health = Invoke-RestMethod -TimeoutSec 15 "$base/api/health" -Headers $headers
    if ($health.status -ne 'ok') { throw 'Health failed' }
    $unauth = Invoke-WebRequest -TimeoutSec 15 "$base/api/health" -SkipHttpErrorCheck
    if ([int]$unauth.StatusCode -ne 401) { throw 'Unauthenticated API was not rejected' }
    $html = Invoke-WebRequest -TimeoutSec 15 "$base/"
    if ($html.Content -notmatch 'Windows' -or $html.StatusCode -ne 200) { throw 'WebUI missing' }
    $docs = Invoke-WebRequest -TimeoutSec 15 "$base/API.zh-CN.md"
    if ($docs.Content -notmatch 'PowerShell') { throw 'API docs missing from package' }
    Write-Host 'Smoke phase: submit SYSTEM command'
    $task = Invoke-RestMethod -TimeoutSec 15 "$base/api/tasks" -Method Post -Headers $headers -ContentType application/json -Body '{"command":"whoami; Write-Output service-smoke","timeoutSeconds":60}'
    for ($i=0; $i -lt 140; $i++) {
        $state = Invoke-RestMethod -TimeoutSec 15 "$base/api/tasks/$($task.id)" -Headers $headers
        if ($state.finishedAt) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($state.status -ne 'completed' -or $state.standardOutput -notmatch 'service-smoke' -or $state.standardOutput -notmatch 'system') { Write-Host ($state | ConvertTo-Json -Depth 5); throw 'SCM service task failed' }
    Invoke-RestMethod -TimeoutSec 15 "$base/api/files/content?path=smoke.txt" -Method Put -Headers $headers -ContentType text/plain -Body 'smoke-file' | Out-Null
    $content = Invoke-WebRequest -TimeoutSec 15 "$base/api/files/content?path=smoke.txt" -Headers $headers
    $bodyText = if ($content.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content.Content) } else { $content.Content }
    if ($bodyText -ne 'smoke-file') { throw 'File roundtrip failed' }
    $desktop = Invoke-RestMethod -TimeoutSec 15 "$base/api/desktop/status" -Headers $headers
    if ($desktop.available) { throw 'Desktop must not be enabled without a tray consent' }
    $acl = Get-Acl $keyPath
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'API key ACL permits an unexpected principal' }
    }
    # Restart verifies persistent key and deliberately non-persistent task history.
    Write-Host 'Smoke phase: restart service'
    (Get-Service ZiYinRemote).Stop()
    (Get-Service ZiYinRemote).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    (Get-Service ZiYinRemote).Start()
    (Get-Service ZiYinRemote).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    for ($i=0; $i -lt 20; $i++) {
        try { $health = Invoke-RestMethod -TimeoutSec 15 "$base/api/health" -Headers $headers; break } catch { Start-Sleep -Seconds 1 }
    }
    if ($health.status -ne 'ok') { throw 'Service restart failed' }
    $tasks = Invoke-RestMethod -TimeoutSec 15 "$base/api/tasks" -Headers $headers
    if ($tasks.Count -ne 0) { throw 'Task records unexpectedly persisted' }
    # Validate actual TLS with normal certificate verification on the disposable runner.
    Write-Host 'Smoke phase: install TLS and firewall'
    & "$package\scripts\Install.ps1" -DisableAutoUpdate -EnableLan -GenerateTls
    Write-Host 'Smoke phase: trust temporary certificate in LocalMachine root store'
    $trustedCert = Import-Certificate -FilePath "$env:ProgramData\ZiYin\server.cer" -CertStoreLocation Cert:\LocalMachine\Root
    Write-Host 'Smoke phase: request HTTPS with certificate verification'
    $tlsHealth = Invoke-RestMethod -TimeoutSec 15 'https://127.0.0.1:17643/api/health' -Headers $headers
    if ($tlsHealth.status -ne 'ok') { throw 'Verified TLS health failed' }
    $firewall = Get-NetFirewallRule -Name ZiYinRemote
    if ($firewall.Profile -ne 'Private') { throw 'LAN firewall profile mismatch' }
    $filter = $firewall | Get-NetFirewallAddressFilter
    if ($filter.RemoteAddress -notcontains 'LocalSubnet') { throw 'LAN firewall source is not restricted' }
    Write-Host 'PASS: TLS certificate validation and Private/LocalSubnet firewall.'
    Write-Host 'PASS: SCM automatic LocalSystem service, authenticated health, unauthenticated rejection, WebUI/docs, PowerShell task, file roundtrip, desktop default deny, secret ACL, service restart.'
} finally {
    if ($headers) { $headers.Clear() }; $key = $null
    & "$package\scripts\Uninstall.ps1" -Confirm:$false
    if ($trustedCert) { Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$($trustedCert.Thumbprint)" }
}
if (Get-Service ZiYinRemote -ErrorAction SilentlyContinue) { throw 'Service still exists after uninstall' }
if (-not (Test-Path "$env:ProgramData\ZiYin\files\smoke.txt")) { throw 'Uninstall deleted user data' }
if (Get-NetFirewallRule -Name ZiYinRemote -ErrorAction SilentlyContinue) { throw 'Firewall rule remained after uninstall' }
Write-Host 'PASS: uninstall removed service/firewall and retained user data.'
