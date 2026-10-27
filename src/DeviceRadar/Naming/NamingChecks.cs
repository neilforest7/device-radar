using DeviceRadar.Naming.Native;
using NativeSnapshot = DeviceRadar.Naming.Native.DeviceSnapshot;
using NativeIdentity = DeviceRadar.Naming.Native.DeviceIdentity;

namespace DeviceRadar;

public sealed partial class NamingService
{
    public static int SelfCheck()
    {
        var count = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Naming self-check failed");
            count++;
        }
        void Refuses(Action action, string code)
        {
            try { action(); }
            catch (ProbeException error) when (error.Code == code) { count++; return; }
            throw new InvalidOperationException("Naming boundary check failed: " + code);
        }
        var identity = new NativeIdentity(@"HID\VID_046D&PID_C547&TEST\LOCAL", Format.MouseClass,
            [@"HID\VID_046D&PID_C547"], "79eae121-7817-4acc-8938-199bfca0f733", []);
        var original = RawValue.Missing();
        var proposed = Format.Name("Device Radar test");
        var record = new OperationRecord(Format.Version, Guid.NewGuid().ToString("N"), new string('a', 64),
            DateTimeOffset.UtcNow, "prepared", "self-check", Targets.Mouse, identity, original,
            "HID-compliant mouse", "Device Radar test", proposed);
        var current = new NativeSnapshot(identity, original, record.OriginalResolvedName);
        Safety.ValidateRecord(record);
        Check(Safety.Decide(record, record.MachineFingerprint, current, "apply", false) == MutationDecision.Write);
        Refuses(() => Safety.Decide(record, new string('b', 64), current, "apply", false), "machine-mismatch");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint, current with { Identity = identity with { InstanceId = identity.InstanceId + "X" } }, "apply", false), "target-mismatch");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint, current with { Identity = identity with { ContainerId = Guid.NewGuid().ToString("D") } }, "apply", false), "target-mismatch");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint, current with { FriendlyName = Format.Name("external") }, "apply", false), "current-value-changed");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint, current with { FriendlyName = proposed }, "restore", false), "apply-evidence-missing");
        Check(Safety.Decide(record, record.MachineFingerprint, current with { FriendlyName = proposed }, "restore", true) == MutationDecision.Write);
        Check(original.ForWrite() is { Type: Format.Empty, Bytes: null });
        Check(Safety.Decide(record, record.MachineFingerprint, current, "restore", false) == MutationDecision.NoOp);
        Refuses(() => Format.Name("test\u001b[1m"), "invalid-name");
        Refuses(() => Format.Name("bad\0name"), "invalid-name");
        Refuses(() => Targets.RequireWritable("ble-keyboard"), "invalid-target-kind");
        Refuses(() => Targets.RequireWritable("audio-playback"), "invalid-target-kind");
        Refuses(() => Safety.ValidateRecord(record with { FormatVersion = 3 }), "invalid-record");
        var wrote = false;
        Refuses(() => Safety.GuardedWrite(() => throw new ProbeException("backup-failed"), () => { }, () => wrote = true), "backup-failed");
        Check(!wrote);
        Refuses(() => Safety.GuardedWrite(() => { }, () => throw new ProbeException("fresh-check-failed"), () => wrote = true), "fresh-check-failed");
        Check(!wrote);
        var stages = new List<string>();
        Safety.GuardedWrite(() => stages.Add("intent"), () => stages.Add("fresh"), () => stages.Add("write"));
        Check(stages.SequenceEqual(new[] { "intent", "fresh", "write" }));
        var intent = new OperationEvent(Format.Version, record.OperationId, "hash", DateTimeOffset.UtcNow, "apply", "intent", "write-intent", Before: original);
        Check(Safety.IsApplyEvidence(intent, record, "hash"));
        Check(!Safety.IsApplyEvidence(intent with { RecordHash = "other" }, record, "hash"));
        var restore = record with { Purpose = "restore", SourceOperationId = Guid.NewGuid().ToString("N"),
            Original = proposed, OriginalResolvedName = record.ProposedName, Proposed = original, ProposedName = record.OriginalResolvedName };
        Safety.ValidateRecord(restore);
        Check(Safety.Decide(restore, restore.MachineFingerprint, current with { FriendlyName = proposed }, "apply", false) == MutationDecision.Write);
        var audioIdentity = new NativeIdentity(@"SWD\MMDEVAPI\{0.0.1.00000000}.{79eae121-7817-4acc-8938-199bfca0f733}", Targets.AudioEndpointClass,
            [@"MMDEVAPI\AudioEndpoints"], identity.ContainerId,
            [new(@"USB\VID_001F&PID_0B21&MI_00\LOCAL", Targets.MediaClass, [@"USB\VID_001F&PID_0B21&MI_00"], identity.ContainerId),
             new(@"USB\VID_001F&PID_0B21\LOCAL", Targets.UsbClass, [@"USB\VID_001F&PID_0B21"], identity.ContainerId)]);
        var audioRecord = record with { TargetKind = Targets.UsbMicrophone, Target = audioIdentity,
            Original = Format.Name("Microphone", Format.RegistryString), OriginalResolvedName = "Microphone (AB13X USB Audio)",
            ProposedName = "New microphone", Proposed = Format.Name("New microphone", Format.RegistryString) };
        var audio = new NativeSnapshot(audioIdentity, audioRecord.Original, audioRecord.OriginalResolvedName, " (AB13X USB Audio)",
            new("Microphone", "AB13X USB Audio", audioRecord.OriginalResolvedName, audioRecord.OriginalResolvedName, audioRecord.OriginalResolvedName));
        var mixed = audio with { AudioNameSources = audio.AudioNameSources! with { PnpName = "New microphone (AB13X USB Audio)" } };
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint, mixed, "apply", false), "apply-evidence-missing");
        Check(Safety.Decide(audioRecord, audioRecord.MachineFingerprint, mixed, "apply", true) == MutationDecision.Write);
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint,
            audio with { AudioNameSources = audio.AudioNameSources! with { PnpName = "External name" } }, "apply", true), "audio-name-source-changed");
        Refuses(() => Safety.ValidateRecord(record with { Target = identity with { HardwareIds = [@"HID\VID_046D&PID_OTHER"] } }), "invalid-record-target");
        Check(AudioSettings.Refresh(false) is { ClosedCount: 0, Launched: false, RefreshState: "confirmation-required" });
        Check(AudioSettings.IsExpectedImage(@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", @"C:\Windows"));
        Check(!AudioSettings.IsExpectedImage(@"C:\Temp\SystemSettings.exe", @"C:\Windows"));
        // These checks only parse tokens; they do not open or create the synthetic directory.
        var store = new RecordStore(Path.Combine(Path.GetTempPath(), "DeviceRadar-self-check-unused"), record.MachineFingerprint);
        Refuses(() => store.Resolve(@"..\outside.json"), "invalid-record-token");
        Refuses(() => store.Resolve(@"C:\outside.json"), "invalid-record-token");
        Refuses(() => store.Resolve(record.OperationId + ".json:stream"), "invalid-record-token");
        Check(Path.GetFileName(store.Resolve(record.OperationId + ".json")) == record.OperationId + ".json");
        return count;
    }
}
