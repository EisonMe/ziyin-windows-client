# 紫音 Windows 客户端 API

本文对应当前 LAN API 实现，不使用旧设计中的 WSS/配对服务。

## 连接与权限

- 基础 URL：`https://电脑实际IPv4:17643`；WebUI 显示本机实际网卡 IPv4 和配置端口。VPN/虚拟网卡也可能出现，请选局域网网卡。路由、防火墙和监听设置决定是否可达。
- 正式安装服务为 LocalSystem，开机自动启动，无需用户登录，锁屏仍可执行后台任务。它不是当前登录用户，不自动拥有用户映射盘、用户配置、用户凭证或网络共享身份。管理员/LocalSystem 也不是一切权限，受 ACL、系统保护和安全边界限制。
- API 密钥等价于服务账户执行权限，只交给受信任管理员。密钥由 CSPRNG 生成 32 字节，文件和日志目录只允许 SYSTEM/Administrators 访问。比较使用 SHA-256 摘要的固定时间比较。自定义密钥至少 32 个可见 ASCII 字符；长度不等于随机性，推荐随机生成。
- 除登录入口和静态 WebUI/文档外，所有 API 都认证，包括健康检查、配置和日志。无 CORS。
- HTTP 只允许显式选择 `-TrustedLanHttp` 的可信隔离 LAN；API 密钥、cookie、命令和输出都可能被监听。推荐 TLS。自签名证书须在受信任通道核对指纹，客户端导入信任；不要使用 `curl -k` / `SkipCertificateCheck`。不要把端口映射到公网。
- Bearer 认证：`Authorization: Bearer <API密钥>`。密钥不可放 URL/query，也不可写日志。
- 所有 POST/PUT/DELETE 还需 `X-ZiYin-Request: 1`。浏览器 Origin 必须与当前来源相同；跨站请求、DNS rebinding Host 被拒绝。WebUI 登录采用 30 分钟 HttpOnly、SameSite=Strict cookie；TLS 时 Secure，密钥不放 localStorage/sessionStorage。修改密钥会清除所有 cookie 会话。

## curl 示例

在可信终端中设置变量；密钥不要写进脚本或 shell 历史。下面 Bash 通过隐藏输入读取：

```bash
export ZIYIN_BASE='https://192.168.1.100:17643'
read -rsp 'API key: ' ZIYIN_API_KEY; echo
export ZIYIN_API_KEY
curl --fail-with-body -H "Authorization: Bearer $ZIYIN_API_KEY" "$ZIYIN_BASE/api/health"
curl --fail-with-body -H "Authorization: Bearer $ZIYIN_API_KEY" \
  -H 'X-ZiYin-Request: 1' -H 'Content-Type: application/json' \
  -d '{"command":"Get-Date; whoami","timeoutSeconds":60}' "$ZIYIN_BASE/api/tasks"
# 从返回 JSON 取得 id
TASK_ID='返回的任务id'
curl -H "Authorization: Bearer $ZIYIN_API_KEY" "$ZIYIN_BASE/api/tasks/$TASK_ID"
curl -H "Authorization: Bearer $ZIYIN_API_KEY" "$ZIYIN_BASE/api/tasks/$TASK_ID/output"
curl -X POST -H "Authorization: Bearer $ZIYIN_API_KEY" -H 'X-ZiYin-Request: 1' "$ZIYIN_BASE/api/tasks/$TASK_ID/cancel"
curl -X PUT -H "Authorization: Bearer $ZIYIN_API_KEY" -H 'X-ZiYin-Request: 1' \
  --data-binary @local.txt "$ZIYIN_BASE/api/files/content?path=local.txt"
curl -H "Authorization: Bearer $ZIYIN_API_KEY" "$ZIYIN_BASE/api/files/content?path=local.txt" -o downloaded.txt
unset ZIYIN_API_KEY
```

curl 的参数在本地进程列表中可能被有权限的用户读取；共享机器优先使用 PowerShell/受保护 SDK，不应在不可信主机输入密钥。

## PowerShell 示例

