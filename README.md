# 紫音 Windows 远程客户端

.NET 10 Windows Service + 独立 WinForms 托盘 + 中文 WebUI。管理员安装一次，服务开机自动运行，认证局域网 API 在未登录或锁屏时仍可执行后台 PowerShell 任务。托盘随用户登录启动，右键打开 WebUI。

本次实现以最新 LAN API 需求为准；`DESIGN-draft.md` 仅保留历史草案，不使用其中的 WSS/配对流程，也没有虚构 QQ/AstrBot 聊天连接已验收。

## 下载与安装

1. 打开本仓库 **Actions → Windows build, test and package → 成功运行 → Artifacts**，下载 `ziyin-windows-client-win-x64`。解压 artifact，再解压其中的 zip。只支持 Windows 10/11、Windows Server x64，需有 Windows PowerShell 5.1；程序已自包含 .NET 运行时。
2. 用管理员 PowerShell 进入解压目录，任选一种安装方式：

```powershell
# 默认只监听本机，适合先验收
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1

# 推荐：开放 LAN，生成 TLS 证书
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1 -EnableLan -GenerateTls

# 已有 PFX：在管理员 PowerShell 中安全输入证书密码
$password = Read-Host 'PFX password' -AsSecureString
.\scripts\Install.ps1 -EnableLan -CertificatePath C:\secure\server.pfx -CertificatePassword $password

# 明确接受 HTTP 明文风险，仅限完全可信的隔离局域网
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1 -EnableLan -TrustedLanHttp
```

3. TLS 模式把 `%ProgramData%\ZiYin\server.cer` 交给客户端，先核对安装时显示的指纹再导入受信任根证书。证书包含安装时 IPv4 与 localhost，IP 改变或证书到期需重新生成/换证书。自签名证书在信任前浏览器会报错，不能把忽略验证当成解决方式。已有证书必须覆盖所用 IP/localhost；本版限制 Host 为 localhost 或本机实际 IP，不支持任意 DNS 别名。
4. 在普通用户会话启动 `%ProgramFiles%\ZiYin\tray\ZiYin.Tray.exe`，或注销后重新登录。托盘右键打开 WebUI。不要以管理员身份运行托盘；它不需要提权。安装过程不从提升会话自动启动托盘，避免继承管理员权限。
5. 托盘右键「查看 API 密钥」，通过正常 UAC 确认后在本机窗口查看并登录。也可管理员运行 `%ProgramFiles%\ZiYin\scripts\Get-ApiKey.ps1`。默认隐藏，主动点击才显示。不要把密钥发到聊天或加入 URL。

安装目录固定 `%ProgramFiles%\ZiYin`，防普通用户替换服务/托盘程序；服务名称 `ZiYinRemote`，LocalSystem 自动启动。防火墙仅在显式 `-EnableLan` 时添加 Private 网络、LocalSubnet 来源的入站规则，默认端口 17643；可用 `-Port` 修改。不会开公网端口或修改路由器。网络配置为 Public 时 LAN 规则不放行。

已有安装再次运行脚本会停止并更新服务、托盘和连接配置，保留密钥与用户文件；需要重新带上原 LAN/TLS 参数。升级前请安排维护窗口，运行中任务会终止。首次安装/升级后按下方清单验收。

## 功能和权限

- WebUI 显示实际网卡 IP、端口、监听状态，密钥默认隐藏，可随机轮换或改为自定义密钥。
- 提交命令、查状态/退出码/输出、超时、取消，任务队列和输出有上限。首次 SYSTEM PowerShell 启动可能较慢，首次任务建议至少 60 秒超时。
- 文件列表、上传、下载、建目录、删除单文件；健康检查与滚动审计日志。
- 截图、移动、左键点击、文字和有限按键，由普通登录用户的托盘提供。必须每次托盘启动后在本机手动授权；**不支持锁屏、登录界面、UAC 安全桌面、RDP 会话，不绕过密码或系统安全边界**。
- 管理员/LocalSystem 权限不等于所有权限。后台任务属于服务账户，不属于登录用户；输入受 UIPI、窗口权限、焦点和会话限制。托盘退出不会停止服务。
- 睡眠/休眠/关机期间 API 不可用；断网时远程不可达，已收任务可能继续；任务状态与输出重启即丢失，不自动重放。取消只尽力终止任务树，不回滚副作用，也不是沙箱。

