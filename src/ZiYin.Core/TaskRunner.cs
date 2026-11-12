using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace ZiYin.Core;

public sealed class TaskRunner : IAsyncDisposable
{
    private sealed class Entry(TaskRequest request)
    {
        public readonly object Gate = new();
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly TaskRequest Request = request;
        public readonly CancellationTokenSource Cancel = new();
        public readonly StringBuilder Out = new(), Err = new();
        public readonly DateTimeOffset Created = DateTimeOffset.UtcNow;
        public string Status = "queued";
        public DateTimeOffset? Started, Finished;
        public int? ExitCode;
        public string? Error;
        public bool Truncated;
        public TaskSnapshot Snapshot() { lock (Gate) return new(Id, Status, Created, Started, Finished, ExitCode, Error, Out.ToString(), Err.ToString(), Truncated); }
    }
    private const int OutputLimit = 256 * 1024;
    private readonly object admission = new();
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(64);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] workers;
    private readonly Action<string, string> audit;
    public TaskRunner(Action<string, string>? audit = null)
    {
        this.audit = audit ?? ((_, _) => { });
        workers = Enumerable.Range(0, 2).Select(_ => Task.Run(Worker)).ToArray();
    }
    public TaskSnapshot Submit(TaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 32768)
            throw new ArgumentException("Command 必须为 1–32768 个字符。");
        if (request.TimeoutSeconds is < 1 or > 86400) throw new ArgumentException("TimeoutSeconds 必须为 1–86400。");
        if (request.WorkingDirectory is not null && (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory)))
            throw new ArgumentException("WorkingDirectory 必须是已存在的绝对目录。");
        lock (admission)
        {
            foreach (var old in entries.Values.OrderBy(e => e.Created).Where(e => e.Snapshot().FinishedAt != null).Take(Math.Max(0, entries.Count - 199)))
                if (entries.TryRemove(old.Id, out var removed)) removed.Cancel.Dispose();
            if (shutdown.IsCancellationRequested || entries.Count >= 200) throw new InvalidOperationException("任务容量已满或服务正在停止。");
            var entry = new Entry(request);
            entries[entry.Id] = entry;
            if (!queue.Writer.TryWrite(entry)) { entries.TryRemove(entry.Id, out _); entry.Cancel.Dispose(); throw new InvalidOperationException("任务队列已满。"); }
            audit("task.submitted", entry.Id);
            return entry.Snapshot();
        }
    }
    public TaskSnapshot? Get(string id) => entries.TryGetValue(id, out var e) ? e.Snapshot() : null;
    public TaskSnapshot[] List() => entries.Values.Select(e => e.Snapshot()).OrderByDescending(e => e.CreatedAt).ToArray();
    public bool Cancel(string id)
    {
        lock (admission)
        {
            if (!entries.TryGetValue(id, out var e)) return false;
            lock (e.Gate) { if (e.Finished == null) { e.Cancel.Cancel(); if (e.Status == "queued") { e.Status = "cancelled"; e.Finished = DateTimeOffset.UtcNow; } } }
            audit("task.cancel.requested", id); return true;
        }
    }
    private async Task Worker()
    {
        try { await foreach (var e in queue.Reader.ReadAllAsync(shutdown.Token)) await Execute(e); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
    private async Task Execute(Entry e)
    {
        lock (e.Gate) { if (e.Finished != null) return; e.Status = "running"; e.Started = DateTimeOffset.UtcNow; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(e.Request.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(e.Cancel.Token, timeout.Token, shutdown.Token);
        using var p = new Process();
        WindowsJob? job = null;
        Task readOut = Task.CompletedTask, readErr = Task.CompletedTask;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "/bin/sh",
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = e.Request.WorkingDirectory ?? AppContext.BaseDirectory
            };
            // Do not pass service credentials or hosting configuration to child processes.
            p.StartInfo.Environment.Clear();
            foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "COMSPEC", "PATHEXT", "PSModulePath",
                "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "ProgramData", "ProgramFiles",
                "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
                "COMPUTERNAME", "USERNAME", "USERDOMAIN", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE" })
                if (Environment.GetEnvironmentVariable(name) is { } value) p.StartInfo.Environment[name] = value;
            if (OperatingSystem.IsWindows())
            {
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding; " + e.Request.Command)) }) p.StartInfo.ArgumentList.Add(arg);
            }
            else { p.StartInfo.ArgumentList.Add("-c"); p.StartInfo.ArgumentList.Add(e.Request.Command); }
            p.Start(); p.StandardInput.Close();
            if (OperatingSystem.IsWindows()) job = WindowsJob.Attach(p);
            lock (e.Gate) { e.Status = "running"; e.Started = DateTimeOffset.UtcNow; }
            readOut = Drain(p.StandardOutput, e, e.Out);
            readErr = Drain(p.StandardError, e, e.Err);
            await p.WaitForExitAsync(linked.Token);
            // Closing the job also stops descendants left behind by the shell.
            job?.Dispose(); job = null;
            await Task.WhenAll(readOut, readErr).WaitAsync(linked.Token);
            lock (e.Gate) { e.ExitCode = p.ExitCode; e.Status = p.ExitCode == 0 ? "completed" : "failed"; }
        }
        catch (OperationCanceledException)
        {
            Kill(p); job?.Dispose(); job = null;
            lock (e.Gate) { e.Status = shutdown.IsCancellationRequested ? "interrupted" : e.Cancel.IsCancellationRequested ? "cancelled" : "timed_out"; }
        }
        catch (Exception ex)
        {
            Kill(p);
            lock (e.Gate) { e.Status = "failed"; e.Error = $"进程执行失败 ({ex.GetType().Name})"; }
        }
        finally
        {
            job?.Dispose();
            try { await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            lock (e.Gate) e.Finished = DateTimeOffset.UtcNow;
            audit("task." + e.Snapshot().Status, e.Id);
        }
    }
    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    private static async Task Drain(StreamReader reader, Entry e, StringBuilder target)
    {
        var buffer = new char[4096]; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
        {
            lock (e.Gate)
            {
                int available = OutputLimit - target.Length;
                target.Append(buffer, 0, Math.Min(n, available));
                if (n > available) e.Truncated = true;
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); queue.Writer.TryComplete();
        await Task.WhenAll(workers);
        foreach (var e in entries.Values) e.Cancel.Dispose();
        shutdown.Dispose();
    }
}
