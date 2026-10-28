using System.Runtime.InteropServices;

namespace DeviceRadar.Naming.Native;

// Each token selects one compiled-in surface; records cannot supply a property key or registry path.
internal static class Targets
{
    public const string Pnp = "pnp";
    public const string AudioPlayback = "audio-playback";
    public const string AudioRecording = "audio-recording";
    public const string BluetoothClassic = "bluetooth-classic";
    internal static readonly Guid BluetoothClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    internal static readonly Guid AudioEndpointClass = new("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");
    internal static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    internal static readonly Guid UsbClass = new("36fc9e60-c465-11cf-8056-444553540000");
    internal static readonly Guid HidClass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    public static void RequireWritable(string kind)
    {
        if (kind is not (Pnp or AudioPlayback or AudioRecording or BluetoothClassic)) throw new ProbeException("invalid-target-kind");
    }

    public static bool IsAudio(string kind) => kind is AudioPlayback or AudioRecording;
    public static bool RequiresAdministrator(string kind) => kind is Pnp or BluetoothClassic;

    public static uint PropertyType(string kind)
    {
        RequireWritable(kind);
        return IsAudio(kind) ? Format.RegistryString : kind == BluetoothClassic ? Format.RegistryBinary : Format.String;
    }

    public static RawValue Proposed(string kind, string name)
    {
        RequireWritable(kind);
        return kind == BluetoothClassic ? ClassicTarget.EncodeAlias(name) : Format.Name(name, PropertyType(kind));
    }

    public static string? ValueText(string kind, RawValue value)
        => kind == BluetoothClassic ? ClassicTarget.AliasText(value) : Format.Text(value, PropertyType(kind));

    public static void ValidateValue(string kind, RawValue value)
    {
        if (kind == BluetoothClassic) ClassicTarget.ValidateValue(value);
        else if (IsAudio(kind)) Safety.ValidateAudioValue(value);
        else Safety.ValidateOriginal(value);
    }

    public static string ResolvedSuffix(OperationRecord record)
    {
        if (!IsAudio(record.TargetKind)) return "";
        Safety.ValidateAudioValue(record.Original);
        var split = Format.Text(record.Original, Format.RegistryString)!.Length;
        if (split > record.OriginalResolvedName.Length
            || !record.OriginalResolvedName.StartsWith(Format.Text(record.Original, Format.RegistryString)!, StringComparison.Ordinal))
            throw new ProbeException("invalid-record-resolved-suffix");
        return record.OriginalResolvedName[split..];
    }

    public static void ValidateBinding(string kind, DeviceIdentity target)
    {
        RequireWritable(kind);
        ValidateNode(target.InstanceId, target.ClassGuid, target.HardwareIds, target.ContainerId);
        if (target.EndpointContainerId is not null && (!IsAudio(kind)
            || !Guid.TryParseExact(target.EndpointContainerId, "D", out var endpointContainer) || endpointContainer == Guid.Empty))
            throw new ProbeException("invalid-record-target");
        if (target.Ancestors is null || target.Ancestors.Length > 3) throw new ProbeException("invalid-record-target");
        foreach (var node in target.Ancestors)
        {
            if (node is null) throw new ProbeException("invalid-record-target");
            ValidateNode(node.InstanceId, node.ClassGuid, node.HardwareIds, node.ContainerId);
        }
        if (kind == Pnp)
        {
            if (target.EndpointId is not null || target.RadioAddress is not null) throw new ProbeException("invalid-record-target");
            PnpTarget.ValidateBinding(target);
        }
        else if (IsAudio(kind))
        {
            if (target.RadioAddress is not null) throw new ProbeException("invalid-record-target");
            AudioTarget.ValidateBinding(target);
        }
        else ClassicTarget.ValidateBinding(target);
    }

    private static void ValidateNode(string instance, Guid classGuid, string[] hardwareIds, string? container)
    {
        if (instance is not { Length: > 0 and <= 4096 } || instance.Any(char.IsControl) || classGuid == Guid.Empty
            || hardwareIds is not { Length: <= 32 }
            || hardwareIds.Any(id => id is not { Length: > 0 and <= 4096 } || id.Any(char.IsControl))
            || (container is not null && (!Guid.TryParseExact(container, "D", out var id) || id == Guid.Empty)))
            throw new ProbeException("invalid-record-target");
    }

    public static TargetSession Open(string kind, DeviceIdentity? bound = null, string? targetId = null) => kind switch
    {
        Pnp => new PnpTarget(bound?.InstanceId ?? targetId ?? throw new ProbeException("target-required"), bound),
        AudioPlayback => new AudioTarget(bound?.EndpointId ?? targetId ?? throw new ProbeException("target-required"), 0, bound),
        AudioRecording => new AudioTarget(bound?.EndpointId ?? targetId ?? throw new ProbeException("target-required"), 1, bound),
        BluetoothClassic => new ClassicTarget(bound?.InstanceId ?? targetId ?? throw new ProbeException("target-required"), bound),
        _ => throw new ProbeException("invalid-target-kind")
    };

    public static string Location(string kind) => kind switch
    {
        Pnp => "pnp-friendly-name",
        AudioPlayback or AudioRecording => "audio-policy-main-store-device-description",
        BluetoothClassic => "bluetooth-local-alias",
        _ => throw new ProbeException("invalid-target-kind")
    };
}

internal abstract class TargetSession : IDisposable
{
    public abstract IReadOnlyList<DeviceSnapshot> Candidates { get; }
    public DeviceSnapshot Unique() => Candidates.Count == 1 ? Candidates[0]
        : throw new ProbeException(Candidates.Count == 0 ? "target-offline" : "target-ambiguous");
    public abstract TargetWriteLease OpenWriter(RawValue value);
    public abstract void Dispose();
}

internal sealed class TargetWriteLease(Action write, IDisposable? resource = null, Func<int>? notifications = null) : IDisposable
{
    public void Write() => write();
    public int? NotificationCount => notifications?.Invoke();
    public void Dispose() => resource?.Dispose();
}

internal sealed class DeviceCatalog : IDisposable
{
    private readonly DeviceInfoSet handle;
    public List<DeviceNode> Nodes { get; } = [];
    private readonly Dictionary<string, DeviceNode> byId = new(StringComparer.OrdinalIgnoreCase);
    public DeviceCatalog()
    {
        var noClass = Guid.Empty;
        handle = Native.SetupDiGetClassDevsW(ref noClass, null, IntPtr.Zero, 6 /* PRESENT | ALLCLASSES */);
        if (handle.IsInvalid) throw Native.Error("enumeration-failed");
        try
        {
            for (uint index = 0; ; index++)
            {
                if (index > 4096) throw new ProbeException("device-count-limit");
                var data = new Native.DeviceInfoData { CbSize = (uint)Marshal.SizeOf<Native.DeviceInfoData>() };
                if (!Native.SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new ProbeException("enumeration-failed", error);
                }
                var node = new DeviceNode(handle, data);
                if (!byId.TryAdd(node.InstanceId, node)) throw new ProbeException("duplicate-device-instance");
                Nodes.Add(node);
            }
        }
        catch { handle.Dispose(); throw; }
    }

    public DeviceNode? Parent(DeviceNode node)
    {
        var parentId = Format.Text(node.Read(Native.Parent));
        return parentId is not null && byId.TryGetValue(parentId, out var parent) ? parent : null;
    }

    public void Dispose() => handle.Dispose();
}
