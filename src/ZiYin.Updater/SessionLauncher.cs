using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ZiYin.Updater;

[SupportedOSPlatform("windows")]
internal static class SessionLauncher
{
    public static void Start(int session, string executable)
    {
        if (session <= 0 || !WTSQueryUserToken((uint)session, out var token)) return;
        using (token)
        {
            if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                if (!CreateProcessAsUser(token, executable, new StringBuilder('"' + executable + '"'), IntPtr.Zero, IntPtr.Zero, false, 0x00000400, environment, Path.GetDirectoryName(executable)!, ref startup, out var process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(process.Process); CloseHandle(process.Thread);
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    { public int Size; public string? Reserved, Desktop, Title; public int X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags; public short ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
