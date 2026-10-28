using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceRadar.Naming.Native;

internal static class Format
{
    public const uint Empty = 0;
    public const uint String = 0x12;
    public const uint StringList = 0x2012;
    public const uint Guid = 0x0d;
    public const int MaxPropertyBytes = 8192;
    public const int Version = 101;
    public const uint RegistryString = 1;
    public const uint RegistryBinary = 3;
    public static readonly Guid MouseClass = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    public static readonly UnicodeEncoding Unicode = new(false, false, true);
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public static string TargetHash(string instanceId) => Hash(instanceId.ToUpperInvariant())[..12];

    public static RawValue Name(string name, uint type = String)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
            throw new ProbeException("invalid-name");
        try { return new(true, type, Unicode.GetBytes(name + "\0")); }
        catch (EncoderFallbackException) { throw new ProbeException("invalid-name"); }
    }

    public static string? Text(RawValue value, uint expectedType = String)
    {
        if (!value.Exists) return null;
        if (expectedType is not (String or RegistryString) || value.Type != expectedType || value.Bytes.Length < 2 || value.Bytes.Length % 2 != 0
            || value.Bytes[^1] != 0 || value.Bytes[^2] != 0)
            throw new ProbeException("invalid-string-property");
        try
        {
            var text = Unicode.GetString(value.Bytes, 0, value.Bytes.Length - 2);
            if (text.Contains('\0')) throw new ProbeException("invalid-string-property");
            return text;
        }
        catch (DecoderFallbackException) { throw new ProbeException("invalid-string-property"); }
    }
}

internal sealed record RawValue(bool Exists, uint Type, byte[] Bytes)
{
    public static RawValue Missing() => new(false, Format.Empty, []);
    public bool Same(RawValue other) => Bytes is not null && other.Bytes is not null
        && Exists == other.Exists && Type == other.Type && Bytes.AsSpan().SequenceEqual(other.Bytes);
    public (uint Type, byte[]? Bytes) ForWrite() => Exists ? (Type, Bytes) : (Format.Empty, null);
}

internal sealed record DeviceAncestor(string InstanceId, Guid ClassGuid, string[] HardwareIds, string? ContainerId);
internal sealed record DeviceIdentity(string InstanceId, Guid ClassGuid, string[] HardwareIds, string? ContainerId, DeviceAncestor[] Ancestors,
    string? EndpointId = null, string? RadioAddress = null, string? EndpointContainerId = null);
internal sealed record AudioNameSources(string CoreAudioDescription, string CoreAudioInterfaceName,
    string? PnpDeviceDescription, string? PnpFriendlyName, string? PnpName);
internal sealed record BluetoothNameSources(string RuntimeName, string? PnpName, RawValue DeviceName);
internal sealed record AudioNameStates(string CoreAudioDescription, string CoreAudioInterfaceName,
    string PnpDeviceDescription, string PnpFriendlyName, string PnpName)
{
    internal bool HasUnknown => CoreAudioDescription is not ("original" or "proposed" or "original-and-proposed") || CoreAudioInterfaceName != "unchanged"
        || PnpDeviceDescription != "unchanged" || PnpFriendlyName != "unchanged" || PnpName != "unchanged";
}
internal sealed record DeviceSnapshot(DeviceIdentity Identity, RawValue FriendlyName, string SystemName,
    string ResolvedNameSuffix = "", AudioNameSources? AudioNameSources = null, BluetoothNameSources? BluetoothNameSources = null);

// This immutable file is the recovery point. Attempts and results are separate files.
internal sealed record OperationRecord(
    int FormatVersion,
    string OperationId,
    string MachineFingerprint,
    DateTimeOffset CreatedAtUtc,
    string Status,
    string OsVersion,
    string TargetKind,
    DeviceIdentity Target,
    RawValue Original,
    string OriginalResolvedName,
    string ProposedName,
    RawValue Proposed,
    string Purpose = "rename",
    string? SourceOperationId = null,
    AudioNameSources? OriginalAudioSources = null,
    BluetoothNameSources? OriginalBluetoothSources = null);

