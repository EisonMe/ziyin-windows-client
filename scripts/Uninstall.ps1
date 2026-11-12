#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param([switch]$KeepBinaries)
$ErrorActionPreference = 'Stop'
$install = "$env:ProgramFiles\ZiYin"
if (-not $PSCmdlet.ShouldProcess('ZiYinRemote', '停止并卸载服务、托盘启动项和防火墙规则')) { return }
$service = Get-Service ZiYinRemote -ErrorAction SilentlyContinue
if ($service) {
    $registered = Get-CimInstance Win32_Service -Filter "Name='ZiYinRemote'"
    if ($registered.PathName -ne ('"' + $install + '\service\ZiYin.Service.exe"')) { throw '同名服务路径不匹配，拒绝卸载。' }
    Stop-Service ZiYinRemote -Force
    (Get-Service ZiYinRemote).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    & sc.exe delete ZiYinRemote | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '删除服务失败。' }
}
Get-Process -Name ZiYin.Tray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$install\tray\ZiYin.Tray.exe" } | Stop-Process -Force
Remove-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ZiYinTray -ErrorAction SilentlyContinue
Get-NetFirewallRule -Name ZiYinRemote -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $KeepBinaries -and (Test-Path -LiteralPath $install)) {
    if ((Get-Item -LiteralPath $install -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装目录是链接，拒绝递归删除。' }
    Remove-Item -LiteralPath $install -Recurse -Force
}
Write-Host '已卸载服务和登录启动项。ProgramData\ZiYin 中的密钥、文件、配置与日志完整保留。'
Write-Host '自行导入的 TLS 信任证书也保留，请按证书指纹在客户端按需移除。'
