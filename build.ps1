param([ValidateSet('build','publish','package')][string]$Action = 'build')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DeviceRadar\DeviceRadar.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if ($Action -ne 'package') {
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$candidates = @()
if (Test-Path -LiteralPath $localDotnet) { $candidates += $localDotnet }
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($systemDotnet) { $candidates += $systemDotnet.Source }
$dotnet = $null
foreach ($candidate in $candidates) {
    $sdks = @(& $candidate --list-sdks)
    if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^10\.\d+\.\d+' -and [version](($_ -split ' ')[0] -split '-')[0] -ge [version]'10.0.100' })) { $dotnet = $candidate; break }
}
if (-not $dotnet) { throw 'The .NET 10 SDK (10.0.100 or newer) is required. Install it or place it in .tools\dotnet.' }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
if ($Action -eq 'build') {
    & $dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    return
}
}
$release = Join-Path $root "artifacts\release\v$version-win-x64"
$zip = "$release.zip"
$zipHash = "$zip.sha256"
if ($Action -eq 'publish' -and ((Test-Path -LiteralPath $release) -or (Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $zipHash))) { throw "Release output already exists at $release. Move or remove it before rebuilding." }
$releaseFiles = @('docs\PORTABLE-README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'docs\SUPPORT.md', 'docs\ACCEPTANCE.md', 'third-party\USB-LICENSE.txt', 'third-party\PCI-LICENSE.txt')
foreach ($file in $releaseFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) { throw "Missing release file: $file" }
}
if ($Action -eq 'publish') {
    $stage = Join-Path $root ('.tools\publish-' + [Guid]::NewGuid().ToString('N'))
    try {
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        New-Item -ItemType Directory -Path $release -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $stage 'DeviceRadar.exe') -Destination $release
    } finally {
        if (Test-Path -LiteralPath $stage) {
            $stagePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $stage).Path)
            $prefix = [IO.Path]::GetFullPath((Join-Path $root '.tools\publish-'))
            if (-not $stagePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $stagePath.Length -le $prefix.Length) { throw 'Refusing to remove an unexpected staging path.' }
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }
    }
}
$executable = Join-Path $release 'DeviceRadar.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing validated executable: $executable" }
$packageFiles = @($executable)
foreach ($file in $releaseFiles) {
    $destination = if ($file -eq 'docs\PORTABLE-README.md') { Join-Path $release 'README.md' } else { Join-Path $release ([IO.Path]::GetFileName($file)) }
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination -Force
    $packageFiles += $destination
}
$packageFiles = @($packageFiles | Sort-Object { [IO.Path]::GetFileName($_) })
$lines = foreach ($file in $packageFiles) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file) }
$checksums = Join-Path $release 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksums, [string[]]$lines, [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath ($packageFiles + $checksums) -DestinationPath $zip -CompressionLevel Optimal -Force
$digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zipHash, "$digest  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release: $release"
Write-Host "Archive: $zip"