internal sealed record Observation(string State, string? Reason = null, DeviceSnapshot? Snapshot = null, int? NativeError = null,
    string? ResolvedState = null, string? ExceptionType = null, int? HResult = null,
    string? SynchronizedState = null, AudioNameStates? AudioSourceStates = null);

internal sealed record OperationEvent(
    int FormatVersion,
    string OperationId,
    string RecordHash,
    DateTimeOffset TimeUtc,
    string Command,
    string Stage,
    string Status,
    bool WriteAttempted = false,
    bool? ApiSucceeded = null,
    int? NativeError = null,
    RawValue? Before = null,
    string? ObservedState = null,
    RawValue? ObservedValue = null,
    string? Reason = null,
    string? ResolvedState = null,
    string? ResolvedName = null,
    string? ExceptionType = null,
    int? HResult = null,
    string? SynchronizedState = null,
    AudioNameSources? AudioNameSources = null,
    AudioNameStates? AudioSourceStates = null,
    int? NotificationCount = null);

internal sealed class ProbeException(string code, int? nativeError = null) : Exception(code)
{
    public string Code { get; } = code;
    public int? NativeError { get; } = nativeError;
}

internal enum MutationDecision { Write, NoOp }

internal static class Safety
{
    public static bool ObservationConfirms(string targetKind, string wantedState, Observation observation)
        => Targets.IsAudio(targetKind) || targetKind == Targets.BluetoothClassic
            ? observation.SynchronizedState == wantedState || observation.SynchronizedState == "original-and-proposed"
            : (observation.State == wantedState || observation.State == "original-and-proposed")
                && (observation.ResolvedState == wantedState || observation.ResolvedState == "original-and-proposed");

    public static Observation Classify(OperationRecord record, DeviceSnapshot current, bool emptyAliasIntent = false)
    {
        var state = current.FriendlyName.Same(record.Proposed) && current.FriendlyName.Same(record.Original) ? "original-and-proposed"
            : current.FriendlyName.Same(record.Proposed) ? "proposed"
            : current.FriendlyName.Same(record.Original) ? "original" : "other";
        var suffix = Targets.ResolvedSuffix(record);
        var resolved = NameState(current.SystemName, record.OriginalResolvedName, record.ProposedName + suffix);
        if (record.TargetKind == Targets.BluetoothClassic)
        {
            var original = ClassicAtValue(record, current, false);
            var proposed = ClassicAtValue(record, current, true);
            return new(state, Snapshot: current, ResolvedState: resolved, SynchronizedState:
                original && proposed ? "original-and-proposed" : proposed ? "proposed" : original ? "original"
                : ClassicKnown(record, current, emptyAliasIntent) ? "mixed" : "other");
        }
        if (!Targets.IsAudio(record.TargetKind)) return new(state, Snapshot: current, ResolvedState: resolved);
        var sources = current.AudioNameSources;
        var baseline = record.OriginalAudioSources ?? throw new ProbeException("audio-baseline-missing");
        var originalBase = Format.Text(record.Original, Format.RegistryString)!;
        var originalFull = record.OriginalResolvedName;
        var proposedFull = record.ProposedName + suffix;
        var states = new AudioNameStates(
            NameState(sources?.CoreAudioDescription, originalBase, record.ProposedName),
            Unchanged(sources?.CoreAudioInterfaceName, baseline.CoreAudioInterfaceName),
            Unchanged(sources?.PnpDeviceDescription, baseline.PnpDeviceDescription),
            Unchanged(sources?.PnpFriendlyName, baseline.PnpFriendlyName),
            Unchanged(sources?.PnpName, baseline.PnpName));
        var allOriginal = AudioAtValue(record, current, "original");
        var allProposed = AudioAtValue(record, current, "proposed");
        var synchronized = allOriginal && allProposed ? "original-and-proposed" : allProposed ? "proposed" : allOriginal ? "original"
            : state == "other" || resolved == "other" || states.HasUnknown ? "other" : "mixed";
        return new(state, Snapshot: current, ResolvedState: resolved, SynchronizedState: synchronized, AudioSourceStates: states);
    }

