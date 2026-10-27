using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeviceRadar.Naming.Native;

internal sealed record ValidationProfile(int Version, string Machine, DateTimeOffset CreatedAtUtc, string Kind,
    DeviceIdentity Target, string OsVersion, string EvidenceRecordHash, int EvidenceVersion, string[] BinaryHashes);
internal sealed record LegacyMouseIdentity(string InstanceId, Guid ClassGuid, string[] HardwareIds, string? ContainerId);
internal sealed record LegacyMouseEvidence(int FormatVersion, string OperationId, string MachineFingerprint,
    DateTimeOffset CreatedAtUtc, string Status, string OsVersion, LegacyMouseIdentity Target, RawValue Original,
    string ProposedName, RawValue Proposed);
internal sealed record ValidationEvidence(string Kind, DeviceIdentity Target, string Hash, int Version, RawValue Original);

internal sealed partial class RecordStore
{
    internal void RequireValidation(string kind, DeviceIdentity identity)
    {
        if (!Directory.Exists(Root)) throw new ProbeException("unverified-device-not-validated");
        using var directories = NativeFiles.LockDirectories(Root, create: false);
        var prefix = "validation-" + Format.TargetHash(identity.InstanceId) + "-";
        var paths = Directory.EnumerateFiles(Root, prefix + "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(129).ToArray();
        if (paths.Length > 128) throw new ProbeException("validation-profile-count-limit");
        if (paths.Length == 0) throw new ProbeException("unverified-device-not-validated");
        var path = paths[0];
        if (!Regex.IsMatch(Path.GetFileName(path), @"\Avalidation-[a-f0-9]{12}-[0-9]{19}-[a-f0-9]{32}\.json\z", RegexOptions.CultureInvariant))
            throw new ProbeException("invalid-validation-profile");
        using var file = NativeFiles.OpenRead(path);
        using var seal = NativeFiles.OpenRead(path + ".sha256");
        var bytes = ReadBounded(file);
        ValidateSeal(bytes, ReadBounded(seal));
        var profile = Deserialize<ValidationProfile>(bytes);
        if (profile.Version != 1 || profile.Machine != Machine || profile.Kind != kind || profile.Target is null
            || profile.CreatedAtUtc == default || profile.OsVersion != Runtime.SystemDescription
            || profile.EvidenceVersion is not (1 or 3) || profile.EvidenceRecordHash is not { Length: 64 }
            || profile.EvidenceRecordHash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))
            || profile.BinaryHashes is not { Length: 2 }) throw new ProbeException("validation-profile-mismatch");
        Targets.ValidateBinding(kind, profile.Target);
        Safety.MatchIdentity(profile.Target, profile.Machine, Machine, identity);
        if (!profile.BinaryHashes.SequenceEqual(BinaryHashes(kind), StringComparer.Ordinal)) throw new ProbeException("validated-driver-changed");
    }

    internal string SaveValidation(ValidationEvidence evidence, DeviceIdentity current)
    {
        Targets.ValidateBinding(evidence.Kind, current);
        Safety.MatchIdentity(evidence.Target, Machine, Machine, current);
        using var operationLock = LockMutations();
        var time = DateTimeOffset.UtcNow;
        var profile = new ValidationProfile(1, Machine, time, evidence.Kind, current, Runtime.SystemDescription,
            evidence.Hash, evidence.Version, BinaryHashes(evidence.Kind));
        var token = $"validation-{Format.TargetHash(current.InstanceId)}-{time.UtcTicks:D19}-{Guid.NewGuid():N}.json";
        using var committed = SaveSealed(Path.Combine(Root, token), JsonSerializer.SerializeToUtf8Bytes(profile, Format.Json));
        return token;
    }

    internal ValidationEvidence ReadValidationEvidence(string directory, string token)
    {
        _ = Resolve(token); // Enforces the same opaque-token grammar without opening the product store.
        directory = Path.GetFullPath(directory);
        RequireLocalRoot(directory);
        using var directories = NativeFiles.LockDirectories(directory, create: false);
        using var file = NativeFiles.OpenRead(Path.Combine(directory, token));
        using var seal = NativeFiles.OpenRead(Path.Combine(directory, token) + ".sha256");
        var bytes = ReadBounded(file);
        ValidateSeal(bytes, ReadBounded(seal));
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateKeys(document.RootElement);
        var version = document.RootElement.GetProperty("formatVersion").GetInt32();
        OperationRecord record;
        if (version == 1)
        {
            var legacy = Deserialize<LegacyMouseEvidence>(bytes);
            if (legacy.Target is null) throw new ProbeException("invalid-legacy-evidence");
            var identity = new DeviceIdentity(legacy.Target.InstanceId, legacy.Target.ClassGuid,
                legacy.Target.HardwareIds, legacy.Target.ContainerId, []);
            record = new(Format.Version, legacy.OperationId, legacy.MachineFingerprint, legacy.CreatedAtUtc,
                legacy.Status, legacy.OsVersion, Targets.Mouse, identity, legacy.Original,
                "HID-compliant mouse", legacy.ProposedName, legacy.Proposed);
        }
        else if (version == 3) record = Deserialize<OperationRecord>(bytes) with { FormatVersion = Format.Version };
        else throw new ProbeException("unsupported-validation-evidence");
        Safety.ValidateRecord(record);
        if (record.OperationId + ".json" != token || record.MachineFingerprint != Machine
            || !record.OsVersion.Contains("10.0.22631", StringComparison.Ordinal)) throw new ProbeException("validation-evidence-mismatch");
        var hash = Format.Hash(bytes);
        var eventDirectory = Path.Combine(directory, record.OperationId + ".events");
        using var eventDirectories = NativeFiles.LockDirectories(eventDirectory, create: false);
        var events = Directory.EnumerateFiles(eventDirectory, "*.json", SearchOption.TopDirectoryOnly).Take(513).ToArray();
        if (events.Length > 512) throw new ProbeException("event-count-limit");
        var applied = false;
        var restored = false;
        foreach (var path in events)
        {
            using var eventFile = NativeFiles.OpenRead(path);
            using var eventSeal = NativeFiles.OpenRead(path + ".sha256");
            var eventBytes = ReadBounded(eventFile);
            ValidateSeal(eventBytes, ReadBounded(eventSeal));
            var entry = Deserialize<OperationEvent>(eventBytes);
            if (entry.FormatVersion != version || entry.OperationId != record.OperationId || entry.RecordHash != hash)
                throw new ProbeException("validation-evidence-event-mismatch");
            if (entry.Stage != "completed" || !entry.WriteAttempted || entry.ApiSucceeded != true || entry.ObservedValue is null) continue;
            var audio = record.TargetKind == Targets.UsbMicrophone;
            applied |= entry.Command == "apply" && entry.Status == "applied" && entry.ObservedState == "proposed"
                && entry.ObservedValue.Same(record.Proposed) && (!audio || entry.SynchronizedState == "proposed");
            restored |= entry.Command == "restore" && entry.Status == "restored" && entry.ObservedState == "original"
                && entry.ObservedValue.Same(record.Original) && (!audio || entry.SynchronizedState == "original");
        }
        if (!applied || !restored) throw new ProbeException("complete-reversible-evidence-required");
        return new(record.TargetKind, record.Target, hash, version, Clone(record.Original));
    }

    private static RawValue Clone(RawValue value) => new(value.Exists, value.Type, value.Bytes.ToArray());
    private static string[] BinaryHashes(string kind)
    {
        Targets.RequireWritable(kind);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // File names are compiled in; profiles cannot select a file, driver or module to load.
        var names = kind == Targets.Mouse
            ? new[] { @"drivers\mouhid.sys", @"drivers\mouclass.sys" }
            : new[] { "AudioSes.dll", @"drivers\usbaudio.sys" };
        return names.Select(name =>
        {
            using var file = File.OpenRead(Path.Combine(system, name));
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }).ToArray();
    }
}
