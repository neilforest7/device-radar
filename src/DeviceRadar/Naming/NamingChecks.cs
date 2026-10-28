using DeviceRadar.Naming.Native;
using System.Text;
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

        var machine = new string('a', 64);
        var identity = new NativeIdentity(@"ROOT\DR_GENERIC_NODE\0000", new Guid("c1e68d1d-0b59-451e-b15e-9744de193efe"),
            ["DR_GENERIC_NODE"], "79eae121-7817-4acc-8938-199bfca0f733", []);
        var original = RawValue.Missing();
        var proposed = Format.Name("Device Radar test");
        var record = new OperationRecord(Format.Version, Guid.NewGuid().ToString("N"), machine,
            DateTimeOffset.UtcNow, "prepared", "self-check", Targets.Pnp, identity, original,
            "Generic device", "Device Radar test", proposed);
        var current = new NativeSnapshot(identity, original, record.OriginalResolvedName);
        Safety.ValidateRecord(record);
        Check(Safety.Decide(record, machine, current, "apply", false) == MutationDecision.Write);
        Refuses(() => Safety.Decide(record, new string('b', 64), current, "apply", false), "machine-mismatch");
        Refuses(() => Safety.Decide(record, machine, current with { Identity = identity with { InstanceId = @"ROOT\DR_GENERIC_NODE\0001" } }, "apply", false), "target-mismatch");
        Refuses(() => Safety.Decide(record, machine, current with { Identity = identity with { ContainerId = Guid.NewGuid().ToString("D") } }, "apply", false), "target-mismatch");
        Refuses(() => Safety.Decide(record, machine, current with { FriendlyName = Format.Name("external") }, "apply", false), "current-value-changed");
        Refuses(() => Safety.Decide(record, machine, current with { FriendlyName = proposed }, "restore", false), "apply-evidence-missing");
        Check(Safety.Decide(record, machine, current with { FriendlyName = proposed }, "restore", true) == MutationDecision.Write);
        Check(original.ForWrite() is { Type: Format.Empty, Bytes: null });
        Check(Safety.Decide(record, machine, current, "restore", false) == MutationDecision.NoOp);
        count += PnpTarget.SelfCheck();
        count += ClassicChecks.SelfCheck();

        Refuses(() => Format.Name("test\u001b[1m"), "invalid-name");
        Refuses(() => Format.Name("bad\0name"), "invalid-name");
        Refuses(() => Targets.RequireWritable("ble-keyboard"), "invalid-target-kind");
        foreach (var kind in new[] { Targets.Pnp, Targets.AudioPlayback, Targets.AudioRecording, Targets.BluetoothClassic })
        {
            Targets.RequireWritable(kind);
            count++;
        }
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
            Original = proposed, OriginalResolvedName = record.ProposedName, Proposed = original, ProposedName = "" };
        Safety.ValidateRecord(restore);
        Check(Safety.Decide(restore, machine, current with { FriendlyName = proposed, SystemName = record.ProposedName }, "apply", false) == MutationDecision.Write);

        var container = identity.ContainerId;
        var endpointA = new NativeIdentity(@"SWD\MMDEVAPI\{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}",
            Targets.AudioEndpointClass, [@"MMDEVAPI\AudioEndpoint"], container, [], "endpoint-a", EndpointContainerId: container);
        var endpointB = endpointA with { EndpointId = "endpoint-b" };
        Refuses(() => Safety.MatchIdentity(endpointA, machine, machine, endpointB), "target-mismatch");

        var audioOriginal = Format.Name("Microphone", Format.RegistryString);
        var audioProposed = Format.Name("New microphone", Format.RegistryString);
        var audioSources = new AudioNameSources("Microphone", "USB Audio Interface", "PnP device baseline", "PnP friendly baseline", "PnP name baseline");
        var audioRecord = record with { OperationId = Guid.NewGuid().ToString("N"), TargetKind = Targets.AudioPlayback, Target = endpointA,
            Original = audioOriginal, OriginalResolvedName = "Microphone", ProposedName = "New microphone", Proposed = audioProposed,
            OriginalAudioSources = audioSources };
        Safety.ValidateRecord(audioRecord);
        var audio = new NativeSnapshot(endpointA, audioOriginal, "Microphone", "", audioSources);
        Safety.ValidateAudioBaseline(audioRecord, audio);
        var mixed = audio with
        {
            FriendlyName = audioProposed,
            SystemName = "Microphone",
            AudioNameSources = audioSources with { CoreAudioDescription = "New microphone" }
        };
        var partial = Safety.Classify(audioRecord, mixed);
        Check(partial.SynchronizedState == "mixed");
        Check(partial.AudioSourceStates is { CoreAudioDescription: "proposed", PnpDeviceDescription: "unchanged",
            PnpFriendlyName: "unchanged", PnpName: "unchanged" });
        Check(!Safety.ObservationConfirms(Targets.AudioPlayback, "proposed", partial));
        Refuses(() => Safety.Decide(audioRecord, machine, mixed, "apply", false), "apply-evidence-missing");
        Check(Safety.Decide(audioRecord, machine, mixed, "apply", true) == MutationDecision.Write);
        var external = mixed with { AudioNameSources = mixed.AudioNameSources! with { PnpName = "External name" } };
        Refuses(() => Safety.Decide(audioRecord, machine, external, "restore", true), "audio-name-source-changed");

        var renamedRaw = Format.Name("Renamed microphone", Format.RegistryString);
        var emptyAudioName = new RawValue(true, Format.RegistryString, Format.Unicode.GetBytes("\0"));
        var renamedSources = new AudioNameSources("Renamed microphone", "USB Audio Interface", null, null, null);
        var blankAudioRestore = audioRecord with
        {
            OperationId = Guid.NewGuid().ToString("N"), Original = renamedRaw, OriginalResolvedName = "Renamed microphone",
            ProposedName = "", Proposed = emptyAudioName, Purpose = "restore", SourceOperationId = Guid.NewGuid().ToString("N"),
            OriginalAudioSources = renamedSources
        };
        Safety.ValidateRecord(blankAudioRestore);
        Check(Targets.ResolvedSuffix(blankAudioRestore) == "");
        var renamedAudio = new NativeSnapshot(endpointA, renamedRaw, "Renamed microphone", "", renamedSources);
        Safety.ValidateAudioBaseline(blankAudioRestore, renamedAudio);
        Check(Safety.Decide(blankAudioRestore, machine, renamedAudio, "apply", false) == MutationDecision.Write);

        Refuses(() => Safety.MatchIdentity(identity, machine, machine, identity with { HardwareIds = ["DR_OTHER_DEVICE"] }), "target-mismatch");

        var classicIdentity = new NativeIdentity(@"BTHENUM\DEV_AABBCCDDEEFF\7&LOCAL&0&BLUETOOTHDEVICE_AABBCCDDEEFF",
            Targets.BluetoothClass, [], null, [], RadioAddress: "112233445566");
        var factoryName = "Factory headset";
        var deviceName = new RawValue(true, Format.RegistryBinary, Encoding.UTF8.GetBytes(factoryName + "\0"));
        var classicSources = new BluetoothNameSources(factoryName, factoryName, deviceName);
        var classicRecord = record with
        {
            OperationId = Guid.NewGuid().ToString("N"), TargetKind = Targets.BluetoothClassic, Target = classicIdentity,
            Original = RawValue.Missing(), OriginalResolvedName = factoryName, ProposedName = "New headset",
            Proposed = ClassicTarget.EncodeAlias("New headset"), OriginalBluetoothSources = classicSources
        };
        Safety.ValidateRecord(classicRecord);
        var classicOriginal = new NativeSnapshot(classicIdentity, classicRecord.Original, factoryName,
            BluetoothNameSources: classicSources);
        Safety.ValidateClassicBaseline(classicRecord, classicOriginal);
        var classicMixed = classicOriginal with
        {
            FriendlyName = classicRecord.Proposed,
            SystemName = classicRecord.ProposedName,
            BluetoothNameSources = classicSources with { RuntimeName = classicRecord.ProposedName, PnpName = factoryName }
        };
        var classicPartial = Safety.Classify(classicRecord, classicMixed);
        Check(classicPartial.SynchronizedState == "mixed");
        Check(!Safety.ObservationConfirms(Targets.BluetoothClassic, "proposed", classicPartial));
        Refuses(() => Safety.Decide(classicRecord, machine, classicMixed, "apply", false), "apply-evidence-missing");
        Check(Safety.Decide(classicRecord, machine, classicMixed, "apply", true) == MutationDecision.Write);
        Check(Safety.Decide(classicRecord, machine, classicMixed, "restore", true) == MutationDecision.Write);
        Refuses(() => Safety.Decide(classicRecord, machine,
            classicMixed with { BluetoothNameSources = classicMixed.BluetoothNameSources! with { PnpName = "External name" } },
            "restore", true), "classic-name-source-changed");

        // Exercise the same record construction and exact snapshot check as PreviewRestore -> Prepare.
        Check(!Safety.KnownRestoreState(classicRecord, classicMixed, false, false));
        Check(Safety.KnownRestoreState(classicRecord, classicMixed, true, false));
        var mixedRestore = CreateRestorePreview(classicRecord, classicMixed, machine).Records.Single();
        ValidatePreparedRecord(mixedRestore, classicMixed, machine);
        Check(Safety.Decide(mixedRestore, machine, classicMixed, "apply", false) == MutationDecision.Write);
        Check(!Safety.ObservationConfirms(Targets.BluetoothClassic, "original", Safety.Classify(mixedRestore, classicMixed)));
        Refuses(() => ValidatePreparedRecord(mixedRestore,
            classicMixed with { BluetoothNameSources = classicMixed.BluetoothNameSources! with { PnpName = "External name" } }, machine),
            "classic-baseline-inconsistent");

        var emptyTransition = classicOriginal with { FriendlyName = new(true, Format.RegistryBinary, [0]) };
        var restoreIntent = new OperationEvent(Format.Version, classicRecord.OperationId, "classic-hash", DateTimeOffset.UtcNow,
            "restore", "intent", "write-intent", Before: classicRecord.Proposed);
        Check(Safety.IsClassicEmptyAliasIntent(restoreIntent, classicRecord, "classic-hash"));
        Check(!Safety.IsClassicEmptyAliasIntent(restoreIntent with { Command = "apply" }, classicRecord, "classic-hash"));
        Check(!Safety.IsClassicEmptyAliasIntent(restoreIntent with { RecordHash = "other" }, classicRecord, "classic-hash"));
        Check(!Safety.IsClassicEmptyAliasIntent(restoreIntent with { Before = ClassicTarget.EncodeAlias("External name") }, classicRecord, "classic-hash"));
        Refuses(() => Safety.Decide(classicRecord, machine, emptyTransition, "restore", true), "classic-name-source-changed");
        Check(Safety.Decide(classicRecord, machine, emptyTransition, "restore", true, true) == MutationDecision.Write);
        Check(!Safety.KnownRestoreState(classicRecord, emptyTransition, true, false));
        Check(Safety.KnownRestoreState(classicRecord, emptyTransition, true, true));
        Check(!Safety.KnownRestoreState(classicRecord,
            emptyTransition with { BluetoothNameSources = classicSources with { PnpName = "External name" } }, true, true));
        var emptyRestore = CreateRestorePreview(classicRecord, emptyTransition, machine).Records.Single();
        ValidatePreparedRecord(emptyRestore, emptyTransition, machine);
        Check(!emptyRestore.Proposed.Exists && emptyRestore.Proposed.Bytes.Length == 0);
        Check(Safety.Decide(emptyRestore, machine, emptyTransition, "apply", false) == MutationDecision.Write);
        Check(Safety.IsClassicEmptyAliasIntent(restoreIntent with { OperationId = emptyRestore.OperationId, Command = "apply", Before = emptyRestore.Original },
            emptyRestore, "classic-hash"));
        Check(Safety.ObservationConfirms(Targets.BluetoothClassic, "proposed", Safety.Classify(emptyRestore, classicOriginal)));
        Check(!new NamingLocationResult("record", "location", "apply", "waiting-for-reconnect", true, true,
            "proposed", "proposed", "mixed", "name", "waiting-for-reconnect", null, null, null).Succeeded);
        Check(AudioSettings.Refresh(false) is { ClosedCount: 0, Launched: false, RefreshState: "confirmation-required" });
        Check(AudioSettings.IsExpectedImage(@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", @"C:\Windows"));
        Check(!AudioSettings.IsExpectedImage(@"C:\Temp\SystemSettings.exe", @"C:\Windows"));
        // These checks only parse tokens; they do not open or create the synthetic directory.
        var store = new RecordStore(Path.Combine(Path.GetTempPath(), "DeviceRadar-self-check-unused"), machine);
        Refuses(() => store.Resolve(@"..\outside.json"), "invalid-record-token");
        Refuses(() => store.Resolve(@"C:\outside.json"), "invalid-record-token");
        Refuses(() => store.Resolve(record.OperationId + ".json:stream"), "invalid-record-token");
        Check(Path.GetFileName(store.Resolve(record.OperationId + ".json")) == record.OperationId + ".json");
        return count;
    }
}
