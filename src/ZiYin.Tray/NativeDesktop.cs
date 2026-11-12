using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ZiYin.Core;

namespace ZiYin.Tray;

internal static class NativeDesktop
{
    public static bool IsOrdinaryDesktop()
    {
        if (WTSGetActiveConsoleSessionId() != (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId) return false;
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, 512, out _) && name.ToString() == "Default";
        }
        finally { CloseDesktop(desktop); }
    }
    public static DesktopResponse Capture()
    {
        if (!IsOrdinaryDesktop()) return new(false, "当前不在普通桌面。");
        var bounds = SystemInformation.VirtualScreen;
        if ((long)bounds.Width * bounds.Height > 33_177_600) return new(false, "屏幕总像素超出截图限制。");
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        if (!IsOrdinaryDesktop()) return new(false, "截图时桌面已切换，结果已丢弃。");
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        if (stream.Length > 16 * 1024 * 1024) return new(false, "PNG 超过 16 MiB。");
        return new(true, "普通桌面截图。", Convert.ToBase64String(stream.ToArray()), bounds.Width, bounds.Height, bounds.Left, bounds.Top);
    }
    public static DesktopResponse Input(DesktopRequest request)
    {
        if (!IsOrdinaryDesktop()) return new(false, "当前不在普通桌面。");
        if (request.Action is "move" or "click")
        {
            if (!SystemInformation.VirtualScreen.Contains(request.X, request.Y)) return new(false, "坐标超出虚拟桌面。");
            if (!SetCursorPos(request.X, request.Y)) return new(false, "无法移动指针。");
            if (request.Action == "click") Send([Mouse(0x0002), Mouse(0x0004)]);
        }
        else if (request.Action == "text")
        {
            if (request.Text is not { Length: > 0 and <= 1000 }) return new(false, "Text 必须为 1–1000 个 UTF-16 字符。");
            var inputs = request.Text.SelectMany(c => new[] { Keyboard(0, c, 0x0004), Keyboard(0, c, 0x0004 | 0x0002) }).ToArray();
            Send(inputs);
        }
        else if (request.Action == "key")
        {
            ushort vk = request.Key?.ToUpperInvariant() switch
            { "ENTER" => 0x0D, "TAB" => 0x09, "ESCAPE" => 0x1B, "BACKSPACE" => 0x08, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, _ => 0 };
            if (vk == 0) return new(false, "Key 仅支持 ENTER/TAB/ESCAPE/BACKSPACE/LEFT/UP/RIGHT/DOWN。");
            Send([Keyboard(vk, '\0', 0), Keyboard(vk, '\0', 0x0002)]);
        }
        else return new(false, "未知操作。");
        return new(true, "已向当前普通桌面发送输入；受 UIPI 和目标程序权限限制，需观察实际效果。");
    }
    private static void Send(InputEvent[] events)
    {
        if (!IsOrdinaryDesktop() || SendInput((uint)events.Length, events, Marshal.SizeOf<InputEvent>()) != events.Length) throw new InvalidOperationException();
    }
    private static InputEvent Mouse(uint flags) => new() { Type = 0, Data = new Union { Mouse = new MouseInput { Flags = flags } } };
    private static InputEvent Keyboard(ushort vk, char scan, uint flags) => new() { Type = 1, Data = new Union { Keyboard = new KeyboardInput { VirtualKey = vk, Scan = scan, Flags = flags } } };
    [StructLayout(LayoutKind.Sequential)] private struct InputEvent { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, InputEvent[] events, int size);
}
