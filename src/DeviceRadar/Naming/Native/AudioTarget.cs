namespace DeviceRadar.Naming.Native;

internal sealed class AudioTarget : TargetSession
{
    public const string VidPid = "001F:0B21";
    internal const string FriendlyNameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";
    internal const string DescriptionValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string UsbPrefix = @"USB\VID_001F&PID_0B21";
    private const string CapturePrefix = @"SWD\MMDEVAPI\{0.0.1.00000000}.";
    private readonly DeviceCatalog catalog = new();
    public override IReadOnlyList<DeviceSnapshot> Candidates { get; }
    private readonly Dictionary<string, string> propertyPaths = new(StringComparer.OrdinalIgnoreCase);

    public AudioTarget(DeviceIdentity? bound)
    {
        try
        {
            var audio = CoreAudio.ActiveCaptures();
            var candidates = new List<DeviceSnapshot>();
            foreach (var node in catalog.Nodes.Where(node => node.ClassGuid == Targets.AudioEndpointClass
                && node.InstanceId.StartsWith(CapturePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var media = catalog.Parent(node);
                var usb = media is null ? null : catalog.Parent(media);
                if (media is null || usb is null || media.ClassGuid != Targets.MediaClass || usb.ClassGuid != Targets.UsbClass) continue;
                var mediaId = media.Identity();
                var usbId = usb.Identity();
                if (!mediaId.HardwareIds.Any(IsUsbId) || !usbId.HardwareIds.Any(IsUsbId)) continue;
                if (Format.Text(media.Read(Native.Service)) != "usbaudio" || Format.Text(usb.Read(Native.Service)) != "usbccgp") continue;
                var identity = node.Identity([Ancestor(mediaId), Ancestor(usbId)]);
                ValidateBinding(identity);
                if (bound is not null && !string.Equals(identity.InstanceId, bound.InstanceId, StringComparison.Ordinal)) continue;
                var endpointId = identity.InstanceId[@"SWD\MMDEVAPI\".Length..];
                var resolved = audio.Where(item => string.Equals(item.Id, endpointId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (resolved.Length != 1) continue;
                if (!string.Equals(resolved[0].ContainerId, identity.ContainerId, StringComparison.OrdinalIgnoreCase))
                    throw new ProbeException("audio-container-mismatch");
                if (!resolved[0].DefaultForAllRoles) throw new ProbeException("audio-default-role-changed");
                var path = PropertiesPath(identity);
                using var properties = NativeRegistry.OpenExisting(path);
                var raw = NativeRegistry.Read(properties, DescriptionValue);
                Safety.ValidateAudioValue(raw);
                var suffix = " (" + resolved[0].InterfaceName + ")";
                var sources = new AudioNameSources(resolved[0].Description, resolved[0].InterfaceName,
                    ReadName(node, Native.DeviceDescription), ReadName(node, Native.FriendlyName), ReadName(node, Native.Name));
                candidates.Add(new(identity, raw, resolved[0].Name, suffix, sources));
                propertyPaths.Add(identity.InstanceId, path);
            }
            Candidates = candidates;
        }
        catch { catalog.Dispose(); throw; }
    }

    internal static void ValidateBinding(DeviceIdentity identity)
    {
        if (identity.ClassGuid != Targets.AudioEndpointClass || identity.ContainerId is null || identity.HardwareIds.Length == 0
            || identity.Ancestors.Length != 2 || identity.Ancestors[0].ClassGuid != Targets.MediaClass
            || identity.Ancestors[1].ClassGuid != Targets.UsbClass
            || !identity.Ancestors.All(parent => IsUsbId(parent.InstanceId) && parent.HardwareIds.Any(IsUsbId)
                && string.Equals(parent.ContainerId, identity.ContainerId, StringComparison.OrdinalIgnoreCase)))
            throw new ProbeException("invalid-record-target");
        _ = PropertiesPath(identity);
    }

    private static DeviceAncestor Ancestor(DeviceIdentity identity)
        => new(identity.InstanceId, identity.ClassGuid, identity.HardwareIds, identity.ContainerId);
    private static string? ReadName(DeviceNode node, Native.PropertyKey property)
    {
        var value = Format.Text(node.Read(property));
        if (value is not null && (value.Length > 512 || value.Any(char.IsControl)))
            throw new ProbeException("invalid-audio-name-source");
        return value;
    }
    private static bool IsUsbId(string value) => value.StartsWith(UsbPrefix, StringComparison.OrdinalIgnoreCase)
        && (value.Length == UsbPrefix.Length || value[UsbPrefix.Length] is '&' or '\\');
    private static string PropertiesPath(DeviceIdentity identity)
    {
        if (!identity.InstanceId.StartsWith(CapturePrefix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(identity.InstanceId[CapturePrefix.Length..], "B", out var guid) || guid == Guid.Empty)
            throw new ProbeException("invalid-capture-endpoint-id");
        return @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\" + guid.ToString("B") + @"\Properties";
    }

    public AudioPropertiesSnapshot CaptureProperties(string machine)
    {
        var target = Unique();
        using var properties = NativeRegistry.OpenExisting(propertyPaths[target.Identity.InstanceId]);
        return new(Format.Version, Guid.NewGuid().ToString("N"), machine, DateTimeOffset.UtcNow,
            Runtime.SystemDescription, target.Identity, target.SystemName, NativeRegistry.Snapshot(properties));
    }

    public override TargetWriteLease OpenWriter(RawValue value)
    {
        if (Runtime.WindowsVersion.Build != 22631) throw new ProbeException("experimental-build-unsupported");
        Safety.ValidateAudioValue(value);
        var before = Unique();
        using var properties = NativeRegistry.OpenExisting(propertyPaths[before.Identity.InstanceId]);
        if (!NativeRegistry.Read(properties, DescriptionValue).Same(before.FriendlyName)) throw new ProbeException("value-changed-before-write");
        var endpointId = before.Identity.InstanceId[@"SWD\MMDEVAPI\".Length..];
        return AudioPolicy.OpenWriter(endpointId, before.FriendlyName, value);
    }
    public override void Dispose() => catalog.Dispose();
}
