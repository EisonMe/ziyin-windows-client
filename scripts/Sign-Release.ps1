[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$project = Split-Path $PSScriptRoot -Parent
$version = (Get-Content "$project/VERSION" -Raw).Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Only stable three-part versions can be signed.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") { throw 'Git tag does not match VERSION.' }
if (-not $env:ZIYIN_RELEASE_SIGNING_KEY) { throw 'Missing release signing secret.' }
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($env:ZIYIN_RELEASE_SIGNING_KEY)
    $expected = [Security.Cryptography.RSA]::Create()
    try {
        $expected.ImportFromPem((Get-Content "$project/src/ZiYin.Update/release-public.pem" -Raw))
        if ([Convert]::ToBase64String($expected.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())) { throw 'Signing secret does not match embedded public key.' }
    } finally { $expected.Dispose() }
    $zip = Join-Path $Output 'ziyin-windows-client-win-x64.zip'
    $manifest = [ordered]@{schema=1;repository='EisonMe/ziyin-windows-client';version=$version;asset='ziyin-windows-client-win-x64.zip';size=(Get-Item $zip).Length;sha256=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()}
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Compress))
    [IO.File]::WriteAllBytes("$Output/update.json",$bytes)
    $signature = $rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes("$Output/update.json.sig",$signature)
    Write-Host "Signed stable release v$version. No private key was written to the package."
} finally { $rsa.Dispose(); $env:ZIYIN_RELEASE_SIGNING_KEY = $null }
