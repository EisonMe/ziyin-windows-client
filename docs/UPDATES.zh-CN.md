# 稳定版发布与自动更新

1.1.0 开始内置自动更新。Windows Service 启动约 30 秒后检查一次，之后每 3 小时检查；WebUI「版本与自动更新」可查看当前版本、最新稳定版、来源、时间、状态和上次安装结果，也可手动检查。手动检查发现较新且可信的稳定版本会直接安装。安装阶段会短暂停止服务，正在运行的任务可能中断，配置和用户文件保留。

## 版本、来源与国内下载

默认访问本仓库正式 Release 的 `releases/latest/download/update.json` 和独立签名，再从清单中的精确稳定版本下载 zip。签名同时绑定仓库、版本、资产名、大小和 SHA256。只接受 `主.次.补丁` 三段数字，按数字比较，不接收预发布或降级；例如 1.10.0 大于 1.9.9。

先尝试 GitHub HTTPS 直连，网络失败、超时或验证失败后自动按配置顺序尝试 HTTPS 代理。默认备用前缀 `https://ghfast.top/` 是第三方 GitHub 下载加速服务，**不是 GitHub 官方 CDN**。不能保证某个地区、运营商或时间一直可用，失败时保留当前版本，下次重试。不会通过 IP 定位第三方服务判断国籍；直连不可达即自动回退，同样适用于国内网络。

`%ProgramData%\ZiYin\service\service.json` 中可由管理员配置，修改后重启服务：

```json
{
  "AutoUpdate": true,
  "UpdateProxyPrefixes": ["https://ghfast.top/"]
}
```

上面只是要合并到原配置的字段，**不要覆盖原文件中的端口、证书和目录配置**。最多 3 个代理；HTTPS 前缀必须以 `/` 结尾，不允许 URL 凭据、query 或 fragment。设置 `[]` 禁用代理；`AutoUpdate:false` 关闭定时检查，但仍可手动检查。首次安装参数：`-DisableAutoUpdate`、`-UpdateProxyPrefixes @('https://你的下载代理/')`。旧版 1.0 没有更新器，须手动安装本版一次。

下载客户端独立且匿名，不携带 Windows 凭据、API 密钥、浏览器 cookie 或 GitHub token。代理会看到下载 URL、客户端 IP 和时间等网络信息，得不到本地 API 密钥。元数据单请求 15 秒、包单次下载 4 分钟，每来源最多两次；总检查/下载上限 20 分钟。元数据 16 KiB、签名 1 KiB、包 400 MiB、解压 1800 MiB/5000 项有限制；重定向最多 5 次且必须 HTTPS。

## 为什么代理不能伪造更新

客户端和 helper 都内置发布 RSA 公钥。发布流程在 GitHub Actions 中使用仓库 secret `ZIYIN_RELEASE_SIGNING_KEY` 对 `update.json` 做 RSA-PSS/SHA256 签名。客户端验证签名后才相信清单中的 SHA256，下载完成后再次核对长度与哈希；替换前 helper 再验签和验包。公钥位于 `src/ZiYin.Update/release-public.pem`，私钥不在源码、安装包或 Release 内。

这不同于相信同一不可信代理返回的普通 checksum：攻击者即使同时替换 zip、JSON 和 checksum，没有签名私钥也不能制造有效的新更新。代理仍可拒绝服务或重放旧的有效签名清单，使客户端暂时看不到最新版；客户端不会降级。首次安装包的信任来自你选择的本仓库 HTTPS Release/签名公钥来源。信任边界仍包括维护者、GitHub 仓库/Actions 权限、发布私钥和本机管理员；维护者或签名密钥失陷不在这一机制防护范围内。二进制没有商业 Authenticode 代码签名，Windows SmartScreen 可能提示未知发布者，不能把清单签名等同于微软认证。

## 替换、回滚和断电恢复

- 下载期间原服务继续运行。替换在独立 SYSTEM helper 中进行；helper 复制到 ACL 保护的更新工作目录后再启动，不直接覆盖正在运行的自己。
- 内存队列与进程间文件锁防止并发更新。helper 二次验签、检查严格更高版本后，先注册 SYSTEM 开机恢复任务，再写持久化事务记录。
- 包先解压到受保护的安装目录临时目录，拒绝路径穿越、链接、重复文件和缺失必要程序。只替换 `service/tray/updater/scripts/docs/README.md/VERSION`，不改 ProgramData 的 service.json、api-key.json、PFX、client.json 或用户文件。
- 停止 SCM 服务和已运行的本项目托盘，逐目录改名保存旧程序并换入新程序。新服务必须通过本机认证健康检查且版本正确；HTTPS 本机检查固定匹配现有 PFX 证书，不向代理发送密钥。
- 服务无法启动、健康检查失败或文件锁阻止替换时，恢复已备份的旧文件并重新启动旧服务。正常错误不会把配置清空。回滚也失败时，保留备份和恢复记录，明确记录 failed，需管理员检查磁盘/权限/服务状态。
- 替换期间断电或 helper 崩溃，`ZiYinUpdateRecovery` 开机任务读取持久化记录并恢复旧程序。恢复过程可重复执行。断电恢复没有在物理机器拉电源测试过；代码以中断文件状态测试覆盖，仍需实机验收。
- 正常更新或回滚后，用已有用户会话的普通用户 token 尝试重启原先运行的托盘，不在会话 0 启动交互 UI，也不提权或绕过锁屏。会话已注销、权限不足时托盘可能要手动启动或下次登录恢复；桌面控制授权会重置为关闭。
- 成功或回滚后保留本次备份与结果，下一次获得锁且确认没有未完成事务时清理旧 work/备份，避免每 3 小时累积整个安装包。未完成事务绝不被自动清理。

若 WebUI 显示 `recovery_required`，管理员查看 `%ProgramData%\ZiYin\service\updates\journal.json`、`result.json` 与任务计划程序中的 `ZiYinUpdateRecovery`。可运行已登记的恢复任务：

```powershell
# 管理员 PowerShell：仅针对未完成的本项目更新
Start-ScheduledTask -TaskName ZiYinUpdateRecovery
```

不会把「开始更新」当成更新成功。最终状态以 `/api/health` 的实际版本和 `/api/updates` 的安装结果为准。

## 发布维护

`VERSION` 是唯一版本源，SDK 为服务、共享库和 helper 生成对应版本。修改版本、测试后推送 `vX.Y.Z` tag，Windows 工作流测试、编译、打包、签名、真实 helper 升级/故障回滚验证全部通过才由发布 job 创建正式 Release。只发布稳定版，tag 必须和 VERSION 一致。PR 不读取签名私钥；fork PR 无签名/发布权限。

Release 资产：Windows 自包含 zip、SHA256SUMS.txt、update.json、update.json.sig。zip 解压后管理员运行 scripts/Install.ps1；README 提供 TLS/LAN 选项。Actions artifact 为短期构建下载，正式 Release 资产不受 30 天 artifact 保留期限影响。
