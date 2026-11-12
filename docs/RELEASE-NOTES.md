紫音 Windows 客户端 1.1.0，首个公开稳定 Release。

- 管理员安装的 Windows Service、登录启动托盘、中文 WebUI 和认证 LAN API。
- 启动及每 3 小时检查稳定版本，后台下载、RSA-PSS 签名与 SHA256 校验，独立 helper 替换、服务健康检查、失败回滚及开机恢复任务。
- GitHub 直连失败后自动尝试可配置 HTTPS 下载代理；默认备用 ghfast.top 为第三方服务，不是 GitHub 官方 CDN。代理请求不含 API 密钥或账号凭据。
- WebUI 展示当前/最新版本和更新状态，支持手动检查。
- 更新保留配置、API 密钥、证书和用户文件。替换会中断当前后台任务，托盘恢复后需重新授权桌面控制。

下载 `ziyin-windows-client-win-x64.zip` 并完整解压，管理员运行 `scripts/Install.ps1`。首次 LAN 安装推荐 `-EnableLan -GenerateTls`，按 README 核对并信任证书。程序自包含 .NET 10，无需单独安装运行时。

Windows 自动测试覆盖认证、CSRF、任务/文件、SCM 服务、TLS、签名/哈希、版本比较、配置保留、升级和文件锁故障回滚。真实用户电脑的锁屏/未登录、托盘重启、多屏输入、跨设备国内网络和断电恢复仍须实机验收。不支持绕过密码、UAC、Winlogon 或安全桌面。

安装包未做商业 Authenticode 签名；发布清单签名用于自动更新验证，不代表微软认证。详细信任边界、代理配置和恢复操作见包内 `docs/UPDATES.zh-CN.md`。
