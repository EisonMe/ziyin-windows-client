using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZiYin.Update;
using Xunit;

namespace ZiYin.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ziyin-update-test-" + Guid.NewGuid().ToString("N"));
    public UpdateTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.1.1", "1.1.0")]
    public void ComparesVersionsNumerically(string newer, string older) => Assert.True(ReleaseVersion.Parse(newer) > ReleaseVersion.Parse(older));
    [Theory]
    [InlineData("1.2.3-beta")][InlineData("v1.2.3")][InlineData("01.2.3")][InlineData("1.2")][InlineData("1.2.3+build")][InlineData("1.2.3.4")]
    public void RejectsNonStableVersions(string value) => Assert.Throws<InvalidDataException>(() => ReleaseVersion.Parse(value));
    [Fact]
    public void SignatureBindsVersionHashRepositoryAndPackage()
    {
        using var key = RSA.Create(2048); using var attacker = RSA.Create(2048);
        var json = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest(1, ReleaseManifest.Repo, "1.2.3", ReleaseManifest.AssetName, 12, new string('a',64)));
        var signature = key.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        Assert.Equal("1.2.3", ReleaseManifest.Verify(json, signature, key.ExportSubjectPublicKeyInfoPem()).Version);
        var altered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace("1.2.3", "9.9.9"));
        Assert.Throws<InvalidDataException>(() => ReleaseManifest.Verify(altered, signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<InvalidDataException>(() => ReleaseManifest.Verify(json, attacker.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), key.ExportSubjectPublicKeyInfoPem()));
    }
    [Fact]
    public async Task DirectFailureUsesAnonymousHttpsProxyAndRejectsTampering()
    {
        byte[] payload = Encoding.UTF8.GetBytes("verified package");
        var manifest = new ReleaseManifest(1, ReleaseManifest.Repo, "1.2.0", ReleaseManifest.AssetName, payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var handler = new FakeHandler(request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("https", request.RequestUri!.Scheme);
            return request.RequestUri.Host == "github.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var transport = new ReleaseTransport(["https://cdn.example/"], handler);
        var path = Path.Combine(root, "download.zip"); await transport.Download(manifest, path, CancellationToken.None);
        Assert.Equal(payload, File.ReadAllBytes(path)); Assert.Contains("cdn.example", transport.LastSource);
        File.WriteAllText(path, "tampered package"); await Assert.ThrowsAsync<InvalidDataException>(() => manifest.VerifyPackage(path));
    }
    [Fact]
    public async Task RejectsDowngradeToHttpRedirect()
    {
        var handler = new FakeHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("http://evil.example/pkg"); return r; });
        using var transport = new ReleaseTransport([], handler);
        await Assert.ThrowsAsync<IOException>(() => transport.Download(new(1, ReleaseManifest.Repo, "1.2.0", ReleaseManifest.AssetName, 1, new string('a',64)), Path.Combine(root,"x"), CancellationToken.None));
    }
    [Theory]
    [InlineData("http://cdn.example/")][InlineData("https://user:password@cdn.example/")][InlineData("https://cdn.example/?token=x")]
    public void RejectsCredentialBearingOrPlaintextProxy(string prefix) => Assert.Throws<ArgumentException>(() => new ReleaseTransport([prefix]));
    [Fact]
    public async Task UpgradePreservesConfigKeyCertificateAndFiles()
    {
        var install = MakeInstalled(); var data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
        foreach (var file in new[] {"service.json","api-key.json","server.pfx","user.txt"}) File.WriteAllText(Path.Combine(data,file), "preserve-"+file);
        var before = Directory.GetFiles(data).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
        var lifecycle = new FakeLifecycle(); var installer = new PackageInstaller(lifecycle);
        await installer.Apply(MakePackage("1.1.0"), install, Path.Combine(data,"journal.json"), "1.1.0");
        Assert.Equal("1.1.0",File.ReadAllText(Path.Combine(install,"VERSION")));
        Assert.Equal("new",File.ReadAllText(Path.Combine(install,"service","ZiYin.Service.exe")));
        foreach (var pair in before) Assert.Equal(pair.Value,File.ReadAllBytes(Path.Combine(data,pair.Key!)));
        Assert.Contains("committed",File.ReadAllText(Path.Combine(data,"journal.json")));
    }
    [Fact]
    public async Task FailedHealthRestoresOldVersionAndCanRecoverIdempotently()
    {
        var install=MakeInstalled(); var journal=Path.Combine(root,"data","journal.json");
        var lifecycle=new FakeLifecycle { FailNewHealth=true }; var engine=new PackageInstaller(lifecycle);
        await Assert.ThrowsAsync<IOException>(() => engine.Apply(MakePackage("1.1.0"),install,journal,"1.1.0"));
        Assert.Equal("1.0.0",File.ReadAllText(Path.Combine(install,"VERSION")));
        Assert.Equal("old",File.ReadAllText(Path.Combine(install,"service","ZiYin.Service.exe")));
        Assert.Contains("rolled_back",File.ReadAllText(journal));
        await engine.Recover(journal,CancellationToken.None);
        Assert.True(lifecycle.Starts>=2);
    }
    [Fact]
    public async Task FailedRollbackLeavesJournalIncompleteDespiteOlderSuccessResult()
    {
        var install=MakeInstalled(); var journal=Path.Combine(root,"data","journal.json");
        PackageInstaller.Save(Path.Combine(root,"data","result.json"),new InstallResult("rolled_back","1.0.0","previous attempt",DateTimeOffset.UtcNow.AddDays(-1)));
        var lifecycle=new FakeLifecycle { FailNewHealth=true, FailOldHealth=true };
        await Assert.ThrowsAsync<IOException>(()=>new PackageInstaller(lifecycle).Apply(MakePackage("1.1.0"),install,journal,"1.1.0"));
        var state=JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journal))!;
        Assert.Equal("replacing",state.Phase);
        Assert.True(Directory.Exists(state.Backup));
        lifecycle.FailOldHealth=false;
        await new PackageInstaller(lifecycle).Recover(journal,CancellationToken.None);
        Assert.Contains("rolled_back",File.ReadAllText(journal));
    }
    [Fact]
    public async Task InterruptedSwapRollsBackFromDurableJournal()
    {
        var install=MakeInstalled(); var backup=Path.Combine(install,".update-backup-test"); Directory.CreateDirectory(backup);
        var present=PackageInstaller.Managed.Where(n=>File.Exists(Path.Combine(install,n))||Directory.Exists(Path.Combine(install,n))).ToArray();
        Directory.Move(Path.Combine(install,"service"),Path.Combine(backup,"service"));
        Directory.CreateDirectory(Path.Combine(install,"service")); File.WriteAllText(Path.Combine(install,"service","ZiYin.Service.exe"),"partial-new");
        var journal=Path.Combine(root,"data","journal.json");
        PackageInstaller.Save(journal,new InstallJournal(install,"unused",backup,"1.1.0","1.0.0",present,"replacing"));
        await new PackageInstaller(new FakeLifecycle()).Recover(journal,CancellationToken.None);
        Assert.Equal("old",File.ReadAllText(Path.Combine(install,"service","ZiYin.Service.exe")));
    }
    [Fact]
    public async Task InvalidArchiveDoesNotStopOldService()
    {
        var lifecycle=new FakeLifecycle(); var zip=Path.Combine(root,"evil.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) { using var w=new StreamWriter(archive.CreateEntry("../escape").Open()); w.Write("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(()=>new PackageInstaller(lifecycle).Apply(zip,MakeInstalled(),Path.Combine(root,"j.json"),"1.1.0"));
        Assert.Equal(0,lifecycle.Stops); Assert.False(File.Exists(Path.Combine(root,"escape")));
    }
    [Fact]
    public async Task ShutdownFailureLeavesOriginalBinariesIntact()
    {
        var install=MakeInstalled();var lifecycle=new FakeLifecycle { FailStopOnce=true };
        await Assert.ThrowsAsync<IOException>(()=>new PackageInstaller(lifecycle).Apply(MakePackage("1.1.0"),install,Path.Combine(root,"data","journal.json"),"1.1.0"));
        Assert.Equal("old",File.ReadAllText(Path.Combine(install,"service","ZiYin.Service.exe")));
    }
    private string MakeInstalled()
    {
        var install=Path.Combine(root,"install");Directory.CreateDirectory(Path.Combine(install,"service"));
        File.WriteAllText(Path.Combine(install,"service","ZiYin.Service.exe"),"old");File.WriteAllText(Path.Combine(install,"VERSION"),"1.0.0");return install;
    }
    private string MakePackage(string version)
    {
        var zip=Path.Combine(root,"package.zip");using var archive=ZipFile.Open(zip,ZipArchiveMode.Create);
        foreach(var name in new[]{"service/ZiYin.Service.exe","tray/ZiYin.Tray.exe","updater/ZiYin.Updater.exe","scripts/Install.ps1","VERSION"})
        {using var w=new StreamWriter(archive.CreateEntry(name).Open());w.Write(name=="VERSION"?version:"new");}return zip;
    }
    public void Dispose()=>Directory.Delete(root,true);
    private sealed class FakeLifecycle:IInstallLifecycle
    {
        public bool FailNewHealth, FailStopOnce, FailOldHealth; public int Stops, Starts;
        public Task Stop(CancellationToken t) { Stops++; if(FailStopOnce){FailStopOnce=false;throw new IOException("stop unavailable");}return Task.CompletedTask; }
        public Task Start(CancellationToken t){Starts++;return Task.CompletedTask;}
        public Task<bool> Healthy(string v,CancellationToken t)=>Task.FromResult(!(FailNewHealth&&v=="1.1.0") && !(FailOldHealth&&v=="1.0.0"));
    }
    private sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(response(request));}
}
