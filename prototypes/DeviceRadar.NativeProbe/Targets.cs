using System.Runtime.InteropServices;

namespace DeviceRadar.NativeProbe;

// Each token selects one compiled-in surface; records cannot supply a property key or registry path.
internal static class Targets
{
    public const string Mouse = "mouse";
    public const string BleKeyboard = "ble-keyboard";
    public const string BleKeyboardPnp = "ble-keyboard-pnp";
    public const string BleLeNameCache = "ble-le-name-cache";
    public const string UsbMicrophone = "usb-microphone";
    internal static readonly Guid BluetoothClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    internal static readonly Guid AudioEndpointClass = new("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");
    internal static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    internal static readonly Guid UsbClass = new("36fc9e60-c465-11cf-8056-444553540000");
    internal static readonly Guid HidClass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    public static void RequireWritable(string kind)
    {
        if (kind is not (Mouse or BleKeyboard or BleKeyboardPnp or BleLeNameCache or UsbMicrophone)) throw new ProbeException("invalid-target-kind");
    }

    public static uint PropertyType(string kind)
    {
        RequireWritable(kind);
        return kind == UsbMicrophone ? Format.RegistryString : kind is BleKeyboard or BleLeNameCache ? Format.RegistryBinary : Format.String;
    }

    public static RawValue Proposed(string kind, string name)
    {
        var text = Format.Name(name, PropertyType(kind));
        return kind is BleKeyboard or BleLeNameCache ? new(true, Format.RegistryBinary, System.Text.Encoding.UTF8.GetBytes(name + "\0")) : text;
    }

    public static string ResolvedSuffix(OperationRecord record)
    {
        if (record.TargetKind != UsbMicrophone) return "";
        Safety.ValidateAudioValue(record.Original);
        var split = Format.Text(record.Original, Format.RegistryString)!.Length;
        if (split < 0 || split >= record.OriginalResolvedName.Length) throw new ProbeException("invalid-record-resolved-suffix");
        var suffix = record.OriginalResolvedName[split..];
        if (suffix.Length is < 4 or > 260 || !suffix.StartsWith(" (", StringComparison.Ordinal) || !suffix.EndsWith(')')
            || Format.Text(record.Original, Format.RegistryString) + suffix != record.OriginalResolvedName)
            throw new ProbeException("invalid-record-resolved-suffix");
        return suffix;
    }

    public static void ValidateBinding(string kind, DeviceIdentity target)
    {
        RequireWritable(kind);
        ValidateNode(target.InstanceId, target.ClassGuid, target.HardwareIds, target.ContainerId);
        if (target.Ancestors is null || target.Ancestors.Length > 3) throw new ProbeException("invalid-record-target");
        foreach (var node in target.Ancestors)
        {
            if (node is null) throw new ProbeException("invalid-record-target");
            ValidateNode(node.InstanceId, node.ClassGuid, node.HardwareIds, node.ContainerId);
        }
        if (kind == Mouse)
        {
            if (target.ClassGuid != Format.MouseClass || !Format.IsCandidateId(target.InstanceId)
                || !target.HardwareIds.Any(Format.IsCandidateId) || target.Ancestors.Length != 0)
                throw new ProbeException("invalid-record-target");
        }
        else if (kind is BleKeyboard or BleKeyboardPnp or BleLeNameCache) BleTarget.ValidateBinding(target);
        else AudioTarget.ValidateBinding(target);
    }

    private static void ValidateNode(string instance, Guid classGuid, string[] hardwareIds, string? container)
    {
        if (instance is not { Length: > 0 and <= 4096 } || instance.Any(char.IsControl) || classGuid == Guid.Empty
            || hardwareIds is not { Length: <= 32 }
            || hardwareIds.Any(id => id is not { Length: > 0 and <= 4096 } || id.Any(char.IsControl))
            || (container is not null && (!Guid.TryParseExact(container, "D", out var id) || id == Guid.Empty)))
            throw new ProbeException("invalid-record-target");
    }

    public static TargetSession Open(string kind, DeviceIdentity? bound = null) => kind switch
    {
        Mouse => new MouseTarget(),
        BleKeyboard => new BleTarget(bound),
        BleKeyboardPnp => new BleTarget(bound, pnp: true),
        BleLeNameCache => new BleTarget(bound, leName: true),
        UsbMicrophone => new AudioTarget(bound),
        _ => throw new ProbeException("invalid-target-kind")
    };

    public static string Location(string kind) => kind switch
    {
        Mouse => "pnp-friendly-name",
        BleKeyboard => "ble-cache-friendly-name-experimental",
        BleKeyboardPnp => "ble-pnp-root-friendly-name-experimental",
        BleLeNameCache => "ble-source-le-name-cache-experimental",
        UsbMicrophone => "audio-policy-main-store-device-description-experimental",
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

internal sealed class MouseTarget : TargetSession
{
    private readonly LiveDevices live = new();
    public override IReadOnlyList<DeviceSnapshot> Candidates { get; }
    public MouseTarget()
    {
        try { Candidates = live.Candidates.Select(node => node.Snapshot()).ToArray(); }
        catch { live.Dispose(); throw; }
    }
    public override TargetWriteLease OpenWriter(RawValue value) => new(() => live.Unique().SetFriendlyName(value));
    public override void Dispose() => live.Dispose();
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
