using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DeviceRadar;

public static class Runtime
{
    public static string BaseDirectory => AppContext.BaseDirectory;
    public static Version WindowsVersion { get; } = GetWindowsVersion();
    public static bool IsSupportedWindows => OperatingSystem.IsWindows() && Environment.Is64BitProcess && RuntimeInformation.OSArchitecture == Architecture.X64 && WindowsVersion >= new Version(10, 0, 19045);
    public static string SystemDescription => OperatingSystem.IsWindows() ? $"Windows {WindowsVersion} {(Environment.Is64BitOperatingSystem ? RuntimeInformation.OSArchitecture.ToString() : "x86")}" : "Unsupported operating system";
    public static bool IsAdministrator
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // Elevation starts this executable alone. User input is never interpreted as a command line.
    public static bool RequestElevation()
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !string.Equals(Path.GetFileName(executable), "DeviceRadar.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Elevation requires the published DeviceRadar.exe.");
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = BaseDirectory };
            return Process.Start(start) is not null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
    }

    private static Version GetWindowsVersion()
    {
        if (!OperatingSystem.IsWindows()) return new Version(0, 0);
        var info = new OsVersionInfo { Size = (uint)Marshal.SizeOf<OsVersionInfo>(), ServicePack = "" };
        if (RtlGetVersion(ref info) != 0) throw new InvalidOperationException("Unable to read Windows version.");
        return new Version((int)info.Major, (int)info.Minor, (int)info.Build);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }
    [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OsVersionInfo info);
}