    private static string NameState(string? value, string original, string proposed)
        => value == proposed && value == original ? "original-and-proposed"
            : value == proposed ? "proposed" : value == original ? "original" : value is null ? "missing" : "other";

    private static string Unchanged(string? value, string? original) => value == original ? "unchanged" : "other";

    private static bool ClassicAtValue(OperationRecord record, DeviceSnapshot current, bool proposed)
    {
        var value = proposed ? record.Proposed : record.Original;
        var name = proposed ? record.ProposedName : ClassicOriginalName(record);
        var sources = current.BluetoothNameSources;
        return current.FriendlyName.Same(value) && current.SystemName == name && sources is not null
            && sources.RuntimeName == name && sources.PnpName == name
            && sources.DeviceName.Same(record.OriginalBluetoothSources!.DeviceName);
    }

    private static string ClassicOriginalName(OperationRecord record)
        => ClassicTarget.AliasText(record.Original) ?? ClassicTarget.DeviceName(record.OriginalBluetoothSources!.DeviceName);

    private static bool ClassicAtBaseline(OperationRecord record, DeviceSnapshot current)
    {
        var sources = current.BluetoothNameSources;
        var baseline = record.OriginalBluetoothSources;
        return sources is not null && baseline is not null && current.FriendlyName.Same(record.Original)
            && current.SystemName == record.OriginalResolvedName && sources.RuntimeName == baseline.RuntimeName
            && sources.PnpName == baseline.PnpName && sources.DeviceName.Same(baseline.DeviceName);
    }

    private static bool ClassicKnown(OperationRecord record, DeviceSnapshot current, bool emptyAliasIntent)
    {
        var sources = current.BluetoothNameSources;
        var baseline = record.OriginalBluetoothSources!;
        bool Known(string? name) => name is not null && (name == record.OriginalResolvedName || name == record.ProposedName
            || name == baseline.RuntimeName || name == baseline.PnpName || name == ClassicOriginalName(record));
        // The public empty-name call can persist REG_BINARY 00 before the exact missing/empty
        // original is restored. Only an already durable restore intent authorizes this transition.
        var emptyTransition = emptyAliasIntent && current.FriendlyName is { Exists: true, Type: Format.RegistryBinary, Bytes: [0] }
            && (ClassicTarget.AliasText(record.Original) is null || ClassicTarget.AliasText(record.Proposed) is null);
        return sources is not null && sources.DeviceName.Same(record.OriginalBluetoothSources!.DeviceName)
            && (current.FriendlyName.Same(record.Original) || current.FriendlyName.Same(record.Proposed) || emptyTransition)
            && Known(current.SystemName) && Known(sources.RuntimeName) && Known(sources.PnpName);
    }

    public static bool KnownRestoreState(OperationRecord record, DeviceSnapshot current, bool applyEvidence, bool emptyAliasIntent)
    {
        var observed = Classify(record, current, emptyAliasIntent);
        if (ObservationConfirms(record.TargetKind, "original", observed)) return true;
        return applyEvidence && (ObservationConfirms(record.TargetKind, "proposed", observed)
            || (record.TargetKind == Targets.BluetoothClassic && ClassicKnown(record, current, emptyAliasIntent)));
    }

    public static void ValidateClassicBaseline(OperationRecord record, DeviceSnapshot current)
    {
        if (record.TargetKind == Targets.BluetoothClassic && !ClassicAtBaseline(record, current))
            throw new ProbeException("classic-baseline-inconsistent");
    }

