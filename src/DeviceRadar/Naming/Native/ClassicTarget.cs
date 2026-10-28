using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeviceRadar.Naming.Native;

internal sealed partial class ClassicTarget : TargetSession
{
    private const string DeviceCacheRoot = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\";
    private const string AliasValue = "FriendlyName";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly DeviceCatalog catalog;
    private readonly BluetoothClassicNative bluetooth;
    private readonly DeviceNode node;
    private readonly BluetoothClassicNative.Match match;
    private readonly string cachePath;
    private readonly DeviceSnapshot initial;
    public override IReadOnlyList<DeviceSnapshot> Candidates { get; }

    internal ClassicTarget(string instanceId, DeviceIdentity? bound = null)
    {
        var address = DeviceAddress(instanceId);
        if (bound is not null)
        {
            ValidateBinding(bound);
            if (!string.Equals(instanceId, bound.InstanceId, StringComparison.OrdinalIgnoreCase))
                throw new ProbeException("target-mismatch");
        }
        catalog = new();
        try
        {
            var nodes = catalog.Nodes.Where(item => string.Equals(item.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nodes.Length != 1) throw new ProbeException(nodes.Length == 0 ? "target-offline" : "target-ambiguous");
            node = nodes[0];
            if (node.ClassGuid != Targets.BluetoothClass) throw new ProbeException("invalid-classic-target");
            bluetooth = new();
            try
            {
                var matches = bluetooth.Find(address);
                // UpdateDeviceRecord has no hRadio argument. Selecting one of duplicate radio records cannot
                // constrain that mutation, so duplicate associations remain read-only even with a bound radio.
                if (matches.Length != 1) throw new ProbeException(matches.Length == 0 ? "classic-cache-record-missing" : "classic-radio-ambiguous");
                match = matches[0];
                cachePath = DeviceCacheRoot + BluetoothClassicNative.Address(address).ToLowerInvariant();
                initial = Snapshot();
                ValidateBinding(initial.Identity);
                if (bound is not null) MatchBinding(bound, initial.Identity);
                Candidates = [initial];
            }
            catch { bluetooth.Dispose(); throw; }
        }
        catch { catalog.Dispose(); throw; }
    }

    private DeviceSnapshot Snapshot()
    {
        var present = Native.CM_Locate_DevNodeW(out var currentDevInst, node.InstanceId, 0);
        if (present != 0 || currentDevInst != node.DevInst) throw new ProbeException("target-offline", present);
        var identity = node.Identity() with { RadioAddress = match.Radio.Address };
        var info = BluetoothClassicNative.Read(match.Radio, match.Info.Address);
        using var key = NativeRegistry.OpenExisting(cachePath);
        var alias = NativeRegistry.Read(key, AliasValue);
        ValidateValue(alias);
        var deviceName = NativeRegistry.Read(key, "Name");
        _ = DeviceName(deviceName);
        var pnpName = Format.Text(node.Read(Native.Name));
        var pnpFriendlyName = Format.Text(node.Read(Native.FriendlyName));
        if ((pnpName is not null && (pnpName.Length > 512 || pnpName.Any(char.IsControl)))
            || (pnpFriendlyName is not null && (pnpFriendlyName.Length > 512 || pnpFriendlyName.Any(char.IsControl))))
            throw new ProbeException("bluetooth-invalid-pnp-name");
        return new(identity, alias, info.Name, BluetoothNameSources: new(info.Name, pnpName, deviceName));
    }

    public override TargetWriteLease OpenWriter(RawValue value)
    {
        ValidateValue(value);
        var before = Snapshot();
        MatchBinding(initial.Identity, before.Identity);
        if (!before.FriendlyName.Same(initial.FriendlyName) || before.SystemName != initial.SystemName
            || !before.BluetoothNameSources!.DeviceName.Same(initial.BluetoothNameSources!.DeviceName)
            || before.BluetoothNameSources.PnpName != initial.BluetoothNameSources.PnpName)
            throw new ProbeException("value-changed-before-write");
        var alias = AliasText(value) ?? "";
        var expectedName = alias.Length == 0 ? DeviceName(before.BluetoothNameSources.DeviceName) : alias;

        // Acquire the exact cache value's write rights before any runtime change. No key is created.
        var key = OpenCacheWriter(cachePath);
        try
        {
            if (!NativeRegistry.Read(key, AliasValue).Same(before.FriendlyName)) throw new ProbeException("value-changed-before-write");
            return new(() =>
            {
                using var fresh = new ClassicTarget(before.Identity.InstanceId, before.Identity);
                var actual = fresh.Unique();
                if (!actual.FriendlyName.Same(before.FriendlyName) || actual.SystemName != before.SystemName
                    || !actual.BluetoothNameSources!.DeviceName.Same(before.BluetoothNameSources.DeviceName)
                    || actual.BluetoothNameSources.PnpName != before.BluetoothNameSources.PnpName)
                    throw new ProbeException("value-changed-before-write");
                var currentInfo = BluetoothClassicNative.Read(match.Radio, match.Info.Address);
                if (currentInfo.Name != before.SystemName) throw new ProbeException("value-changed-before-write");
                BluetoothClassicNative.Update(currentInfo, alias);
                var runtime = BluetoothClassicNative.Read(match.Radio, match.Info.Address);
                if (runtime.Name != expectedName) throw new ProbeException("classic-runtime-readback-mismatch");

                // The public API's successful runtime update does not promise synchronous persistence.
                // Prefer its result; normalize only this compiled-in value when independent raw readback differs.
                if (!NativeRegistry.Read(key, "Name").Same(before.BluetoothNameSources.DeviceName))
                    throw new ProbeException("classic-device-name-changed");
                var persisted = NativeRegistry.Read(key, AliasValue);
                if (!persisted.Same(value))
                {
                    if (!persisted.Same(before.FriendlyName) && !persisted.Same(EncodeStoredAlias(alias)))
                        throw new ProbeException("classic-alias-changed-during-write");
                    SetExactAlias(key, value);
                }
                if (!NativeRegistry.Read(key, AliasValue).Same(value)) throw new ProbeException("classic-alias-readback-mismatch");
                if (!NativeRegistry.Read(key, "Name").Same(before.BluetoothNameSources.DeviceName))
                    throw new ProbeException("classic-device-name-changed");
                // PnP is observed by the caller's independent session; API success is not display success.
            }, key);
        }
        catch { key.Dispose(); throw; }
    }

    internal static RawValue EncodeAlias(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ProbeException("invalid-name");
        var value = EncodeStoredAlias(name);
        ValidateValue(value);
        return value;
    }

    private static RawValue EncodeStoredAlias(string name)
    {
        try { return new(true, Format.RegistryBinary, Utf8.GetBytes(name + "\0")); }
        catch (EncoderFallbackException) { throw new ProbeException("invalid-name"); }
    }

    internal static void ValidateValue(RawValue value)
    {
        if (value is null || value.Bytes is null || (!value.Exists && (value.Type != Format.Empty || value.Bytes.Length != 0)))
            throw new ProbeException("invalid-bluetooth-alias");
        if (!value.Exists) return;
        if (value.Type != Format.RegistryBinary || value.Bytes.Length > 248) throw new ProbeException("invalid-bluetooth-alias");
        var text = Decode(value, "invalid-bluetooth-alias");
        if (text.Length > 247 || text.Any(char.IsControl)) throw new ProbeException("invalid-bluetooth-alias");
        // On the inspected Microsoft stack the conversion consumes a fixed 248 input bytes.
        // Keep the name's NUL inside those 124 UTF-16 units and include zero padding in the UTF-8 capacity check.
        // Reject a non-restorable original alias as well as an unsafe proposed alias before any mutation.
        if (text.Length > 123 || Utf8.GetByteCount(text) + 124 - text.Length > 248)
            throw new ProbeException("bluetooth-alias-length-unsupported");
    }

    internal static string? AliasText(RawValue value)
    {
        ValidateValue(value);
        if (!value.Exists) return null;
        var text = Decode(value, "invalid-bluetooth-alias");
        return text.Length == 0 ? null : text;
    }

    internal static string DeviceName(RawValue value)
    {
        if (value is null || !value.Exists || value.Type != Format.RegistryBinary || value.Bytes is null || value.Bytes.Length is 0 or > 248)
            throw new ProbeException("classic-device-name-missing");
        var text = Decode(value, "invalid-classic-device-name");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 247 || text.Any(char.IsControl))
            throw new ProbeException("invalid-classic-device-name");
        return text;
    }

