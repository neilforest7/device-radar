using System.Runtime.InteropServices;

namespace DeviceRadar.NativeProbe;

internal static class SelfCheck
{
    public static int Run()
    {
        var count = 0;
        var identity = new DeviceIdentity(@"HID\VID_046D&PID_C547&TEST\LOCAL", Format.MouseClass,
            [@"HID\VID_046D&PID_C547"], "79eae121-7817-4acc-8938-199bfca0f733", []);
        var missing = RawValue.Missing();
        var proposed = Format.Name("Device Radar 测试鼠标");
        var record = new OperationRecord(Format.Version, Guid.NewGuid().ToString("N"), new string('a', 64),
            DateTimeOffset.UtcNow, "prepared", "self-check", Targets.Mouse, identity, missing, "HID-compliant mouse", "Device Radar 测试鼠标", proposed);
        var current = new DeviceSnapshot(identity, missing, "HID-compliant mouse");
        void Check(bool condition)
        {
            if (!condition) throw new ProbeException("self-check-failed");
            count++;
        }
        void Refuses(Action action, string code)
        {
            try { action(); }
            catch (ProbeException error) when (error.Code == code) { count++; return; }
            throw new ProbeException("self-check-failed");
        }

        Safety.ValidateRecord(record);
        Check(Marshal.SizeOf<Native.DeviceInfoData>() == (Environment.Is64BitProcess ? 32 : 28));
        Check(Marshal.SizeOf<Native.PropertyKey>() == 20);
        Check(Safety.Decide(record, record.MachineFingerprint, current, "apply", false) == MutationDecision.Write);
        Refuses(() => Safety.Decide(record, new string('b', 64), current, "apply", false), "machine-mismatch");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint,
            current with { Identity = identity with { InstanceId = identity.InstanceId + "X" } }, "apply", false), "target-mismatch");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint,
            current with { Identity = identity with { ContainerId = Guid.NewGuid().ToString("D") } }, "apply", false), "target-mismatch");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint,
            current with { FriendlyName = Format.Name("external name") }, "apply", false), "current-value-changed");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint,
            current with { FriendlyName = proposed }, "restore", false), "apply-evidence-missing");
        Refuses(() => Safety.Decide(record, record.MachineFingerprint,
            current with { FriendlyName = Format.Name("external name") }, "restore", true), "current-value-changed");
        Check(Safety.Decide(record, record.MachineFingerprint, current with { FriendlyName = proposed }, "restore", true) == MutationDecision.Write);
        var deletion = record.Original.ForWrite();
        Check(!record.Original.Exists && deletion.Type == Format.Empty && deletion.Bytes is null && (deletion.Bytes?.Length ?? 0) == 0);
        Check(Safety.Decide(record, record.MachineFingerprint, current, "restore", false) == MutationDecision.NoOp);
        Check(Safety.Decide(record, record.MachineFingerprint, current with { FriendlyName = proposed }, "apply", true) == MutationDecision.NoOp);
        Refuses(() => Format.Name("invalid\0name"), "invalid-name");
        Refuses(() => Format.Name("invalid\nname"), "invalid-name");
        Refuses(() => Safety.ValidateRecord(record with { Target = identity with { ClassGuid = Guid.Empty } }), "invalid-record-target");
        var wrote = false;
        Refuses(() => Safety.GuardedWrite(() => throw new ProbeException("backup-failed"), () => { }, () => wrote = true), "backup-failed");
        Check(!wrote);
        var persisted = false;
        Safety.GuardedWrite(() => persisted = true, () => Check(persisted), () => wrote = true);
        Check(wrote);
        var stages = new List<string>();
        Safety.GuardedWrite(() => stages.Add("intent"), () => stages.Add("fresh-check-and-open"), () => stages.Add("write"));
        Check(stages.SequenceEqual(new[] { "intent", "fresh-check-and-open", "write" }));
        wrote = false;
        Refuses(() => Safety.GuardedWrite(() => { }, () => throw new ProbeException("fresh-check-failed"), () => wrote = true), "fresh-check-failed");
        Check(!wrote);
        // A crash after Set but before the result is saved still leaves the flushed intent and exact proposed bytes.
        const string recordHash = "local-self-check-record-hash";
        var intent = new OperationEvent(Format.Version, record.OperationId, recordHash, DateTimeOffset.UtcNow,
            "apply", "intent", "write-intent", Before: missing);
        Check(Safety.Decide(record, record.MachineFingerprint, current with { FriendlyName = proposed }, "restore",
            Safety.IsApplyEvidence(intent, record, recordHash)) == MutationDecision.Write);
        Check(!Safety.IsApplyEvidence(intent with { RecordHash = "changed" }, record, recordHash));
        var bleIdentity = new DeviceIdentity(@"BTHLE\DEV_AABBCCDDEEFF\LOCAL", Targets.BluetoothClass,
            [@"BTHLE\DEV_AABBCCDDEEFF"], identity.ContainerId, []);
        var bleOriginal = new RawValue(true, Format.RegistryBinary, [0]);
        var bleRecord = record with { TargetKind = Targets.BleKeyboard, Target = bleIdentity, Original = bleOriginal,
            OriginalResolvedName = "VGN N75 PRO-1", ProposedName = "Device Radar 测试键盘", Proposed = Targets.Proposed(Targets.BleKeyboard, "Device Radar 测试键盘") };
        Safety.ValidateRecord(bleRecord);
        Refuses(() => Safety.ValidateRecord(bleRecord with { Target = bleIdentity with { HardwareIds = [@"BTHLE\DEV_AABBCCDDEE00"] } }), "invalid-record-target");
        Check(bleRecord.Proposed.Type == Format.RegistryBinary
            && bleRecord.Proposed.Bytes.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes("Device Radar 测试键盘\0")));
        Check(bleOriginal.ForWrite() is { Type: Format.RegistryBinary, Bytes: [0] });
        Refuses(() => Safety.ValidateRecord(bleRecord with { Original = RawValue.Missing() }), "required-alias-value-missing");
        var audioIdentity = new DeviceIdentity(@"SWD\MMDEVAPI\{0.0.1.00000000}.{79eae121-7817-4acc-8938-199bfca0f733}", Targets.AudioEndpointClass,
            [@"MMDEVAPI\AudioEndpoints"], identity.ContainerId,
            [new(@"USB\VID_001F&PID_0B21&MI_00\LOCAL", Targets.MediaClass, [@"USB\VID_001F&PID_0B21&MI_00"], identity.ContainerId),
             new(@"USB\VID_001F&PID_0B21\LOCAL", Targets.UsbClass, [@"USB\VID_001F&PID_0B21"], identity.ContainerId)]);
        var audioRecord = record with { TargetKind = Targets.UsbMicrophone, Target = audioIdentity,
            Original = Format.Name("Microphone", Format.RegistryString), OriginalResolvedName = "Microphone (AB13X USB Audio)",
            ProposedName = "Device Radar 测试麦克风", Proposed = Targets.Proposed(Targets.UsbMicrophone, "Device Radar 测试麦克风") };
        Safety.ValidateRecord(audioRecord);
        Check(Targets.ResolvedSuffix(audioRecord) == " (AB13X USB Audio)" && audioRecord.Proposed.Type == Format.RegistryString);
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint,
            new(audioIdentity with { Ancestors = [audioIdentity.Ancestors[0] with { InstanceId = audioIdentity.Ancestors[0].InstanceId + "X" }, audioIdentity.Ancestors[1]] }, audioRecord.Original, audioRecord.OriginalResolvedName),
            "apply", false), "target-mismatch");
        var absentAudio = audioRecord with { Original = RawValue.Missing() };
        Refuses(() => Safety.ValidateRecord(absentAudio), "required-audio-description-missing");
        Refuses(() => Safety.ValidateRecord(audioRecord with { FormatVersion = 2 }), "invalid-record");
        var audioOriginal = new DeviceSnapshot(audioIdentity, audioRecord.Original, audioRecord.OriginalResolvedName,
            " (AB13X USB Audio)", new("Microphone", "AB13X USB Audio", audioRecord.OriginalResolvedName,
                audioRecord.OriginalResolvedName, audioRecord.OriginalResolvedName));
        var proposedFull = audioRecord.ProposedName + audioOriginal.ResolvedNameSuffix;
        var audioProposed = audioOriginal with { FriendlyName = audioRecord.Proposed, SystemName = proposedFull,
            AudioNameSources = new(audioRecord.ProposedName, "AB13X USB Audio", proposedFull, proposedFull, proposedFull) };
        Safety.ValidateAudioBaseline(audioRecord, audioOriginal);
        Check(Safety.Decide(audioRecord, audioRecord.MachineFingerprint, audioOriginal, "apply", false) == MutationDecision.Write
            && Safety.Decide(audioRecord, audioRecord.MachineFingerprint, audioOriginal, "restore", false) == MutationDecision.NoOp
            && Safety.Decide(audioRecord, audioRecord.MachineFingerprint, audioProposed, "apply", true) == MutationDecision.NoOp);
        var stalePnp = audioProposed with { AudioNameSources = audioProposed.AudioNameSources! with { PnpDeviceDescription = audioRecord.OriginalResolvedName } };
        var staleObservation = Safety.Classify(audioRecord, stalePnp);
        Check(staleObservation.State == "proposed" && staleObservation.ResolvedState == "proposed"
            && staleObservation.SynchronizedState == "mixed" && staleObservation.AudioSourceStates?.PnpDeviceDescription == "original"
            && !Safety.ObservationConfirms(Targets.UsbMicrophone, "proposed", staleObservation));
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint, stalePnp, "apply", false), "current-value-changed");
        Check(Safety.Decide(audioRecord, audioRecord.MachineFingerprint, stalePnp, "apply", true) == MutationDecision.Write);
        var staleOriginal = audioOriginal with { AudioNameSources = audioOriginal.AudioNameSources! with { PnpName = proposedFull } };
        Refuses(() => Safety.ValidateAudioBaseline(audioRecord, staleOriginal), "audio-baseline-inconsistent");
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint, staleOriginal, "restore", false), "apply-evidence-missing");
        Check(Safety.Decide(audioRecord, audioRecord.MachineFingerprint, staleOriginal, "restore", true) == MutationDecision.Write);
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint,
            audioProposed with { AudioNameSources = audioProposed.AudioNameSources! with { PnpFriendlyName = "external name" } }, "restore", true), "audio-name-source-changed");
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint,
            audioProposed with { AudioNameSources = audioProposed.AudioNameSources! with { CoreAudioInterfaceName = "another interface" } }, "apply", true), "audio-name-source-changed");
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint,
            audioProposed with { AudioNameSources = audioProposed.AudioNameSources! with { PnpName = null } }, "apply", true), "audio-name-source-changed");
        Refuses(() => Safety.Decide(audioRecord, audioRecord.MachineFingerprint,
            audioOriginal with { FriendlyName = RawValue.Missing() }, "apply", false), "required-audio-description-missing");
        Check(!Safety.ObservationConfirms(Targets.Mouse, "original", new("offline"))
            && !Safety.ObservationConfirms(Targets.UsbMicrophone, "proposed", new("proposed", ResolvedState: "original"))
            && !Safety.ObservationConfirms(Targets.UsbMicrophone, "proposed", new("proposed", ResolvedState: "proposed"))
            && Safety.ObservationConfirms(Targets.UsbMicrophone, "proposed", Safety.Classify(audioRecord, audioProposed)));
        var blePnpRecord = bleRecord with { TargetKind = Targets.BleKeyboardPnp, Original = Format.Name("VGN N75 PRO-1"),
            Proposed = Targets.Proposed(Targets.BleKeyboardPnp, bleRecord.ProposedName) };
        Safety.ValidateRecord(blePnpRecord);
        Check(blePnpRecord.Proposed.Type == Format.String && blePnpRecord.Proposed.Same(Format.Name(bleRecord.ProposedName)));
        Check(Program.NativeError(new System.Security.SecurityException()) is null
            && Program.NativeError(new UnauthorizedAccessException()) == 5);
        var bleFamilyName = new RawValue(true, Format.RegistryBinary, System.Text.Encoding.UTF8.GetBytes("VGN N75 PRO-1\0"));
        Check(BleTarget.MatchesKnownFamily(bleFamilyName, bleFamilyName)
            && !BleTarget.MatchesKnownFamily(bleFamilyName, Targets.Proposed(Targets.BleKeyboard, "another keyboard")));
        BleTarget.ValidateLeNameSourceGuards(bleFamilyName, bleOriginal);
        var leNameRecord = bleRecord with { TargetKind = Targets.BleLeNameCache, Original = bleFamilyName,
            Proposed = Targets.Proposed(Targets.BleLeNameCache, bleRecord.ProposedName) };
        Safety.ValidateRecord(leNameRecord);
        Check(BleTarget.IsSelectedHidHardwareId(@"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02320F_PID&5055_REV&0104")
            && !BleTarget.IsSelectedHidHardwareId(@"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02320f_PID&5055_REV&0105")
            && Safety.Decide(leNameRecord, leNameRecord.MachineFingerprint, new(bleIdentity, leNameRecord.Proposed, "VGN N75 PRO-1"), "restore", true) == MutationDecision.Write);
        Refuses(() => BleTarget.ValidateLeNameSourceGuards(leNameRecord.Proposed, bleOriginal), "ble-cache-coupling-name-changed");
        Refuses(() => BleTarget.ValidateLeNameSourceGuards(bleFamilyName, leNameRecord.Proposed), "ble-cache-coupling-friendly-name-changed");
        Refuses(() => Safety.ValidateRecord(leNameRecord with { Original = new(true, Format.RegistryBinary, [0xff, 0]) }), "invalid-ble-le-name-property");
        Refuses(() => BleTarget.ValidateLeName(new(true, Format.RegistryBinary, [0x56])), "invalid-ble-le-name-property");
        Refuses(() => BleTarget.ValidateLeName(new(true, Format.RegistryBinary, [0x56, 0, 0])), "invalid-ble-le-name-property");
        var noConfirmation = AudioSettings.Refresh(false);
        Check(noConfirmation is { RefreshState: "confirmation-required", ClosedCount: 0, Launched: false });
        Check(AudioSettings.IsExpectedImage(@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", @"C:\Windows")
            && !AudioSettings.IsExpectedImage(@"C:\Other\SystemSettings.exe", @"C:\Windows")
            && !AudioSettings.IsExpectedImage(@"C:\Windows\System32\ApplicationFrameHost.exe", @"C:\Windows")
            && !AudioSettings.IsExpectedImage("SystemSettings.exe", @"C:\Windows"));
        Check(AudioSettings.UniqueRoot([new IntPtr(1), new IntPtr(1)]) == new IntPtr(1));
        Refuses(() => AudioSettings.UniqueRoot([]), "settings-window-not-found");
        Refuses(() => AudioSettings.UniqueRoot([IntPtr.Zero]), "settings-window-not-found");
        Refuses(() => AudioSettings.UniqueRoot([new IntPtr(1), new IntPtr(2)]), "settings-window-ambiguous");
        Program.Output(new { command = "self-check", status = "passed", checks = count, systemWrites = 0, filesWritten = 0 });
        return 0;
    }
}
