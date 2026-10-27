using System.Runtime.InteropServices;

namespace DeviceRadar.Inventory.Native;

internal sealed class DeviceNotifications : IDisposable
{
    // CM_NOTIFY_FILTER includes a union whose largest member is WCHAR[MAX_DEVICE_ID_LEN] (200).
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct Filter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint Type;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr handle, IntPtr context, uint action, IntPtr data, uint size);
    private readonly Callback callback;
    private IntPtr handle;
    internal DeviceNotifications(Action changed)
    {
        callback = (_, _, _, _, _) => { try { changed(); } catch (ObjectDisposedException) { } return 0; };
        var filter = new Filter { Size = (uint)Marshal.SizeOf<Filter>(), Flags = 1, Type = 0 };
        var result = Register(ref filter, IntPtr.Zero, callback, out handle);
        if (result != 0) throw new InvalidOperationException($"Device notification registration failed: CONFIGRET 0x{result:X}");
    }
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        _ = Unregister(handle);
        handle = IntPtr.Zero;
        GC.KeepAlive(callback);
    }
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification")]
    private static extern uint Register(ref Filter filter, IntPtr context, Callback callback, out IntPtr handle);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification")]
    private static extern uint Unregister(IntPtr handle);
}