    private static string Decode(RawValue value, string code)
    {
        if (value.Bytes.Length == 0) return "";
        if (value.Bytes[^1] != 0) throw new ProbeException(code);
        try
        {
            var text = Utf8.GetString(value.Bytes, 0, value.Bytes.Length - 1);
            if (text.Contains('\0')) throw new ProbeException(code);
            return text;
        }
        catch (DecoderFallbackException) { throw new ProbeException(code); }
    }

    internal static void ValidateBinding(DeviceIdentity identity)
    {
        if (identity is null) throw new ProbeException("invalid-record-target");
        _ = DeviceAddress(identity.InstanceId);
        if (identity.ClassGuid != Targets.BluetoothClass || identity.EndpointId is not null || identity.RadioAddress is null
            || identity.HardwareIds is not { Length: <= 32 }
            || identity.HardwareIds.Any(id => id is not { Length: > 0 and <= 4096 } || id.Any(char.IsControl))
            || identity.Ancestors is not { Length: 0 }
            || (identity.ContainerId is not null && (!Guid.TryParseExact(identity.ContainerId, "D", out var container) || container == Guid.Empty)))
            throw new ProbeException("invalid-record-target");
        _ = BluetoothClassicNative.ParseAddress(identity.RadioAddress);
    }

    internal static ulong DeviceAddress(string instanceId)
    {
        if (instanceId is null || instanceId.Length > 4096 || instanceId.Any(char.IsControl)) throw new ProbeException("invalid-classic-target");
        var match = RootId().Match(instanceId);
        if (!match.Success) throw new ProbeException("invalid-classic-target");
        return BluetoothClassicNative.ParseAddress(match.Groups[1].Value);
    }

