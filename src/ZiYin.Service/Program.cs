using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ZiYin.Core;
using ZiYin.Service;

var settings = ServiceSettings.Load();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(options => options.ServiceName = "ZiYinRemote");
// Deliberately exclude HTTP request logging: credentials never belong in URLs or logs.
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = FileStore.MaxBytes + 65536;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    server.Listen(settings.AllowRemote ? IPAddress.Any : IPAddress.Loopback, settings.Port, endpoint =>
    {
        if (settings.Tls) endpoint.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath!, settings.CertificatePassword));
    });
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<ApiSecurity>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<DesktopClient>();
builder.Services.AddSingleton(sp => new TaskRunner(sp.GetRequiredService<AuditLog>().Write));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var security = app.Services.GetRequiredService<ApiSecurity>();
var audit = app.Services.GetRequiredService<AuditLog>();
// Initialize job management eagerly so host disposal always terminates active work.
_ = app.Services.GetRequiredService<TaskRunner>();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    var host = context.Request.Host.Host;
    if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
        !(IPAddress.TryParse(host, out var address) && (IPAddress.IsLoopback(address) || settings.Addresses().Contains(address.ToString()))))
    { context.Response.StatusCode = 400; return; }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!ApiSecurity.BrowserRequestAllowed(context.Request)) { context.Response.StatusCode = 403; return; }
        if (context.Request.Path != "/api/auth/login" && !security.Authorized(context)) { context.Response.StatusCode = 401; return; }
    }
    try { await next(); }
    catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (FileNotFoundException) { context.Response.StatusCode = 404; }
    catch (DirectoryNotFoundException) { context.Response.StatusCode = 404; }
    catch (UnauthorizedAccessException) { context.Response.StatusCode = 403; }
    catch (IOException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "文件正被使用或存储不可用。" }); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception) { audit.Write("request.failed"); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "内部错误；请检查服务状态和审计日志。" }); }
});
app.UseRateLimiter();
app.UseDefaultFiles();
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".md"] = "text/plain; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
app.MapPost("/api/auth/login", (LoginRequest request, HttpContext context) =>
{
    var token = security.Login(request.ApiKey);
    if (token is null) return Results.Unauthorized();
    context.Response.Cookies.Append(ApiSecurity.CookieName, token, new CookieOptions { HttpOnly = true, Secure = settings.Tls, SameSite = SameSiteMode.Strict, Path = "/api", MaxAge = TimeSpan.FromMinutes(30), IsEssential = true });
    audit.Write("session.login"); return Results.Ok(new { expiresInSeconds = 1800 });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", (HttpContext context) => { security.Logout(context); context.Response.Cookies.Delete(ApiSecurity.CookieName, new CookieOptions { Path = "/api" }); return Results.NoContent(); });
app.MapGet("/api/health", () => new { status = audit.Healthy ? "ok" : "degraded", auditHealthy = audit.Healthy, version = "1.0.0", utc = DateTimeOffset.UtcNow, platform = OperatingSystem.IsWindows() ? "windows" : "development", taskStatePersistent = false });
app.MapGet("/api/settings", () => new { addresses = settings.Addresses(), port = settings.Port, scheme = settings.Tls ? "https" : "http", allowRemote = settings.AllowRemote,
    trustedLanHttp = settings.TrustedLanHttp, fileRoot = settings.FileRoot, serviceIdentity = OperatingSystem.IsWindows() ? "LocalSystem (默认安装)" : "当前开发用户",
    desktopPolicy = "仅本机活动控制台会话；托盘需手动授权；不支持锁屏、登录界面和安全桌面。" });
app.MapGet("/api/key", () => Results.Ok(new { apiKey = security.CurrentKey }));
app.MapPut("/api/key", (KeyRequest request) => { var key = security.Rotate(request.ApiKey); audit.Write("key.rotated"); return Results.Ok(new { apiKey = key, sessionsInvalidated = true }); });
app.MapGet("/api/tasks", (TaskRunner tasks) => tasks.List());
app.MapPost("/api/tasks", (TaskRequest request, TaskRunner tasks) =>
{
    try { var task = tasks.Submit(request); return Results.Accepted($"/api/tasks/{task.Id}", task); }
    catch (InvalidOperationException) { return Results.Json(new { error = "队列已满或服务正在停止。" }, statusCode: 429); }
});
app.MapGet("/api/tasks/{id}", (string id, TaskRunner tasks) => tasks.Get(id) is { } task ? Results.Ok(task) : Results.NotFound());
app.MapGet("/api/tasks/{id}/output", (string id, TaskRunner tasks) => tasks.Get(id) is { } task ? Results.Ok(new { task.StandardOutput, task.StandardError, task.OutputTruncated, task.Status }) : Results.NotFound());
app.MapPost("/api/tasks/{id}/cancel", (string id, TaskRunner tasks) => tasks.Cancel(id) ? Results.Accepted($"/api/tasks/{id}") : Results.NotFound());
app.MapGet("/api/logs", (AuditLog logs) => Results.Ok(new { entries = logs.Read() }));
app.MapGet("/api/files", (string? path, FileStore files) => Results.Ok(files.List(path)));
app.MapGet("/api/files/content", (string path, FileStore files) =>
{
    var file = files.Resolve(path);
    if (new FileInfo(file).Length > FileStore.MaxBytes) return Results.BadRequest(new { error = "下载上限为 10 MiB。" });
    return Results.File(file, "application/octet-stream", Path.GetFileName(file), enableRangeProcessing: false);
});
app.MapPut("/api/files/content", async (string path, FileStore files, HttpContext context) =>
{ await files.Write(path, context.Request.Body, context.RequestAborted); audit.Write("file.written"); return Results.NoContent(); });
app.MapPost("/api/files/directory", (PathRequest request, FileStore files) => { Directory.CreateDirectory(files.Resolve(request.Path)); audit.Write("directory.created"); return Results.NoContent(); });
app.MapDelete("/api/files", (string path, FileStore files) =>
{ var file = files.Resolve(path); if (!File.Exists(file)) return Results.NotFound(); File.Delete(file); audit.Write("file.deleted"); return Results.NoContent(); });
app.MapGet("/api/desktop/status", async (DesktopClient desktop, HttpContext context) => Results.Ok(await desktop.Send(new("status"), context.RequestAborted)));
app.MapPost("/api/desktop/screenshot", async (DesktopClient desktop, HttpContext context) =>
{
    var result = await desktop.Send(new("screenshot"), context.RequestAborted);
    if (!result.Available || result.ImageBase64 is null) return Results.Json(result, statusCode: 409);
    audit.Write("desktop.screenshot"); return Results.File(Convert.FromBase64String(result.ImageBase64), "image/png");
});
app.MapPost("/api/desktop/input", async (DesktopRequest request, DesktopClient desktop, HttpContext context) =>
{
    if (request.Action is not ("move" or "click" or "text" or "key")) return Results.BadRequest(new { error = "Action 必须为 move/click/text/key。" });
    var result = await desktop.Send(request, context.RequestAborted);
    audit.Write("desktop.input"); return Results.Json(result, statusCode: result.Available ? 200 : 409);
});
audit.Write("service.started");
app.Run();

public partial class Program { }
public sealed record LoginRequest(string? ApiKey);
public sealed record KeyRequest(string? ApiKey);
public sealed record PathRequest(string Path);
