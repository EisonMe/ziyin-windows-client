#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateRange(1024,65535)][int]$Port = 17643,
    [switch]$EnableLan,
    [switch]$TrustedLanHttp,
    [switch]$GenerateTls,
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [string]$InstallDirectory = "$env:ProgramFiles\ZiYin"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($EnableLan -and -not ($TrustedLanHttp -or $GenerateTls -or $CertificatePath)) { throw '开放 LAN 需要 -GenerateTls、-CertificatePath 或显式 -TrustedLanHttp。' }
if ($GenerateTls -and $CertificatePath) { throw 'GenerateTls 和 CertificatePath 不能同时使用。' }
$source = Split-Path $PSScriptRoot -Parent
$expected = [IO.Path]::GetFullPath("$env:ProgramFiles\ZiYin")
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ($InstallDirectory -ne $expected) { throw '正式安装目录必须为 Program Files\ZiYin，以保护服务和托盘二进制。' }
if (-not (Test-Path "$source\service\ZiYin.Service.exe") -or -not (Test-Path "$source\tray\ZiYin.Tray.exe")) { throw '请先解压完整 Windows artifact，再运行其中的 scripts\Install.ps1。' }
$base = "$env:ProgramData\ZiYin"
$data = "$base\service"
$files = "$base\files"
function Assert-NoLink([string]$Path) {
    if ((Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "安装目录不允许重解析点：$Path" }
}
foreach ($path in @($base,$data,$files,$InstallDirectory)) { Assert-NoLink $path }
# Validate BEFORE changing ACLs: never adopt keys/binaries planted by an ordinary user.
function Assert-TrustedTree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $items = @((Get-Item -LiteralPath $Path -Force)) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        Assert-NoLink $item.FullName
        $acl = Get-Acl -LiteralPath $item.FullName
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-18','S-1-5-32-544')) { throw "已有目录/文件所有者不可信，请管理员另行检查或备份改名：$($item.FullName)" }
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
            if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writeMask) -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544','S-1-3-0')) { throw "已有目录/文件允许普通账户写入，拒绝接管：$($item.FullName)" }
        }
    }
}
foreach ($path in @($base,$InstallDirectory)) { Assert-TrustedTree $path }
function Protect-Directory([string]$Path, [bool]$AllowRead) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($AllowRead) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Protect-Directory $base $true
Protect-Directory $data $false
Protect-Directory $files $false
Protect-Directory $InstallDirectory $true
$existing = Get-Service -Name ZiYinRemote -ErrorAction SilentlyContinue
if ($existing) {
    $registered = Get-CimInstance Win32_Service -Filter "Name='ZiYinRemote'"
    if ($registered.PathName -ne ('"' + $InstallDirectory + '\service\ZiYin.Service.exe"')) { throw '发现同名服务但安装路径不一致，停止覆盖。' }
    Stop-Service ZiYinRemote -Force
    (Get-Service ZiYinRemote).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
}
Get-Process -Name ZiYin.Tray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$InstallDirectory\tray\ZiYin.Tray.exe" } | Stop-Process -Force
if ($source -ne $InstallDirectory) {
    foreach ($folder in @('service','tray','scripts','docs')) { Copy-Item -LiteralPath "$source\$folder" -Destination $InstallDirectory -Recurse -Force }
    Copy-Item "$source\README.md" $InstallDirectory -Force
}
# Reset inherited file ACLs; extracted binaries must never retain user-writable ACLs.
& icacls.exe $InstallDirectory /reset /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '安装文件 ACL 设置失败。' }
Protect-Directory $InstallDirectory $true
$plainPassword = $null
$certificateTarget = $null
if ($GenerateTls) {
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.AddressState -eq 'Preferred' } | Select-Object -ExpandProperty IPAddress)
    $san = '2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1'
    foreach ($ip in $addresses) { $san += "&IPAddress=$ip" }
    $cert = New-SelfSignedCertificate -Subject 'CN=ZiYin Windows Client' -TextExtension $san -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(1)
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $plainPassword = [Convert]::ToBase64String($bytes)
    $certificateTarget = "$data\server.pfx"
    Export-PfxCertificate -Cert $cert -FilePath $certificateTarget -Password (ConvertTo-SecureString $plainPassword -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath "$base\server.cer" | Out-Null
    Write-Host "TLS 公钥证书：$base\server.cer"
    Write-Host "证书指纹：$($cert.Thumbprint)；客户端核对后自行导入信任，禁止忽略证书验证。"
    Remove-Item -LiteralPath "Cert:\LocalMachine\My\$($cert.Thumbprint)"
} elseif ($CertificatePath) {
    $certificateTarget = "$data\server.pfx"
    if ([IO.Path]::GetFullPath($CertificatePath) -ne $certificateTarget) { Copy-Item -LiteralPath $CertificatePath -Destination $certificateTarget -Force }
    if ($CertificatePassword) { $plainPassword = [Net.NetworkCredential]::new('', $CertificatePassword).Password }
}
$settings = @{
    Port=$Port; AllowRemote=[bool]$EnableLan; TrustedLanHttp=[bool]$TrustedLanHttp
    CertificatePath=$certificateTarget; CertificatePassword=$plainPassword
    DataDirectory=$data; FileRoot=$files
}
$settings | ConvertTo-Json | Set-Content -LiteralPath "$data\service.json" -Encoding UTF8
@{port=$Port; scheme=$(if ($certificateTarget) {'https'} else {'http'})} | ConvertTo-Json | Set-Content -LiteralPath "$base\client.json" -Encoding UTF8
if (-not $existing) {
    New-Service -Name ZiYinRemote -BinaryPathName ('"' + $InstallDirectory + '\service\ZiYin.Service.exe"') -DisplayName '紫音 Windows 远程服务' -StartupType Automatic -Description '认证 LAN API 与后台任务执行；独立于登录会话运行。' | Out-Null
} else { Set-Service ZiYinRemote -StartupType Automatic }
& sc.exe failure ZiYinRemote reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw '服务恢复策略设置失败。' }
$runPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runPath -Name ZiYinTray -Value ('"' + $InstallDirectory + '\tray\ZiYin.Tray.exe"')
Get-NetFirewallRule -Name ZiYinRemote -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($EnableLan) {
    New-NetFirewallRule -Name ZiYinRemote -DisplayName 'ZiYin Remote LAN API' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Private -RemoteAddress LocalSubnet -Program "$InstallDirectory\service\ZiYin.Service.exe" | Out-Null
}
Start-Service ZiYinRemote
(Get-Service ZiYinRemote).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
Start-Sleep -Seconds 2
if ((Get-Service ZiYinRemote).Status -ne 'Running') { throw '服务未持续运行，请检查配置与端口占用。' }
Write-Host "安装完成。服务：ZiYinRemote（LocalSystem，自动启动）。"
Write-Host "浏览器打开：$(if ($certificateTarget) {'https'} else {'http'})://localhost:$Port/"
Write-Host '首次登录密钥：管理员运行 scripts\Get-ApiKey.ps1，在本机窗口查看。'
Write-Host '托盘会在每次登录启动。现在请从普通用户会话运行 tray\ZiYin.Tray.exe，不要以管理员运行托盘。'
if ($TrustedLanHttp -and -not $certificateTarget) { Write-Warning '当前为 HTTP 明文，仅允许在完全可信的隔离 LAN 使用；密钥与命令可能被同网段监听。' }