    private static void MatchBinding(DeviceIdentity expected, DeviceIdentity actual)
    {
        ValidateBinding(actual);
        Safety.MatchIdentity(expected, "classic-session", "classic-session", actual);
        if (!string.Equals(expected.RadioAddress, actual.RadioAddress, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("bluetooth-radio-binding-changed");
    }

    private static RegistryKey OpenCacheWriter(string path)
    {
        var error = RegOpenKeyExW(new IntPtr(unchecked((int)0x80000002)), path, 0, 0x103 /* QUERY | SET | WOW64_64KEY */, out var handle);
        if (error != 0)
        {
            handle?.Dispose();
            throw new ProbeException(error == 5 ? "administrator-required" : "classic-alias-open-write-failed", error);
        }
        try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
        catch { handle.Dispose(); throw; }
    }

    private static void SetExactAlias(RegistryKey key, RawValue value)
    {
        var error = value.Exists ? RegSetValueExW(key.Handle, AliasValue, 0, Format.RegistryBinary, value.Bytes, (uint)value.Bytes.Length)
            : RegDeleteValueW(key.Handle, AliasValue);
        if (error != 0 && !(error == 2 && !value.Exists)) throw new ProbeException("classic-alias-persist-failed", error);
    }

    public override void Dispose() { bluetooth.Dispose(); catalog.Dispose(); }

    [GeneratedRegex(@"\ABTHENUM\\DEV_([0-9A-F]{12})\\[^\\]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootId();
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyExW(IntPtr key, string path, uint options, uint access, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteValueW(SafeRegistryHandle key, string name);
}
