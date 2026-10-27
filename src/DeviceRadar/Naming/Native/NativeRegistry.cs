using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DeviceRadar.Naming.Native;

internal static class NativeRegistry
{
    public static RegistryKey OpenExisting(string path)
    {
        // RegistryKey.Handle on a predefined root can request additional rights. Pass HKLM directly instead.
        var localMachine = new IntPtr(unchecked((int)0x80000002));
        var error = RegOpenKeyExW(localMachine, path, 0, 1u /* QUERY_VALUE */ | 0x100u /* WOW64_64KEY */, out var handle);
        if (error != 0)
        {
            handle?.Dispose();
            throw new ProbeException("registry-open-read-failed", error);
        }
        try { return RegistryKey.FromHandle(handle, RegistryView.Registry64); }
        catch { handle.Dispose(); throw; }
    }

    public static RawValue Read(RegistryKey key, string name)
    {
        byte[]? buffer = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            uint count = (uint)(buffer?.Length ?? 0);
            var error = RegQueryValueExW(key.Handle, name, IntPtr.Zero, out var type, buffer, ref count);
            if (error == 2) return RawValue.Missing();
            if (count > Format.MaxPropertyBytes) throw new ProbeException("property-size-limit");
            if (error == 0 && count <= (uint)(buffer?.Length ?? 0))
                return new(true, type, buffer is null ? [] : buffer.AsSpan(0, (int)count).ToArray());
            if (error is not (0 or 234) || count == 0) throw new ProbeException("registry-read-failed", error);
            buffer = new byte[count];
        }
        throw new ProbeException("property-changed-during-read");
    }

    public static RegistryEntry[] Snapshot(RegistryKey key)
    {
        var names = key.GetValueNames().Order(StringComparer.Ordinal).ToArray();
        if (names.Length is 0 or > 128 || names.Any(name => name.Length > 256 || name.Any(char.IsControl)))
            throw new ProbeException("registry-snapshot-limit");
        var entries = names.Select(name => new RegistryEntry(name, Read(key, name))).ToArray();
        if (entries.Any(entry => !entry.Value.Exists)
            || !names.SequenceEqual(key.GetValueNames().Order(StringComparer.Ordinal))) throw new ProbeException("registry-snapshot-changed");
        foreach (var entry in entries)
            if (!entry.Value.Same(Read(key, entry.Name))) throw new ProbeException("registry-snapshot-changed");
        return entries;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyExW(IntPtr key, string path, uint options, uint access, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, [Out] byte[]? data, ref uint count);

}

internal sealed record RegistryEntry(string Name, RawValue Value);
internal sealed record AudioPropertiesSnapshot(int FormatVersion, string OperationId, string MachineFingerprint,
    DateTimeOffset CreatedAtUtc, string OsVersion, DeviceIdentity Target, string ResolvedName, RegistryEntry[] Properties);
internal sealed record BlePropertiesSnapshot(int FormatVersion, string OperationId, string MachineFingerprint,
    DateTimeOffset CreatedAtUtc, string OsVersion, DeviceIdentity Target, string PnpName, RawValue PnpFriendlyName, RegistryEntry[] Properties);
