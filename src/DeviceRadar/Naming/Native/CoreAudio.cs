using System.Runtime.InteropServices;

namespace DeviceRadar.Naming.Native;

// Read-only independent resolution. Basic endpoint IPropertyStore values are not a supported application write API.
internal static class CoreAudio
{
    internal sealed record Endpoint(string Id, int Flow, uint State, string PnpInstanceId,
        string Name, RawValue Description, string InterfaceName, string ContainerId);
    internal sealed record EnvironmentSnapshot(uint State, string?[] Defaults);

    // The connected topology device exposes the actual PnP instance ID; the endpoint ID stays opaque.
    internal static Endpoint Open(string endpointId, int flow)
    {
        if (!Runtime.IsSupportedWindows) throw new ProbeException("audio-platform-unsupported");
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 4096 || endpointId.Any(char.IsControl))
            throw new ProbeException("invalid-audio-endpoint-id");
        if (flow is not (0 or 1)) throw new ProbeException("invalid-audio-flow");
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? endpoint = null;
        IPropertyStore? properties = null;
        try
        {
            Check(enumerator.GetDevice(endpointId, out endpoint));
            Check(endpoint.GetId(out var actualId));
            if (!string.Equals(endpointId, actualId, StringComparison.Ordinal))
                throw new ProbeException("audio-endpoint-id-mismatch");
            Check(((IMMEndpoint)endpoint).GetDataFlow(out var actualFlow));
            if (actualFlow != flow) throw new ProbeException("audio-flow-mismatch");
            Check(endpoint.GetState(out var state));
            if (state is not (1 or 2)) throw new ProbeException("audio-endpoint-unavailable");
            Check(endpoint.OpenPropertyStore(0, out properties));
            var description = ReadDescription(properties);
            var name = ReadString(properties, Native.FriendlyName);
            var interfaceName = ReadString(properties, Native.InterfaceFriendlyName);
            var containerId = ReadGuid(properties, Native.ContainerId);
            var pnpId = ConnectedPnpId(endpoint, enumerator);
            return new(actualId, actualFlow, state, pnpId, name, description, interfaceName, containerId);
        }
        finally
        {
            if (properties is not null) Marshal.ReleaseComObject(properties);
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string ConnectedPnpId(IMMDevice endpoint, IMMDeviceEnumerator enumerator)
    {
        var iid = typeof(IDeviceTopology).GUID;
        IDeviceTopology? topology = null;
        IConnector? connector = null;
        IMMDevice? connected = null;
        IPropertyStore? properties = null;
        try
        {
            Check(endpoint.Activate(ref iid, 23, IntPtr.Zero, out var instance));
            topology = (IDeviceTopology)instance;
            Check(topology.GetConnectorCount(out var count));
            if (count != 1) throw new ProbeException("audio-topology-ambiguous");
            Check(topology.GetConnector(0, out connector));
            Check(connector.GetDeviceIdConnectedTo(out var connectedId));
            Check(enumerator.GetDevice(connectedId, out connected));
            Check(connected.OpenPropertyStore(0, out properties));
            var key = new Native.PropertyKey(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
            return ReadString(properties, key);
        }
        finally
        {
            if (properties is not null) Marshal.ReleaseComObject(properties);
            if (connected is not null) Marshal.ReleaseComObject(connected);
            if (connector is not null) Marshal.ReleaseComObject(connector);
            if (topology is not null) Marshal.ReleaseComObject(topology);
        }
    }

    internal static EnvironmentSnapshot ObserveEnvironment(string endpointId)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? endpoint = null;
        try
        {
            Check(enumerator.GetDevice(endpointId, out endpoint));
            Check(endpoint.GetState(out var state));
            var defaults = new string?[6];
            for (var flow = 0; flow < 2; flow++)
            for (var role = 0; role < 3; role++)
            {
                var hr = enumerator.GetDefaultAudioEndpoint(flow, role, out var item);
                if (hr == unchecked((int)0x80070490)) continue; // E_NOTFOUND
                Check(hr);
                try { Check(item.GetId(out defaults[flow * 3 + role])); }
                finally { Marshal.ReleaseComObject(item); }
            }
            return new(state, defaults);
        }
        finally
        {
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    internal static bool SameEnvironment(EnvironmentSnapshot a, EnvironmentSnapshot b)
        => a.State == b.State && a.Defaults.SequenceEqual(b.Defaults, StringComparer.Ordinal);

    private static RawValue ReadDescription(IPropertyStore store)
    {
        var key = Native.DeviceDescription;
        Check(store.GetValue(ref key, out var value));
        try
        {
            if (value.Type == 0) return RawValue.Missing();
            if (value.Type != 31 || value.Pointer == IntPtr.Zero) throw new ProbeException("audio-description-type-unsupported");
            var text = Marshal.PtrToStringUni(value.Pointer) ?? throw new ProbeException("audio-description-unavailable");
            if (text.Length > 4095) throw new ProbeException("audio-description-size-limit");
            return new(true, Format.RegistryString, Format.Unicode.GetBytes(text + "\0"));
        }
        finally { _ = PropVariantClear(ref value); }
    }

    // Observe the system's notifications; this never broadcasts or manufactures a property change.
    internal static NameChangeMonitor WatchNameChanges(string endpointId) => new(endpointId);

    internal sealed class NameChangeMonitor : IDisposable
    {
        private readonly IMMDeviceEnumerator enumerator;
        private readonly NameNotificationClient client;
        private bool registered;
        public int Count => client.Count;

        internal NameChangeMonitor(string endpointId)
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            client = new NameNotificationClient(endpointId);
            try
            {
                Check(enumerator.GetDevice(endpointId, out var endpoint));
                Marshal.ReleaseComObject(endpoint);
                var result = enumerator.RegisterEndpointNotificationCallback(client);
                if (result < 0) throw new ProbeException("audio-notification-register-failed", result);
                registered = true;
            }
            catch
            {
                Marshal.ReleaseComObject(enumerator);
                throw;
            }
        }

        public void Dispose()
        {
            if (!registered) return;
            registered = false;
            _ = enumerator.UnregisterEndpointNotificationCallback(client);
            Marshal.ReleaseComObject(enumerator);
            GC.KeepAlive(client);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class NameNotificationClient(string endpointId) : IMMNotificationClient
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public int OnDeviceStateChanged(string id, uint state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string? id) => 0;
        public int OnPropertyValueChanged(string id, Native.PropertyKey key)
        {
            if (string.Equals(id, endpointId, StringComparison.Ordinal)
                && key.FormatId == Native.DeviceDescription.FormatId && key.PropertyId is 2 or 14)
                Interlocked.Increment(ref count);
            return 0;
        }
    }

    private static string ReadString(IPropertyStore store, Native.PropertyKey key)
    {
        Check(store.GetValue(ref key, out var value));
        try
        {
            if (value.Type != 31 || value.Pointer == IntPtr.Zero) throw new ProbeException("audio-resolved-name-unavailable");
            var text = Marshal.PtrToStringUni(value.Pointer) ?? "";
            if (text.Length is 0 or > 512 || text.Any(char.IsControl)) throw new ProbeException("audio-resolved-name-invalid");
            return text;
        }
        finally { _ = PropVariantClear(ref value); }
    }

    private static string ReadGuid(IPropertyStore store, Native.PropertyKey key)
    {
        Check(store.GetValue(ref key, out var value));
        try
        {
            if (value.Type != 72 || value.Pointer == IntPtr.Zero) throw new ProbeException("audio-container-unavailable");
            var guid = Marshal.PtrToStructure<Guid>(value.Pointer);
            if (guid == Guid.Empty) throw new ProbeException("audio-container-unavailable");
            return guid.ToString("D");
        }
        finally { _ = PropVariantClear(ref value); }
    }

    private static void Check(int result) { if (result < 0) throw new ProbeException("core-audio-read-failed", result); }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }
    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, Native.PropertyKey key);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice endpoint);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint { [PreserveSig] int GetDataFlow(out int flow); }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }
    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IntPtr connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out int connected);
        [PreserveSig] int GetConnectedTo(out IntPtr connector);
        [PreserveSig] int GetConnectorIdConnectedTo(out IntPtr id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out Native.PropertyKey key);
        [PreserveSig] int GetValue(ref Native.PropertyKey key, out PropVariant value);
    }
}
