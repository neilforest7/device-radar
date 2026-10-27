using System.Runtime.InteropServices;

namespace DeviceRadar;

internal static class ConsoleAppearance
{
    // Console font APIs apply to the whole console, not to a process. Only adjust
    // a visible, unshared classic console; pseudoconsoles render in their host.
    public static IDisposable Prepare()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return Empty.Instance;
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) || GetConsoleProcessList(new uint[2], 2) != 1)
            return Empty.Instance;

        var output = GetStdHandle(-11);
        var original = new ConsoleFontInfo { Size = (uint)Marshal.SizeOf<ConsoleFontInfo>(), FaceName = "" };
        if (output == IntPtr.Zero || output == new IntPtr(-1) || !GetCurrentConsoleFontEx(output, false, ref original))
            return Empty.Instance;

        var font = original;
        font.FontSize.X = 0; // Let the console select the width for the existing height.
        font.FontFamily = 0x36; // Fixed-pitch TrueType, modern family.
        font.FontWeight = 400;
        font.FaceName = "Consolas";
        return SetCurrentConsoleFontEx(output, false, ref font) ? new RestoreFont(output, original) : Empty.Instance;
    }

    private sealed class RestoreFont(IntPtr output, ConsoleFontInfo original) : IDisposable
    {
        public void Dispose() => SetCurrentConsoleFontEx(output, false, ref original);
    }

    private sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();
        public void Dispose() { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConsoleFontInfo
    {
        public uint Size, FontIndex;
        public Coord FontSize;
        public uint FontFamily, FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr output, [MarshalAs(UnmanagedType.Bool)] bool maximumWindow, ref ConsoleFontInfo font);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCurrentConsoleFontEx(IntPtr output, [MarshalAs(UnmanagedType.Bool)] bool maximumWindow, ref ConsoleFontInfo font);
}
