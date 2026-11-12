'use strict';
const $ = id => document.getElementById(id);
let signedIn = false, screenUrl = null;
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
function setSignedIn(value) { signedIn = value; $('login').hidden = value; $('workspace').hidden = !value; $('health').textContent = value ? '服务在线' : '等待登录'; if (!value) { $('api-key').value = '••••••••••••••••'; $('api-key').type = 'password'; $('show-key').textContent = '查看密钥'; $('tasks').replaceChildren(); $('logs').textContent = ''; $('screen').hidden = true; if (screenUrl) URL.revokeObjectURL(screenUrl); } }
function run(id, event, fn) { $(id).addEventListener(event, async e => { e.preventDefault(); try { await fn(); } catch (error) { notice(error.message); } }); }
async function refresh() {
  const data = await api('/settings');
  setSignedIn(true);
  $('connection').textContent = `实际网卡 IPv4：${data.addresses.join('、') || '未发现可用 IPv4'}\n监听端口：${data.port}\nLAN 访问：${data.allowRemote ? '已开启' : '未开启，仅本机'}\n连接地址：${data.addresses.map(ip => `${data.scheme}://${ip}:${data.port}`).join('  ')}\n传输：${data.scheme === 'https' ? 'TLS' : 'HTTP 明文，仅限可信网络'}\n文件根目录：${data.fileRoot}\n服务账户：${data.serviceIdentity}\n${data.desktopPolicy}`;
  await refreshTasks();
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
