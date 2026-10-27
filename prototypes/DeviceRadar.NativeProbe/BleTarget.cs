using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceRadar.NativeProbe;

// Controlled cache experiment on the selected BLE keyboard. It does not establish paired-list or Settings behavior.
internal sealed class BleTarget : TargetSession
{
    private const string OriginalName = "VGN N75 PRO-1";
    private static readonly byte[] OriginalNameBytes = Encoding.UTF8.GetBytes(OriginalName + "\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DeviceCatalog catalog = new();
    public override IReadOnlyList<DeviceSnapshot> Candidates { get; }
    private readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceNode> pnpNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool pnp;
    private readonly bool leName;

    public BleTarget(DeviceIdentity? bound, bool pnp = false, bool leName = false)
    {
        this.pnp = pnp;
        this.leName = leName;
        try
        {
            var candidates = new List<DeviceSnapshot>();
            foreach (var node in catalog.Nodes.Where(node => node.ClassGuid == Targets.BluetoothClass
                && node.InstanceId.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)))
            {
                if (bound is not null && node.InstanceId != bound.InstanceId) continue;
                if (bound is null && !pnp && !leName && Format.Text(node.Read(Native.FriendlyName)) != OriginalName) continue;
                var identity = node.Identity();
                if (leName && !HasSelectedHidModel(identity.ContainerId))
                {
                    if (bound is not null) throw new ProbeException("ble-hid-model-mismatch");
                    continue;
                }
                var path = CachePath(node.InstanceId);
                using var key = NativeRegistry.OpenExisting(path);
                // PnP FriendlyName is editable by this backend; select its immutable family independently.
                if (bound is null && pnp && !UnderlyingNamesMatch(key)) continue;
                if (leName) ValidateLeNameSourceGuards(key);
                else ValidateUnderlyingNames(key);
                ValidateBinding(identity);
                var raw = pnp ? node.Read(Native.FriendlyName) : NativeRegistry.Read(key, leName ? "LEName" : "FriendlyName");
                if (leName) ValidateLeName(raw);
                else Safety.ValidateOriginal(raw, pnp ? Format.String : Format.RegistryBinary);
                var pnpName = Format.Text(node.Read(Native.Name)) ?? Format.Text(node.Read(Native.FriendlyName)) ?? "";
                candidates.Add(new(identity, raw, pnpName));
                paths.Add(identity.InstanceId, path);
                pnpNodes.Add(identity.InstanceId, node);
            }
            Candidates = candidates;
        }
        catch { catalog.Dispose(); throw; }
    }

    internal static void ValidateBinding(DeviceIdentity identity)
    {
        var hardwarePrefix = @"BTHLE\DEV_" + RootAddress(identity.InstanceId);
        if (identity.ClassGuid != Targets.BluetoothClass || identity.ContainerId is null || identity.Ancestors.Length != 0
            || identity.HardwareIds.Length == 0 || !identity.HardwareIds.Any(id => id.StartsWith(hardwarePrefix, StringComparison.OrdinalIgnoreCase)
                && (id.Length == hardwarePrefix.Length || id[hardwarePrefix.Length] is '&' or '\\')))
            throw new ProbeException("invalid-record-target");
    }

