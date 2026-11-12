using System.Diagnostics;
using System.Text.Json;

namespace ZiYin.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ZiYin.Tray", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly DesktopBroker broker = new();
    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 WebUI", null, (_, _) => OpenUi());
        menu.Items.Add("查看 API 密钥（需要管理员）", null, (_, _) => ReadKey());
        var enable = new ToolStripMenuItem("允许本次登录的截图与输入") { CheckOnClick = false };
        enable.Click += (_, _) =>
        {
            if (!broker.Enabled && MessageBox.Show("开启后，持有 API 密钥的人可查看当前屏幕、移动鼠标和输入文字。仅限普通桌面；不支持锁屏和 UAC 安全桌面。确定开启？", "紫音 · 本机授权", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            broker.Enabled = !broker.Enabled; enable.Checked = broker.Enabled;
            icon!.Text = broker.Enabled ? "紫音 · 桌面控制已开启" : "紫音 · 桌面控制未开启";
        };
        menu.Items.Add(enable);
        menu.Items.Add("退出托盘（后台服务继续运行）", null, (_, _) => ExitThread());
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "紫音 · 桌面控制未开启", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => OpenUi();
        broker.Start();
    }
    private static void OpenUi()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZiYin", "client.json");
            using var config = JsonDocument.Parse(File.ReadAllText(path));
            var port = config.RootElement.GetProperty("port").GetInt32();
            var scheme = config.RootElement.GetProperty("scheme").GetString() == "https" ? "https" : "http";
            if (port is < 1024 or > 65535) throw new InvalidDataException();
            Process.Start(new ProcessStartInfo($"{scheme}://localhost:{port}/") { UseShellExecute = true });
        }
        catch (Exception) { MessageBox.Show("无法读取连接信息。请先运行安装脚本，并确认服务已启动。", "紫音"); }
    }
    private static void ReadKey()
    {
        try
        {
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "scripts", "Get-ApiKey.ps1"));
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception) { MessageBox.Show("未完成管理员授权。可手动以管理员身份运行 Get-ApiKey.ps1。", "紫音"); }
    }
    protected override void ExitThreadCore()
    {
        icon.Visible = false; icon.Dispose(); broker.Dispose(); base.ExitThreadCore();
    }
}
