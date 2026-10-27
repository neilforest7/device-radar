using System.Runtime.InteropServices;

namespace DeviceRadar.Inventory.Native;

internal static class CoreAudio
{
    internal sealed record Endpoint(string Id, string Name, string Description, string InterfaceName, string? ContainerId, bool Present, bool Capture);
    internal static List<Endpoint> Enumerate()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? collection = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(2, 0xF, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var result = new List<Endpoint>();
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(index, out var endpoint));
                IPropertyStore? properties = null;
                try
                {
                    Marshal.ThrowExceptionForHR(endpoint.GetId(out var id));
                    Marshal.ThrowExceptionForHR(endpoint.GetState(out var state));
                    Marshal.ThrowExceptionForHR(endpoint.OpenPropertyStore(0, out properties));
                    var name = ReadString(properties, SetupApi.FriendlyName);
                    var description = ReadString(properties, SetupApi.Description);
                    var iface = ReadString(properties, SetupApi.InterfaceName);
                    var container = ReadGuid(properties, SetupApi.Container);
                    var audioEndpoint = (IMMEndpoint)endpoint;
                    Marshal.ThrowExceptionForHR(audioEndpoint.GetDataFlow(out var flow));
                    var capture = flow == 1;
                    result.Add(new(id, name ?? description ?? id, description ?? "", iface ?? "", container, (state & 1) != 0, capture));
                }
                finally { if (properties is not null) Marshal.ReleaseComObject(properties); Marshal.ReleaseComObject(endpoint); }
            }
            return result;
        }
        finally { if (collection is not null) Marshal.ReleaseComObject(collection); Marshal.ReleaseComObject(enumerator); }
    }
    private static string? ReadString(IPropertyStore store, SetupApi.Key key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }
    private static string? ReadGuid(IPropertyStore store, SetupApi.Key key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { var guid = value.Type == 72 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.Pointer) : Guid.Empty; return guid == Guid.Empty ? null : guid.ToString("D"); }
        finally { PropVariantClear(ref value); }
    }
    [StructLayout(LayoutKind.Explicit, Size=24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDeviceCollection
    { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice endpoint); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMEndpoint
    { [PreserveSig] int GetDataFlow(out int flow); }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IPropertyStore
    { [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetAt(uint index, out SetupApi.Key key); [PreserveSig] int GetValue(ref SetupApi.Key key, out PropVariant value); }
}
