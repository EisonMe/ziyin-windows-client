using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text.Json;
using System.Runtime.Versioning;
using ZiYin.Update;

namespace ZiYin.Updater;

[SupportedOSPlatform("windows")]
internal sealed class WindowsLifecycle(string install, string data) : IInstallLifecycle
{
    private readonly HashSet<int> sessions = [];
    public async Task Stop(CancellationToken token)
    {
        var savedSessions = Path.Combine(data, "updates", "tray-sessions.json");
        if (File.Exists(savedSessions)) foreach (var id in JsonSerializer.Deserialize<int[]>(File.ReadAllText(savedSessions)) ?? []) sessions.Add(id);
        // Record only existing trays and restart those sessions with their ordinary user token.
        foreach (var process in Process.GetProcessesByName("ZiYin.Tray"))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, Path.Combine(install, "tray", "ZiYin.Tray.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                    sessions.Add(process.SessionId);
                    PackageInstaller.Save(Path.Combine(data, "updates", "tray-sessions.json"), sessions.ToArray());
                    process.Kill(); await process.WaitForExitAsync(token);
                }
                catch (InvalidOperationException) { }
            }
        }
        using var service = new ServiceController("ZiYinRemote");
        if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) service.Stop();
        await Wait(service, ServiceControllerStatus.Stopped, token);
    }
    public async Task Start(CancellationToken token)
    {
        using var service = new ServiceController("ZiYinRemote"); service.Refresh();
        if (service.Status != ServiceControllerStatus.Running) service.Start();
        await Wait(service, ServiceControllerStatus.Running, token);
    }
    private static async Task Wait(ServiceController service, ServiceControllerStatus expected, CancellationToken token)
    {
        for (int i = 0; i < 120; i++) { service.Refresh(); if (service.Status == expected) return; await Task.Delay(500, token); }
        throw new IOException("服务状态变更超时。");
    }
    public async Task<bool> Healthy(string version, CancellationToken token)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "service.json")));
        JsonElement Find(string name) => config.RootElement.EnumerateObject().First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var port = Find("Port").GetInt32();
        var certPath = Find("CertificatePath").GetString();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        if (!string.IsNullOrEmpty(certPath))
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, Find("CertificatePassword").GetString());
            var pinned = cert.GetCertHash(HashAlgorithmName.SHA256);
            handler.ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null && CryptographicOperations.FixedTimeEquals(pinned, presented.GetCertHash(HashAlgorithmName.SHA256));
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var keyFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "api-key.json")));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyFile.RootElement.GetProperty("ApiKey").GetString());
        var uri = $"{(string.IsNullOrEmpty(certPath) ? "http" : "https")}://127.0.0.1:{port}/api/health";
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var response = await client.GetAsync(uri, token); response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                if (json.RootElement.GetProperty("version").GetString() == version && json.RootElement.GetProperty("status").GetString() == "ok") return true;
            }
            catch (Exception ex) when ((ex is HttpRequestException or JsonException or OperationCanceledException) && !token.IsCancellationRequested) { }
            await Task.Delay(1000, token);
        }
        return false;
    }
    public Task RestartTrays(CancellationToken token)
    {
        var savedSessions = Path.Combine(data, "updates", "tray-sessions.json");
        if (File.Exists(savedSessions)) foreach (var id in JsonSerializer.Deserialize<int[]>(File.ReadAllText(savedSessions)) ?? []) sessions.Add(id);
        foreach (var session in sessions)
        {
            token.ThrowIfCancellationRequested();
            try { SessionLauncher.Start(session, Path.Combine(install, "tray", "ZiYin.Tray.exe")); }
            catch { /* Tray restart is best-effort; service remains healthy, next logon starts it. */ }
        }
        if (File.Exists(savedSessions)) File.Delete(savedSessions);
        return Task.CompletedTask;
    }
}
