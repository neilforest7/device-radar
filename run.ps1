$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') | ForEach-Object { [Console]::Error.WriteLine($_) }
& (Join-Path $PSScriptRoot 'src\DeviceRadar\bin\Release\net10.0-windows\win-x64\DeviceRadar.exe') @args
exit $LASTEXITCODE
