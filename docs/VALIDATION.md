# 验证记录

验证日期：2026-11-12。项目独立仓库：[EisonMe/ziyin-windows-client](https://github.com/EisonMe/ziyin-windows-client)，private。父工作区不在本仓库提交历史中。

## 已验证

- Linux 本地 .NET 10：25 项测试通过。覆盖无认证访问拒绝、同源/CSRF/Host 检查、登录限流、密钥轮换撤销旧凭据与会话、文件上传下载/路径越界/符号链接拒绝、命令双输出流/退出码/中文/输出截断/超时/运行取消/排队取消、审计故障降级、静态 WebUI 与文档发布。
- Windows 目标托盘跨编译：0 warning、0 error。
- 本地 Google Chrome + Playwright 实际浏览器：登录、查看/隐藏密钥、中文任务输出、桌面不可用提示、密钥轮换后重新登录、退出通过；无 JavaScript 异常；localStorage/sessionStorage 中没有保存密钥。截图在本地 `artifacts/webui-smoke.png`，截图中密钥保持隐藏。
- 独立代理做了三轮只读安全审查，主代理修复后复核通过。包括预置密钥/文件 ACL、已有敏感文件显式读取权限、审计失败拖垮 worker、排队取消、卸载服务路径校验、PowerShell 5.1 编码及中文输出。此项属于静态审查，不代替真实桌面验收。
- Windows Actions 已实际通过 25 项测试、服务/托盘自包含编译及 zip 打包、PowerShell 5.1 脚本语法检查。早期打包路径错误已修正。
- SCM 验证已确认 LocalSystem 自动服务、认证 API、未认证拒绝、WebUI/文档、PowerShell 命令、文件读写、密钥 ACL、服务重启和不保留任务历史。TLS 使用正常证书校验通过；Private/LocalSubnet 防火墙规则通过；卸载移除服务/防火墙并保留用户数据通过。

## Windows 成功证据

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

## 下载与复现

Windows 下载 artifact 名为 `ziyin-windows-client-win-x64`，内含自包含 zip 和 SHA256SUMS.txt；测试结果单独保存在 `windows-test-results`。下载需要仓库访问权限，artifact 保留 30 天，可运行 workflow_dispatch 重新生成。

本地运行：`dotnet test tests/ZiYin.Tests/ZiYin.Tests.csproj -c Release`。Windows 构建：`pwsh -NoProfile -File scripts/Build.ps1`。安装与 API 详情见 README 和 API.zh-CN.md。