详细接口、状态边界、curl 和 PowerShell 示例见 [中文 API 文档](docs/API.zh-CN.md)，WebUI 内也可打开。

## 配置与卸载

`%ProgramData%\ZiYin\service\service.json` 保存监听/证书/数据目录配置；`api-key.json` 保存强随机密钥。服务目录和文件目录 ACL 只允许 SYSTEM/Administrators，PFX 密码也在该 ACL 目录内。`client.json` 仅含非秘密端口与协议，普通用户只读。密钥比较采用固定时间摘要比较；WebUI 使用 HttpOnly/SameSite cookie、同源检查、CSRF 自定义头和 CSP。

```powershell
# 管理员执行；保留 ProgramData 内所有密钥、配置、审计和用户文件
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:ProgramFiles\ZiYin\scripts\Uninstall.ps1"
# 仅预览
& "$env:ProgramFiles\ZiYin\scripts\Uninstall.ps1" -WhatIf
```

卸载只移除服务、托盘启动项、防火墙规则和已安装二进制。用户数据和客户端已导入的 TLS 信任证书保留，不自动清除。需要撤销旧密钥时先轮换或由管理员单独处理保留目录。

## 开发与验证

```powershell
dotnet test tests/ZiYin.Tests/ZiYin.Tests.csproj -c Release
pwsh -NoProfile -File .\scripts\Build.ps1
```

开发构建需要 .NET 10 SDK 和 PowerShell 7；安装、卸载和密钥窗口支持 Windows PowerShell 5.1。

构建输出 `artifacts/ziyin-windows-client-win-x64.zip` 与 `SHA256SUMS.txt`。GitHub Actions 使用 windows-latest，测试认证、CSRF、密钥轮换、文件路径限制、任务输出/超时/取消，编译服务和托盘、打包并实际安装服务做 smoke test，之后卸载并上传 artifact。工作流未进行真实用户交互桌面验收。

Linux 仅用于本地核心/API 测试，任务使用 `/bin/sh` 的开发替身，不能替代 Windows PowerShell/SCM/WinForms 验收。运行 SDK 时如 HOME 未设置，可设置 `DOTNET_CLI_HOME` 到构建缓存，勿把开发配置/密钥提交进仓库。配置路径可通过 `ZIYIN_CONFIG` 覆盖，正式安装不需要此变量。

## 真实 Windows 验收清单

- [ ] 安装、服务自动启动、未登录前 API 可达，重启后服务恢复。
- [ ] 普通用户登录后托盘启动，右键打开 WebUI；退出托盘不影响服务。
- [ ] 实际 LAN 另一台设备通过 TLS 访问，证书验证成功；Public 网络/非 LocalSubnet 不放行。
- [ ] 无密钥/错密钥被拒绝；密钥轮换让旧 key 和 cookie 失效；普通用户无法读取 service 目录或改安装程序。
- [ ] 锁屏时后台任务仍能完成；截图/输入拒绝，解锁且已授权后恢复。
- [ ] 未授权、UAC 安全桌面、Winlogon、RDP 和用户切换均不越权；普通程序输入、多屏和中文截图实测。
- [ ] 超时/取消终止普通子进程；输出截断、服务停止时任务中断符合文档。
- [ ] 断网、休眠恢复、关机重启行为符合文档；无任务重复执行承诺。
- [ ] 卸载移除服务/启动项/防火墙且保留用户数据。

目前已通过的机器验证及剩余项以 [验证记录](docs/VALIDATION.md) 为准。自动化成功不代表上述真实桌面清单已全部完成。