兼容 Windows PowerShell 5.1 和 PowerShell 7。不要开启 PowerShell Transcript 记录敏感返回；命令输出也可能有私密内容。

```powershell
$base = 'https://192.168.1.100:17643'
$secret = Read-Host 'API key' -AsSecureString
$key = [Net.NetworkCredential]::new('', $secret).Password
$headers = @{ Authorization = "Bearer $key"; 'X-ZiYin-Request' = '1' }
Invoke-RestMethod "$base/api/health" -Headers $headers
$body = @{ command = 'Get-Date; whoami'; timeoutSeconds = 60 } | ConvertTo-Json
$task = Invoke-RestMethod "$base/api/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
Invoke-RestMethod "$base/api/tasks/$($task.id)" -Headers $headers
Invoke-RestMethod "$base/api/tasks/$($task.id)/output" -Headers $headers
Invoke-RestMethod "$base/api/tasks/$($task.id)/cancel" -Method Post -Headers $headers
Invoke-RestMethod "$base/api/files/content?path=note.txt" -Method Put -Headers $headers -InFile .\note.txt -ContentType 'application/octet-stream'
Invoke-WebRequest "$base/api/files/content?path=note.txt" -Headers $headers -OutFile .\downloaded.txt
# 截图必须先在被控电脑的托盘里开启权限
Invoke-WebRequest "$base/api/desktop/screenshot" -Method Post -Headers $headers -OutFile .\screen.png
$inputBody = @{action='text'; text='你好'} | ConvertTo-Json
Invoke-RestMethod "$base/api/desktop/input" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($inputBody))
$headers.Clear(); $key = $null; $secret = $null
```

## 接口表

JSON 属性采用 camelCase。所有 API 返回 `Cache-Control: no-store`。

| 方法 / 路径 | 请求 / 返回 |
|---|---|
| POST `/api/auth/login` | `{apiKey}`；成功设置 WebUI cookie；每来源 IP 每分钟 5 次。此入口不要求已有认证，仍要求 CSRF header |
| POST `/api/auth/logout` | 注销当前 cookie |
| GET `/api/health` | `status/version/utc/platform/taskStatePersistent` |
| GET `/api/updates` | 当前版本、最新稳定版、自动更新状态/时间/来源、上次安装结果；需要认证 |
| POST `/api/updates/check` | 手动排队检查，202 表示受理；发现较新可信稳定版自动安装，忙时 409；需认证和 CSRF header |
| GET `/api/settings` | 实际 `addresses/port/scheme/allowRemote/trustedLanHttp/fileRoot/serviceIdentity/desktopPolicy`；不含密钥 |
| GET `/api/key` | `{apiKey}`；主动点击查看才调用；需要认证 |
| PUT `/api/key` | `{apiKey:null}` 随机生成，或传自定义值；返回新密钥；撤销旧密钥及所有会话。已经通过认证、正在执行的请求不会回滚 |
| POST `/api/tasks` | `{command, timeoutSeconds:60, workingDirectory:null}`；返回 202 和任务信息，Location 指向任务 URL |
| GET `/api/tasks` | 最新任务信息数组 |
| GET `/api/tasks/{id}` | 单任务状态、输出和退出码；不存在返回 404 |
| GET `/api/tasks/{id}/output` | `standardOutput/standardError/outputTruncated/status` |
| POST `/api/tasks/{id}/cancel` | 返回 202 表示收到取消请求；轮询直到终态；已结束任务保持原状态 |
| GET `/api/logs` | `{entries:[JSON行...]}`，最近 200 条审计事件 |
| GET `/api/files?path=` | 根目录或相对目录列表，最多 1000 项，不递归 |
| GET `/api/files/content?path=a.txt` | 下载单文件，最多 10 MiB |
| PUT `/api/files/content?path=a.txt` | 原始字节，最多 10 MiB；临时文件写完再原子替换；覆盖同名文件，父目录必须存在 |
| POST `/api/files/directory` | `{path:"folder/sub"}`；创建目录 |
| DELETE `/api/files?path=a.txt` | 只删除文件，不递归删目录 |
| GET `/api/desktop/status` | `available/message`，没有托盘或未授权会明确说明 |
| POST `/api/desktop/screenshot` | 成功返回 PNG；不可用 409；最大 16 MiB PNG、总像素不超过 33,177,600 |
| POST `/api/desktop/input` | `{action:"move"或"click",x:100,y:100}`；`{action:"text",text:"文字"}`；`{action:"key",key:"ENTER"}` |

