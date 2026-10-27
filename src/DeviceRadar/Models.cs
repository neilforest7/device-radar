namespace DeviceRadar;

public enum DeviceClassification { External, Internal, Virtual, Unknown }
public enum IdentityStrength { Weak, Instance }
public enum NamingLocationKind { Pnp, AudioPlayback, AudioRecording, BluetoothClassic, BluetoothLe }
public enum DeviceNameSource { PnpSystemName, BusReported, AudioEndpoint }

// Raw identifiers are local-only. Instance identity is scoped to this Windows installation and connection.
public sealed record DeviceIdentity(string InstanceId, string? ContainerId, IdentityStrength Strength);
public sealed record DeviceFunction(string InstanceId, string Name, Guid ClassGuid, string[] HardwareIds,
    string? ContainerId, string? ParentId, bool IsPresent);
public sealed record NamingLocation(string Id, NamingLocationKind Kind, string Name, string TargetId, bool IsPresent);
public sealed record DeviceSnapshot(string DeviceId, string Name, string Connection, DeviceClassification Classification,
    bool IsPresent, DeviceIdentity Identity, string? ContainerId, string[] HardwareIds,
    DeviceFunction[] Functions, NamingLocation[] Locations)
{
    public DeviceNameSource NameSource { get; init; } = DeviceNameSource.PnpSystemName;
    public string? NameSourceTargetId { get; init; }
}
