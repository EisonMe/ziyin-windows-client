紫音 Windows 客户端 1.2.1

- 修复 WebUI 局域网 HTTP 环境下「复制 Skill 提示词」与「复制插件提示词」两个按钮点击无反应的问题：在非安全上下文或 Clipboard API 不可用时自动回退到受保护的临时文本框复制，并在完成后立即清空移除；剪贴板被浏览器完全拒绝时弹出明确提示。
- 保留 1.2.0 的 Skill 与 AstrBot 插件接入说明及安全复制流程：API Key 默认不复制，显式勾选后先复位勾选框再二次确认才从受认证接口获取，不写入浏览器持久存储或日志。
- 自动更新机制保持 RSA-PSS 签名、SHA256 校验和配置保留链路不变。

下载 `ziyin-windows-client-win-x64.zip` 并完整解压，管理员运行 `scripts/Install.ps1`。首次 LAN 安装推荐 `-EnableLan -GenerateTls`，按 README 核对并信任证书。程序自包含 .NET 10，无需单独安装运行时。

Windows 自动测试覆盖认证、CSRF、任务/文件、SCM 服务、TLS、签名/哈希、版本比较、配置保留、升级和文件锁故障回滚。真实用户电脑的锁屏/未登录、托盘重启、多屏输入、跨设备国内网络和断电恢复仍须实机验收。不支持绕过密码、UAC、Winlogon 或安全桌面。

安装包未做商业 Authenticode 签名；发布清单签名用于自动更新验证，不代表微软认证。详细信任边界、代理配置和恢复操作见包内 `docs/UPDATES.zh-CN.md`。
