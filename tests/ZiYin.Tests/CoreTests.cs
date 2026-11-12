using ZiYin.Core;
using Xunit;

namespace ZiYin.Tests;

public class CoreTests
{
    [Fact]
    public void KeysAreRandomAndComparedExactly()
    {
        var first = Secrets.Generate(); var second = Secrets.Generate();
        Assert.Equal(64, first.Length); Assert.NotEqual(first, second);
        Assert.True(Secrets.FixedEquals(first, first)); Assert.False(Secrets.FixedEquals(first + "x", first));
        Assert.False(Secrets.FixedEquals(null, first)); Assert.False(Secrets.ValidKey("short"));
    }
    [Fact]
    public async Task CapturesBothStreamsAndExitCode()
    {
        await using var runner = new TaskRunner();
        var task = runner.Submit(new(OperatingSystem.IsWindows() ? "[Console]::Out.Write('hello'); [Console]::Error.Write('problem'); exit 7" : "printf hello; printf problem >&2; exit 7"));
        var done = await Finish(runner, task.Id);
        Assert.Equal("failed", done.Status); Assert.Equal(7, done.ExitCode);
        Assert.Contains("hello", done.StandardOutput); Assert.Contains("problem", done.StandardError);
    }
    [Fact]
    public async Task TimesOutAndCancelsRunningTasks()
    {
        await using var runner = new TaskRunner();
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var timed = runner.Submit(new(command, 1));
        Assert.Equal("timed_out", (await Finish(runner, timed.Id)).Status);
        var cancelled = runner.Submit(new(command));
        for (int i = 0; i < 100 && runner.Get(cancelled.Id)!.Status == "queued"; i++) await Task.Delay(20);
        Assert.True(runner.Cancel(cancelled.Id));
        Assert.Equal("cancelled", (await Finish(runner, cancelled.Id)).Status);
    }
    [Fact]
    public async Task CancellationOfQueuedTaskPreventsExecution()
    {
        await using var runner = new TaskRunner();
        string wait = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var a = runner.Submit(new(wait)); var b = runner.Submit(new(wait));
        var path = Path.Combine(Path.GetTempPath(), "ziyin-cancel-" + Guid.NewGuid().ToString("N"));
        var queued = runner.Submit(new(OperatingSystem.IsWindows() ? $"New-Item -ItemType File -Path '{path}'" : $"touch '{path}'"));
        runner.Cancel(queued.Id); Assert.Equal("cancelled", runner.Get(queued.Id)!.Status); runner.Cancel(a.Id); runner.Cancel(b.Id);
        Assert.Equal("cancelled", (await Finish(runner, queued.Id)).Status); Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task BoundsOutputWithoutBlockingChild()
    {
        await using var runner = new TaskRunner();
        var task = runner.Submit(new(OperatingSystem.IsWindows() ? "[Console]::Write(('x' * 600000))" : "head -c 600000 /dev/zero"));
        var done = await Finish(runner, task.Id);
        Assert.Equal("completed", done.Status); Assert.True(done.OutputTruncated); Assert.Equal(256 * 1024, done.StandardOutput.Length);
    }
    [Fact]
    public async Task PreservesChineseOutput()
    {
        await using var runner = new TaskRunner();
        var task = runner.Submit(new(OperatingSystem.IsWindows() ? "Write-Output '中文内容'" : "printf '中文内容'"));
        Assert.Contains("中文内容", (await Finish(runner, task.Id)).StandardOutput);
    }
    [Fact]
    public async Task RejectsInvalidTask()
    {
        await using var runner = new TaskRunner();
        Assert.Throws<ArgumentException>(() => runner.Submit(new("")));
        Assert.Throws<ArgumentException>(() => runner.Submit(new("echo x", 0)));
        Assert.Throws<ArgumentException>(() => runner.Submit(new("echo x", 1, "relative")));
    }
    internal static async Task<TaskSnapshot> Finish(TaskRunner runner, string id)
    {
        for (int i = 0; i < 400; i++)
        { var task = runner.Get(id)!; if (task.FinishedAt != null) return task; await Task.Delay(50); }
        throw new TimeoutException("Task failed to reach terminal state.");
    }
}
