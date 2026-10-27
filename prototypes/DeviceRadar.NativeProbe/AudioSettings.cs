using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeviceRadar.NativeProbe;

internal sealed record AudioSettingsResult(string RefreshState, int ClosedCount, bool Launched,
    string? Reason = null, int? NativeError = null);

// This explicit UI action is separate from device writes and only addresses Windows Settings' process cache.
internal static class AudioSettings
{
    internal const string Confirmation = "将关闭并重新打开 Windows 设置";
    private const string SoundPage = "ms-settings:sound";
    private const uint CloseMessage = 0x0010;

    public static AudioSettingsResult Refresh(bool confirmClose)
    {
        if (!confirmClose) return new("confirmation-required", 0, false, "confirm-close-required");
        var closedCount = 0;
        try
        {
            var processes = Process.GetProcessesByName("SystemSettings");
            try
            {
                if (processes.Length > 1) return new("refused", 0, false, "settings-process-ambiguous");
                if (processes.Length == 1)
                {
                    var process = processes[0];
                    // Keep this exact process object open through image verification, window close, and exit observation.
                    var pinned = process.SafeHandle;
                    if (!process.HasExited)
                    {
                        VerifyImage(pinned);
                        var roots = VisibleRoots((uint)process.Id);
                        if (roots.Count == 0 && !process.WaitForExit(500))
                            return new("refused", 0, false, "settings-window-not-found");
                        if (!process.HasExited)
                        {
                            var root = UniqueRoot(roots);
                            RequireOnlyProcess(process.Id);
                            VerifyImage(pinned);
                            var freshRoot = UniqueRoot(VisibleRoots((uint)process.Id));
                            if (freshRoot != root || !IsWindowVisible(root) || !RootContainsProcess(root, (uint)process.Id))
                                return new("refused", 0, false, "settings-window-changed");
                            if (process.HasExited) return new("refused", 0, false, "settings-process-changed");
                            if (!PostMessageW(root, CloseMessage, IntPtr.Zero, IntPtr.Zero))
                                throw new ProbeException("settings-close-request-failed", Marshal.GetLastWin32Error());
                            if (!process.WaitForExit(5000)) return new("close-pending", 0, false, "settings-close-timeout");
                            closedCount = 1;
                        }
                    }
                    GC.KeepAlive(pinned);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }

            // A concurrently opened Settings process must not silently replace the process we just closed.
            var remaining = Process.GetProcessesByName("SystemSettings");
            try
            {
                if (remaining.Length != 0) return new("refused", closedCount, false, "settings-process-changed");
            }
            finally { foreach (var process in remaining) process.Dispose(); }
            using var launched = Process.Start(new ProcessStartInfo(SoundPage) { UseShellExecute = true });
            return new("sound-page-launch-requested", closedCount, true);
        }
        catch (Exception error)
        {
            return new("failed", closedCount, false,
                error is Win32Exception ? "settings-process-access-failed" : Program.ErrorCode(error),
                error is Win32Exception native ? native.NativeErrorCode : Program.NativeError(error));
        }
    }

    internal static bool IsExpectedImage(string actual, string windowsDirectory)
        => Path.IsPathFullyQualified(actual) && Path.IsPathFullyQualified(windowsDirectory)
            && string.Equals(Path.GetFullPath(actual),
                Path.Combine(Path.GetFullPath(windowsDirectory), "ImmersiveControlPanel", "SystemSettings.exe"),
                StringComparison.OrdinalIgnoreCase);

    internal static IntPtr UniqueRoot(IEnumerable<IntPtr> roots)
    {
        var unique = roots.Distinct().ToArray();
        if (unique.Length == 0 || unique is [var empty] && empty == IntPtr.Zero) throw new ProbeException("settings-window-not-found");
        if (unique.Length != 1) throw new ProbeException("settings-window-ambiguous");
        return unique[0];
    }

    private static void VerifyImage(SafeProcessHandle process)
    {
        var image = new StringBuilder(32768);
        var length = (uint)image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref length))
            throw new ProbeException("settings-image-query-failed", Marshal.GetLastWin32Error());
        if (!IsExpectedImage(image.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            throw new ProbeException("settings-image-mismatch");
    }

    private static void RequireOnlyProcess(int expectedId)
    {
        var processes = Process.GetProcessesByName("SystemSettings");
        try
        {
            if (processes.Length != 1 || processes[0].Id != expectedId) throw new ProbeException("settings-process-changed");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static List<IntPtr> VisibleRoots(uint processId)
    {
        var roots = new List<IntPtr>();
        if (!EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && RootContainsProcess(window, processId)) roots.Add(window);
                return true;
            }, IntPtr.Zero)) throw new ProbeException("settings-window-enumeration-failed", Marshal.GetLastWin32Error());
        return roots;
    }

    private static bool RootContainsProcess(IntPtr root, uint expectedId)
    {
        _ = GetWindowThreadProcessId(root, out var owner);
        if (owner == expectedId) return true;
        var found = false;
        _ = EnumChildWindows(root, (child, parameter) =>
        {
            _ = GetWindowThreadProcessId(child, out var childOwner);
            if (childOwner == expectedId) found = true;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}