    private static bool AudioAtValue(OperationRecord record, DeviceSnapshot current, string state)
    {
        var desired = state == "proposed" ? record.Proposed : record.Original;
        var name = Format.Text(desired, Format.RegistryString)!;
        var suffix = Targets.ResolvedSuffix(record);
        var sources = current.AudioNameSources;
        var baseline = record.OriginalAudioSources;
        return current.FriendlyName.Same(desired) && current.ResolvedNameSuffix == suffix && current.SystemName == name + suffix
            && sources is not null && baseline is not null && sources.CoreAudioDescription == name
            && sources.CoreAudioInterfaceName == baseline.CoreAudioInterfaceName
            && sources.PnpDeviceDescription == baseline.PnpDeviceDescription
            && sources.PnpFriendlyName == baseline.PnpFriendlyName && sources.PnpName == baseline.PnpName;
    }

    public static void ValidateAudioBaseline(OperationRecord record, DeviceSnapshot current)
    {
        if (!Targets.IsAudio(record.TargetKind)) return;
        ValidateAudioValue(current.FriendlyName);
        if (!AudioAtValue(record, current, "original")) throw new ProbeException("audio-baseline-inconsistent");
    }

    public static void ValidateAudioValue(RawValue value)
    {
        ValidateOriginal(value, Format.RegistryString);
        if (!value.Exists)
            throw new ProbeException("required-audio-description-missing");
    }

    public static bool IsApplyEvidence(OperationEvent entry, OperationRecord record, string recordHash)
        => entry.FormatVersion == Format.Version && entry.OperationId == record.OperationId && entry.RecordHash == recordHash
            && entry.Command == "apply"
            && ((entry.Stage == "intent" && entry.Status == "write-intent" && entry.Before is not null && entry.Before.Same(record.Original))
                || (entry.Stage == "completed" && entry.WriteAttempted && entry.ObservedState == "proposed"
                    && entry.ObservedValue is not null && entry.ObservedValue.Same(record.Proposed)));

    public static bool IsClassicEmptyAliasIntent(OperationEvent entry, OperationRecord record, string recordHash)
    {
        if (record.TargetKind != Targets.BluetoothClassic || entry.FormatVersion != Format.Version
            || entry.OperationId != record.OperationId || entry.RecordHash != recordHash
            || entry.Stage != "intent" || entry.Status != "write-intent" || entry.Before is null
            || (!entry.Before.Same(record.Original) && !entry.Before.Same(record.Proposed))) return false;
        var desired = entry.Command == "restore" ? record.Original
            : entry.Command == "apply" && record.Purpose == "restore" ? record.Proposed : null;
        return desired is not null && ClassicTarget.AliasText(desired) is null;
    }

