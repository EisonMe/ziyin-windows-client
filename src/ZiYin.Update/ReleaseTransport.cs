using System.Net;

namespace ZiYin.Update;

// A dedicated anonymous client: never reuse a browser/API client or attach credentials.
public sealed class ReleaseTransport : IDisposable
{
    private readonly HttpClient client;
    private readonly string[] proxies;
    private readonly TimeSpan metadataTimeout;
    public string LastSource { get; private set; } = "GitHub 直连";
    public ReleaseTransport(string[] proxies, HttpMessageHandler? handler = null, TimeSpan? metadataTimeout = null)
    {
        foreach (var prefix in proxies) ValidateProxy(prefix);
        this.proxies = proxies;
        this.metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(15);
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZiYin-Windows-Update/1.1");
    }
    public static void ValidateProxy(string prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !prefix.EndsWith('/')) throw new ArgumentException("下载代理必须是无凭据、无查询参数、以 / 结尾的 HTTPS 前缀。");
    }
    public IEnumerable<Uri> Sources(Uri original)
    {
        if (original.Scheme != "https" || original.Host != "github.com" || !original.AbsolutePath.StartsWith("/" + ReleaseManifest.Repo + "/releases/", StringComparison.Ordinal)) throw new InvalidDataException("禁止下载其它仓库或非 HTTPS 地址。");
        yield return original;
        foreach (var proxy in proxies) yield return new Uri(proxy + original.AbsoluteUri);
    }
    public async Task<(ReleaseManifest Manifest, byte[] Json, byte[] Signature)> Latest(CancellationToken token)
    {
        var original = new Uri($"https://github.com/{ReleaseManifest.Repo}/releases/latest/download/update.json");
        foreach (var source in Sources(original))
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var json = await ReadBytes(source, 16384, metadataTimeout, token);
                    var signature = await ReadBytes(new Uri(source.AbsoluteUri + ".sig"), 1024, metadataTimeout, token);
                    var manifest = ReleaseManifest.Verify(json, signature);
                    LastSource = source == original ? "GitHub 直连" : $"第三方代理：{source.Host}";
                    return (manifest, json, signature);
                }
                catch (Exception ex) when (Retryable(ex) && !token.IsCancellationRequested)
                { if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(1), token); }
            }
        }
        throw new IOException("无法取得可信的稳定版清单。直连及配置的代理均失败；保持当前版本。");
    }
    public async Task Download(ReleaseManifest manifest, string destination, CancellationToken token)
    {
        foreach (var source in Sources(manifest.DownloadUri))
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await Fetch(source, destination, manifest.Size, TimeSpan.FromMinutes(4), token);
                    await manifest.VerifyPackage(destination, token);
                    LastSource = source == manifest.DownloadUri ? "GitHub 直连" : $"第三方代理：{source.Host}";
                    return;
                }
                catch (Exception ex) when (Retryable(ex) && !token.IsCancellationRequested)
                { if (File.Exists(destination)) File.Delete(destination); if (attempt == 0) await Task.Delay(1000, token); }
            }
        }
        throw new IOException("更新下载或签名清单 SHA256 验证失败；保持当前版本。");
    }
    private static bool Retryable(Exception ex) => ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException;
    private async Task<byte[]> ReadBytes(Uri uri, int max, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(timeout);
        using var response = await Get(uri, cts.Token);
        await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
        using var buffer = new MemoryStream(); await CopyBounded(source, buffer, max, cts.Token); return buffer.ToArray();
    }
    private async Task Fetch(Uri uri, string path, long max, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(timeout);
        using var response = await Get(uri, cts.Token);
        await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await CopyBounded(source, target, max, cts.Token);
    }
    private async Task<HttpResponseMessage> Get(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new InvalidDataException("拒绝不安全的下载重定向。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location; response.Dispose();
                if (next is null) throw new IOException("缺少重定向地址。");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next); continue;
            }
            if (!response.IsSuccessStatusCode) { response.Dispose(); throw new HttpRequestException("Release 下载端返回错误状态。"); }
            return response;
        }
        throw new IOException("下载重定向次数超限。");
    }
    private static async Task CopyBounded(Stream input, Stream output, long max, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; long total = 0; int n;
        while ((n = await input.ReadAsync(buffer, token)) != 0)
        { total += n; if (total > max) throw new InvalidDataException("下载内容超过已验证大小限制。"); await output.WriteAsync(buffer.AsMemory(0, n), token); }
    }
    public void Dispose() => client.Dispose();
}
