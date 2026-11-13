using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ZiYin.Service;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace ZiYin.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ziyin-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private string key = "";
    private string? previousConfig;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new ServiceSettings { AutoUpdate = false, DataDirectory = Path.Combine(directory, "data"), FileRoot = Path.Combine(directory, "files") }));
        previousConfig = Environment.GetEnvironmentVariable("ZIYIN_CONFIG"); Environment.SetEnvironmentVariable("ZIYIN_CONFIG", config);
        factory = new WebApplicationFactory<Program>();
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        key = factory.Services.GetRequiredService<ApiSecurity>().CurrentKey;
        return Task.CompletedTask;
    }
    [Theory]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/api/updates")]
    [InlineData("POST", "/api/updates/check")]
    [InlineData("GET", "/api/settings")]
    [InlineData("GET", "/api/key")]
    [InlineData("GET", "/api/tasks")]
    [InlineData("GET", "/api/logs")]
    [InlineData("GET", "/api/files")]
    [InlineData("GET", "/api/desktop/status")]
    [InlineData("POST", "/api/tasks")]
    [InlineData("PUT", "/api/key")]
    [InlineData("POST", "/api/desktop/input")]
    public async Task SensitiveEndpointsRequireAuthentication(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-ZiYin-Request", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }
    [Fact]
    public async Task RejectsCrossOriginMissingCsrfAndDnsRebinding()
    {
        Auth();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/key")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/key")).StatusCode);
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/tasks", new { command = "echo x" })).StatusCode);
        client.DefaultRequestHeaders.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/health")).StatusCode);
    }
    [Fact]
    public async Task LoginRotationInvalidatesKeyAndCookieAndKeepsKeyOutOfLogs()
    {
        client.DefaultRequestHeaders.Add("X-ZiYin-Request", "1");
        var login = await client.PostAsJsonAsync("/api/auth/login", new { apiKey = key });
        login.EnsureSuccessStatusCode();
        var setCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", setCookie.ToLowerInvariant()); Assert.Contains("samesite=strict", setCookie.ToLowerInvariant());
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/key")).StatusCode);
        var rotate = await client.PutAsJsonAsync("/api/key", new { apiKey = (string?)null });
        rotate.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await rotate.Content.ReadAsStringAsync()); var newKey = json.RootElement.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(key, newKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/key")).StatusCode);
        Auth(); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/key")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newKey);
        var log = await client.GetStringAsync("/api/logs"); Assert.DoesNotContain(key, log); Assert.DoesNotContain(newKey, log);
        Assert.Equal("no-store", (await client.GetAsync("/api/key")).Headers.CacheControl!.ToString());
    }
    [Fact]
    public async Task FilesRoundTripAndRejectTraversal()
    {
        Auth(); client.DefaultRequestHeaders.Add("X-ZiYin-Request", "1");
        (await client.PutAsync("/api/files/content?path=hello.txt", new StringContent("中文内容"))).EnsureSuccessStatusCode();
        Assert.Equal("中文内容", await client.GetStringAsync("/api/files/content?path=hello.txt"));
        foreach (var path in new[] { "../outside", "C:\\Windows\\test", "a/../b", "a:stream", "../" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/files/content?path=" + Uri.EscapeDataString(path))).StatusCode);
        (await client.DeleteAsync("/api/files?path=hello.txt")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/files/content?path=hello.txt")).StatusCode);
    }
    [Fact]
    public void FileStoreRejectsLinks()
    {
        var files = factory.Services.GetRequiredService<FileStore>();
        var outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside);
        var link = Path.Combine(directory, "files", "link");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<ArgumentException>(() => files.Resolve("link/secret.txt"));
        Directory.Delete(link);
    }
    [Fact]
    public async Task ApiExecutesAndReportsOutput()
    {
        Auth(); client.DefaultRequestHeaders.Add("X-ZiYin-Request", "1");
        var response = await client.PostAsJsonAsync("/api/tasks", new { command = "echo api-ok", timeoutSeconds = 10 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var task = await CoreTests.Finish(factory.Services.GetRequiredService<ZiYin.Core.TaskRunner>(), json.RootElement.GetProperty("id").GetString()!);
        Assert.Equal("completed", task.Status); Assert.Contains("api-ok", task.StandardOutput);
    }
    [Fact]
    public async Task LoginIsRateLimited()
    {
        client.DefaultRequestHeaders.Add("X-ZiYin-Request", "1");
        for (int i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { apiKey = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login", new { apiKey = "wrong" })).StatusCode);
    }
    [Fact]
    public async Task StaticUiAndDocsArePackagedWithSecurityHeaders()
    {
        var ui = await client.GetAsync("/"); ui.EnsureSuccessStatusCode();
        var html = await ui.Content.ReadAsStringAsync();
        Assert.Contains("<html", html);
        Assert.Contains("id=\"copy-skill-prompt\"", html);
        Assert.Contains("id=\"copy-astrbot-prompt\"", html);
        Assert.Contains("frame-ancestors 'none'", ui.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("PowerShell", await client.GetStringAsync("/API.zh-CN.md"));
        var appJs = await client.GetStringAsync("/app.js");
        Assert.Contains("use strict", appJs);
        Assert.Contains("async function copyText(text)", appJs);
        Assert.Contains("document.execCommand?.('copy')", appJs);
    }
    [Fact]
    public async Task AuditStorageFailureDegradesHealthWithoutLosingTaskWorkers()
    {
        Auth(); client.DefaultRequestHeaders.Add("X-ZiYin-Request", "1");
        var logPath = Path.Combine(directory, "data", "audit.jsonl");
        if (File.Exists(logPath)) File.Delete(logPath);
        Directory.CreateDirectory(logPath);
        var response = await client.PostAsJsonAsync("/api/tasks", new { command = "echo audit-safe", timeoutSeconds = 10 });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var task = await CoreTests.Finish(factory.Services.GetRequiredService<ZiYin.Core.TaskRunner>(), json.RootElement.GetProperty("id").GetString()!);
        Assert.Equal("completed", task.Status);
        Assert.Contains("degraded", await client.GetStringAsync("/api/health"));
    }
    private void Auth() => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    public async Task DisposeAsync()
    {
        client.Dispose(); await factory.DisposeAsync();
        Environment.SetEnvironmentVariable("ZIYIN_CONFIG", previousConfig);
        Directory.Delete(directory, true);
    }
}
