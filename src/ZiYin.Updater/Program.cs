using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ZiYin.Update;
using ZiYin.Updater;

if (!OperatingSystem.IsWindows()) return 2;
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
if (args.Length != 3 || args[0] is not ("apply" or "recover")) return 4;
var work = Path.GetFullPath(args[1]);
var data = Path.GetFullPath(args[2]);
var updateRoot = Path.Combine(data, "updates");
var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZiYin");
// A local administrator already controls SYSTEM. Reject accidental alternate roots and reparse points.
if (!work.StartsWith(updateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 5;
foreach (var path in new[] { install, data, updateRoot, work })
    if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return 6;
var journalPath = Path.Combine(updateRoot, "journal.json");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
FileStream? updateLock = null;
WindowsLifecycle? lifecycle = null;
try
{
    for (int i = 0; i < 20 && updateLock is null; i++)
    {
        try { updateLock = new FileStream(Path.Combine(updateRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { await Task.Delay(500, deadline.Token); }
    }
    if (updateLock is null) return 7;
    // Re-check after acquiring the lock: a manual uninstall may have won the handoff race.
    if (!Directory.Exists(install)) return 11;
    if (args[0] == "apply" && (!File.Exists(Path.Combine(install, "service", "ZiYin.Service.exe")) || !File.Exists(Path.Combine(install, "VERSION")))) return 11;
    using (var serviceKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ZiYinRemote"))
    {
        var registered = serviceKey?.GetValue("ImagePath") as string;
        if (!string.Equals(registered?.Trim().Trim('"'), Path.Combine(install, "service", "ZiYin.Service.exe"), StringComparison.OrdinalIgnoreCase)) return 12;
    }
    lifecycle = new WindowsLifecycle(install, data);
    var engine = new PackageInstaller(lifecycle);
    if (args[0] == "recover")
    {
        if (File.Exists(journalPath))
        {
            var journal = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journalPath));
            if (journal is null || !string.Equals(journal.InstallRoot, install, StringComparison.OrdinalIgnoreCase) ||
                !journal.Backup.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !journal.Stage.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 8;
        }
        await engine.Recover(journalPath, deadline.Token);
    }
    else
    {
        var release = ReleaseManifest.Verify(await File.ReadAllBytesAsync(Path.Combine(work, "update.json"), deadline.Token),
            await File.ReadAllBytesAsync(Path.Combine(work, "update.json.sig"), deadline.Token));
        var oldVersion = File.Exists(Path.Combine(install, "VERSION")) ? File.ReadAllText(Path.Combine(install, "VERSION")).Trim() : "1.0.0";
        if (ReleaseVersion.Parse(release.Version) <= ReleaseVersion.Parse(oldVersion)) return 9;
        var package = Path.Combine(work, ReleaseManifest.AssetName);
        await release.VerifyPackage(package, deadline.Token);
        if (File.Exists(journalPath))
        {
            var existing = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journalPath));
            if (existing is not null && existing.Phase is not ("committed" or "rolled_back")) return 10;
        }
        var command = $"\"{Environment.ProcessPath}\" recover \"{work}\" \"{data}\"";
        await Run("schtasks.exe", ["/Create", "/TN", "ZiYinUpdateRecovery", "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", command, "/F"], deadline.Token);
        await engine.Apply(package, install, journalPath, release.Version, deadline.Token);
    }
    await lifecycle.RestartTrays(deadline.Token);
    await Run("schtasks.exe", ["/Delete", "/TN", "ZiYinUpdateRecovery", "/F"], deadline.Token, ignoreFailure: true);
    return 0;
}
catch (Exception ex)
{
    // No secrets, URLs, API response bodies or arbitrary exception messages in status files.
    try
    {
        var resultPath = Path.Combine(updateRoot, "result.json");
        var previous = File.Exists(resultPath) ? JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(resultPath)) : null;
        if (previous?.Status == "rolled_back")
            await Run("schtasks.exe", ["/Delete", "/TN", "ZiYinUpdateRecovery", "/F"], CancellationToken.None, ignoreFailure: true);
        if (previous?.Status != "rolled_back") PackageInstaller.Save(resultPath, new InstallResult("failed", ReleaseVersion.Current, $"更新失败 ({ex.GetType().Name})。旧文件保留；如果服务未恢复，请运行恢复命令。", DateTimeOffset.UtcNow));
    }
    catch { }
    if (lifecycle is not null) { try { await lifecycle.RestartTrays(CancellationToken.None); } catch { } }
    return 1;
}
finally { updateLock?.Dispose(); }

static async Task Run(string exe, string[] args, CancellationToken token, bool ignoreFailure = false)
{
    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new IOException("无法启动系统工具。");
    var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
    await process.WaitForExitAsync(token); await Task.WhenAll(stdout, stderr);
    if (process.ExitCode != 0 && !ignoreFailure) throw new IOException("无法注册开机恢复任务。");
}
