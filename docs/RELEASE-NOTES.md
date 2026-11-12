紫音 Windows 客户端 1.2.0

- 增加 Skill 与 AstrBot 插件接入说明及可复制配置提示词。
- WebUI 登录后提供 LAN 地址选择、Windows 网卡实际拥有的 VPN IP 输入及安全复制提示词；API Key 默认不复制，显式勾选后再次确认才包含。
- API 密钥不写入浏览器持久存储；保留完整中文 API 参考。
- 包含独立 AstrBot 插件仓库链接；安装仅通过 AstrBot 官方管理器，由管理员操作。
- 自动更新机制保持 v1.1.0 的 RSA-PSS 签名、SHA256 和配置保留链路不变。

下载 `ziyin-windows-client-win-x64.zip` 并完整解压，管理员运行 `scripts/Install.ps1`。首次 LAN 安装推荐 `-EnableLan -GenerateTls`，按 README 核对并信任证书。程序自包含 .NET 10，无需单独安装运行时。

Windows 自动测试覆盖认证、CSRF、任务/文件、SCM 服务、TLS、签名/哈希、版本比较、配置保留、升级和文件锁故障回滚。真实用户电脑的锁屏/未登录、托盘重启、多屏输入、跨设备国内网络和断电恢复仍须实机验收。不支持绕过密码、UAC、Winlogon 或安全桌面。

安装包未做商业 Authenticode 签名；发布清单签名用于自动更新验证，不代表微软认证。详细信任边界、代理配置和恢复操作见包内 `docs/UPDATES.zh-CN.md`。
