using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceRadar.Naming.Native;

// The documented BR/EDR API only. Searches use the existing cache and never issue inquiry.
internal sealed class BluetoothClassicNative : IDisposable
{
    internal sealed record Radio(SafeFileHandle Handle, string Address);
    internal sealed record Match(Radio Radio, DeviceInfo Info);
    private readonly List<Radio> radios = [];
    internal int RadioCount => radios.Count;

    internal BluetoothClassicNative()
    {
        ValidateLayout();
        var parameters = new RadioSearch { Size = (uint)Marshal.SizeOf<RadioSearch>() };
        var search = BluetoothFindFirstRadio(ref parameters, out var radio);
        if (search == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            radio?.Dispose();
            if (error is 259 or 1168) return;
            throw new ProbeException("bluetooth-radio-enumeration-failed", error);
        }
        try
        {
            while (true)
            {
                try
                {
                    if (radio.IsInvalid) throw new ProbeException("bluetooth-invalid-radio");
                    var info = new RadioInfo { Size = (uint)Marshal.SizeOf<RadioInfo>(), Name = "" };
                    var error = BluetoothGetRadioInfo(radio, ref info);
                    if (error != 0) throw new ProbeException("bluetooth-radio-info-failed", (int)error);
                    var address = Address(info.Address);
                    if (radios.Any(item => item.Address == address)) throw new ProbeException("bluetooth-radio-ambiguous");
                    radios.Add(new(radio, address));
                }
                catch { radio.Dispose(); throw; }
                if (radios.Count > 32) throw new ProbeException("bluetooth-radio-count-limit");
                if (BluetoothFindNextRadio(search, out radio)) continue;
                var lastError = Marshal.GetLastWin32Error();
                radio?.Dispose();
                if (lastError != 259) throw new ProbeException("bluetooth-radio-enumeration-failed", lastError);
                break;
            }
        }
        catch { Dispose(); throw; }
        finally { _ = BluetoothFindRadioClose(search); }
    }