    private static string RootAddress(string instanceId)
    {
        var match = Regex.Match(instanceId, @"\ABTHLE\\DEV_([a-f0-9]{12})\\[^\\]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new ProbeException("invalid-ble-cache-association");
        return match.Groups[1].Value.ToLowerInvariant();
    }

    private static string CachePath(string instanceId)
        => @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\" + RootAddress(instanceId);

    private static void ValidateUnderlyingNames(RegistryKey key)
    {
        if (!UnderlyingNamesMatch(key)) throw new ProbeException("ble-underlying-name-changed");
    }

    private static bool UnderlyingNamesMatch(RegistryKey key)
        => MatchesKnownFamily(NativeRegistry.Read(key, "Name"), NativeRegistry.Read(key, "LEName"));

    // PnP candidate selection depends only on these unchanged cache fields, never its editable display name.
    internal static bool MatchesKnownFamily(RawValue name, RawValue leName)
        => new[] { name, leName }.All(raw => raw.Exists && raw.Type == Format.RegistryBinary
            && raw.Bytes.AsSpan().SequenceEqual(OriginalNameBytes));

    private static void ValidateLeNameSourceGuards(RegistryKey key)
        => ValidateLeNameSourceGuards(NativeRegistry.Read(key, "Name"), NativeRegistry.Read(key, "FriendlyName"));

    internal static void ValidateLeNameSourceGuards(RawValue factoryName, RawValue alias)
    {
        if (!factoryName.Exists || factoryName.Type != Format.RegistryBinary || !factoryName.Bytes.AsSpan().SequenceEqual(OriginalNameBytes))
            throw new ProbeException("ble-cache-coupling-name-changed");
        if (!alias.Exists || alias.Type != Format.RegistryBinary || !alias.Bytes.AsSpan().SequenceEqual(new byte[] { 0 }))
            throw new ProbeException("ble-cache-coupling-friendly-name-changed");
    }

    internal static void ValidateLeName(RawValue value)
    {
        Safety.ValidateOriginal(value, Format.RegistryBinary);
        if (value.Bytes[^1] != 0) throw new ProbeException("invalid-ble-le-name-property");
        try
        {
            var text = StrictUtf8.GetString(value.Bytes, 0, value.Bytes.Length - 1);
            if (text.Contains('\0')) throw new ProbeException("invalid-ble-le-name-property");
        }
        catch (DecoderFallbackException) { throw new ProbeException("invalid-ble-le-name-property"); }
    }

    private bool HasSelectedHidModel(string? containerId)
    {
        if (containerId is null) return false;
        foreach (var node in catalog.Nodes.Where(node => node.ClassGuid == Targets.HidClass))
        {
            var identity = node.Identity();
            if (string.Equals(identity.ContainerId, containerId, StringComparison.OrdinalIgnoreCase)
                && identity.HardwareIds.Any(IsSelectedHidHardwareId)) return true;
        }
        return false;
    }

    internal static bool IsSelectedHidHardwareId(string id)
        => string.Equals(id, @"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02320f_PID&5055_REV&0104", StringComparison.OrdinalIgnoreCase);

    public BlePropertiesSnapshot CaptureProperties(string machine)
    {
        if (!leName) throw new ProbeException("ble-source-snapshot-kind-required");
        var target = Unique();
        var node = pnpNodes[target.Identity.InstanceId];
        using var key = NativeRegistry.OpenExisting(paths[target.Identity.InstanceId]);
        ValidateLeNameSourceGuards(key);
        var entries = new[] { "Name", "LEName", "FriendlyName" }.Select(name => new RegistryEntry(name, NativeRegistry.Read(key, name))).ToArray();
        foreach (var entry in entries)
        {
            if (entry.Name == "LEName") ValidateLeName(entry.Value);
            else Safety.ValidateOriginal(entry.Value, Format.RegistryBinary);
        }
        var pnpFriendly = node.Read(Native.FriendlyName);
        Safety.ValidateOriginal(pnpFriendly);
        foreach (var entry in entries)
            if (!entry.Value.Same(NativeRegistry.Read(key, entry.Name))) throw new ProbeException("ble-snapshot-changed");
        if (!pnpFriendly.Same(node.Read(Native.FriendlyName))) throw new ProbeException("ble-snapshot-changed");
        var pnpName = Format.Text(node.Read(Native.Name)) ?? Format.Text(pnpFriendly) ?? "";
        return new(Format.Version, Guid.NewGuid().ToString("N"), machine, DateTimeOffset.UtcNow,
            Environment.OSVersion.VersionString, target.Identity, pnpName, pnpFriendly, entries);
    }

    public override TargetWriteLease OpenWriter(RawValue value)
    {
        if (Environment.OSVersion.Version.Build != 22631) throw new ProbeException("experimental-build-unsupported");
        if (leName) ValidateLeName(value);
        else Safety.ValidateOriginal(value, pnp ? Format.String : Format.RegistryBinary);
        var before = Unique();
        var key = NativeRegistry.OpenExisting(paths[before.Identity.InstanceId], writable: !pnp);
        try
        {
            if (leName)
            {
                if (!HasSelectedHidModel(before.Identity.ContainerId)) throw new ProbeException("ble-hid-model-mismatch");
                ValidateLeNameSourceGuards(key);
            }
            else ValidateUnderlyingNames(key);
            var node = pnpNodes[before.Identity.InstanceId];
            var current = pnp ? node.Read(Native.FriendlyName) : NativeRegistry.Read(key, leName ? "LEName" : "FriendlyName");
            if (!current.Same(before.FriendlyName)) throw new ProbeException("value-changed-before-write");
            return new(pnp ? () => node.SetFriendlyName(value) : () => NativeRegistry.Write(key, leName ? "LEName" : "FriendlyName", value), key);
        }
        catch { key.Dispose(); throw; }
    }

    public override void Dispose() => catalog.Dispose();
}
