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
            location.Kind is NamingLocationKind.Pnp or NamingLocationKind.BluetoothClassic, Evidence(location.Kind));
        if (!device.IsPresent) return Blocked("target-offline");
        if (!location.IsPresent && location.Kind is not (NamingLocationKind.AudioPlayback or NamingLocationKind.AudioRecording))
            return Blocked("target-offline");
        if (location.Kind == NamingLocationKind.BluetoothLe) return Blocked("ble-next-version");
        var kind = Kind(location);
        if (kind is null) return Blocked("naming-location-unsupported");
        if (!Runtime.IsSupportedWindows) return Blocked("platform-unsupported");
        try
        {
            using var live = Targets.Open(kind, targetId: location.TargetId);
            var snapshot = Select(live, location);
            MatchSelection(device, location, snapshot);
            var requiresAdmin = Targets.RequiresAdministrator(kind);
            var canWrite = !requiresAdmin || Runtime.IsAdministrator;
            return new(location, canWrite, canWrite ? "ready" : "administrator-required", DisplayEditable(kind, snapshot), settings,
                requiresAdmin, Evidence(location.Kind)) { IsSupported = true };
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
            var kind = Kind(location) ?? throw new ProbeException("naming-location-unsupported");
            var proposed = Targets.Proposed(kind, edit.NewName);
            using var live = Targets.Open(kind, targetId: location.TargetId);
            var current = Select(live, location);
            MatchSelection(device, location, current);
            var record = NewRecord(kind, current, edit.NewName, proposed, Store.Machine);
            Safety.ValidateRecord(record);
            Safety.ValidateAudioBaseline(record, current);
            Safety.ValidateClassicBaseline(record, current);
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
        RequireKnownRestoreState(source, current);
        return CreateRestorePreview(source.Record, current, Store.Machine);
    }

    private static NamingPreview CreateRestorePreview(OperationRecord source, NativeSnapshot current, string machine)
    {
        // A selected older version becomes a fresh operation. Its before-value is the present
        // configuration, so cancellation and an intervening external rename are both safe.
        var desired = Clone(source.Original);
        var desiredName = Targets.ValueText(source.TargetKind, desired)
            ?? (source.TargetKind == Targets.BluetoothClassic
                ? ClassicTarget.DeviceName(source.OriginalBluetoothSources!.DeviceName) : source.OriginalResolvedName);
        var record = NewRecord(source.TargetKind, current, desiredName, desired, machine) with
        {
            Purpose = "restore", SourceOperationId = source.OperationId
        };
        if (Targets.ResolvedSuffix(source) != current.ResolvedNameSuffix) throw new ProbeException("audio-name-source-changed");
        Safety.ValidateRecord(record);
        Safety.ValidateAudioBaseline(record, current);
        Safety.ValidateClassicBaseline(record, current);
        var kind = LocationKind(record.TargetKind);
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
            ValidatePreparedRecord(record, current, Store.Machine);
            RequireRestoreProvenance(record, current);
        }
        return new(preview.Records.Select(Store.SaveRecoveryPoint).ToArray());
    }

    private static void ValidatePreparedRecord(OperationRecord record, NativeSnapshot current, string machine)
    {
        Safety.ValidateRecord(record);
        Safety.MatchIdentity(record, machine, current.Identity);
        if (!current.FriendlyName.Same(record.Original) || current.SystemName != record.OriginalResolvedName
            || current.ResolvedNameSuffix != Targets.ResolvedSuffix(record)) throw new ProbeException("preview-stale");
        Safety.ValidateAudioBaseline(record, current);
        Safety.ValidateClassicBaseline(record, current);
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
        var observed = Observe(lease.Record, Store.HasClassicEmptyAliasIntent(lease));
        var confirmed = Safety.ObservationConfirms(lease.Record.TargetKind, "proposed", observed)
            || Safety.ObservationConfirms(lease.Record.TargetKind, "original", observed);
        return Result([LocationResult(lease, "verify", confirmed ? "verified" : "unverified", false, null, observed, null, null)]);
    }

    public IReadOnlyList<HistoryEntry> History(DeviceSnapshot device)
    {
        var locationIds = device.Locations.Select(location => location.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Store.History().Where(item => item.Record is null || locationIds.Contains(LocationId(item.Record)))
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

    private string? HistoryReason(OperationRecord record, string? previousReason)
    {
        using var lease = Store.Load(record.OperationId + ".json");
        var observed = Observe(record, Store.HasClassicEmptyAliasIntent(lease));
        var unknown = Targets.IsAudio(record.TargetKind) || record.TargetKind == Targets.BluetoothClassic
            ? observed.SynchronizedState == "other" : observed.State == "other";
        return unknown ? "external-value-changed"
            : observed.State is "offline" or "unavailable" ? observed.Reason : previousReason;
    }

    private static OperationRecord NewRecord(string kind, NativeSnapshot current, string desiredName, RawValue desired, string machine)
        => new(Format.Version, Guid.NewGuid().ToString("N"), machine, DateTimeOffset.UtcNow, "prepared",
            Runtime.SystemDescription, kind, current.Identity, Clone(current.FriendlyName), current.SystemName,
            desiredName, Clone(desired), OriginalAudioSources: current.AudioNameSources,
            OriginalBluetoothSources: current.BluetoothNameSources);

    private void RequireKnownRestoreState(RecordLease source, NativeSnapshot current)
    {
        // An older version may be selected after several successful local renames. Only
        // recognize observed states of this exact target; a new external value is a conflict.
        if (Safety.KnownRestoreState(source.Record, current, Store.HasApplyEvidence(source), Store.HasClassicEmptyAliasIntent(source))) return;
        foreach (var item in Store.History().Where(item => item.Record is { } record
            && record.TargetKind == source.Record.TargetKind && LocationId(record) == LocationId(source.Record))
            .OrderByDescending(item => item.Record!.CreatedAtUtc))
        {
            using var candidate = Store.Load(item.Token);
            try { Safety.MatchIdentity(candidate.Record, Store.Machine, current.Identity); }
            catch (ProbeException error) when (error.Code == "target-mismatch") { continue; }
            if (Safety.KnownRestoreState(candidate.Record, current, Store.HasApplyEvidence(candidate),
                Store.HasClassicEmptyAliasIntent(candidate))) return;
        }
        throw new ProbeException("external-value-changed");
    }

    private void RequireRestoreProvenance(OperationRecord record, NativeSnapshot current)
    {
        if (record.TargetKind != Targets.BluetoothClassic || record.Purpose != "restore") return;
        using var source = Store.Load(record.SourceOperationId + ".json");
        Safety.MatchIdentity(source.Record, Store.Machine, current.Identity);
        RequireKnownRestoreState(source, current);
    }

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
        var emptyAliasIntent = false;
        var newEmptyAliasIntent = false;
        try
        {
            if (Targets.RequiresAdministrator(lease.Record.TargetKind)) RequireAdministrator();
            var applyEvidence = Store.HasApplyEvidence(lease);
            emptyAliasIntent = Store.HasClassicEmptyAliasIntent(lease);
            using var initial = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
            var before = initial.Unique();
            RequireRestoreProvenance(lease.Record, before);
            var decision = Safety.Decide(lease.Record, Store.Machine, before, command, applyEvidence, emptyAliasIntent);
            if (decision == MutationDecision.NoOp) status = "already-at-value";
            else
            {
                TargetSession? final = null;
                try
                {
                    Safety.GuardedWrite(
                        () =>
                        {
                            var intentEntry = Event(lease, command, "intent", "write-intent", before: before.FriendlyName);
                            intent = Store.HoldEvent(intentEntry);
                            newEmptyAliasIntent = Safety.IsClassicEmptyAliasIntent(intentEntry, lease.Record, lease.Hash);
                        },
                        () =>
                        {
                            final = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
                            // Keep authorization bound to the evidence that existed before this attempt.
                            if (Safety.Decide(lease.Record, Store.Machine, final.Unique(), command, applyEvidence, emptyAliasIntent) != MutationDecision.Write)
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
        var observed = Observe(lease.Record, emptyAliasIntent || newEmptyAliasIntent);
        if (attempted && lease.Record.TargetKind != Targets.Pnp)
        {
            var propagation = System.Diagnostics.Stopwatch.StartNew();
            while (!Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed) && propagation.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(100);
                observed = Observe(lease.Record, emptyAliasIntent || newEmptyAliasIntent);
            }
        }
        if (status == "already-at-value" && !Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed)) status = "observation-unconfirmed";
        if (attempted && Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed))
            status = apiSucceeded == true ? command == "apply" ? "applied" : "restored" : "value-confirmed-api-error";
        if (attempted && observed.State == wanted && lease.Record.TargetKind != Targets.Pnp
            && !Safety.ObservationConfirms(lease.Record.TargetKind, wanted, observed)) status = "raw-confirmed-sources-stale";
        if (status == "raw-confirmed-sources-stale" && lease.Record.TargetKind == Targets.BluetoothClassic
            && observed.SynchronizedState == "mixed") status = "waiting-for-reconnect";
        var entry = Event(lease, command, "completed", status, attempted, apiSucceeded, observed: observed,
            reason: status == "waiting-for-reconnect" ? status : failure is null ? null : ErrorCode(failure), exception: failure, notificationCount: writer?.NotificationCount);
        string? eventToken = null;
        try { eventToken = Store.AppendEvent(entry); }
        catch (Exception error) { status = "result-log-failed"; failure = error; }
        return LocationResult(lease, command, status, attempted, apiSucceeded, observed, failure, eventToken, writer?.NotificationCount);
    }

    private Observation Observe(OperationRecord record, bool emptyAliasIntent = false)
    {
        try
        {
            using var live = Targets.Open(record.TargetKind, record.Target);
            if (live.Candidates.Count == 0) return new("offline", "target-offline");
            if (live.Candidates.Count != 1) return new("unavailable", "target-ambiguous");
            var current = live.Unique();
            Safety.MatchIdentity(record, Store.Machine, current.Identity);
            return Safety.Classify(record, current, emptyAliasIntent);
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
            status == "waiting-for-reconnect" ? status : failure is null ? observation.Reason : ErrorCode(failure),
            failure is null ? observation.NativeError : NativeError(failure),
            notifications, eventToken);

    private static NamingResult Result(NamingLocationResult[] locations)
        => new(locations.All(item => item.Succeeded) ? "complete" : locations.Any(item => item.Succeeded) ? "partial" : "incomplete", locations);
    private static NamingPreviewEntry Entry(OperationRecord record, NamingLocationKind kind)
        => new(LocationId(record), kind, record.OriginalResolvedName, record.ProposedName,
            record.ProposedName + Targets.ResolvedSuffix(record), record.TargetKind switch
            {
                Targets.Pnp => "SetupDiSetDeviceProperty/FriendlyName",
                Targets.BluetoothClassic => "BluetoothUpdateDeviceRecord/local alias",
                _ => "IPolicyConfig.SetPropertyValue/DeviceDesc"
            }, Evidence(kind));
    private static string LocationId(OperationRecord record)
        => Targets.IsAudio(record.TargetKind) ? "audio:" + record.Target.EndpointId
            : (record.TargetKind == Targets.Pnp ? "pnp:" : "bluetooth:") + record.Target.InstanceId;
    private static string DisplayEditable(string kind, NativeSnapshot current)
        => Targets.IsAudio(kind) ? Format.Text(current.FriendlyName, Format.RegistryString)! : current.SystemName;
    private static RawValue Clone(RawValue value) => new(value.Exists, value.Type, value.Bytes.ToArray());
    private static string? Kind(NamingLocation location) => location.Kind switch
    {
        NamingLocationKind.Pnp => Targets.Pnp,
        NamingLocationKind.AudioPlayback => Targets.AudioPlayback,
        NamingLocationKind.AudioRecording => Targets.AudioRecording,
        NamingLocationKind.BluetoothClassic => Targets.BluetoothClassic,
        _ => null
    };
    private static NamingLocationKind LocationKind(string kind) => kind switch
    {
        Targets.Pnp => NamingLocationKind.Pnp,
        Targets.AudioPlayback => NamingLocationKind.AudioPlayback,
        Targets.AudioRecording => NamingLocationKind.AudioRecording,
        Targets.BluetoothClassic => NamingLocationKind.BluetoothClassic,
        _ => throw new ProbeException("invalid-target-kind")
    };
    private static NativeSnapshot Select(TargetSession live, NamingLocation location)
    {
        var matches = live.Candidates.Where(item => location.Kind is NamingLocationKind.AudioPlayback or NamingLocationKind.AudioRecording
            ? string.Equals(item.Identity.EndpointId, location.TargetId, StringComparison.Ordinal)
            : string.Equals(item.Identity.InstanceId, location.TargetId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ProbeException(matches.Length == 0 ? "target-offline" : "target-ambiguous");
    }
    private static void MatchSelection(DeviceSnapshot device, NamingLocation location, NativeSnapshot current)
    {
        var audio = location.Kind is NamingLocationKind.AudioPlayback or NamingLocationKind.AudioRecording;
        if (!device.IsPresent || (!audio && !location.IsPresent)) throw new ProbeException("target-offline");
        if (audio)
        {
            if (!string.Equals(location.TargetId, current.Identity.EndpointId, StringComparison.Ordinal))
                throw new ProbeException("selection-identity-changed");
        }
        else if (!device.Functions.Any(function => function.IsPresent
            && string.Equals(function.InstanceId, current.Identity.InstanceId, StringComparison.OrdinalIgnoreCase)
            && function.ClassGuid == current.Identity.ClassGuid
            && function.HardwareIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                current.Identity.HardwareIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)))
            throw new ProbeException("selection-identity-changed");
        if (!string.Equals(device.ContainerId, audio ? current.Identity.EndpointContainerId : current.Identity.ContainerId, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("selection-identity-changed");
    }
    private static string Evidence(NamingLocationKind kind) => kind switch
    {
        NamingLocationKind.Pnp => "Windows device-instance FriendlyName; exact instance selection and original-property recovery",
        NamingLocationKind.AudioRecording or NamingLocationKind.AudioPlayback => "Audio endpoint DeviceDesc; native policy interface with independent Core Audio readback",
        NamingLocationKind.BluetoothClassic => "Classic Bluetooth local alias; public device-record update and original-alias recovery",
        _ => "BLE naming is planned for the next version"
    };
    private static void RequireBuild()
    {
        if (!Runtime.IsSupportedWindows) throw new ProbeException("platform-unsupported");
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
