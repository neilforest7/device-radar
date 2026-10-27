using System.Runtime.InteropServices;

namespace DeviceRadar.NativeProbe;

// Read-only independent resolution. Basic endpoint IPropertyStore values are not a supported application write API.
internal static class CoreAudio
{
    internal sealed record Capture(string Id, string Name, string Description, string InterfaceName, string ContainerId, bool DefaultForAllRoles);
    public static Capture[] ActiveCaptures()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? collection = null;
        try
        {
            Check(enumerator.EnumAudioEndpoints(1 /* capture */, 1 /* active */, out collection));
            Check(collection.GetCount(out var count));
            if (count > 256) throw new ProbeException("audio-endpoint-count-limit");
            var defaults = new List<string>();
            for (var role = 0; role < 3; role++)
            {
                Check(enumerator.GetDefaultAudioEndpoint(1, role, out var endpoint));
                try { Check(endpoint.GetId(out var id)); defaults.Add(id); }
                finally { Marshal.ReleaseComObject(endpoint); }
            }
            var results = new List<Capture>();
            for (uint index = 0; index < count; index++)
            {
                Check(collection.Item(index, out var endpoint));
                IPropertyStore? properties = null;
                try
                {
                    Check(endpoint.GetId(out var id));
                    Check(endpoint.OpenPropertyStore(0 /* STGM_READ */, out properties));
                    var name = ReadString(properties, Native.FriendlyName);
                    var description = ReadString(properties, Native.DeviceDescription);
                    var interfaceName = ReadString(properties, Native.InterfaceFriendlyName);
                    var container = ReadGuid(properties, Native.ContainerId);
                    results.Add(new(id, name, description, interfaceName, container, defaults.All(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase))));
                }
                finally
                {
                    if (properties is not null) Marshal.ReleaseComObject(properties);
                    Marshal.ReleaseComObject(endpoint);
                }
            }
            return results.ToArray();
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(enumerator);
        }
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
            if (string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase)
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
            return Marshal.PtrToStructure<Guid>(value.Pointer).ToString("D");
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
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out Native.PropertyKey key);
        [PreserveSig] int GetValue(ref Native.PropertyKey key, out PropVariant value);
    }
}
