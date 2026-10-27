using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using DeviceRadar.Naming.Native;
using NativeIdentity = DeviceRadar.Naming.Native.DeviceIdentity;

namespace DeviceRadar;

public sealed partial class NamingService
{
    public static int FileCheck() => SelfCheckFiles();
    // Explicit filesystem checks use a new temporary directory; they never address a real device.
    public static int SelfCheckFiles()
    {
        var count = 0;
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporary, "DeviceRadar-state-check-" + Guid.NewGuid().ToString("N"));
        var machine = new string('a', 64);
        var store = new RecordStore(root, machine);
        var identity = new NativeIdentity(@"HID\VID_046D&PID_C547&TEST\LOCAL", Format.MouseClass,
            [@"HID\VID_046D&PID_C547"], "79eae121-7817-4acc-8938-199bfca0f733", []);
        var record = new OperationRecord(Format.Version, Guid.NewGuid().ToString("N"), machine,
            DateTimeOffset.UtcNow, "prepared", "self-check", Targets.Mouse, identity, RawValue.Missing(),
            "HID-compliant mouse", "Device Radar test", Format.Name("Device Radar test"));
        var junction = Path.Combine(root, "junction");
        var junctionTarget = Path.Combine(root, "junction-target");
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("State filesystem check failed");
            count++;
        }
        void Refuses(Action action, string? code = null)
        {
            try { action(); }
            catch (ProbeException error) when (code is null || error.Code == code) { count++; return; }
            catch (IOException) when (code is null) { count++; return; }
            throw new InvalidOperationException("State filesystem refusal check failed: " + code);
        }
        try
        {
            Refuses(() => store.RequireValidation(Targets.Mouse, identity), "unverified-device-not-validated");
            var token = store.SaveRecoveryPoint(record);
            using (var lease = store.Load(token))
            {
                Check(lease.Record.Original.Same(record.Original));
                Refuses(() => { using var blocked = new FileStream(Path.Combine(root, token), FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
                Refuses(() => { using var blocked = new FileStream(Path.Combine(root, token) + ".sha256", FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            }
            Refuses(() => store.SaveRecoveryPoint(record)); // A second save cannot overwrite an immutable recovery point.
            using (var mutation = store.LockMutations())
            {
                Refuses(() => { using var other = store.LockMutations(); }, "operation-busy");
                Refuses(() => Directory.Move(root, root + "-moved"));
            }
            using (var lease = store.Load(token))
            {
                store.AppendEvent(new(Format.Version, record.OperationId, lease.Hash, DateTimeOffset.UtcNow,
                    "apply", "intent", "write-intent", Before: record.Original));
                Check(store.HasApplyEvidence(lease)); // A durable intent remains sufficient after a process interruption.
            }
            Check(store.History().Single(item => item.Token == token).Status == "interrupted");
            Refuses(() => { using var wrong = new RecordStore(root, new string('b', 64)).Load(token); }, "machine-mismatch");
            store.SaveSettings(new("zh", false));
            Check(store.LoadSettings().Settings == new UserSettings("zh", false));
            Refuses(() => store.SaveSettings(new("unsupported")), "invalid-settings");

            var incomplete = record with { OperationId = Guid.NewGuid().ToString("N") };
            File.WriteAllBytes(Path.Combine(root, incomplete.OperationId + ".json"), JsonSerializer.SerializeToUtf8Bytes(incomplete, Format.Json));
            Refuses(() => { using var missingSeal = store.Load(incomplete.OperationId + ".json"); });
            File.WriteAllText(Path.Combine(root, incomplete.OperationId + ".json.sha256"), new string('a', 32));
            Refuses(() => { using var partialSeal = store.Load(incomplete.OperationId + ".json"); }, "invalid-file-seal");

            var injected = record with { OperationId = Guid.NewGuid().ToString("N") };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(injected, Format.Json);
            var invalidJson = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)[..^1] + ",\"command\":\"external.exe\",\"registryPath\":\"arbitrary\"}");
            var injectedPath = Path.Combine(root, injected.OperationId + ".json");
            File.WriteAllBytes(injectedPath, invalidJson);
            File.WriteAllText(injectedPath + ".sha256", Format.Hash(invalidJson), new UTF8Encoding(false));
            Refuses(() => { using var injection = store.Load(injected.OperationId + ".json"); }, "invalid-record-json");

            var duplicate = record with { OperationId = Guid.NewGuid().ToString("N") };
            var duplicateJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(duplicate, Format.Json)[..^1] + ",\"status\":\"prepared\"}");
            var duplicatePath = Path.Combine(root, duplicate.OperationId + ".json");
            File.WriteAllBytes(duplicatePath, duplicateJson);
            File.WriteAllText(duplicatePath + ".sha256", Format.Hash(duplicateJson), new UTF8Encoding(false));
            Refuses(() => { using var duplicateField = store.Load(duplicate.OperationId + ".json"); }, "duplicate-json-field");

            var failedIntent = record with { OperationId = Guid.NewGuid().ToString("N") };
            var failedIntentToken = store.SaveRecoveryPoint(failedIntent);
            File.WriteAllText(Path.Combine(root, failedIntent.OperationId + ".events"), "blocks directory creation");
            var wrote = false;
            using (var lease = store.Load(failedIntentToken))
                Refuses(() => Safety.GuardedWrite(() => store.AppendEvent(new(Format.Version, failedIntent.OperationId,
                    lease.Hash, DateTimeOffset.UtcNow, "apply", "intent", "write-intent", Before: failedIntent.Original)),
                    () => { }, () => wrote = true));
            Check(!wrote);

            Directory.CreateDirectory(junctionTarget);
            CreateCheckJunction(junction, junctionTarget);
            Refuses(() => { using var unsafeRoot = new RecordStore(junction, machine).LockMutations(); }, "reparse-point-refused");

            File.WriteAllText(Path.Combine(root, token) + ".sha256", new string('0', 64), new UTF8Encoding(false));
            Refuses(() => { using var corrupt = store.Load(token); }, "invalid-file-seal");
            Check(store.History().Any(item => item.Token == token && item.Record is null && item.Status == "unavailable"));
            return count;
        }
        finally
        {
            // Delete only this generated directory and the one-level event directories created above.
            var checkedRoot = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(checkedRoot), Path.TrimEndingDirectorySeparator(temporary), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(checkedRoot).StartsWith("DeviceRadar-state-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected temporary check directory");
            if (Directory.Exists(checkedRoot))
            {
                foreach (var directory in Directory.GetDirectories(checkedRoot))
                {
                    if (string.Equals(directory, junction, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(directory, junctionTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(directory);
                        continue;
                    }
                    if (!string.Equals(Path.GetFileName(directory), record.OperationId + ".events", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected temporary child directory");
                    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                    Directory.Delete(directory);
                }
                foreach (var file in Directory.GetFiles(checkedRoot)) File.Delete(file);
                Directory.Delete(checkedRoot);
            }
        }
    }

    private static void CreateCheckJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var printable = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printable.Length));
        substitute.CopyTo(buffer, 16);
        printable.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateCheckDirectoryHandle(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !SetCheckReparsePoint(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Cannot create isolated junction check", Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateCheckDirectoryHandle(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCheckReparsePoint(SafeFileHandle file, uint controlCode, byte[] input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
