[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts'), [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$project = Split-Path $PSScriptRoot -Parent
Push-Location $project
try {
    if (-not $SkipTests) { dotnet test tests/ZiYin.Tests/ZiYin.Tests.csproj -c Release --logger 'trx;LogFileName=tests.trx'; if ($LASTEXITCODE -ne 0) { throw 'Tests failed' } }
    $package = Join-Path $Output 'ziyin-windows-client-win-x64'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    foreach ($part in @(@('ZiYin.Service','service'),@('ZiYin.Tray','tray'),@('ZiYin.Updater','updater'))) {
        dotnet publish "src/$($part[0])/$($part[0]).csproj" -c Release -r win-x64 --self-contained true -o "$package/$($part[1])" -p:PublishSingleFile=false -p:DebugType=None
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($part[0])" }
    }
    foreach ($folder in @('scripts','docs')) { Copy-Item -LiteralPath "$project/$folder" -Destination $package -Recurse -Force }
    Copy-Item "$project/README.md" $package -Force
    Copy-Item "$project/VERSION" $package -Force
    Get-ChildItem $package -Recurse -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $([IO.Path]::GetRelativePath($package, $_.Path))" } | Set-Content "$Output/SHA256SUMS.txt"
    Compress-Archive -Path "$package/*" -DestinationPath "$Output/ziyin-windows-client-win-x64.zip" -Force
    Get-FileHash "$Output/ziyin-windows-client-win-x64.zip" -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  ziyin-windows-client-win-x64.zip" } | Add-Content "$Output/SHA256SUMS.txt"
} finally { Pop-Location }
