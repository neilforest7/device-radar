param(
    [Parameter(Mandatory = $true)][string]$RecordToken,
    [Parameter(Mandatory = $true)][ValidateSet('original', 'proposed')][string]$ExpectedState,
    [string]$ProbePath
)

# Independent acceptance check. Use the built-in 64-bit Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or ![Environment]::Is64BitProcess) {
    throw 'Requires 64-bit Windows PowerShell 5.1'
}
if ([string]::IsNullOrEmpty($ProbePath)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ProbePath = Join-Path $scriptDirectory 'bin\Release\net10.0-windows\win-x64\DeviceRadar.NativeProbe.exe'
}

function Get-ShortHash([string]$Value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $digest = $algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)) }
    finally { $algorithm.Dispose() }
    return ([BitConverter]::ToString($digest).Replace('-', '').Substring(0, 12)).ToLowerInvariant()
}

function Get-WinRtProperty($Device, [string]$Key) {
    # IMapView's indexer returns null in this runtime; enumerate its key/value pairs.
    foreach ($entry in $Device.Properties) {
        if ([string]::Equals($entry.Key, $Key, [StringComparison]::OrdinalIgnoreCase)) { return $entry.Value }
    }
    return $null
}

function Get-AsyncResult($Operation, [Type]$ResultType) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.IsGenericType -and
        $_.GetParameters()[0].ParameterType.GetGenericTypeDefinition().FullName -eq 'Windows.Foundation.IAsyncOperation`1'
    } | Select-Object -First 1
    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    if (!$task.Wait(15000)) { throw 'Audio device enumeration timed out' }
    return ,$task.Result
}

$probeOutput = & $ProbePath verify --record $RecordToken
if ($LASTEXITCODE -ne 0) { throw 'Bound native probe verification failed' }
$verified = ($probeOutput -join "`n") | ConvertFrom-Json
if ($verified.synchronizedState -ne $ExpectedState -and $verified.synchronizedState -ne 'original-and-proposed') {
    throw 'Native endpoint is not in the expected state'
}

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Devices.MediaDevice, Windows.Media.Devices, ContentType = WindowsRuntime]
$singleType = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime]
$collectionType = [Windows.Devices.Enumeration.DeviceInformationCollection, Windows.Devices.Enumeration, ContentType = WindowsRuntime]
$requested = [System.Collections.Generic.List[string]]::new()
@('System.ItemNameDisplay', 'System.Devices.DeviceInstanceId', 'System.Devices.InterfaceEnabled',
    '{026e516e-b814-414b-83cd-856d6fef4822} 2') | ForEach-Object { $requested.Add($_) }
$devices = Get-AsyncResult ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync(
    [Windows.Media.Devices.MediaDevice]::GetAudioCaptureSelector(), $requested,
    [Windows.Devices.Enumeration.DeviceInformationKind]::DeviceInterface)) $collectionType
$matches = @($devices | Where-Object {
    $instance = [string](Get-WinRtProperty $_ 'System.Devices.DeviceInstanceId')
    $instance.Length -gt 0 -and (Get-ShortHash $instance.ToUpperInvariant()) -eq $verified.target.hash
})
if ($matches.Count -ne 1) { throw 'Bound capture device interface is not unique' }
$fresh = Get-AsyncResult ([Windows.Devices.Enumeration.DeviceInformation]::CreateFromIdAsync(
    $matches[0].Id, $requested, [Windows.Devices.Enumeration.DeviceInformationKind]::DeviceInterface)) $singleType

$views = @()
foreach ($item in @(@{ Kind = 'FindAll'; Device = $matches[0] }, @{ Kind = 'CreateFromIdFresh'; Device = $fresh })) {
    $device = $item.Device
    $name = [string]$device.Name
    $displayName = [string](Get-WinRtProperty $device 'System.ItemNameDisplay')
    $interfaceName = [string](Get-WinRtProperty $device '{026e516e-b814-414b-83cd-856d6fef4822} 2')
    $instance = [string](Get-WinRtProperty $device 'System.Devices.DeviceInstanceId')
    if ((Get-ShortHash $instance.ToUpperInvariant()) -ne $verified.target.hash -or
        !(Get-WinRtProperty $device 'System.Devices.InterfaceEnabled') -or
        $name -cne $verified.target.systemName -or $displayName -cne $name -or $interfaceName -cne $name) {
        throw 'Fresh WinRT device names or identity do not match the bound endpoint'
    }
    $views += [pscustomobject]@{ view = $item.Kind; name = $name; itemNameDisplay = $displayName; interfaceFriendlyName = $interfaceName }
}

[pscustomobject]@{
    status = 'passed'; timeUtc = [DateTime]::UtcNow.ToString('O'); expectedState = $ExpectedState
    targetHash = $verified.target.hash; interfaceIdHash = (Get-ShortHash ([string]$fresh.Id))
    views = $views; systemWrites = 0
} | ConvertTo-Json -Depth 5 -Compress
