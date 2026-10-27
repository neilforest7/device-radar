using System.Security;
using System.Security.Principal;
using System.Text.Json;
using DeviceRadar.Naming.Native;
using NativeSnapshot = DeviceRadar.Naming.Native.DeviceSnapshot;
using NativeIdentity = DeviceRadar.Naming.Native.DeviceIdentity;

namespace DeviceRadar;

public sealed record SoundSettingsResult(string RefreshState, int ClosedCount, bool Launched, string? Reason, int? NativeError);

public sealed partial class NamingService
{
    private readonly Lazy<RecordStore> store = new(() => new());
    private RecordStore Store => store.Value;

    public IReadOnlyList<NamingCapability> Capabilities(DeviceSnapshot device)
        => device.Locations.Select(location => Capability(device, location)).ToArray();

    private NamingCapability Capability(DeviceSnapshot device, NamingLocation location)
    {
        var settings = location.Kind switch
        {
            NamingLocationKind.AudioPlayback or NamingLocationKind.AudioRecording => "ms-settings:sound",
            NamingLocationKind.BluetoothClassic or NamingLocationKind.BluetoothLe => "ms-settings:bluetooth",
            _ => "ms-settings:devices"
        };
        NamingCapability Blocked(string reason) => new(location, false, reason, location.Name, settings,
            location.Kind == NamingLocationKind.Pnp, "This device and naming location have not passed the complete validated workflow.");
        if (!device.IsPresent) return Blocked("target-offline");
        if (!location.IsPresent) return Blocked(location.Kind is NamingLocationKind.AudioPlayback or NamingLocationKind.AudioRecording
            ? "audio-endpoint-inactive" : "target-offline");
        if (location.Kind == NamingLocationKind.BluetoothLe) return Blocked("ble-reversible-path-unverified");
        if (location.Kind == NamingLocationKind.BluetoothClassic) return Blocked("classic-settings-and-persistence-unverified");
        if (location.Kind == NamingLocationKind.AudioPlayback) return Blocked("audio-playback-unverified");
        var kind = Kind(location);
        if (kind is null) return Blocked("device-and-location-unverified");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Runtime.WindowsVersion.Build != 22631)
            return Blocked("validated-build-required");
        try
        {
            using var live = Targets.Open(kind);
            var snapshot = Select(live, location);
            MatchSelection(device, location, snapshot);
            Store.RequireValidation(kind, snapshot.Identity);
            if (kind == Targets.Mouse && !Runtime.IsAdministrator) return Blocked("administrator-required");
            return new(location, true, "validated-device-and-build", DisplayEditable(kind, snapshot), settings,
                kind == Targets.Mouse, Evidence(location.Kind));
        }
        catch (Exception error) { return Blocked(ErrorCode(error)); }
    }

    public NamingPreview Preview(DeviceSnapshot device, IReadOnlyList<NameEdit> edits)
    {
        RequireBuild();
        if (edits is null || edits.Count is < 1 or > 32 || edits.Select(edit => edit.LocationId).Distinct(StringComparer.Ordinal).Count() != edits.Count)
            throw new ProbeException("invalid-selection");
        var entries = new List<NamingPreviewEntry>();
        var records = new List<OperationRecord>();
        foreach (var edit in edits)
        {
            var location = device.Locations.SingleOrDefault(item => item.Id == edit.LocationId)
                ?? throw new ProbeException("location-not-found");
            var capability = Capability(device, location);
            if (!capability.CanWrite) throw new ProbeException(capability.Reason);
            var kind = Kind(location) ?? throw new ProbeException("device-and-location-unverified");
            var proposed = Targets.Proposed(kind, edit.NewName);
            using var live = Targets.Open(kind);
            var current = Select(live, location);
            MatchSelection(device, location, current);
            var record = NewRecord(kind, current, edit.NewName, proposed);
            Safety.ValidateRecord(record);
            Safety.ValidateAudioBaseline(record, current);
            entries.Add(Entry(record, location.Kind));
            records.Add(record);
        }
        return new(device.DeviceId, entries.ToArray(), records.ToArray());
    }

    public NamingPreview PreviewRestore(string recordToken)
    {
        RequireBuild();
        using var source = Store.Load(recordToken);
        using var live = Targets.Open(source.Record.TargetKind, source.Record.Target);
        var current = live.Unique();
        Safety.MatchIdentity(source.Record, Store.Machine, current.Identity);
        Store.RequireValidation(source.Record.TargetKind, current.Identity);
        // A selected older version becomes a fresh operation. Its before-value is the present
        // configuration, so cancellation and an intervening external rename are both safe.
        var desired = Clone(source.Record.Original);
        var desiredName = Format.Text(desired, Targets.PropertyType(source.Record.TargetKind)) ?? source.Record.OriginalResolvedName;
        var record = NewRecord(source.Record.TargetKind, current, desiredName, desired) with
        {
            Purpose = "restore", SourceOperationId = source.Record.OperationId
        };
        if (Targets.ResolvedSuffix(source.Record) != current.ResolvedNameSuffix) throw new ProbeException("audio-name-source-changed");
        Safety.ValidateRecord(record);
        Safety.ValidateAudioBaseline(record, current);
        var kind = record.TargetKind == Targets.Mouse ? NamingLocationKind.Pnp : NamingLocationKind.AudioRecording;
        return new(Format.TargetHash(current.Identity.InstanceId), [Entry(record, kind)], [record]);
    }

    public PreparedNaming Prepare(NamingPreview preview)
    {
        RequireBuild();
        if (preview is null || preview.Records.Length is < 1 or > 32) throw new ProbeException("invalid-preview");
        using var operationLock = Store.LockMutations();
        // Validate every selected location before persisting the immutable recovery points.
        foreach (var record in preview.Records)
        {
            Safety.ValidateRecord(record);
            using var live = Targets.Open(record.TargetKind, record.Target);
            var current = live.Unique();
            Safety.MatchIdentity(record, Store.Machine, current.Identity);
            Store.RequireValidation(record.TargetKind, current.Identity);
            if (!current.FriendlyName.Same(record.Original) || current.SystemName != record.OriginalResolvedName
                || current.ResolvedNameSuffix != Targets.ResolvedSuffix(record)) throw new ProbeException("preview-stale");
            Safety.ValidateAudioBaseline(record, current);
        }
        return new(preview.Records.Select(Store.SaveRecoveryPoint).ToArray());
    }

    public NamingResult Apply(PreparedNaming prepared)
    {
        if (prepared?.RecordTokens is not { Length: > 0 and <= 32 }) throw new ProbeException("invalid-selection");
        return Result(prepared.RecordTokens.Select(token => MutateSafely("apply", token)).ToArray());
    }

    public NamingResult Apply(string recordToken) => Result([MutateSafely("apply", recordToken)]);
    public NamingResult Restore(string recordToken) => Result([MutateSafely("restore", recordToken)]);
    public NamingResult Restore(NamingPreview preview)
    {
        if (preview.Records.Any(record => record.Purpose != "restore")) throw new ProbeException("restore-preview-required");
        return Apply(Prepare(preview));
    }

    public NamingResult Verify(string recordToken)
    {
        using var lease = Store.Load(recordToken);
        var observed = Observe(lease.Record);
        var confirmed = Safety.ObservationConfirms(lease.Record.TargetKind, "proposed", observed)
            || Safety.ObservationConfirms(lease.Record.TargetKind, "original", observed);
        return Result([LocationResult(lease, "verify", confirmed ? "verified" : "unverified", false, null, observed, null, null)]);
    }

    public IReadOnlyList<HistoryEntry> History(DeviceSnapshot device)
    {
        var targetIds = device.Locations.Select(location => PnpId(location.TargetId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Store.History().Where(item => item.Record is null || targetIds.Contains(item.Record.Target.InstanceId))
            .Select(item => item.Record is { } record
                ? new HistoryEntry(item.Token, record.CreatedAtUtc, LocationId(record), record.OriginalResolvedName,
                    record.ProposedName + Targets.ResolvedSuffix(record), item.Status,
                    HistoryReason(record, item.Reason), record.Purpose != "restore")
                : new HistoryEntry(item.Token, default, "", "", "", item.Status, item.Reason))
            .OrderByDescending(entry => entry.CreatedAtUtc).ToArray();
    }

    // Read the immutable history once; matching against the current inventory is done by the caller.
    // This path performs no native device observation and never changes a record.
    public IReadOnlyList<HistoryEntry> StoredHistory()
        => Store.History()
            .Select(item => item.Record is { } record
                ? new HistoryEntry(item.Token, record.CreatedAtUtc, LocationId(record),
                    record.OriginalResolvedName, record.ProposedName + Targets.ResolvedSuffix(record),
                    item.Status, item.Reason, record.Purpose != "restore")
                : new HistoryEntry(item.Token, default, "", "", "", item.Status, item.Reason))
            .ToArray();

    public SoundSettingsResult RefreshSoundSettings(bool confirmClose = true)
    {
        var result = AudioSettings.Refresh(confirmClose);
        return new(result.RefreshState, result.ClosedCount, result.Launched, result.Reason, result.NativeError);
    }

    // Explicit local evidence registration only. This never loads an old recovery point for mutation.
    public ValidationImportResult ImportValidationEvidence(string evidenceDirectory, string oldRecordToken)
    {
        RequireBuild();
        RequireAdministrator();
        var evidence = Store.ReadValidationEvidence(evidenceDirectory, oldRecordToken);
        using var live = Targets.Open(evidence.Kind, evidence.Target);
        var current = live.Unique();
        Safety.MatchIdentity(evidence.Target, Store.Machine, Store.Machine, current.Identity);
        if (!current.FriendlyName.Same(evidence.Original)) throw new ProbeException("validation-evidence-baseline-changed");
        var token = Store.SaveValidation(evidence, current.Identity);
        return new(token, evidence.Kind, Format.TargetHash(current.Identity.InstanceId));
    }

    private string? HistoryReason(OperationRecord record, string? previousReason)
    {
        var observed = Observe(record);
        return observed.State is "other" ? "external-value-changed"
            : observed.State is "offline" or "unavailable" ? observed.Reason : previousReason;
    }

    private OperationRecord NewRecord(string kind, NativeSnapshot current, string desiredName, RawValue desired)
        => new(Format.Version, Guid.NewGuid().ToString("N"), Store.Machine, DateTimeOffset.UtcNow, "prepared",
            Runtime.SystemDescription, kind, current.Identity, Clone(current.FriendlyName), current.SystemName,
            desiredName, Clone(desired));

    private NamingLocationResult MutateSafely(string command, string token)
    {
        try { return Mutate(command, token); }
        catch (Exception error)
        {
            return new(token, "", command, "blocked", false, null, "unavailable", null, null, null,
                ErrorCode(error), NativeError(error), null, null);
        }
    }

    private NamingLocationResult Mutate(string command, string token)
    {
        RequireBuild();
        using var operationLock = Store.LockMutations();
        using var lease = Store.Load(token);
        var attempted = false;
        bool? apiSucceeded = null;
        Exception? failure = null;
        var status = "blocked";
        EventLease? intent = null;
        TargetWriteLease? writer = null;
        try
        {
            if (lease.Record.TargetKind == Targets.Mouse) RequireAdministrator();
            var applyEvidence = Store.HasApplyEvidence(lease);
            using var initial = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
            var before = initial.Unique();
            Store.RequireValidation(lease.Record.TargetKind, before.Identity);
            var decision = Safety.Decide(lease.Record, Store.Machine, before, command, applyEvidence);
            if (decision == MutationDecision.NoOp) status = "already-at-value";
            else
            {
                TargetSession? final = null;
                try
                {
                    Safety.GuardedWrite(
                        () => intent = Store.HoldEvent(Event(lease, command, "intent", "write-intent", before: before.FriendlyName)),
                        () =>
                        {
                            final = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
                            Store.RequireValidation(lease.Record.TargetKind, final.Unique().Identity);
                            if (Safety.Decide(lease.Record, Store.Machine, final.Unique(), command, applyEvidence) != MutationDecision.Write)
                                throw new ProbeException("value-changed-before-write");
                            writer = final.OpenWriter(command == "apply" ? lease.Record.Proposed : lease.Record.Original);
                        },
                        () => { attempted = true; writer!.Write(); apiSucceeded = true; });
                }
                finally { final?.Dispose(); }
                status = "write-unconfirmed";
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (attempted) { apiSucceeded = false; status = "write-unconfirmed"; }
        }

        using var durableIntent = intent;
        using var durableWriter = writer;
        // A new native enumeration performs readback after either a successful or failed write.
        var wanted = command == "apply" ? "proposed" : "original";
        var observed = Observe(lease.Record);
        if (attempted && lease.Record.TargetKind == Targets.UsbMicrophone)
        {
            var propagation = System.Diagnostics.Stopwatch.StartNew();
            while (!Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed) && propagation.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(100);
                observed = Observe(lease.Record);
            }
        }
        if (status == "already-at-value" && !Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed)) status = "observation-unconfirmed";
        if (attempted && Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed))
            status = apiSucceeded == true ? command == "apply" ? "applied" : "restored" : "value-confirmed-api-error";
        if (attempted && observed.State == wanted && lease.Record.TargetKind == Targets.UsbMicrophone
            && !Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed)) status = "raw-confirmed-sources-stale";
        var entry = Event(lease, command, "completed", status, attempted, apiSucceeded, observed: observed,
            reason: failure is null ? null : ErrorCode(failure), exception: failure, notificationCount: writer?.NotificationCount);
        string? eventToken = null;
        try { eventToken = Store.AppendEvent(entry); }
        catch (Exception error) { status = "result-log-failed"; failure = error; }
        return LocationResult(lease, command, status, attempted, apiSucceeded, observed, failure, eventToken, writer?.NotificationCount);
    }

    private Observation Observe(OperationRecord record)
    {
        try
        {
            using var live = Targets.Open(record.TargetKind, record.Target);
            if (live.Candidates.Count == 0) return new("offline", "target-offline");
            if (live.Candidates.Count != 1) return new("unavailable", "target-ambiguous");
            var current = live.Unique();
            Safety.MatchIdentity(record, Store.Machine, current.Identity);
            return Safety.Classify(record, current);
        }
        catch (Exception error) { return new("unavailable", ErrorCode(error), NativeError: NativeError(error)); }
    }

    private static OperationEvent Event(RecordLease lease, string command, string stage, string status,
        bool attempted = false, bool? apiSucceeded = null, RawValue? before = null, Observation? observed = null,
        string? reason = null, Exception? exception = null, int? notificationCount = null)
        => new(Format.Version, lease.Record.OperationId, lease.Hash, DateTimeOffset.UtcNow, command, stage, status,
            attempted, apiSucceeded, exception is null ? null : NativeError(exception), before,
            observed?.State, observed?.Snapshot?.FriendlyName, reason ?? observed?.Reason, observed?.ResolvedState,
            observed?.Snapshot?.SystemName, exception?.GetType().FullName, exception?.HResult,
            observed?.SynchronizedState, observed?.Snapshot?.AudioNameSources, observed?.AudioSourceStates, notificationCount);

    private static NamingLocationResult LocationResult(RecordLease lease, string command, string status, bool attempted,
        bool? apiSucceeded, Observation observation, Exception? failure, string? eventToken, int? notifications = null)
        => new(lease.Token, LocationId(lease.Record), command, status, attempted, apiSucceeded, observation.State,
            observation.ResolvedState, observation.SynchronizedState, observation.Snapshot?.SystemName,
            failure is null ? observation.Reason : ErrorCode(failure), failure is null ? observation.NativeError : NativeError(failure),
            notifications, eventToken);

    private static NamingResult Result(NamingLocationResult[] locations)
        => new(locations.All(item => item.Succeeded) ? "complete" : locations.Any(item => item.Succeeded) ? "partial" : "incomplete", locations);
    private static NamingPreviewEntry Entry(OperationRecord record, NamingLocationKind kind)
        => new(LocationId(record), kind, record.OriginalResolvedName, record.ProposedName,
            record.ProposedName + Targets.ResolvedSuffix(record), record.TargetKind == Targets.Mouse
                ? "SetupDiSetDeviceProperty/FriendlyName" : "IPolicyConfig.SetPropertyValue/DeviceDesc/build22631", Evidence(kind));
    private static string LocationId(OperationRecord record)
        => record.TargetKind == Targets.Mouse ? "pnp:" + record.Target.InstanceId : "audio:" + record.Target.InstanceId[@"SWD\MMDEVAPI\".Length..];
    private static string DisplayEditable(string kind, NativeSnapshot current)
        => kind == Targets.UsbMicrophone ? Format.Text(current.FriendlyName, Format.RegistryString)! : current.SystemName;
    private static RawValue Clone(RawValue value) => new(value.Exists, value.Type, value.Bytes.ToArray());
    private static string PnpId(string id) => id.StartsWith("{0.0.", StringComparison.Ordinal) ? @"SWD\MMDEVAPI\" + id : id;
    private static string? Kind(NamingLocation location) => location.Kind switch
    {
        NamingLocationKind.Pnp when Format.IsCandidateId(location.TargetId) => Targets.Mouse,
        NamingLocationKind.AudioRecording => Targets.UsbMicrophone,
        _ => null
    };
    private static NativeSnapshot Select(TargetSession live, NamingLocation location)
    {
        var matches = live.Candidates.Where(item => string.Equals(item.Identity.InstanceId, PnpId(location.TargetId), StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ProbeException(matches.Length == 0 ? "validated-target-unavailable" : "target-ambiguous");
    }
    private static void MatchSelection(DeviceSnapshot device, NamingLocation location, NativeSnapshot current)
    {
        if (!device.IsPresent || !location.IsPresent) throw new ProbeException("target-offline");
        if (!string.Equals(PnpId(location.TargetId), current.Identity.InstanceId, StringComparison.OrdinalIgnoreCase)
            || (location.Kind == NamingLocationKind.Pnp && !device.Functions.Any(function => function.IsPresent && string.Equals(function.InstanceId, current.Identity.InstanceId, StringComparison.OrdinalIgnoreCase)))
            || !string.Equals(device.ContainerId, current.Identity.ContainerId, StringComparison.OrdinalIgnoreCase)) throw new ProbeException("selection-identity-changed");
    }
    private static string Evidence(NamingLocationKind kind) => kind switch
    {
        NamingLocationKind.Pnp => "Logitech VID046D PIDC547 Mouse; build22631; exit/reconnect/reboot/restore verified; other devices unverified",
        NamingLocationKind.AudioRecording => "AB13X capture VID001F PID0B21; build22631; internal policy/readback/UI/restore verified; new-backend reconnect/reboot unverified",
        NamingLocationKind.AudioPlayback => "Core Audio basic properties are documented read-only; playback write/UI/persistence unverified",
        NamingLocationKind.BluetoothClassic => "BluetoothUpdateDeviceRecord is a public candidate; modern Settings/reconnect/restore unverified",
        _ => "BLE runtime/persistent/PnP synchronization and complete reversible flow unverified"
    };
    private static void RequireBuild()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Runtime.WindowsVersion.Build != 22631)
            throw new ProbeException("validated-build-required");
    }
    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new ProbeException("administrator-required");
    }
    public static string ErrorCode(Exception error) => error switch
    {
        ProbeException probe => probe.Code,
        UnauthorizedAccessException => "file-access-denied",
        SecurityException => "security-check-failed",
        IOException => "file-io-error",
        JsonException => "invalid-record-json",
        ArgumentException => "invalid-argument",
        _ => "unexpected-error"
    };
    internal static int? NativeError(Exception error) => error is ProbeException probe ? probe.NativeError
        : error.HResult == unchecked((int)0x80070005) ? 5 : null;
}
