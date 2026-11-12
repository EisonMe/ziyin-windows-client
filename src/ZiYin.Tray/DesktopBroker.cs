using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ZiYin.Core;

namespace ZiYin.Tray;

internal sealed class DesktopBroker : IDisposable
{
    public volatile bool Enabled;
    private readonly CancellationTokenSource stop = new();
    public void Start() => _ = Task.Run(Listen);
    private async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
                await using var pipe = NamedPipeServerStreamAcl.Create($"ZiYin.Desktop.{Process.GetCurrentProcess().SessionId}", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, security);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = new StringBuilder(); var character = new char[1];
                while (await reader.ReadAsync(character.AsMemory(), deadline.Token) > 0 && character[0] != '\n')
                { line.Append(character[0]); if (line.Length > 32768) throw new InvalidDataException(); }
                var request = JsonSerializer.Deserialize<DesktopRequest>(line.ToString());
                var response = request is null ? new DesktopResponse(false, "请求无效。") : Handle(request);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteAsync(JsonSerializer.Serialize(response).AsMemory(), deadline.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception) { try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    private DesktopResponse Handle(DesktopRequest request)
    {
        if (!Enabled) return new(false, "请在本机托盘中手动允许本次登录的截图与输入。");
        if (!NativeDesktop.IsOrdinaryDesktop()) return new(false, "当前是锁屏、登录界面或安全桌面，拒绝操作。");
        try
        {
            if (request.Action == "status") return new(true, "当前普通桌面可用；权限等同于托盘用户。");
            if (request.Action == "screenshot") return NativeDesktop.Capture();
            return NativeDesktop.Input(request);
        }
        catch (Exception) { return new(false, "当前桌面不可访问、权限不足或操作参数无效。"); }
    }
    public void Dispose() { Enabled = false; stop.Cancel(); }
}
