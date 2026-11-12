'use strict';
const $ = id => document.getElementById(id);
let signedIn = false, screenUrl = null, remoteEnabled = false;
const notice = text => { $('notice').textContent = text; };
async function api(path, method = 'GET', body, raw = false) {
  const headers = { 'X-ZiYin-Request': '1' };
  if (body !== undefined && !(body instanceof File)) headers['Content-Type'] = 'application/json';
  const response = await fetch('/api' + path, { method, headers, credentials: 'same-origin', cache: 'no-store', body: body instanceof File ? body : body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) {
    if (response.status === 401) setSignedIn(false);
    let message = `请求失败 (${response.status})`;
    try { const data = await response.json(); message += ': ' + (data.error || data.message || ''); } catch {}
    throw new Error(message);
  }
  if (raw) return response;
  return response.status === 204 ? null : response.json();
}
function setSignedIn(value) { signedIn = value; $('login').hidden = value; $('workspace').hidden = !value; $('integration').hidden = !value; $('health').textContent = value ? '服务在线' : '等待登录'; if (!value) { $('include-key').checked = false; $('api-key').value = '••••••••••••••••'; $('api-key').type = 'password'; $('show-key').textContent = '查看密钥'; $('tasks').replaceChildren(); $('logs').textContent = ''; $('screen').hidden = true; if (screenUrl) URL.revokeObjectURL(screenUrl); } }
function run(id, event, fn) { $(id).addEventListener(event, async e => { e.preventDefault(); try { await fn(); } catch (error) { notice(error.message); } }); }
async function refresh() {
  const data = await api('/settings');
  setSignedIn(true);
  $('connection').textContent = `实际网卡 IPv4：${data.addresses.join('、') || '未发现可用 IPv4'}\n监听端口：${data.port}\nLAN 访问：${data.allowRemote ? '已开启' : '未开启，仅本机'}\n连接地址：${data.addresses.map(ip => `${data.scheme}://${ip}:${data.port}`).join('  ')}\n传输：${data.scheme === 'https' ? 'TLS' : 'HTTP 明文，仅限可信网络'}\n文件根目录：${data.fileRoot}\n服务账户：${data.serviceIdentity}\n${data.desktopPolicy}`;
  populateRemoteAddresses(data);
  await refreshTasks();
  await refreshUpdates();
}
async function refreshTasks() {
  const tasks = await api('/tasks'); $('tasks').replaceChildren();
  for (const task of tasks.slice(0, 20)) {
    const article = document.createElement('article'), title = document.createElement('div'), output = document.createElement('pre');
    title.textContent = `${task.id} · ${task.status} · 退出码 ${task.exitCode ?? '—'}${task.outputTruncated ? ' · 输出已截断' : ''}`;
    output.textContent = task.standardOutput + (task.standardError ? '\n[stderr]\n' + task.standardError : '') + (task.error ? '\n' + task.error : '');
    article.append(title, output);
    if (['queued', 'running'].includes(task.status)) { const cancel = document.createElement('button'); cancel.textContent = '取消任务'; cancel.addEventListener('click', async () => { try { await api(`/tasks/${task.id}/cancel`, 'POST', {}, true); notice('已请求取消，等待任务状态更新。'); await refreshTasks(); } catch (e) { notice(e.message); } }); article.append(cancel); }
    $('tasks').append(article);
  }
}
run('login-form', 'submit', async () => { const key = $('login-key').value; $('login-key').value = ''; await api('/auth/login', 'POST', { apiKey: key }); await refresh(); notice('已登录，会话有效期 30 分钟。'); });
run('logout', 'click', async () => { await api('/auth/logout', 'POST'); setSignedIn(false); notice('已退出。'); });
run('refresh', 'click', refresh);
run('show-key', 'click', async () => { if ($('api-key').type === 'text') { $('api-key').type = 'password'; $('api-key').value = '••••••••••••••••'; $('show-key').textContent = '查看密钥'; } else { $('api-key').value = (await api('/key')).apiKey; $('api-key').type = 'text'; $('show-key').textContent = '隐藏密钥'; } });
run('key-form', 'submit', async () => { if (!confirm('更新后所有会话和旧密钥将立即失效，继续？')) return; const result = await api('/key', 'PUT', { apiKey: $('new-key').value || null }); $('new-key').value = ''; await api('/auth/login', 'POST', { apiKey: result.apiKey }); await refresh(); $('api-key').value = result.apiKey; $('api-key').type = 'password'; $('show-key').textContent = '查看密钥'; notice('密钥已更新。点击查看并保存，托盘管理员工具也可再次查看。'); });
run('task-form', 'submit', async () => { await api('/tasks', 'POST', { command: $('command').value, timeoutSeconds: Number($('timeout').value), workingDirectory: $('working-directory').value || null }); notice('任务已提交。'); await refreshTasks(); });
const filePath = () => encodeURIComponent($('file-path').value);
run('list-files', 'click', async () => { $('files').textContent = JSON.stringify(await api('/files?path=' + filePath()), null, 2); });
run('mkdir', 'click', async () => { await api('/files/directory', 'POST', { path: $('file-path').value }); notice('目录已创建。'); });
run('delete-file', 'click', async () => { if (confirm('确认删除这个文件？')) { await api('/files?path=' + filePath(), 'DELETE'); notice('文件已删除。'); } });
run('upload', 'click', async () => { const file = $('upload-file').files[0]; if (!file) throw new Error('请选择文件。'); if (file.size > 10 * 1024 * 1024) throw new Error('文件超过 10 MiB。'); if (!confirm('上传会覆盖同名文件，继续？')) return; await api('/files/content?path=' + filePath(), 'PUT', file); notice('文件已上传。'); });
run('download', 'click', async () => { const response = await api('/files/content?path=' + filePath(), 'GET', undefined, true); const url = URL.createObjectURL(await response.blob()); const a = document.createElement('a'); a.href = url; a.download = $('file-path').value.split(/[\\/]/).pop(); a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); });
run('logs-refresh', 'click', async () => { $('logs').textContent = (await api('/logs')).entries.join('\n'); });
run('desktop-status', 'click', async () => { $('desktop-result').textContent = (await api('/desktop/status')).message; });
run('screenshot', 'click', async () => { const response = await api('/desktop/screenshot', 'POST', {}, true); if (screenUrl) URL.revokeObjectURL(screenUrl); screenUrl = URL.createObjectURL(await response.blob()); $('screen').src = screenUrl; $('screen').hidden = false; });
run('input-form', 'submit', async () => { const result = await api('/desktop/input', 'POST', { action: $('input-action').value, x: Number($('input-x').value), y: Number($('input-y').value), text: $('input-text').value, key: $('input-text').value }); $('desktop-result').textContent = result.message; });
setInterval(() => { if (signedIn && !document.hidden) refreshTasks().catch(e => notice(e.message)); }, 2500);
refresh().catch(() => setSignedIn(false));