按键支持 ENTER/TAB/ESCAPE/BACKSPACE/LEFT/UP/RIGHT/DOWN；不实现 Ctrl+Alt+Del。坐标为虚拟桌面像素，副屏可以有负坐标；click 为左键。文字最多 1000 个 UTF-16 字符。输入 API 成功只说明系统接收输入，目标应用可能因 UIPI/焦点/权限不接受，必须观察实际效果。

## 任务与存储限制

任务状态：`queued → running → completed/failed/timed_out/cancelled/interrupted`；排队取消可直接到 cancelled。`exitCode` 仅在进程正常退出时提供，超时/取消可能为空。Shell 为系统自带 Windows PowerShell，`-NoProfile -NonInteractive -EncodedCommand`。需要用户交互的命令不适用；超时 1–86400 秒，从工作线程取出任务时开始计时，包含 PowerShell 启动和模块初始化。首次以 SYSTEM 运行可能较慢，首次调用建议至少 60 秒。

并发 2，排队最多 64，总保留最多 200 条；stdout/stderr 各 256 Ki 字符，超出会继续排空但不保留。输出和任务记录只保存在内存，服务重启丢失；提交无幂等去重，网络重试前先查任务状态。不要在任务中故意重启/停止本服务；服务停机/异常退出不会重放任务。审计文件轮转上限约 5 MiB × 2，不记录命令内容、输出、密钥或输入文字。被执行命令本身仍可能写系统日志或泄露秘密。

Windows 任务加入 kill-on-close Job Object，并在取消时请求终止整个进程树。进程启动与加入 Job 之间存在很短的时间窗口；特意脱离、创建系统服务/计划任务或其他受保护进程无法保证被取消。这不是安全沙箱；远程执行凭据必须交给可信管理员。终止不回滚文件/网络/系统副作用。

文件 API 限制在受保护的配置根目录，拒绝绝对路径、`..`、ADS 和重解析点。具有命令执行权的调用方仍能用 PowerShell 访问服务账户有权访问的其它路径。文件根目录保护不是命令沙箱；管理员并发修改文件系统可能绕过路径检查的时序，属于同权限信任范围。

## 状态和权限边界

| 电脑状态 | 后台 API / 命令 | 截图 / 输入 |
|---|---|---|
| 开机未登录 | 服务启动后可用，依赖网络和防火墙 | 不可用 |
| 普通用户已登录，桌面解锁 | 可用 | 本机托盘启动并手动授权后可用 |
| 锁屏、Winlogon、UAC 安全桌面 | 后台仍可用 | 拒绝，不解锁、不输入密码、不绕过 UAC |
| RDP 会话、用户快速切换 | 服务仍可用 | 首版仅活动本机控制台会话，RDP 不支持 |
| 托盘退出 | 服务仍可用 | 不可用 |
| 断网 | API 不可达，已接收任务通常继续到结束/超时 | 远程不可达 |
| 休眠/睡眠 | API 不可达，进程挂起；恢复后继续或超时，不保证精确实时截止 | 不可用 |
| 关机/重启 | 不可达，任务中断；重启后服务恢复，任务不重放 | 重新登录及重新授权后才可用 |

HTTP 状态：400 参数错误或 Host 不允许；401 认证失败；403 跨站/CSRF 或权限不足；404 不存在；409 文件冲突或桌面不可用；413 请求过大；429 登录限流或任务容量满；500 内部错误。不要仅凭 202 判断任务完成。

自动更新详情见 [更新说明](UPDATES.zh-CN.md)。升级期间服务短暂停止，已执行任务不回放。