    public static void ValidateRecord(OperationRecord record)
    {
        if (record.FormatVersion != Format.Version || !System.Guid.TryParseExact(record.OperationId, "N", out _)
            || record.OperationId != record.OperationId.ToLowerInvariant()
            || record.MachineFingerprint is not { Length: 64 }
            || record.MachineFingerprint.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))
            || record.Status != "prepared" || record.CreatedAtUtc == default
            || record.OsVersion is not { Length: > 0 and <= 256 }
            || record.Target is null || record.Original is null || record.Proposed is null)
            throw new ProbeException("invalid-record");
        Targets.ValidateBinding(record.TargetKind, record.Target);
        if (record.OriginalResolvedName is not { Length: > 0 and <= 512 } || record.OriginalResolvedName.Any(char.IsControl))
            throw new ProbeException("invalid-record-resolved-name");
        Targets.ValidateValue(record.TargetKind, record.Original);
        if (Targets.IsAudio(record.TargetKind))
        {
            var sources = record.OriginalAudioSources ?? throw new ProbeException("audio-baseline-missing");
            if (sources.CoreAudioDescription != Format.Text(record.Original, Format.RegistryString)
                || new[] { sources.CoreAudioDescription, sources.CoreAudioInterfaceName, sources.PnpDeviceDescription,
                    sources.PnpFriendlyName, sources.PnpName }.Any(s => s is not null && (s.Length > 512 || s.Any(char.IsControl))))
                throw new ProbeException("invalid-audio-baseline");
        }
        else if (record.OriginalAudioSources is not null) throw new ProbeException("invalid-record");
        if (record.TargetKind == Targets.BluetoothClassic)
        {
            var sources = record.OriginalBluetoothSources ?? throw new ProbeException("classic-baseline-missing");
            var factoryName = ClassicTarget.DeviceName(sources.DeviceName);
            var originalName = ClassicTarget.AliasText(record.Original) ?? factoryName;
            if (sources.RuntimeName != record.OriginalResolvedName || sources.PnpName is not { Length: > 0 and <= 512 }
                || sources.PnpName.Any(char.IsControl)
                || (record.Purpose != "restore" && (record.OriginalResolvedName != originalName || sources.PnpName != originalName)))
                throw new ProbeException("invalid-classic-baseline");
            if (record.Purpose == "restore" && record.ProposedName != (ClassicTarget.AliasText(record.Proposed) ?? factoryName))
                throw new ProbeException("invalid-record-proposed");
        }
        else if (record.OriginalBluetoothSources is not null) throw new ProbeException("invalid-record");
        _ = Targets.ResolvedSuffix(record);
        if (record.Purpose == "rename")
        {
            if (record.SourceOperationId is not null || !Targets.Proposed(record.TargetKind, record.ProposedName).Same(record.Proposed))
                throw new ProbeException("invalid-record-proposed");
        }
        else if (record.Purpose == "restore")
        {
            if (!System.Guid.TryParseExact(record.SourceOperationId, "N", out _) || record.SourceOperationId != record.SourceOperationId.ToLowerInvariant())
                throw new ProbeException("invalid-restore-source");
            Targets.ValidateValue(record.TargetKind, record.Proposed);
            if (record.ProposedName is not { Length: <= 512 } || record.ProposedName.Any(char.IsControl)) throw new ProbeException("invalid-record-proposed");
            if (Targets.ValueText(record.TargetKind, record.Proposed) is { } text && text != record.ProposedName)
                throw new ProbeException("invalid-record-proposed");
        }
        else throw new ProbeException("invalid-operation-purpose");
    }

    public static void ValidateOriginal(RawValue original, uint expectedType = Format.String)
    {
        if (original.Bytes is null || original.Bytes.Length > Format.MaxPropertyBytes
            || (!original.Exists && (original.Type != Format.Empty || original.Bytes.Length != 0)))
            throw new ProbeException("invalid-original-property");
        // FriendlyName's documented type is STRING. An unexpected type needs investigation before a write.
        if (original.Exists)
        {
            if (original.Type != expectedType) throw new ProbeException("invalid-original-property");
            if (expectedType != Format.RegistryBinary) _ = Format.Text(original, expectedType);
            else if (original.Bytes.Length == 0) throw new ProbeException("invalid-original-property");
        }
    }

    public static void MatchIdentity(OperationRecord record, string machine, DeviceIdentity current)
        => MatchIdentity(record.Target, record.MachineFingerprint, machine, current);

    public static void MatchIdentity(DeviceIdentity expected, string expectedMachine, string machine, DeviceIdentity current)
    {
        if (expectedMachine != machine) throw new ProbeException("machine-mismatch");
        if (expected.ClassGuid != current.ClassGuid
            || !string.Equals(expected.InstanceId, current.InstanceId, StringComparison.Ordinal)
            || !string.Equals(expected.ContainerId, current.ContainerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.EndpointId, current.EndpointId, StringComparison.Ordinal)
            || !string.Equals(expected.EndpointContainerId, current.EndpointContainerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.RadioAddress, current.RadioAddress, StringComparison.OrdinalIgnoreCase)
            || !expected.HardwareIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                current.HardwareIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            || expected.Ancestors.Length != current.Ancestors.Length
            || !expected.Ancestors.Zip(current.Ancestors).All(pair => SameAncestor(pair.First, pair.Second)))
            throw new ProbeException("target-mismatch");
    }

    private static bool SameAncestor(DeviceAncestor expected, DeviceAncestor actual)
        => expected.ClassGuid == actual.ClassGuid && expected.InstanceId == actual.InstanceId
            && string.Equals(expected.ContainerId, actual.ContainerId, StringComparison.OrdinalIgnoreCase)
            && expected.HardwareIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                actual.HardwareIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public static MutationDecision Decide(OperationRecord record, string machine, DeviceSnapshot current, string command, bool applyEvidence,
        bool emptyAliasIntent = false)
    {
        MatchIdentity(record, machine, current.Identity);
        if (Targets.IsAudio(record.TargetKind)) return DecideAudio(record, current, command, applyEvidence);
        if (record.TargetKind == Targets.BluetoothClassic)
        {
            if (command is not ("apply" or "restore")) throw new ProbeException("invalid-command");
            if (!ClassicKnown(record, current, emptyAliasIntent)) throw new ProbeException("classic-name-source-changed");
            if (ClassicAtValue(record, current, command == "apply")) return MutationDecision.NoOp;
            if (!applyEvidence && (command == "restore" || !ClassicAtBaseline(record, current)))
                throw new ProbeException("apply-evidence-missing");
            return MutationDecision.Write;
        }
        if (command == "apply")
        {
            if (current.FriendlyName.Same(record.Original))
                return record.Original.Same(record.Proposed) ? MutationDecision.NoOp : MutationDecision.Write;
            if (applyEvidence && current.FriendlyName.Same(record.Proposed)) return MutationDecision.NoOp;
            throw new ProbeException("current-value-changed");
        }
        if (command != "restore") throw new ProbeException("invalid-command");
        if (current.FriendlyName.Same(record.Original)) return MutationDecision.NoOp;
        if (!applyEvidence) throw new ProbeException("apply-evidence-missing");
        if (!current.FriendlyName.Same(record.Proposed)) throw new ProbeException("current-value-changed");
        return MutationDecision.Write;
    }

    private static MutationDecision DecideAudio(OperationRecord record, DeviceSnapshot current, string command, bool applyEvidence)
    {
        ValidateAudioValue(current.FriendlyName);
        var atOriginal = current.FriendlyName.Same(record.Original);
        var atProposed = current.FriendlyName.Same(record.Proposed);
        if (!atOriginal && !atProposed) throw new ProbeException("current-value-changed");
        var observation = Classify(record, current);
        if (observation.AudioSourceStates!.HasUnknown || observation.ResolvedState is not ("original" or "proposed" or "original-and-proposed")
            || current.ResolvedNameSuffix != Targets.ResolvedSuffix(record)) throw new ProbeException("audio-name-source-changed");
        if (command == "apply")
        {
            if (observation.SynchronizedState == "mixed" && !applyEvidence) throw new ProbeException("apply-evidence-missing");
            if (!atOriginal && !applyEvidence) throw new ProbeException("current-value-changed");
            return ObservationConfirms(record.TargetKind, "proposed", observation) ? MutationDecision.NoOp : MutationDecision.Write;
        }
        if (command != "restore") throw new ProbeException("invalid-command");
        if (ObservationConfirms(record.TargetKind, "original", observation)) return MutationDecision.NoOp;
        if (!applyEvidence) throw new ProbeException("apply-evidence-missing");
        return MutationDecision.Write;
    }

    // The only route to the system write: durable intent must succeed first, then recheck immediately.
    public static void GuardedWrite(Action persistIntent, Action recheck, Action write)
    {
        persistIntent();
        recheck();
        write();
    }
}