    internal Match[] Find(ulong address)
    {
        _ = Address(address);
        var matches = new List<Match>();
        foreach (var radio in radios)
        {
            ValidateRadio(radio);
            var parameters = new DeviceSearch
            {
                Size = (uint)Marshal.SizeOf<DeviceSearch>(),
                ReturnAuthenticated = 1, ReturnRemembered = 1, ReturnConnected = 1,
                IssueInquiry = 0, Radio = radio.Handle.DangerousGetHandle()
            };
            var info = EmptyDevice();
            var search = BluetoothFindFirstDevice(ref parameters, ref info);
            if (search == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) continue;
                throw new ProbeException("bluetooth-device-enumeration-failed", error);
            }
            try
            {
                for (var count = 0; ; count++)
                {
                    if (count >= 4096) throw new ProbeException("bluetooth-device-count-limit");
                    if (info.Size != Marshal.SizeOf<DeviceInfo>()) throw new ProbeException("bluetooth-device-layout-mismatch");
                    if (info.Address == address)
                    {
                        // Re-read independently; a name supplied by the enumerator is not our final observation.
                        var actual = Read(radio, address);
                        matches.Add(new(radio, actual));
                    }
                    info = EmptyDevice();
                    if (BluetoothFindNextDevice(search, ref info)) continue;
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) throw new ProbeException("bluetooth-device-enumeration-failed", error);
                    break;
                }
            }
            finally { _ = BluetoothFindDeviceClose(search); }
        }
        return matches.ToArray();
    }

    internal static DeviceInfo Read(Radio radio, ulong address)
    {
        ValidateRadio(radio);
        var info = EmptyDevice();
        info.Address = address;
        var error = BluetoothGetDeviceInfo(radio.Handle, ref info);
        if (error != 0) throw new ProbeException("bluetooth-device-info-failed", (int)error);
        if (info.Size != Marshal.SizeOf<DeviceInfo>() || info.Address != address)
            throw new ProbeException("bluetooth-device-binding-changed");
        if (info.Name is not { Length: > 0 and <= 247 } || info.Name.Any(char.IsControl))
            throw new ProbeException("bluetooth-invalid-resolved-name");
        return info;
    }

    internal static void Update(DeviceInfo existing, string alias)
    {
        if (existing.Size != Marshal.SizeOf<DeviceInfo>() || alias.Length > 247 || alias.Any(char.IsControl))
            throw new ProbeException("invalid-bluetooth-alias");
        _ = Address(existing.Address);
        // Preserve all observed fields. The API has no caller-selectable update flags or radio parameter.
        existing.Name = alias;
        var error = BluetoothUpdateDeviceRecord(ref existing);
        if (error != 0) throw new ProbeException("bluetooth-update-failed", (int)error);
    }

    private static void ValidateRadio(Radio radio)
    {
        if (radio.Handle.IsClosed || radio.Handle.IsInvalid) throw new ProbeException("bluetooth-radio-unavailable");
        var info = new RadioInfo { Size = (uint)Marshal.SizeOf<RadioInfo>(), Name = "" };
        var error = BluetoothGetRadioInfo(radio.Handle, ref info);
        if (error != 0) throw new ProbeException("bluetooth-radio-info-failed", (int)error);
        if (Address(info.Address) != radio.Address) throw new ProbeException("bluetooth-radio-binding-changed");
    }

    internal static string Address(ulong value) => value is > 0 and <= 0xffffffffffff
        ? value.ToString("X12", CultureInfo.InvariantCulture) : throw new ProbeException("invalid-bluetooth-address");

    internal static ulong ParseAddress(string value)
    {
        if (value is not { Length: 12 } || value.Any(c => !char.IsAsciiHexDigit(c))
            || !ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)
            || address == 0) throw new ProbeException("invalid-bluetooth-address");
        return address;
    }

    internal static void ValidateLayout()
    {
        if (!Environment.Is64BitProcess || Marshal.SizeOf<DeviceInfo>() != 560 || Marshal.SizeOf<RadioInfo>() != 520
            || Marshal.SizeOf<DeviceSearch>() != 40 || Marshal.OffsetOf<DeviceInfo>(nameof(DeviceInfo.Name)).ToInt32() != 64)
            throw new ProbeException("bluetooth-native-layout-unsupported");
    }

    private static DeviceInfo EmptyDevice() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Name = "" };
    public void Dispose() { foreach (var radio in radios) radio.Handle.Dispose(); radios.Clear(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct RadioSearch { internal uint Size; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RadioInfo
    {
        internal uint Size;
        internal ulong Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] internal string Name;
        internal uint ClassOfDevice;
        internal ushort Subversion;
        internal ushort Manufacturer;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSearch
    {
        internal uint Size;
        internal int ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        internal byte TimeoutMultiplier;
        internal IntPtr Radio;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTime
    {
        internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DeviceInfo
    {
        internal uint Size;
        internal ulong Address;
        internal uint ClassOfDevice;
        internal int Connected, Remembered, Authenticated;
        internal SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] internal string Name;
    }

    [DllImport("bthprops.cpl", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref RadioSearch parameters, out SafeFileHandle radio);
    [DllImport("bthprops.cpl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(IntPtr search, out SafeFileHandle radio);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr search);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    private static extern uint BluetoothGetRadioInfo(SafeFileHandle radio, ref RadioInfo info);
    [DllImport("bthprops.cpl", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref DeviceSearch parameters, ref DeviceInfo info);
    [DllImport("bthprops.cpl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr search, ref DeviceInfo info);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr search);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    private static extern uint BluetoothGetDeviceInfo(SafeFileHandle radio, ref DeviceInfo info);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    private static extern uint BluetoothUpdateDeviceRecord(ref DeviceInfo info);
}
