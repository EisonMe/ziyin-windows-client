[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$project = Split-Path $PSScriptRoot -Parent
$version = (Get-Content "$project/VERSION" -Raw).Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Only stable three-part versions can be signed.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") { throw 'Git tag does not match VERSION.' }
if (-not $env:ZIYIN_RELEASE_SIGNING_KEY) { throw 'Missing release signing secret.' }
Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
public static class ZiYinReleaseSigner {
    public static byte[] Sign(string privatePem, string publicPem, byte[] body) {
        // Never let a PowerShell binder stringify a private-key argument in an error.
        try {
            using (var rsa = RSA.Create()) using (var expected = RSA.Create()) {
                rsa.ImportFromPem(privatePem); expected.ImportFromPem(publicPem);
                if (!CryptographicOperations.FixedTimeEquals(rsa.ExportSubjectPublicKeyInfo(), expected.ExportSubjectPublicKeyInfo())) return null;
                return rsa.SignData(body, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
        } catch { return null; }
    }
}
'@
try {
    $zip = Join-Path $Output 'ziyin-windows-client-win-x64.zip'
    $manifest = [ordered]@{schema=1;repository='EisonMe/ziyin-windows-client';version=$version;asset='ziyin-windows-client-win-x64.zip';size=(Get-Item $zip).Length;sha256=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Compress))
    [IO.File]::WriteAllBytes("$Output/update.json",$bytes)
    $signature = [ZiYinReleaseSigner]::Sign($env:ZIYIN_RELEASE_SIGNING_KEY, (Get-Content "$project/src/ZiYin.Update/release-public.pem" -Raw), $bytes)
    if (-not $signature) { throw "Release signing failed: invalid key or public-key mismatch." }
    [IO.File]::WriteAllBytes("$Output/update.json.sig",$signature)
    Write-Host "Signed stable release v$version. No private key was written to the package."
} finally { $env:ZIYIN_RELEASE_SIGNING_KEY = $null }
