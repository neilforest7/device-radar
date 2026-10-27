using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeviceRadar.Inventory.Native;

internal static class SetupApi
{
    internal sealed record Node(string Id, Guid ClassGuid, string Name, string[] HardwareIds, string? ContainerId, string? ParentId, bool Present, uint? RemovalPolicy = null, string? BusReportedName = null);
    [StructLayout(LayoutKind.Sequential)] private struct Info { public uint Size; public Guid Class; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Key(Guid fmt, uint pid) { public Guid Format = fmt; public uint Id = pid; }
    private sealed class Set : SafeHandleZeroOrMinusOneIsInvalid { private Set() : base(true) { } protected override bool ReleaseHandle() => Destroy(handle); }
    private static readonly Guid Device = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    internal static readonly Key Description = new(Device, 2), HardwareIds = new(Device, 3), FriendlyName = new(Device, 14),
        Name = new(new("b725f130-47ef-101a-a5f1-02608c9eebac"), 10),
        Container = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2),
        Parent = new(new("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8),
        BusReportedName = new(new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4),
        InterfaceName = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    internal static List<Node> Enumerate()
    {
        var present = ReadSet(true).Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ReadSet(false).Select(n => n with { Present = present.Contains(n.Id) }).ToList();
    }
    private static List<Node> ReadSet(bool presentOnly)
    {
        using var set = GetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 4u | (presentOnly ? 2u : 0));
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Device enumeration failed");
        var nodes = new List<Node>();
        for (uint i = 0; ; i++)
        {
            var info = new Info { Size = (uint)Marshal.SizeOf<Info>() };
            if (!EnumDeviceInfo(set, i, ref info))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error, "Device enumeration failed");
            }
            var id = InstanceId(set, ref info);
            if (id is null) continue; // A device can disappear while the snapshot is being read.
            var name = Text(set, ref info, Name) ?? Text(set, ref info, FriendlyName) ?? Text(set, ref info, Description) ?? id;
            var hardware = Read(set, ref info, HardwareIds);
            var ids = hardware.Type == 0x2012 && hardware.Bytes is not null
                ? Encoding.Unicode.GetString(hardware.Bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
            var container = Read(set, ref info, Container);
            var guid = container.Type == 13 && container.Bytes is { Length: 16 } ? new Guid(container.Bytes) : Guid.Empty;
            var removalBytes = new byte[4];
            var removal = GetRegistryProperty(set, ref info, 0x1f, out _, removalBytes, 4, out var needed) && needed == 4
                ? BitConverter.ToUInt32(removalBytes) : (uint?)null;
            nodes.Add(new(id, info.Class, name, ids, guid == Guid.Empty ? null : guid.ToString("D"), Text(set, ref info, Parent), presentOnly, removal,
                Text(set, ref info, BusReportedName)));
        }
        return nodes;
    }
    private static string? InstanceId(Set set, ref Info info)
    {
        _ = GetInstanceId(set, ref info, null, 0, out var size);
        if (size is 0 or > 32768) return null;
        var buffer = new StringBuilder((int)size);
        return GetInstanceId(set, ref info, buffer, size, out _) ? buffer.ToString() : null;
    }
    private static string? Text(Set set, ref Info info, Key key)
    {
        var (type, bytes) = Read(set, ref info, key);
        return type == 0x12 && bytes is not null ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;
    }
    private static (uint Type, byte[]? Bytes) Read(Set set, ref Info info, Key key)
    {
        _ = GetProperty(set, ref info, ref key, out var type, null, 0, out var size, 0);
        if (size is 0 or > 1048576) return (type, null);
        var bytes = new byte[size];
        return GetProperty(set, ref info, ref key, out type, bytes, size, out _, 0) ? (type, bytes) : (type, null);
    }
    [DllImport("setupapi.dll", EntryPoint="SetupDiGetClassDevsW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern Set GetClassDevs(IntPtr cls, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", EntryPoint="SetupDiEnumDeviceInfo", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDeviceInfo(Set set, uint index, ref Info info);
    [DllImport("setupapi.dll", EntryPoint="SetupDiGetDeviceInstanceIdW", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetInstanceId(Set set, ref Info info, StringBuilder? id, uint size, out uint required);
    [DllImport("setupapi.dll", EntryPoint="SetupDiGetDevicePropertyW", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProperty(Set set, ref Info info, ref Key key, out uint type, byte[]? bytes, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", EntryPoint="SetupDiGetDeviceRegistryPropertyW", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetRegistryProperty(Set set, ref Info info, uint property, out uint type, byte[] bytes, uint size, out uint required);
    [DllImport("setupapi.dll", EntryPoint="SetupDiDestroyDeviceInfoList", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Destroy(IntPtr set);
}
