using System.Security.Principal;
using System.Security;
using System.Text;
using System.Text.Json;

namespace DeviceRadar.NativeProbe;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args is ["--self-check"]) return SelfCheck.Run();
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new ProbeException("windows-x64-required");
            if (args is ["inspect", "--target", var inspectTarget]) return Inspect(inspectTarget);
            if (args is ["capture-audio-baseline"]) return CaptureAudioBaseline();
            if (args is ["capture-ble-baseline"]) return CaptureBleBaseline();
            if (args is ["diff-audio-baseline", "--snapshot", var snapshotPath]) return DiffAudioBaseline(snapshotPath);
            if (args is ["prepare", "--target", var prepareTarget, "--name", var name]) return Prepare(prepareTarget, name);
            if (args is [var command, "--record", var path] && command is "apply" or "verify" or "restore")
                return command == "verify" ? Verify(path) : Mutate(command, path);
            if (args is ["probe-write-access", "--record", var probePath]) return ProbeWriteAccess(probePath);
            if (args is ["refresh-sound-settings"]) return RefreshSoundSettings(false);
            if (args is ["refresh-sound-settings", "--confirm-close"]) return RefreshSoundSettings(true);
            Output(new { status = "usage", targets = new[] { Targets.Mouse, Targets.BleKeyboard, Targets.BleKeyboardPnp, Targets.BleLeNameCache, Targets.UsbMicrophone }, commands = new[] { "inspect --target <target>", "prepare --target <target> --name <name>", "apply --record <file>", "verify --record <file>", "restore --record <file>", "probe-write-access --record <file>", "capture-ble-baseline", "capture-audio-baseline", "diff-audio-baseline --snapshot <file>", "refresh-sound-settings --confirm-close", "--self-check" } });
            return 2;
        }
        catch (Exception exception)
        {
            Output(new { status = "error", error = ErrorCode(exception), nativeError = NativeError(exception),
                exceptionType = exception.GetType().FullName, hResult = exception.HResult });
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RefreshSoundSettings(bool confirmClose)
    {
        var result = AudioSettings.Refresh(confirmClose);
        Output(new { command = "refresh-sound-settings", refreshState = result.RefreshState, closedCount = result.ClosedCount,
            launched = result.Launched, reason = result.Reason, nativeError = result.NativeError,
            confirmationText = confirmClose ? null : AudioSettings.Confirmation });
        return result.Launched ? 0 : 1;
    }

    private static object Target(string kind, DeviceSnapshot snapshot) => new
    {
        hash = Format.TargetHash(snapshot.Identity.InstanceId),
        kind,
        location = Targets.Location(kind),
        property = kind == Targets.BleKeyboard ? "FriendlyName (REG_BINARY)" : kind == Targets.BleLeNameCache ? "LEName (REG_BINARY)" : kind == Targets.UsbMicrophone ? AudioTarget.DescriptionValue : "{a45c254e-df1c-4efd-8020-67d146a850e0},14",
        writeApi = kind == Targets.UsbMicrophone ? "IPolicyConfig.SetPropertyValue/main-store/experimental" : null,
        resolvedSource = kind == Targets.UsbMicrophone ? "CoreAudio/STGM_READ + PnP/SetupAPI" : "PnP-root-name",
        @class = kind == Targets.Mouse ? "Mouse" : kind is Targets.BleKeyboard or Targets.BleKeyboardPnp or Targets.BleLeNameCache ? "Bluetooth LE" : "AudioEndpoint/Capture",
        vidPid = kind == Targets.Mouse ? Format.VidPid : kind == Targets.UsbMicrophone ? AudioTarget.VidPid : kind == Targets.BleLeNameCache ? "320F:5055" : null,
        hidRevision = kind == Targets.BleLeNameCache ? "0104" : null,
        systemName = snapshot.SystemName,
        audioNameSources = snapshot.AudioNameSources,
        rawProperty = new { exists = snapshot.FriendlyName.Exists, type = snapshot.FriendlyName.Type },
        containerIdPresent = snapshot.Identity.ContainerId is not null,
        hardwareIdsPresent = snapshot.Identity.HardwareIds.Length != 0,
        ancestryCount = snapshot.Identity.Ancestors.Length
    };

    private static int Inspect(string kind)
    {
        using var live = Targets.Open(kind);
        Output(new
        {
            command = "inspect",
            status = live.Candidates.Count == 1 ? "unique" : live.Candidates.Count == 0 ? "offline" : "ambiguous",
            elevated = IsAdministrator(),
            candidateCount = live.Candidates.Count,
            writable = true,
            requiresAdministrator = kind != Targets.UsbMicrophone,
            experimental = kind != Targets.Mouse,
            experimentalBuildSupported = kind == Targets.Mouse || Environment.OSVersion.Version.Build == 22631,
            targets = live.Candidates.Select(snapshot => Target(kind, snapshot)).ToArray()
        });
        return live.Candidates.Count == 1 ? 0 : 1;
    }

    private static int Prepare(string kind, string name)
    {
        Targets.RequireWritable(kind);
        var proposed = Targets.Proposed(kind, name);
        var store = new RecordStore();
        using var operationLock = store.LockMutations();
        using var live = Targets.Open(kind);
        var snapshot = live.Unique();
        var record = new OperationRecord(Format.Version, Guid.NewGuid().ToString("N"), store.Machine,
            DateTimeOffset.UtcNow, "prepared", Environment.OSVersion.VersionString, kind, snapshot.Identity,
            snapshot.FriendlyName, snapshot.SystemName, name, proposed);
        if (Targets.ResolvedSuffix(record) != snapshot.ResolvedNameSuffix) throw new ProbeException("audio-resolved-suffix-mismatch");
        Safety.ValidateAudioBaseline(record, snapshot);
        var token = store.SaveRecoveryPoint(record);
        Output(new { command = "prepare", status = "prepared", record = token, target = Target(kind, snapshot), proposedName = name,
            expectedResolvedName = kind == Targets.BleLeNameCache ? null : name + snapshot.ResolvedNameSuffix });
        return 0;
    }

    private static int CaptureAudioBaseline()
    {
        var store = new RecordStore();
        using var operationLock = store.LockMutations();
        using var live = new AudioTarget(null);
        var snapshot = live.CaptureProperties(store.Machine);
        var token = store.SaveAudioSnapshot(snapshot);
        Output(new { command = "capture-audio-baseline", status = "captured", snapshot = token,
            target = Target(Targets.UsbMicrophone, live.Unique()), propertyCount = snapshot.Properties.Length, systemWrites = 0 });
        return 0;
    }

    private static int CaptureBleBaseline()
    {
        var store = new RecordStore();
        using var operationLock = store.LockMutations();
        using var live = new BleTarget(null, leName: true);
        var snapshot = live.CaptureProperties(store.Machine);
        var token = store.SaveBleSnapshot(snapshot);
        Output(new { command = "capture-ble-baseline", status = "captured", snapshot = token,
            target = Target(Targets.BleLeNameCache, live.Unique()), propertyCount = snapshot.Properties.Length,
            pnpFriendlyName = new { exists = snapshot.PnpFriendlyName.Exists, type = snapshot.PnpFriendlyName.Type }, systemWrites = 0 });
        return 0;
    }

    private static int DiffAudioBaseline(string path)
    {
        var store = new RecordStore();
        using var lease = store.LoadAudioSnapshot(path);
        using var live = new AudioTarget(lease.Snapshot.Target);
        var current = live.CaptureProperties(store.Machine);
        Safety.MatchIdentity(lease.Snapshot.Target, lease.Snapshot.MachineFingerprint, store.Machine, current.Target);
        var before = lease.Snapshot.Properties.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var after = current.Properties.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var changes = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new { name, original = before.GetValueOrDefault(name, RawValue.Missing()), observed = after.GetValueOrDefault(name, RawValue.Missing()) })
            .Where(change => !change.original.Same(change.observed))
            .Select(change => new { change.name, original = SummarizeProperty(change.name, change.original), observed = SummarizeProperty(change.name, change.observed) }).ToArray();
        Output(new { command = "diff-audio-baseline", status = changes.Length == 0 ? "unchanged" : "changed", snapshot = lease.Token,
            targetHash = Format.TargetHash(current.Target.InstanceId), originalResolvedName = lease.Snapshot.ResolvedName,
            observedResolvedName = current.ResolvedName, propertyCount = current.Properties.Length, changedPropertyCount = changes.Length,
            changes, systemWrites = 0, filesWritten = 0 });
        return 0;
    }

    private static object SummarizeProperty(string name, RawValue value)
    {
        var isName = string.Equals(name, AudioTarget.FriendlyNameValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "{a45c254e-df1c-4efd-8020-67d146a850e0},2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "{b3f8fa53-0004-438e-9003-51a46e139bfc},6", StringComparison.OrdinalIgnoreCase);
        return new { exists = value.Exists, type = value.Type, byteCount = value.Bytes.Length, valueHash = Format.Hash(value.Bytes)[..12],
            text = isName && value.Exists && value.Type == Format.RegistryString ? Format.Text(value, Format.RegistryString) : null };
    }

    private static Observation Observe(OperationRecord record, string machine)
    {
        try
        {
            using var live = Targets.Open(record.TargetKind, record.Target);
            if (live.Candidates.Count == 0) return new("offline", "target-offline");
            if (live.Candidates.Count != 1) return new("unavailable", "target-ambiguous");
            var current = live.Unique();
            try { Safety.MatchIdentity(record, machine, current.Identity); }
            catch (ProbeException error) { return new("offline", error.Code); }
            return Safety.Classify(record, current);
        }
        catch (Exception error) { return new("unavailable", ErrorCode(error), NativeError: NativeError(error),
            ExceptionType: error.GetType().FullName, HResult: error.HResult); }
    }

    private static int Verify(string path)
    {
        var store = new RecordStore();
        using var lease = store.Load(path);
        var observed = Observe(lease.Record, store.Machine);
        var result = Event(lease, "verify", "completed", "observed", observed: observed);
        var eventToken = store.AppendEvent(result);
        OutputResult(lease, result, observed, eventToken);
        return Safety.ObservationConfirms(lease.Record.TargetKind, "proposed", observed)
            || Safety.ObservationConfirms(lease.Record.TargetKind, "original", observed) ? 0 : 1;
    }

    private static int ProbeWriteAccess(string path)
    {
        var store = new RecordStore();
        using var operationLock = store.LockMutations();
        using var lease = store.Load(path);
        Exception? failure = null;
        var status = lease.Record.TargetKind == Targets.UsbMicrophone ? "audio-policy-get-confirmed" : "write-handle-opened";
        try
        {
            if (lease.Record.TargetKind is not (Targets.BleKeyboard or Targets.BleLeNameCache or Targets.UsbMicrophone))
                throw new ProbeException("write-access-probe-not-supported-for-pnp");
            using var live = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
            var current = live.Unique();
            Safety.MatchIdentity(lease.Record, store.Machine, current.Identity);
            if (!current.FriendlyName.Same(lease.Record.Original) && !current.FriendlyName.Same(lease.Record.Proposed))
                throw new ProbeException("current-value-changed");
            using var access = live.OpenWriter(current.FriendlyName); // No Write call: capability only.
        }
        catch (Exception error) { failure = error; status = "write-access-unavailable"; }
        var observed = Observe(lease.Record, store.Machine);
        var result = Event(lease, "probe-write-access", "completed", status, nativeError: failure is null ? null : NativeError(failure),
            observed: observed, reason: failure is null ? null : ErrorCode(failure), exception: failure);
        var eventToken = store.AppendEvent(result);
        OutputResult(lease, result, observed, eventToken);
        return failure is null ? 0 : 1;
    }

    private static int Mutate(string command, string path)
    {
        var store = new RecordStore();
        using var operationLock = store.LockMutations(); // Serializes every local mutation, including different recovery points.
        using var lease = store.Load(path); // Keeps the immutable recovery point open without write/delete sharing.
        var attempted = false;
        bool? apiSucceeded = null;
        int? nativeError = null;
        string? failure = null;
        Exception? failureException = null;
        var status = "blocked";
        EventLease? intent = null;
        TargetWriteLease? writer = null;
        try
        {
            if (lease.Record.TargetKind != Targets.UsbMicrophone && !IsAdministrator()) throw new ProbeException("administrator-required");
            var applyEvidence = store.HasApplyEvidence(lease);
            using var initial = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
            var before = initial.Unique();
            var decision = Safety.Decide(lease.Record, store.Machine, before, command, applyEvidence);
            if (decision == MutationDecision.NoOp) status = "already-at-value";
            else
            {
                TargetSession? final = null;
                try
                {
                    Safety.GuardedWrite(
                        () => intent = store.HoldEvent(Event(lease, command, "intent", "write-intent", before: before.FriendlyName)),
                        () =>
                        {
                            final = Targets.Open(lease.Record.TargetKind, lease.Record.Target);
                            if (Safety.Decide(lease.Record, store.Machine, final.Unique(), command, applyEvidence) != MutationDecision.Write)
                                throw new ProbeException("value-changed-before-write");
                            writer = final.OpenWriter(command == "apply" ? lease.Record.Proposed : lease.Record.Original);
                        },
                        () =>
                        {
                            attempted = true;
                            writer!.Write();
                            apiSucceeded = true;
                        });
                }
                finally { final?.Dispose(); }
                status = "write-unconfirmed";
            }
        }
        catch (Exception exception)
        {
            failure = ErrorCode(exception);
            failureException = exception;
            nativeError = NativeError(exception);
            if (attempted) { apiSucceeded = false; status = "write-unconfirmed"; }
        }

        // Read back even after a failed Set call; a failure never proves that the value stayed unchanged.
        using var durableIntent = intent; // Keep JSON + seal and their pinned directories alive through readback/results.
        using var durableWriter = writer;
        var wantedState = command == "apply" ? "proposed" : "original";
        var observed = Observe(lease.Record, store.Machine);
        if (attempted && lease.Record.TargetKind == Targets.UsbMicrophone)
        {
            var propagation = System.Diagnostics.Stopwatch.StartNew();
            while (!Safety.ObservationConfirms(lease.Record.TargetKind, wantedState, observed) && propagation.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep((int)Math.Max(0, Math.Min(200, 5000 - propagation.ElapsedMilliseconds)));
                observed = Observe(lease.Record, store.Machine);
            }
        }
        if (status == "already-at-value" && !Safety.ObservationConfirms(lease.Record.TargetKind, wantedState, observed))
            status = "observation-unconfirmed";
        if (attempted && Safety.ObservationConfirms(lease.Record.TargetKind, wantedState, observed))
            status = apiSucceeded == true ? (command == "apply" ? "applied" : "restored") : "value-confirmed-api-error";
        if (attempted && observed.State == wantedState && lease.Record.TargetKind == Targets.UsbMicrophone
            && !Safety.ObservationConfirms(lease.Record.TargetKind, wantedState, observed)) status = "raw-confirmed-sources-stale";
        if (attempted && observed.State == wantedState && lease.Record.TargetKind == Targets.BleKeyboard)
            status = "storage-confirmed-ui-unverified";
        if (attempted && observed.State == wantedState && lease.Record.TargetKind == Targets.BleKeyboardPnp)
            status = "pnp-property-confirmed-paired-name-unverified";
        if (attempted && observed.State == wantedState && lease.Record.TargetKind == Targets.BleLeNameCache)
            status = "source-cache-confirmed-ui-unverified";
        var completed = Event(lease, command, "completed", status, attempted, apiSucceeded, nativeError, observed: observed,
            reason: failure, exception: failureException, notificationCount: writer?.NotificationCount);
        try
        {
            var eventToken = store.AppendEvent(completed);
            OutputResult(lease, completed, observed, eventToken);
        }
        catch (Exception exception)
        {
            Output(new
            {
                command, record = lease.Token, status = "result-log-failed", writeAttempted = attempted,
                apiSucceeded, nativeError, observedState = observed.State, resolvedState = observed.ResolvedState,
                synchronizedState = observed.SynchronizedState, audioNameSources = observed.Snapshot?.AudioNameSources,
                audioSourceStates = observed.AudioSourceStates, notificationCount = completed.NotificationCount, error = ErrorCode(exception),
                exceptionType = exception.GetType().FullName, hResult = exception.HResult
            });
            return 1;
        }
        return status is "applied" or "restored" or "already-at-value" ? 0 : 1;
    }

    private static OperationEvent Event(RecordLease lease, string command, string stage, string status,
        bool attempted = false, bool? apiSucceeded = null, int? nativeError = null, RawValue? before = null,
        Observation? observed = null, string? reason = null, Exception? exception = null, int? notificationCount = null)
        => new(Format.Version, lease.Record.OperationId, lease.Hash, DateTimeOffset.UtcNow, command, stage, status,
            attempted, apiSucceeded, nativeError, before, observed?.State, observed?.Snapshot?.FriendlyName, reason ?? observed?.Reason,
            observed?.ResolvedState, observed?.Snapshot?.SystemName, exception?.GetType().FullName ?? observed?.ExceptionType,
            exception?.HResult ?? observed?.HResult, observed?.SynchronizedState, observed?.Snapshot?.AudioNameSources,
            observed?.AudioSourceStates, notificationCount);

    private static void OutputResult(RecordLease lease, OperationEvent result, Observation observation, string eventToken)
        => Output(new
        {
            command = result.Command, record = lease.Token, status = result.Status,
            writeAttempted = result.WriteAttempted, apiSucceeded = result.ApiSucceeded, nativeError = result.NativeError,
            exceptionType = result.ExceptionType, hResult = result.HResult,
            observedState = observation.State, reason = result.Reason,
            resolvedState = observation.ResolvedState,
            synchronizedState = observation.SynchronizedState,
            audioSourceStates = observation.AudioSourceStates,
            notificationCount = result.NotificationCount,
            capability = result.Command == "probe-write-access" && lease.Record.TargetKind == Targets.UsbMicrophone ? "audio-policy-get-only-no-set" : null,
            observationNativeError = observation.NativeError,
            target = observation.Snapshot is not null ? Target(lease.Record.TargetKind, observation.Snapshot) : null,
            result = eventToken
        });

    internal static string ErrorCode(Exception error) => error switch
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

    internal static void Output(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Format.Json));
}