async function refreshUpdates() {
 const u = await api('/updates');
 $('update-status').textContent = `当前版本：${u.currentVersion}\n最新稳定版：${u.latestVersion || '尚未检查'}\n自动更新：${u.enabled ? '已开启，每 3 小时' : '已关闭，可手动检查'}\n状态：${u.status} · ${u.message}\n最近检查：${u.lastCheckedAt || '—'}\n下次检查：${u.nextCheckAt || '—'}\n下载来源：${u.source}\n上次安装：${u.lastResult ? u.lastResult.status + ' · ' + u.lastResult.message : '暂无'}`;
}
function populateRemoteAddresses(data) {
  remoteEnabled = data.allowRemote;
  const select = $('remote-address');
  const previous = select.value;
  select.replaceChildren();
  for (const ip of data.addresses || []) {
    if (ip === '127.0.0.1' || ip === '::1') continue;
    const option = document.createElement('option');
    option.value = `${data.scheme}://${ip}:${data.port}`;
    option.textContent = option.value;
    select.append(option);
  }
  const custom = $('custom-address').value.trim();
  if (custom) select.value = custom;
  else if (previous && [...select.options].some(option => option.value === previous)) select.value = previous;
  else if (select.options.length) select.selectedIndex = 0;
}
function remoteBase() {
  if (!remoteEnabled) throw new Error('当前服务尚未开启 LAN 监听；请先在 Windows 安装配置中启用远程访问。');
  const raw = $('custom-address').value.trim() || $('remote-address').value;
  if (!raw) throw new Error('没有可用网卡地址，请填写调用端可达的 LAN/VPN 地址。');
  const url = new URL(raw);
  const octets = url.hostname.split('.').map(Number);
  if (!['https:', 'http:'].includes(url.protocol) || !url.port || url.username || url.password || url.pathname !== '/' || url.search || url.hash || octets.length !== 4 || octets.some(value => !Number.isInteger(value) || value < 0 || value > 255)) {
    throw new Error('远程地址须为 Windows 当前网卡实际拥有的 IPv4 和端口，且不含路径或凭据。');
  }
  return url.origin;
}
async function integrationPrompt(kind) {
  if (!signedIn) throw new Error('请先登录 WebUI。');
  const base = remoteBase();
  if (base.startsWith('http://') && !confirm('此地址使用明文 HTTP，密钥及命令可能被监听。确认仅用于可信隔离局域网？')) return;
  let keyValue = '<由用户在目标工具的安全配置中填写>';
  if ($('include-key').checked) {
    if (!confirm('密钥将进入剪贴板。只在当前电脑可信、且目标工具受你控制时继续？')) return;
    keyValue = (await api('/key')).apiKey;
  }
  const prompt = kind === 'skill'
    ? `请为紫音 Windows 客户端创建真实可调用的 Agent Skill：写入 skills/ziyin-windows-client/SKILL.md 与 skills/ziyin-windows-client/scripts/ziyin_client.py，并按当前 Agent 的 Skill 发现机制放置。参考模板 https://github.com/EisonMe/astrbot_plugin_ziyin_client/tree/main/skills/ziyin-windows-client 和 API https://github.com/EisonMe/ziyin-windows-client/blob/main/docs/API.zh-CN.md。调用端可达地址为 ${base}；API Key：${keyValue}。从安全环境变量或凭证管理器读取密钥，不写入 Skill、日志或仓库。默认验证 TLS 证书和主机名，自签名证书配置受信任 CA；HTTP 仅在用户明确选择的可信隔离 LAN 中使用。先只读调用 GET /api/health；未取得真实密钥或地址时报告未联通。任务、文件和桌面写操作须得到用户明确授权。`
    : `请使用 AstrBot 官方插件管理器的 GitHub 安装入口安装公开仓库 https://github.com/EisonMe/astrbot_plugin_ziyin_client，核实 metadata.yaml；需要当前实例管理员权限，没有权限就报告未安装。插件 base_url=${base}，api_key=${keyValue}。这是 AstrBot 主机可达的 Windows 客户端地址，不要改成 localhost；端口 ${new URL(base).port}。默认 TLS 证书与主机名验证，私有证书配置 ca_file；HTTP 仅在用户明确选择的可信隔离 LAN 中启用 allow_insecure_http。保持 allow_group、allow_file_write、allow_desktop_control 关闭。配置后仅调用 ziyin_client_health 做只读连通测试；未配置真实密钥或网络不可达就报告未联通，不执行命令。`;
  await navigator.clipboard.writeText(prompt);
  $('include-key').checked = false;
  notice('提示词已复制。剪贴板含敏感信息时请及时清除。');
}
run('copy-skill-prompt', 'click', () => integrationPrompt('skill'));
run('copy-astrbot-prompt', 'click', () => integrationPrompt('astrbot'));
run('check-update', 'click', async () => { await api('/updates/check', 'POST', {}); notice('检查已排队；若发现新版会自动安装，任务可能被中断。'); await refreshUpdates(); });
setInterval(() => { if (signedIn && !document.hidden) refreshUpdates().catch(e => notice(e.message)); }, 5000);
