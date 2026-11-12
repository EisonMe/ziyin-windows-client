# 验证记录

验证日期：2026-11-12。项目独立仓库：[EisonMe/ziyin-windows-client](https://github.com/EisonMe/ziyin-windows-client)，已公开。父工作区不在本仓库提交历史中。

## 1.1.0 自动更新与公开发布验证

- 公开前独立审查全部已跟踪文件及可达 Git 历史，未发现真实凭据、私人数据或父工作区备份。发布签名私钥在仓库外生成，仅通过 GitHub Actions secret 提供；跟踪文件仅含验证用公钥。
- Linux 本地 .NET 10：48 项测试通过，0 失败、0 跳过。新增覆盖稳定版本数字比较、拒绝预发布/降级、签名篡改/错误公钥、包哈希、HTTPS 代理回退、匿名请求、路径穿越、事务恢复、失败回滚和配置保留。包含历史回滚结果不能掩盖本次回滚失败的回归测试。Updater 编译 0 warning、0 error。
- Windows [运行 43199773783](https://github.com/EisonMe/ziyin-windows-client/actions/runs/43199773783)（提交 `e33d5e26157781ea1ca58cba04db4140617a5d31`）及 [运行 43200246615](https://github.com/EisonMe/ziyin-windows-client/actions/runs/43200246615)（提交 `6ea9d02375dea0afe23ddd05a111f8fa90fde103`）均成功：47 项测试、Service/Tray/Updater 自包含编译打包、PowerShell 5.1 语法、SCM/TLS/防火墙/ACL/卸载、签名和真实升级测试通过。
- 真实 Windows SYSTEM helper 测试：安装旧版 1.0.0，锁住程序文件制造替换失败，确认回滚后旧服务正常；解除文件占用后，使用签名包升级到 1.1.0，新服务健康及版本状态 API 正确。service.json、api-key.json、PFX 和用户文件升级前后 SHA256 一致。全程保留 TLS，未跳过证书验证。
- 末轮独立静态审查通过；修复了更新与卸载交接的竞争、托盘 token 提权边界及旧结果误删当前恢复任务的问题。最终补丁的 Windows 结果以 [Actions](https://github.com/EisonMe/ziyin-windows-client/actions) 对应提交/`v1.1.0` 为准，不能用前述 47 项测试冒充最终 48 项结果。
- 正式 Release 工作流必须先通过 Windows 测试、安装和签名升级/回滚，再发布 zip、校验文件及签名清单；标签必须匹配 VERSION。正式包及对应流水线可从 [v1.1.0 Release](https://github.com/EisonMe/ziyin-windows-client/releases/tag/v1.1.0) 和标签对应 Actions 追溯。

## 初版功能验证记录

- Linux 本地 .NET 10：25 项测试通过。覆盖无认证访问拒绝、同源/CSRF/Host 检查、登录限流、密钥轮换撤销旧凭据与会话、文件上传下载/路径越界/符号链接拒绝、命令双输出流/退出码/中文/输出截断/超时/运行取消/排队取消、审计故障降级、静态 WebUI 与文档发布。
- Windows 目标托盘跨编译：0 warning、0 error。
- 本地 Google Chrome + Playwright 实际浏览器：登录、查看/隐藏密钥、中文任务输出、桌面不可用提示、密钥轮换后重新登录、退出通过；无 JavaScript 异常；localStorage/sessionStorage 中没有保存密钥。截图在本地 `artifacts/webui-smoke.png`，截图中密钥保持隐藏。
- 独立代理做了三轮只读安全审查，主代理修复后复核通过。包括预置密钥/文件 ACL、已有敏感文件显式读取权限、审计失败拖垮 worker、排队取消、卸载服务路径校验、PowerShell 5.1 编码及中文输出。此项属于静态审查，不代替真实桌面验收。
- Windows Actions 已实际通过 25 项测试、服务/托盘自包含编译及 zip 打包、PowerShell 5.1 脚本语法检查。早期打包路径错误已修正。
- SCM 验证已确认 LocalSystem 自动服务、认证 API、未认证拒绝、WebUI/文档、PowerShell 命令、文件读写、密钥 ACL、服务重启和不保留任务历史。TLS 使用正常证书校验通过；Private/LocalSubnet 防火墙规则通过；卸载移除服务/防火墙并保留用户数据通过。

## 初版 Windows 成功证据（历史构建）

- 已通过的代码提交：`9cac505fb052ee7eafb618944771797244d5ed9d`。
- [成功 Actions 运行 43193123097](https://github.com/EisonMe/ziyin-windows-client/actions/runs/43193123097)，各构建、测试、安装 smoke 和上传步骤全部 success。
- [该运行 Windows 下载 artifact](https://github.com/EisonMe/ziyin-windows-client/actions/runs/43193123097/artifacts/13931986267)。主代理已下载并验证 zip 可打开、630 个文件的 SHA-256 全部匹配；所需 exe、脚本、WebUI、API 文档齐全，不含运行时 api-key.json、service.json 或 PFX。
- 该构建 zip SHA-256：`b90d201a566b4c4a00a4faec729f05483e828ca59f5d4d7360aa40f664405bb6`。后续仅更新文档的构建会有新的压缩包校验值，以随包 SHA256SUMS.txt 为准。
- GitHub 日志的成功标记包括 `PASS: TLS certificate validation and Private/LocalSubnet firewall.`、`PASS: SCM automatic LocalSystem service...`、`PASS: uninstall removed service/firewall and retained user data.`。服务测试在无人交互条件下完成，没有忽略 HTTPS 证书错误。
- 首次 SYSTEM PowerShell 在 runner 中启动较慢，任务超时包括启动/模块初始化；smoke 使用 60 秒。Windows 托盘截图/输入不在无人交互 runner 中冒充实测。

## 仍须真实 Windows 验收

自动测试在临时 GitHub Windows runner 完成，不宣称用户电脑已安装或已连接。

- 电脑重启后未登录/锁屏时，通过另一台 LAN 设备实际调用 API。
- 普通用户登录启动托盘、UAC 正常确认后的密钥窗口、右键 WebUI。
- 锁屏、Winlogon、UAC 安全桌面和用户切换时拒绝截图/输入；普通桌面授权后截图、中文输入、多屏坐标、权限更高窗口拒绝；RDP 不支持。
- 实际网络防火墙、证书分发/信任、网卡 IP 变化后的证书更新。
- 睡眠/休眠恢复、断网、关机及取消进程树边界。取消不回滚副作用，对脱离任务树或服务化工作不作完全终止保证。
- 真正经过 3 小时的定时检查、国内不同运营商直连失败后的第三方代理回退、用户网络上的下载中断与恢复。
- 更新后已登录普通用户的托盘恢复、多用户切换、当前任务中断提示；托盘重新启动后桌面控制必须重新授权。
- 物理断电/磁盘故障后的开机恢复任务与管理员恢复流程。自动测试已覆盖中断事务文件状态，但没有在用户电脑上断电测试。

## 下载与复现

正式安装包从 [最新 Release](https://github.com/EisonMe/ziyin-windows-client/releases/latest) 匿名下载，无需仓库账号。包为自包含 zip，完整解压后管理员执行 scripts/Install.ps1。update.json 的 RSA-PSS 签名和包 SHA256 校验方式及信任边界见 [更新文档](UPDATES.zh-CN.md)。

Windows 构建 artifact 名为 `ziyin-windows-client-win-x64`，内含 zip、SHA256SUMS.txt、update.json 和签名；测试结果单独保存在 `windows-test-results`。Actions artifact 下载通常需要 GitHub 登录，保留 30 天；正式 Release 资产不受此期限影响，可运行 workflow_dispatch 重新构建。

本地运行：`dotnet test tests/ZiYin.Tests/ZiYin.Tests.csproj -c Release`。Windows 构建：`pwsh -NoProfile -File scripts/Build.ps1`。安装与 API 详情见 README 和 API.zh-CN.md。
