using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace ZiYin.Service;

public sealed class ServiceSettings
{
    public bool AutoUpdate { get; set; } = true;
    public string[] UpdateProxyPrefixes { get; set; } = ["https://ghfast.top/"];
    public int Port { get; set; } = 17643;
    public bool AllowRemote { get; set; }
    public bool TrustedLanHttp { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZiYin", "service");
    public string FileRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZiYin", "files");
    public bool Tls => !string.IsNullOrWhiteSpace(CertificatePath);
    public static ServiceSettings Load()
    {
        var path = Environment.GetEnvironmentVariable("ZIYIN_CONFIG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZiYin", "service", "service.json");
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("配置为空。") : new ServiceSettings();
        if (settings.Port is < 1024 or > 65535) throw new InvalidDataException("Port 必须为 1024–65535。");
        if (settings.AllowRemote && !settings.Tls && !settings.TrustedLanHttp) throw new InvalidDataException("开放 LAN 必须配置 TLS 或显式选择 TrustedLanHttp。");
        if (!Path.IsPathFullyQualified(settings.DataDirectory) || !Path.IsPathFullyQualified(settings.FileRoot)) throw new InvalidDataException("目录必须为绝对路径。");
        if (settings.UpdateProxyPrefixes is null || settings.UpdateProxyPrefixes.Length > 3) throw new InvalidDataException("更新代理最多 3 个。");
        foreach (var proxy in settings.UpdateProxyPrefixes) ZiYin.Update.ReleaseTransport.ValidateProxy(proxy);
        SecureStorage.ProtectDirectory(settings.DataDirectory);
        SecureStorage.ProtectDirectory(settings.FileRoot);
        return settings;
    }
    public string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .Select(a => a.ToString()).Distinct().Order().ToArray();
}

public static class SecureStorage
{
    public static void ProtectDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("受保护目录不能是链接。");
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static void WriteAtomic(string path, string contents)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
