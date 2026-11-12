using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ZiYin.Update;

namespace ZiYin.Service;

public sealed record UpdateView(string CurrentVersion, string? LatestVersion, string Status, string Message,
    DateTimeOffset? LastCheckedAt, DateTimeOffset? NextCheckAt, string Source, bool Enabled, int IntervalHours, InstallResult? LastResult);

public sealed class UpdateCoordinator(ServiceSettings settings, AuditLog audit) : BackgroundService
{
    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object gate = new();
    private string status = "idle", message = "等待启动检查。", source = "GitHub 直连";
    private string? latest;
    private DateTimeOffset? lastCheck, nextCheck;
    private bool pending;
    private string UpdateRoot => Path.Combine(settings.DataDirectory, "updates");
    public UpdateView Snapshot()
    {
        InstallResult? result = null;
        try { if (File.Exists(Path.Combine(UpdateRoot, "result.json"))) result = JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(Path.Combine(UpdateRoot, "result.json"))); } catch (IOException) { } catch (JsonException) { }
        lock (gate) return new(ReleaseVersion.Current, latest, status, message, lastCheck, nextCheck, source, settings.AutoUpdate, 3, result);
    }
    public bool RequestCheck()
    {
        lock (gate)
        {
            if (!OperatingSystem.IsWindows() || pending || status == "applying") return false;
            pending = true; requests.Writer.TryWrite(true); return true;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) { Set("unsupported", "自动安装仅在 Windows 服务安装中启用。"); return; }
        try
        {
            // Startup delay lets SCM, network and any interrupted-update recovery settle.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            if (settings.AutoUpdate) RequestCheck();
            var timer = PeriodicChecks(stoppingToken);
            await foreach (var _ in requests.Reader.ReadAllAsync(stoppingToken))
            {
                try { await Check(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
                { Set("failed", "检查或更新失败，旧版保持可用。请查看网络、磁盘或权限；下次定时检查重试。"); audit.Write("update.failed"); }
                finally { lock (gate) pending = false; }
            }
            await timer;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    private async Task PeriodicChecks(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(3));
        while (await timer.WaitForNextTickAsync(token)) if (settings.AutoUpdate) RequestCheck();
    }
    private async Task Check(CancellationToken token)
    {
        SecureStorage.ProtectDirectory(UpdateRoot);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(token); overall.CancelAfter(TimeSpan.FromMinutes(20));
        var ct = overall.Token;
        // File lock is shared with the detached installer and startup recovery.
        using var updateLock = new FileStream(Path.Combine(UpdateRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journalPath = Path.Combine(UpdateRoot, "journal.json");
        if (File.Exists(journalPath))
        {
            var journal = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journalPath));
            if (journal is not null && journal.Phase is not ("committed" or "rolled_back")) { Set("recovery_required", "存在未完成更新，等待开机恢复任务或管理员运行恢复命令。"); return; }
        }
        CleanupCompletedWork(journalPath);
        Set("checking", "正在检查最新稳定版及发布签名。");
        lock (gate) { lastCheck = DateTimeOffset.UtcNow; nextCheck = settings.AutoUpdate ? lastCheck.Value.AddHours(3) : null; }
        using var transport = new ReleaseTransport(settings.UpdateProxyPrefixes);
        var release = await transport.Latest(ct);
        lock (gate) { latest = release.Manifest.Version; source = transport.LastSource; }
        if (ReleaseVersion.Parse(latest) <= ReleaseVersion.Parse(ReleaseVersion.Current)) { Set("up_to_date", "当前已是最新稳定版；不会降级。"); return; }
        var install = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZiYin");
        if (!string.Equals(install.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase)) { Set("manual_required", "当前不在正式安装目录，请先运行安装脚本。"); return; }
        var work = Path.Combine(UpdateRoot, "work-" + Guid.NewGuid().ToString("N"));
        SecureStorage.ProtectDirectory(work);
        var package = Path.Combine(work, ReleaseManifest.AssetName);
        Set("downloading", "后台下载并验证更新；服务仍在运行。");
        await transport.Download(release.Manifest, package, ct);
        await File.WriteAllBytesAsync(Path.Combine(work, "update.json"), release.Json, ct);
        await File.WriteAllBytesAsync(Path.Combine(work, "update.json.sig"), release.Signature, ct);
        var worker = Path.Combine(work, "worker"); Directory.CreateDirectory(worker);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(install, "updater"))) File.Copy(file, Path.Combine(worker, Path.GetFileName(file)));
        lock (gate) source = transport.LastSource;
        var start = new ProcessStartInfo(Path.Combine(worker, "ZiYin.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "apply", work, settings.DataDirectory }) start.ArgumentList.Add(arg);
        Set("applying", "签名和 SHA256 已验证；即将短暂重启服务和已运行的托盘，正在运行的后台任务将中断。");
        audit.Write("update.applying");
        // Release before handing over. In-process state prevents a second request launching another worker.
        updateLock.Dispose();
        using var child = Process.Start(start) ?? throw new IOException("更新 helper 无法启动。");
        await child.WaitForExitAsync(token);
        if (child.ExitCode != 0) Set("failed", "更新程序未完成，旧服务仍运行。请查看上次结果或等待下次检查。");
    }
    private void CleanupCompletedWork(string journalPath)
    {
        // Called only while holding the cross-process lock and after excluding incomplete journals.
        if (File.Exists(journalPath))
        {
            var journal = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journalPath));
            var install = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            if (journal is not null)
                foreach (var path in new[] { journal.Backup, journal.Stage })
                    if (Path.GetDirectoryName(path) == install && (Path.GetFileName(path).StartsWith(".update-backup-", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith(".update-stage-", StringComparison.Ordinal))) TryCleanup(path);
        }
        foreach (var path in Directory.EnumerateDirectories(UpdateRoot, "work-*"))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) TryCleanup(path);
    }
    private static void TryCleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void Set(string state, string text) { lock (gate) { status = state; message = text; } }
}
