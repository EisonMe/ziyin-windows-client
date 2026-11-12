using System.Security.Cryptography;
using System.Text;

namespace ZiYin.Core;

public static class Secrets
{
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static bool ValidKey(string? key) => key is { Length: >= 32 and <= 256 } && key.All(c => c is >= '!' and <= '~');
    public static bool FixedEquals(string? candidate, string expected)
    {
        if (candidate is null || candidate.Length > 4096) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
