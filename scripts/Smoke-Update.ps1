#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Only run on disposable GitHub Actions runners.' }
$root = Split-Path $PSScriptRoot -Parent
$package = "$root\artifacts\ziyin-windows-client-win-x64"
$install = "$env:ProgramFiles\ZiYin"
$data = "$env:ProgramData\ZiYin\service"
$headers = $null
$lock = $null
$trust = $null
try {
    & "$package\scripts\Install.ps1" -DisableAutoUpdate -GenerateTls
    $trust=Import-Certificate -FilePath "$env:ProgramData/ZiYin/server.cer" -CertStoreLocation Cert:\LocalMachine\Root
    $key = (Get-Content "$data\api-key.json" -Raw | ConvertFrom-Json).ApiKey
    $headers = @{Authorization="Bearer $key";'X-ZiYin-Request'='1'}
    # Build a real older service from the same sources to exercise versioned binary replacement.
    dotnet publish "$root/src/ZiYin.Service/ZiYin.Service.csproj" -c Release -r win-x64 --self-contained true -p:Version=1.0.0 -o "$root/artifacts/update-baseline"
    if ($LASTEXITCODE -ne 0) { throw 'Baseline build failed' }
    (Get-Service ZiYinRemote).Stop()
    (Get-Service ZiYinRemote).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    Copy-Item "$root/artifacts/update-baseline/*" "$install/service" -Recurse -Force
    [IO.File]::WriteAllText("$install/VERSION",'1.0.0')
    (Get-Service ZiYinRemote).Start()
    (Get-Service ZiYinRemote).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    $health = Invoke-RestMethod 'https://127.0.0.1:17643/api/health' -Headers $headers -TimeoutSec 15
    if ($health.version -ne '1.0.0') { throw 'Baseline version mismatch' }
    $before = @{}
    foreach ($path in @("$data/service.json","$data/api-key.json","$data/server.pfx","$env:ProgramData/ZiYin/files/smoke.txt")) {
        if (Test-Path $path) { $before[$path]=(Get-FileHash $path -Algorithm SHA256).Hash }
    }
    $work = "$data\updates\work-ci"
    New-Item -ItemType Directory -Path "$work\worker" -Force | Out-Null
    Copy-Item "$package/updater/*" "$work/worker" -Recurse -Force
    foreach ($file in @('update.json','update.json.sig','ziyin-windows-client-win-x64.zip')) { Copy-Item "$root/artifacts/$file" $work -Force }
    # A real file lock causes rename failure. The helper must roll back without losing service/config.
    $lock = [IO.File]::Open("$install/service/ZiYin.Service.dll",[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $command = '"' + $work + '\worker\ZiYin.Updater.exe" apply "' + $work + '" "' + $data + '"'
    & schtasks.exe /Create /TN ZiYinUpdateSmoke /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /TR $command /F | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create smoke task' }
    & schtasks.exe /Run /TN ZiYinUpdateSmoke | Out-Null
    for ($i=0;$i -lt 120;$i++) {
        if (Test-Path "$data/updates/result.json") { $result=Get-Content "$data/updates/result.json" -Raw | ConvertFrom-Json; if ($result.Status -eq 'rolled_back') { break } }
        Start-Sleep -Seconds 1
    }
    if (-not (Test-Path "$data/updates/result.json") -or $result.Status -ne 'rolled_back') { throw 'Locked binary did not produce a successful rollback' }
    $lock.Dispose(); $lock=$null
    $health=Invoke-RestMethod 'https://127.0.0.1:17643/api/health' -Headers $headers -TimeoutSec 15
    if ($health.version -ne '1.0.0') { throw 'Old service not running after rollback' }
    Write-Host 'PASS: real helper/SCM locked-binary failure rolled back to working 1.0.0.'
    # Re-run same trusted package after removing the lock.
    for ($i=0;$i -lt 20;$i++) { if ((Get-ScheduledTask -TaskName ZiYinUpdateSmoke).State -ne 'Running') { break }; Start-Sleep -Seconds 1 }
    Remove-Item "$data/updates/result.json"
    & schtasks.exe /Run /TN ZiYinUpdateSmoke | Out-Null
    for ($i=0;$i -lt 120;$i++) {
        if (Test-Path "$data/updates/result.json") { $result=Get-Content "$data/updates/result.json" -Raw | ConvertFrom-Json; if ($result.Status -eq 'updated') { break } }
        Start-Sleep -Seconds 1
    }
    if (-not (Test-Path "$data/updates/result.json") -or $result.Status -ne 'updated') { throw 'Signed update helper failed' }
    $health=Invoke-RestMethod 'https://127.0.0.1:17643/api/health' -Headers $headers -TimeoutSec 15
    $target=(Get-Content "$root/VERSION" -Raw).Trim()
    if ($health.version -ne $target) { throw 'Updated service version mismatch' }
    foreach ($path in $before.Keys) { if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $before[$path]) { throw 'Update changed protected configuration or user data' } }
    $updates=Invoke-RestMethod 'https://127.0.0.1:17643/api/updates' -Headers $headers -TimeoutSec 15
    if ($updates.currentVersion -ne $target -or $updates.lastResult.status -ne 'updated') { throw 'Update status API mismatch' }
    Write-Host "PASS: signed helper upgraded 1.0.0 to $target; config/API key/certificate/user-file hashes unchanged; status API correct."
} finally {
    if ($lock) { $lock.Dispose() }
    if ($headers) { $headers.Clear() };$key=$null
    for ($i=0;$i -lt 30;$i++) { $task=Get-ScheduledTask -TaskName ZiYinUpdateSmoke -ErrorAction SilentlyContinue; if (-not $task -or $task.State -ne 'Running') { break }; Start-Sleep -Seconds 1 }
    if ($task) { Unregister-ScheduledTask -TaskName ZiYinUpdateSmoke -Confirm:$false }
    & "$package/scripts/Uninstall.ps1" -Confirm:$false
    if ($trust) { Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$($trust.Thumbprint)" }
}
