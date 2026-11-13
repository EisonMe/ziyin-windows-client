紫音 Windows 客户端 1.2.2

- WebUI 直接展示 Skill 与 AstrBot 插件两份完整接入提示词。提示词使用原生 HTML 折叠区，默认收起，点击标题展开；即使 JavaScript 未运行，也可阅读、选中并手动复制。
- 提示词中的客户端地址与 API Key 使用明确占位符。页面不嵌入真实密钥，也不再提供可能受浏览器剪贴板权限影响的复制按钮或“包含 API Key”选项。
- 保留现有登录保护、后台任务、文件、桌面控制及自动更新功能。签名、SHA256 校验和配置保留链路不变。

下载 `ziyin-windows-client-win-x64.zip` 并完整解压，管理员运行 `scripts/Install.ps1`。首次 LAN 安装推荐 `-EnableLan -GenerateTls`，按 README 核对并信任证书。程序自包含 .NET 10，无需单独安装运行时。

Windows 自动测试覆盖认证、CSRF、任务/文件、SCM 服务、TLS、签名/哈希、版本比较、配置保留、升级和文件锁故障回滚。真实用户电脑的锁屏/未登录、托盘重启、多屏输入、跨设备国内网络和断电恢复仍须实机验收。不支持绕过密码、UAC、Winlogon 或安全桌面。

安装包未做商业 Authenticode 签名；发布清单签名用于自动更新验证，不代表微软认证。详细信任边界、代理配置和恢复操作见包内 `docs/UPDATES.zh-CN.md`。
