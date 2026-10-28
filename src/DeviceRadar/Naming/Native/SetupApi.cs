using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeviceRadar.Naming.Native;

internal sealed class DeviceNode(DeviceInfoSet handle, Native.DeviceInfoData initialData)
{
    private Native.DeviceInfoData data = initialData;
    public string InstanceId { get; } = GetInstanceId(handle, initialData);
    public Guid ClassGuid => data.ClassGuid;
    public uint DevInst => data.DevInst;

    public DeviceIdentity Identity(DeviceAncestor[]? ancestors = null)
    {
        var hardware = Read(Native.HardwareIds);
        var ids = Array.Empty<string>();
        if (hardware.Exists)
        {
        if (hardware.Type != Format.StringList || hardware.Bytes.Length < 4 || hardware.Bytes.Length % 2 != 0)
            throw new ProbeException("invalid-hardware-ids");
        string all;
        try { all = Format.Unicode.GetString(hardware.Bytes); }
        catch (DecoderFallbackException) { throw new ProbeException("invalid-hardware-ids"); }
        if (!all.EndsWith("\0\0", StringComparison.Ordinal)) throw new ProbeException("invalid-hardware-ids");
        ids = all[..^2].Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length is 0 or > 32) throw new ProbeException("invalid-hardware-ids");
        }

        var container = Read(Native.ContainerId);
        string? containerId = null;
        if (container.Exists)
        {
            if (container.Type != Format.Guid || container.Bytes.Length != 16) throw new ProbeException("invalid-container-id");
            var guid = new Guid(container.Bytes);
            if (guid != Guid.Empty) containerId = guid.ToString("D");
        }
        return new(InstanceId, data.ClassGuid, ids, containerId, ancestors ?? []);
    }

    public DeviceSnapshot Snapshot()
    {
        var identity = Identity();
        var friendly = Read(Native.FriendlyName);
        Safety.ValidateOriginal(friendly);
        var name = Format.Text(Read(Native.Name)) ?? Format.Text(friendly) ?? Format.Text(Read(Native.DeviceDescription)) ?? "";
        return new(identity, friendly, name);
    }

    public void SetFriendlyName(RawValue value)
    {
        // There is deliberately no caller-supplied property key, registry path, or command.
        var key = Native.FriendlyName;
        var (type, bytes) = value.ForWrite();
        if (!Native.SetupDiSetDevicePropertyW(handle, ref data, ref key, type,
                bytes, (uint)(bytes?.Length ?? 0), 0))
            throw Native.Error("set-property-failed");
    }

    internal RawValue Read(Native.PropertyKey key)
    {
        byte[]? buffer = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var ok = Native.SetupDiGetDevicePropertyW(handle, ref data, ref key, out var type,
                buffer, (uint)(buffer?.Length ?? 0), out var required, 0);
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error == 1168 /* ERROR_NOT_FOUND */)
            {
                if (type != Format.Empty) throw new ProbeException("inconsistent-missing-property", error);
                return RawValue.Missing();
            }
            if (required > Format.MaxPropertyBytes) throw new ProbeException("property-size-limit");
            if (ok)
            {
                if (required > (uint)(buffer?.Length ?? 0)) throw new ProbeException("invalid-property-length");
                return new(true, type, buffer is null ? [] : buffer.AsSpan(0, (int)required).ToArray());
            }
            if (error != 122 /* ERROR_INSUFFICIENT_BUFFER */ || required == 0) throw new ProbeException("get-property-failed", error);
            buffer = new byte[required];
        }
        throw new ProbeException("property-changed-during-read");
    }

    private static string GetInstanceId(DeviceInfoSet handle, Native.DeviceInfoData data)
    {
        StringBuilder? buffer = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var ok = Native.SetupDiGetDeviceInstanceIdW(handle, ref data, buffer, (uint)(buffer?.Capacity ?? 0), out var required);
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            if (required > 4096) throw new ProbeException("instance-id-size-limit");
            if (ok)
            {
                var value = buffer?.ToString() ?? "";
                if (value.Length == 0 || required > (uint)buffer!.Capacity) throw new ProbeException("invalid-instance-id");
                return value;
            }
            if (error != 122 || required == 0) throw new ProbeException("get-instance-id-failed", error);
            buffer = new StringBuilder((int)required);
        }
        throw new ProbeException("instance-id-changed-during-read");
    }
}

internal sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
{
    private DeviceInfoSet() : base(true) { }
    protected override bool ReleaseHandle() => Native.SetupDiDestroyDeviceInfoList(handle);
}

internal sealed class SelectedDevice : IDisposable
{
    private readonly DeviceInfoSet handle;
    public DeviceNode Node { get; private set; } = null!;

    public SelectedDevice(string instanceId)
    {
        if (instanceId is not { Length: > 0 and <= 4096 } || instanceId.Any(char.IsControl))
            throw new ProbeException("invalid-instance-id");
        handle = Native.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (handle.IsInvalid) throw Native.Error("device-open-failed");
        try
        {
            var data = new Native.DeviceInfoData { CbSize = (uint)Marshal.SizeOf<Native.DeviceInfoData>() };
            if (!Native.SetupDiOpenDeviceInfoW(handle, instanceId, IntPtr.Zero, 0, ref data))
                throw Native.Error("target-offline");
            Node = new DeviceNode(handle, data);
            if (!string.Equals(Node.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                throw new ProbeException("target-mismatch");
            EnsurePresent();
        }
        catch { handle.Dispose(); throw; }
    }

    public void EnsurePresent()
    {
        var result = Native.CM_Locate_DevNodeW(out var currentDevInst, Node.InstanceId, 0 /* CM_LOCATE_DEVNODE_NORMAL */);
        if (result != 0) throw new ProbeException("target-offline", result);
        if (currentDevInst != Node.DevInst) throw new ProbeException("target-mismatch");
    }

    public void Dispose() => handle.Dispose();
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    internal static readonly PropertyKey DeviceDescription = new(DeviceProperties, 2);
    internal static readonly PropertyKey HardwareIds = new(DeviceProperties, 3);
    internal static readonly PropertyKey Service = new(DeviceProperties, 6);
    internal static readonly PropertyKey FriendlyName = new(DeviceProperties, 14);
    internal static readonly PropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    internal static readonly PropertyKey Name = new(new("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    internal static readonly PropertyKey Parent = new(new("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);
    internal static readonly PropertyKey InterfaceFriendlyName = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    internal static ProbeException Error(string code) => new(code, Marshal.GetLastWin32Error());

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern DeviceInfoSet SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern DeviceInfoSet SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiOpenDeviceInfoW(DeviceInfoSet deviceInfoSet, string deviceInstanceId,
        IntPtr hwndParent, uint openFlags, ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInfo(DeviceInfoSet deviceInfoSet, uint index, ref DeviceInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(DeviceInfoSet deviceInfoSet, ref DeviceInfoData data,
        StringBuilder? instanceId, uint instanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDevicePropertyW(DeviceInfoSet deviceInfoSet, ref DeviceInfoData data,
        ref PropertyKey key, out uint type, [Out] byte[]? buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiSetDevicePropertyW(DeviceInfoSet deviceInfoSet, ref DeviceInfoData data,
        ref PropertyKey key, uint type, [In] byte[]? buffer, uint bufferSize, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CM_Locate_DevNodeW(out uint devInst, string deviceInstanceId, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
