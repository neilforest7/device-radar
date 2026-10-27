$ErrorActionPreference = 'Stop'
$radarDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $radarDotnet)) {
    $radarDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$radarSdkVersion = & $radarDotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($radarSdkVersion.Split('.')[0]) -lt 10) {
    throw '需要 .NET 10 SDK 或更新版本。'
}
& $radarDotnet run --project (Join-Path $PSScriptRoot 'prototypes\DeviceRadar.Preview\DeviceRadar.Preview.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
