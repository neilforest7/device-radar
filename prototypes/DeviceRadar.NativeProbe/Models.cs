using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceRadar.NativeProbe;

internal static class Format
{
    public const uint Empty = 0;
    public const uint String = 0x12;
    public const uint StringList = 0x2012;
    public const uint Guid = 0x0d;
    public const int MaxPropertyBytes = 8192;
    public const int Version = 3;
    public const uint RegistryString = 1;
    public const uint RegistryBinary = 3;
    public const string VidPid = "046D:C547";
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

    public static bool IsCandidateId(string value)
    {
        const string prefix = @"HID\VID_046D&PID_C547";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (value.Length == prefix.Length || value[prefix.Length] is '&' or '\\');
    }

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
internal sealed record DeviceIdentity(string InstanceId, Guid ClassGuid, string[] HardwareIds, string? ContainerId, DeviceAncestor[] Ancestors);
internal sealed record AudioNameSources(string CoreAudioDescription, string CoreAudioInterfaceName,
    string? PnpDeviceDescription, string? PnpFriendlyName, string? PnpName);
internal sealed record AudioNameStates(string CoreAudioDescription, string CoreAudioInterfaceName,
    string PnpDeviceDescription, string PnpFriendlyName, string PnpName)
{
    internal bool HasUnknown => CoreAudioDescription is not ("original" or "proposed") || CoreAudioInterfaceName != "unchanged"
        || PnpDeviceDescription is not ("original" or "proposed") || PnpFriendlyName is not ("original" or "proposed")
        || PnpName is not ("original" or "proposed");
}
internal sealed record DeviceSnapshot(DeviceIdentity Identity, RawValue FriendlyName, string SystemName,
    string ResolvedNameSuffix = "", AudioNameSources? AudioNameSources = null);

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
    RawValue Proposed);

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
        => targetKind == Targets.UsbMicrophone
            ? observation.SynchronizedState == wantedState || observation.SynchronizedState == "original-and-proposed"
            : observation.State == wantedState;

    public static Observation Classify(OperationRecord record, DeviceSnapshot current)
    {
        var state = current.FriendlyName.Same(record.Proposed) ? "proposed"
            : current.FriendlyName.Same(record.Original) ? "original" : "other";
        var suffix = Targets.ResolvedSuffix(record);
        var resolved = NameState(current.SystemName, record.OriginalResolvedName, record.ProposedName + suffix);
        if (record.TargetKind != Targets.UsbMicrophone) return new(state, Snapshot: current, ResolvedState: resolved);
        var sources = current.AudioNameSources;
        var originalBase = Format.Text(record.Original, Format.RegistryString)!;
        var originalFull = record.OriginalResolvedName;
        var proposedFull = record.ProposedName + suffix;
        var states = new AudioNameStates(
            NameState(sources?.CoreAudioDescription, originalBase, record.ProposedName),
            sources?.CoreAudioInterfaceName == suffix[2..^1] ? "unchanged" : sources?.CoreAudioInterfaceName is null ? "missing" : "other",
            NameState(sources?.PnpDeviceDescription, originalFull, proposedFull),
            NameState(sources?.PnpFriendlyName, originalFull, proposedFull),
            NameState(sources?.PnpName, originalFull, proposedFull));
        var allOriginal = AudioAtValue(record, current, "original");
        var allProposed = AudioAtValue(record, current, "proposed");
        var synchronized = allOriginal && allProposed ? "original-and-proposed" : allProposed ? "proposed" : allOriginal ? "original"
            : state == "other" || resolved == "other" || states.HasUnknown ? "other" : "mixed";
        return new(state, Snapshot: current, ResolvedState: resolved, SynchronizedState: synchronized, AudioSourceStates: states);
    }

    private static string NameState(string? value, string original, string proposed)
        => value == proposed ? "proposed" : value == original ? "original" : value is null ? "missing" : "other";

    private static bool AudioAtValue(OperationRecord record, DeviceSnapshot current, string state)
    {
        var desired = state == "proposed" ? record.Proposed : record.Original;
        var name = Format.Text(desired, Format.RegistryString)!;
        var suffix = Targets.ResolvedSuffix(record);
        var sources = current.AudioNameSources;
        return current.FriendlyName.Same(desired) && current.ResolvedNameSuffix == suffix && current.SystemName == name + suffix
            && sources is not null && sources.CoreAudioDescription == name && sources.CoreAudioInterfaceName == suffix[2..^1]
            && sources.PnpDeviceDescription == name + suffix && sources.PnpFriendlyName == name + suffix && sources.PnpName == name + suffix;
    }

    public static void ValidateAudioBaseline(OperationRecord record, DeviceSnapshot current)
    {
        if (record.TargetKind != Targets.UsbMicrophone) return;
        ValidateAudioValue(current.FriendlyName);
        if (!AudioAtValue(record, current, "original")) throw new ProbeException("audio-baseline-inconsistent");
    }

    public static void ValidateAudioValue(RawValue value)
    {
        ValidateOriginal(value, Format.RegistryString);
        if (!value.Exists || string.IsNullOrWhiteSpace(Format.Text(value, Format.RegistryString)))
            throw new ProbeException("required-audio-description-missing");
    }

    public static bool IsApplyEvidence(OperationEvent entry, OperationRecord record, string recordHash)
        => entry.FormatVersion == Format.Version && entry.OperationId == record.OperationId && entry.RecordHash == recordHash
            && entry.Command == "apply"
            && ((entry.Stage == "intent" && entry.Status == "write-intent" && entry.Before is not null && entry.Before.Same(record.Original))
                || (entry.Stage == "completed" && entry.WriteAttempted && entry.ObservedState == "proposed"
                    && entry.ObservedValue is not null && entry.ObservedValue.Same(record.Proposed)));

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
        ValidateOriginal(record.Original, Targets.PropertyType(record.TargetKind));
        if (record.TargetKind == Targets.UsbMicrophone) ValidateAudioValue(record.Original);
        if (record.TargetKind == Targets.BleLeNameCache)
        {
            BleTarget.ValidateLeName(record.Original);
            BleTarget.ValidateLeName(record.Proposed);
        }
        _ = Targets.ResolvedSuffix(record);
        if (!Targets.Proposed(record.TargetKind, record.ProposedName).Same(record.Proposed)) throw new ProbeException("invalid-record-proposed");
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
        else if (expectedType == Format.RegistryBinary) throw new ProbeException("required-alias-value-missing");
    }

    public static void MatchIdentity(OperationRecord record, string machine, DeviceIdentity current)
        => MatchIdentity(record.Target, record.MachineFingerprint, machine, current);

    public static void MatchIdentity(DeviceIdentity expected, string expectedMachine, string machine, DeviceIdentity current)
    {
        if (expectedMachine != machine) throw new ProbeException("machine-mismatch");
        if (expected.ClassGuid != current.ClassGuid
            || !string.Equals(expected.InstanceId, current.InstanceId, StringComparison.Ordinal)
            || !string.Equals(expected.ContainerId, current.ContainerId, StringComparison.OrdinalIgnoreCase)
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

    public static MutationDecision Decide(OperationRecord record, string machine, DeviceSnapshot current, string command, bool applyEvidence)
    {
        MatchIdentity(record, machine, current.Identity);
        if (record.TargetKind == Targets.UsbMicrophone) return DecideAudio(record, current, command, applyEvidence);
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
        if (observation.AudioSourceStates!.HasUnknown || observation.ResolvedState is not ("original" or "proposed")
            || current.ResolvedNameSuffix != Targets.ResolvedSuffix(record)) throw new ProbeException("audio-name-source-changed");
        if (command == "apply")
        {
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
