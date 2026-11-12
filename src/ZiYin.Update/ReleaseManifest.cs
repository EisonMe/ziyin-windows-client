using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZiYin.Update;

public static partial class ReleaseVersion
{
    public static string Current => typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex StablePattern();
    public static Version Parse(string value)
    {
        if (!StablePattern().IsMatch(value) || !Version.TryParse(value, out var version)) throw new InvalidDataException("只接受三段式稳定版本，不接受预发布、构建后缀或非法版本。");
        return version;
    }
}

public sealed record ReleaseManifest(int Schema, string Repository, string Version, string Asset, long Size, string Sha256)
{
    public const string Repo = "EisonMe/ziyin-windows-client";
    public const string AssetName = "ziyin-windows-client-win-x64.zip";
    public static string PublicKey
    {
        get { using var stream = typeof(ReleaseManifest).Assembly.GetManifestResourceStream("ZiYin.Update.release-public.pem")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    }
    public static ReleaseManifest Verify(byte[] json, byte[] signature, string? publicKey = null)
    {
        if (json.Length > 16384 || signature.Length > 1024) throw new InvalidDataException("签名清单过大。");
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey ?? PublicKey);
        if (!rsa.VerifyData(json, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new InvalidDataException("更新签名不可信，已保留旧版本。");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("清单为空。");
        manifest.Validate(); return manifest;
    }
    public void Validate()
    {
        if (Schema != 1 || Repository != Repo || Asset != AssetName || Size is < 1 or > 400L * 1024 * 1024 || !Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新清单字段不合法。");
        ReleaseVersion.Parse(Version);
    }
    public Uri DownloadUri => new($"https://github.com/{Repo}/releases/download/v{Version}/{Asset}");
    public async Task VerifyPackage(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length != Size) throw new InvalidDataException("更新包大小不匹配。");
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(Sha256))) throw new InvalidDataException("更新包 SHA256 不匹配。");
    }
}
