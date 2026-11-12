using System.IO.Compression;
using System.Text.Json;

namespace ZiYin.Update;

public interface IInstallLifecycle
{
    Task Stop(CancellationToken token);
    Task Start(CancellationToken token);
    Task<bool> Healthy(string version, CancellationToken token);
}
public sealed record InstallJournal(string InstallRoot, string Stage, string Backup, string Version, string OldVersion, string[] PreviouslyPresent, string Phase);
public sealed record InstallResult(string Status, string Version, string Message, DateTimeOffset At);

public sealed class PackageInstaller(IInstallLifecycle lifecycle)
{
    public static readonly string[] Managed = ["service", "tray", "updater", "scripts", "docs", "README.md", "VERSION"];
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public static void Extract(string zip, string destination, string version)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 5000) throw new InvalidDataException("包内文件数超过限制。");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (name.StartsWith('/') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' ')) || !Managed.Contains(parts[0]) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(name)) throw new InvalidDataException("包内包含越界、重复或链接路径。");
            total += entry.Length;
            if (total > 1800L * 1024 * 1024 || entry.Length > 500L * 1024 * 1024) throw new InvalidDataException("解压大小超限。");
            var target = Path.Combine(destination, Path.Combine(parts));
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        foreach (var name in new[] { "service/ZiYin.Service.exe", "tray/ZiYin.Tray.exe", "updater/ZiYin.Updater.exe", "scripts/Install.ps1", "VERSION" })
            if (!File.Exists(Path.Combine(destination, name))) throw new InvalidDataException("更新包缺少必要文件。");
        if (File.ReadAllText(Path.Combine(destination, "VERSION")).Trim() != version) throw new InvalidDataException("包内版本与签名不一致。");
    }
    public async Task Apply(string zip, string installRoot, string journalPath, string version, CancellationToken token = default)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(installRoot, ".update-stage-" + suffix);
        var backup = Path.Combine(installRoot, ".update-backup-" + suffix);
        var oldVersion = File.Exists(Path.Combine(installRoot, "VERSION")) ? File.ReadAllText(Path.Combine(installRoot, "VERSION")).Trim() : "1.0.0";
        InstallJournal? journal = null;
        try
        {
            Extract(zip, stage, version);
            Directory.CreateDirectory(backup);
            journal = new(installRoot, stage, backup, version, oldVersion, Managed.Where(n => Exists(Path.Combine(installRoot, n))).ToArray(), "prepared");
            Save(journalPath, journal);
            await lifecycle.Stop(token);
            Save(journalPath, journal with { Phase = "replacing" });
            foreach (var name in Managed)
            {
                token.ThrowIfCancellationRequested();
                var live = Path.Combine(installRoot, name); var saved = Path.Combine(backup, name); var incoming = Path.Combine(stage, name);
                if (Exists(live)) Move(live, saved);
                if (Exists(incoming)) Move(incoming, live);
            }
            await lifecycle.Start(token);
            if (!await lifecycle.Healthy(version, token)) throw new IOException("新服务未通过版本和健康检查。");
            Save(journalPath, journal with { Phase = "committed" });
            Save(Path.Combine(Path.GetDirectoryName(journalPath)!, "result.json"), new InstallResult("updated", version, "更新成功，配置、密钥和证书保持原样。", DateTimeOffset.UtcNow));
        }
        catch
        {
            if (journal is not null)
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await Recover(journalPath, rollback.Token);
            }
            throw;
        }
        finally
        {
            // Only task-owned staging folders; never delete ProgramData or user files.
            TryDelete(stage);
        }
    }
    public async Task Recover(string journalPath, CancellationToken token)
    {
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journalPath)) ?? throw new InvalidDataException("恢复记录损坏。");
        if (journal.Phase is "committed" or "rolled_back") return;
        await lifecycle.Stop(token);
        foreach (var name in Managed)
        {
            var live = Path.Combine(journal.InstallRoot, name); var saved = Path.Combine(journal.Backup, name);
            if (Exists(saved)) { Delete(live); Move(saved, live); }
            else if (!journal.PreviouslyPresent.Contains(name)) Delete(live);
        }
        await lifecycle.Start(token);
        if (!await lifecycle.Healthy(journal.OldVersion, token)) throw new IOException("旧服务已恢复文件但健康检查失败，需要本机管理员检查。");
        Save(journalPath, journal with { Phase = "rolled_back" });
        Save(Path.Combine(Path.GetDirectoryName(journalPath)!, "result.json"), new InstallResult("rolled_back", journal.OldVersion, "更新失败，已恢复旧版本。", DateTimeOffset.UtcNow));
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void Move(string source, string dest) { if (Directory.Exists(source)) Directory.Move(source, dest); else File.Move(source, dest); }
    private static void Delete(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
    private static void TryDelete(string path) { try { Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
