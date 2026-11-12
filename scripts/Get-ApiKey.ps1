#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$keyFile = "$env:ProgramData\ZiYin\service\api-key.json"
if (-not (Test-Path -LiteralPath $keyFile)) { throw '未找到密钥文件，请确认服务已安装并成功启动。' }
# Show in a local dialog, never stdout, transcripts, process arguments, URLs, or clipboard.
Add-Type -AssemblyName System.Windows.Forms
$data = Get-Content -LiteralPath $keyFile -Raw | ConvertFrom-Json
$form = New-Object Windows.Forms.Form
$form.Text = '紫音 API 密钥 · 请勿分享给不可信的人'
$form.Width = 620; $form.Height = 160; $form.StartPosition = 'CenterScreen'
$text = New-Object Windows.Forms.TextBox
$text.Text = $data.ApiKey; $text.ReadOnly = $true; $text.UseSystemPasswordChar = $true
$text.SetBounds(15,15,570,28)
$button = New-Object Windows.Forms.Button
$button.Text = '显示 / 隐藏'; $button.SetBounds(15,55,140,30)
$button.Add_Click({ $text.UseSystemPasswordChar = -not $text.UseSystemPasswordChar })
$form.Controls.Add($text); $form.Controls.Add($button)
[void]$form.ShowDialog()
$text.Text = ''; $data = $null; $form.Dispose()
