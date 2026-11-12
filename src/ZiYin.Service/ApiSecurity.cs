using System.Collections.Concurrent;
using System.Text.Json;
using ZiYin.Core;

namespace ZiYin.Service;

public sealed class ApiSecurity
{
    public const string CookieName = "ziyin_session";
    private readonly object gate = new();
    private readonly string keyPath;
    private string key;
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new();
    public ApiSecurity(ServiceSettings settings)
    {
        keyPath = Path.Combine(settings.DataDirectory, "api-key.json");
        if (File.Exists(keyPath)) key = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(keyPath))?.ApiKey ?? "";
        else { key = Secrets.Generate(); SecureStorage.WriteAtomic(keyPath, JsonSerializer.Serialize(new KeyFile(key))); }
        if (!Secrets.ValidKey(key)) throw new InvalidDataException("API 密钥格式不合法。");
    }
    public string CurrentKey { get { lock (gate) return key; } }
    public bool AuthenticateKey(string? value) { lock (gate) return Secrets.FixedEquals(value, key); }
    public string? Login(string? value)
    {
        lock (gate)
        {
            if (!Secrets.FixedEquals(value, key)) return null;
            foreach (var pair in sessions.Where(x => x.Value <= DateTimeOffset.UtcNow)) sessions.TryRemove(pair.Key, out _);
            if (sessions.Count >= 256) return null;
            string token = Secrets.Generate(); sessions[token] = DateTimeOffset.UtcNow.AddMinutes(30); return token;
        }
    }
    public bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateKey(header[7..]);
        return context.Request.Cookies.TryGetValue(CookieName, out var token) && token.Length == 64 &&
            sessions.TryGetValue(token, out var expiry) && expiry > DateTimeOffset.UtcNow;
    }
    public void Logout(HttpContext context) { if (context.Request.Cookies.TryGetValue(CookieName, out var token)) sessions.TryRemove(token, out _); }
    public string Rotate(string? value)
    {
        var replacement = string.IsNullOrEmpty(value) ? Secrets.Generate() : value;
        if (!Secrets.ValidKey(replacement)) throw new ArgumentException("密钥需 32–256 个可见 ASCII 字符；推荐留空生成随机密钥。");
        lock (gate)
        {
            SecureStorage.WriteAtomic(keyPath, JsonSerializer.Serialize(new KeyFile(replacement)));
            key = replacement; sessions.Clear(); return key;
        }
    }
    public static bool BrowserRequestAllowed(HttpRequest request)
    {
        // Host validation is performed separately. No CORS; reject cross-origin browser requests.
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)) return false;
        var fetch = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetch.Length > 0 && fetch != "same-origin" && fetch != "none") return false;
        if (request.Method is not ("GET" or "HEAD") && request.Headers["X-ZiYin-Request"] != "1") return false;
        return true;
    }
    private sealed record KeyFile(string ApiKey);
}
