using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZiYin.Core;

namespace ZiYin.Service;

public sealed class DesktopClient
{
    public async Task<DesktopResponse> Send(DesktopRequest request, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "桌面功能仅支持 Windows。");
        uint session = WTSGetActiveConsoleSessionId();
        if (session is 0 or uint.MaxValue) return new(false, "当前没有活动的本机控制台用户会话。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", $"ZiYin.Desktop.{session}", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return new(false, "无法验证托盘身份。");
            using var process = Process.GetProcessById((int)pid);
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tray", "ZiYin.Tray.exe"));
            if (process.SessionId != session || !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return new(false, "托盘路径或会话不匹配，请使用正式安装目录中的托盘。");
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var text = new StringBuilder(); var buffer = new char[8192];
            while (true)
            {
                int n = await reader.ReadAsync(buffer.AsMemory(), deadline.Token);
                if (n == 0) break;
                text.Append(buffer, 0, n);
                if (text.Length > 24 * 1024 * 1024) return new(false, "截图超出传输上限。");
            }
            return JsonSerializer.Deserialize<DesktopResponse>(text.ToString()) ?? new(false, "托盘返回为空。");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(false, "托盘未启动、连接超时或当前桌面不可访问。"); }
    }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
