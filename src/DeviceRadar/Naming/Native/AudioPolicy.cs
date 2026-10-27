using System.Runtime.InteropServices;

namespace DeviceRadar.Naming.Native;

// Experimental Windows build 22631 path. The modern IPolicyConfig ABI is also used by
// SoundSwitch (ee7def135e) and AudioDeviceCmdlets (26d08d30e9). It is not a documented API.
// Only the fixed main-store DeviceDesc property is accessible through this class.
internal static class AudioPolicy
{
    private const int VariantSize = 24; // Full x64 PROPVARIANT, including its largest union member.
    private const short WideStringVariant = 31;
    private static readonly byte[] EmptyVariant = new byte[VariantSize];

    private static IntPtr AllocateVariant()
    {
        var variant = Marshal.AllocCoTaskMem(VariantSize);
        try { Marshal.Copy(EmptyVariant, 0, variant, VariantSize); return variant; }
        catch { Marshal.FreeCoTaskMem(variant); throw; }
    }

    internal static TargetWriteLease OpenWriter(string endpointId, RawValue expected, RawValue desired)
    {
        var lease = new PolicyWriteLease(endpointId, expected, desired);
        return new(lease.Write, lease, () => lease.NotificationCount);
    }

    private sealed class PolicyWriteLease : IDisposable
    {
        private readonly string endpointId;
        private readonly string expected;
        private readonly string desired;
        private readonly IPolicyConfig policy;
        private readonly IntPtr key;
        private CoreAudio.NameChangeMonitor? monitor;
        private bool disposed;
        public int NotificationCount => monitor?.Count ?? 0;

        internal PolicyWriteLease(string endpointId, RawValue expected, RawValue desired)
        {
            if (Runtime.WindowsVersion.Build != 22631 || !Environment.Is64BitProcess)
                throw new ProbeException("experimental-build-unsupported");
            if (!expected.Exists || !desired.Exists)
                throw new ProbeException("audio-policy-description-required");
            this.endpointId = endpointId;
            this.expected = Format.Text(expected, Format.RegistryString) ?? throw new ProbeException("audio-policy-description-required");
            this.desired = Format.Text(desired, Format.RegistryString) ?? throw new ProbeException("audio-policy-description-required");
            policy = (IPolicyConfig)new PolicyConfigClient();
            key = Marshal.AllocCoTaskMem(Marshal.SizeOf<Native.PropertyKey>());
            try
            {
                Marshal.StructureToPtr(Native.DeviceDescription, key, false);
                CheckCurrent(); // Read the same property through the exact native ABI before any Set.
                monitor = CoreAudio.WatchNameChanges(endpointId);
            }
            catch
            {
                Marshal.FreeCoTaskMem(key);
                Marshal.ReleaseComObject(policy);
                throw;
            }
        }

        private void CheckCurrent()
        {
            var variant = AllocateVariant();
            try
            {
                var result = policy.GetPropertyValue(endpointId, false, key, variant);
                if (result < 0) throw new ProbeException("audio-policy-read-failed", result);
                var pointer = Marshal.ReadIntPtr(variant, 8);
                if (Marshal.ReadInt16(variant) != WideStringVariant || pointer == IntPtr.Zero)
                    throw new ProbeException("audio-policy-description-type");
                var text = Marshal.PtrToStringUni(pointer);
                if (!string.Equals(text, expected, StringComparison.Ordinal))
                    throw new ProbeException("audio-policy-value-changed");
            }
            finally
            {
                _ = PropVariantClear(variant);
                Marshal.FreeCoTaskMem(variant);
            }
        }

        public void Write()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckCurrent();
            var variant = AllocateVariant();
            try
            {
                var name = Marshal.StringToCoTaskMemUni(desired);
                Marshal.WriteInt16(variant, WideStringVariant);
                Marshal.WriteIntPtr(variant, 8, name);
                var result = policy.SetPropertyValue(endpointId, false, key, variant);
                if (result < 0) throw new ProbeException("audio-policy-set-failed", result);
                _ = SpinWait.SpinUntil(() => NotificationCount > 0, TimeSpan.FromSeconds(1));
            }
            finally
            {
                _ = PropVariantClear(variant);
                Marshal.FreeCoTaskMem(variant);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            monitor?.Dispose();
            Marshal.FreeCoTaskMem(key);
            Marshal.ReleaseComObject(policy);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr variant);

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    // Keep all preceding entries in order. Unlike old Win7 snippets, the modern
    // f8679f50 interface includes a 32-bit bFxStore before the key for both methods.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, uint role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
