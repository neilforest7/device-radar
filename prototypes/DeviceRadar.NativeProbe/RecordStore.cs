using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeviceRadar.NativeProbe;

internal sealed class RecordStore
{
    private const int MaxFileBytes = 65536;
    public string Machine { get; }
    private string Root { get; }

    public RecordStore()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var value = key?.GetValue("MachineGuid", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!Guid.TryParse(value, out var guid) || guid == Guid.Empty) throw new ProbeException("machine-identity-unavailable");
        Machine = Format.Hash(guid.ToString("D"));
        Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", Machine));
    }

    public string SaveRecoveryPoint(OperationRecord record)
    {
        Safety.ValidateRecord(record);
        if (record.MachineFingerprint != Machine) throw new ProbeException("machine-mismatch");
        using var directories = NativeFiles.LockDirectories(Root, create: true);
        var token = record.OperationId + ".json";
        using var committed = SaveSealed(Path.Combine(Root, token), JsonSerializer.SerializeToUtf8Bytes(record, Format.Json));
        return token;
    }

    public string SaveAudioSnapshot(AudioPropertiesSnapshot snapshot)
    {
        if (snapshot.FormatVersion != Format.Version || snapshot.MachineFingerprint != Machine
            || !Guid.TryParseExact(snapshot.OperationId, "N", out _)) throw new ProbeException("invalid-snapshot");
        Targets.ValidateBinding(Targets.UsbMicrophone, snapshot.Target);
        using var directories = NativeFiles.LockDirectories(Root, create: true);
        var token = snapshot.OperationId + ".audio-snapshot.json";
        using var committed = SaveSealed(Path.Combine(Root, token), JsonSerializer.SerializeToUtf8Bytes(snapshot, Format.Json));
        return token;
    }

    public string SaveBleSnapshot(BlePropertiesSnapshot snapshot)
    {
        if (snapshot.FormatVersion != Format.Version || snapshot.MachineFingerprint != Machine
            || !Guid.TryParseExact(snapshot.OperationId, "N", out _)
            || snapshot.Properties.Length != 3
            || !snapshot.Properties.Select(entry => entry.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "FriendlyName", "LEName", "Name" }))
            throw new ProbeException("invalid-snapshot");
        Targets.ValidateBinding(Targets.BleLeNameCache, snapshot.Target);
        Safety.ValidateOriginal(snapshot.PnpFriendlyName);
        foreach (var entry in snapshot.Properties)
        {
            if (entry.Name == "LEName") BleTarget.ValidateLeName(entry.Value);
            else Safety.ValidateOriginal(entry.Value, Format.RegistryBinary);
        }
        using var directories = NativeFiles.LockDirectories(Root, create: true);
        var token = snapshot.OperationId + ".ble-snapshot.json";
        using var committed = SaveSealed(Path.Combine(Root, token), JsonSerializer.SerializeToUtf8Bytes(snapshot, Format.Json));
        return token;
    }

    public AudioSnapshotLease LoadAudioSnapshot(string suppliedPath)
    {
        var path = Resolve(suppliedPath, audioSnapshot: true);
        var directories = NativeFiles.LockDirectories(Root, create: false);
        FileStream? stream = null;
        FileStream? seal = null;
        try
        {
            stream = NativeFiles.OpenRead(path);
            var bytes = ReadBounded(stream);
            seal = NativeFiles.OpenRead(path + ".sha256");
            ValidateSeal(bytes, ReadBounded(seal));
            var snapshot = Deserialize<AudioPropertiesSnapshot>(bytes);
            if (snapshot.FormatVersion != Format.Version || !Guid.TryParseExact(snapshot.OperationId, "N", out _)
                || snapshot.OperationId + ".audio-snapshot.json" != Path.GetFileName(path)
                || snapshot.CreatedAtUtc == default || snapshot.Target is null
                || snapshot.Properties is not { Length: > 0 and <= 128 }
                || snapshot.Properties.Any(entry => entry is null || entry.Name is null || entry.Name.Length > 256
                    || entry.Name.Any(char.IsControl) || entry.Value is null || !entry.Value.Exists || entry.Value.Bytes is null
                    || entry.Value.Bytes.Length > Format.MaxPropertyBytes)
                || snapshot.Properties.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Properties.Length)
                throw new ProbeException("invalid-snapshot");
            if (snapshot.MachineFingerprint != Machine) throw new ProbeException("machine-mismatch");
            Targets.ValidateBinding(Targets.UsbMicrophone, snapshot.Target);
            return new(stream, seal, snapshot, Path.GetFileName(path), directories);
        }
        catch { stream?.Dispose(); seal?.Dispose(); directories.Dispose(); throw; }
    }

    public RecordLease Load(string suppliedPath)
    {
        var path = Resolve(suppliedPath);
        var directories = NativeFiles.LockDirectories(Root, create: false);
        FileStream? stream = null;
        FileStream? seal = null;
        try
        {
            stream = NativeFiles.OpenRead(path);
            var bytes = ReadBounded(stream);
            seal = NativeFiles.OpenRead(path + ".sha256");
            ValidateSeal(bytes, ReadBounded(seal));
            var record = Deserialize<OperationRecord>(bytes);
            Safety.ValidateRecord(record);
            if (record.OperationId + ".json" != Path.GetFileName(path)) throw new ProbeException("record-filename-mismatch");
            if (record.MachineFingerprint != Machine) throw new ProbeException("machine-mismatch");
            return new(stream, seal, record, Format.Hash(bytes), Path.GetFileName(path), directories);
        }
        catch { stream?.Dispose(); seal?.Dispose(); directories.Dispose(); throw; }
    }

    public MutationLock LockMutations()
    {
        var directories = NativeFiles.LockDirectories(Root, create: true);
        var path = Path.Combine(Root, ".mutation.lock");
        try { return new(directories, NativeFiles.OpenLock(path)); }
        catch { directories.Dispose(); throw; }
    }

    public string AppendEvent(OperationEvent value)
    {
        using var committed = HoldEvent(value);
        return committed.Token;
    }

    public EventLease HoldEvent(OperationEvent value)
    {
        if (!Guid.TryParseExact(value.OperationId, "N", out _)) throw new ProbeException("invalid-operation-id");
        var directory = EventDirectory(value.OperationId);
        var directories = NativeFiles.LockDirectories(directory, create: true);
        try
        {
            var filename = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-" + Guid.NewGuid().ToString("N") + ".json";
            var file = SaveSealed(Path.Combine(directory, filename), JsonSerializer.SerializeToUtf8Bytes(value, Format.Json));
            return new(file, directories, value.OperationId + ".events/" + filename);
        }
        catch { directories.Dispose(); throw; }
    }

    public bool HasApplyEvidence(RecordLease lease)
    {
        var directory = EventDirectory(lease.Record.OperationId);
        if (!Directory.Exists(directory)) return false;
        using var directories = NativeFiles.LockDirectories(directory, create: false);
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(513).ToArray();
        if (files.Length > 512) throw new ProbeException("event-count-limit");
        var found = false;
        foreach (var path in files)
        {
            var sealPath = path + ".sha256";
            if (!File.Exists(sealPath)) continue; // A JSON file is not committed until its complete seal exists.
            using var seal = NativeFiles.OpenRead(sealPath);
            if (seal.Length < 64) continue; // Interrupted seal creation is not apply evidence.
            using var stream = NativeFiles.OpenRead(path);
            var bytes = ReadBounded(stream);
            ValidateSeal(bytes, ReadBounded(seal));
            var entry = Deserialize<OperationEvent>(bytes);
            if (entry.FormatVersion != Format.Version || entry.OperationId != lease.Record.OperationId || entry.RecordHash != lease.Hash)
                throw new ProbeException("event-record-mismatch");
            if (Safety.IsApplyEvidence(entry, lease.Record, lease.Hash)) found = true;
        }
        return found;
    }

    private string EventDirectory(string operationId) => Path.Combine(Root, operationId + ".events");

    private string Resolve(string supplied, bool audioSnapshot = false)
    {
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 4096) throw new ProbeException("invalid-record-path");
        var path = Path.GetFullPath(supplied.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(supplied)
            ? Path.Combine(Root, supplied) : supplied);
        if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(path), audioSnapshot ? @"\A[a-f0-9]{32}\.audio-snapshot\.json\z" : @"\A[a-f0-9]{32}\.json\z", RegexOptions.CultureInvariant))
            throw new ProbeException("record-path-outside-data");
        return path;
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        if (stream.Length is <= 0 or > MaxFileBytes) throw new ProbeException("record-size-limit");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length) throw new ProbeException("file-changed-during-read");
        return bytes;
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            RejectDuplicateKeys(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Format.Json) ?? throw new ProbeException("invalid-record-json");
        }
        catch (JsonException) { throw new ProbeException("invalid-record-json"); }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ProbeException("duplicate-json-field");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static void ValidateSeal(byte[] bytes, byte[] seal)
    {
        if (seal.Length != 64 || seal.Any(c => c is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f'))
            || Encoding.ASCII.GetString(seal) != Format.Hash(bytes))
            throw new ProbeException("invalid-file-seal");
    }

    private static SealedFile SaveSealed(string path, byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new ProbeException("record-size-limit");
        // Immutable create-only files plus a flushed seal form the commit point. An interrupted pair is never accepted.
        // This avoids namespace-changing rename operations while directory handles deny write/delete sharing.
        var stream = NativeFiles.CreateNew(path);
        FileStream? seal = null;
        try
        {
            WriteAndCheck(stream, bytes);
            seal = NativeFiles.CreateNew(path + ".sha256");
            WriteAndCheck(seal, Encoding.ASCII.GetBytes(Format.Hash(bytes)));
            return new(stream, seal);
        }
        catch { stream.Dispose(); seal?.Dispose(); throw; }
    }

    private static void WriteAndCheck(FileStream stream, byte[] bytes)
    {
        stream.Write(bytes);
        stream.Flush(true);
        stream.Position = 0;
        if (!ReadBounded(stream).AsSpan().SequenceEqual(bytes)) throw new ProbeException("persistence-verification-failed");
    }
}

internal sealed class AudioSnapshotLease(FileStream stream, FileStream seal, AudioPropertiesSnapshot snapshot, string token, DirectoryGuard directories) : IDisposable
{
    public AudioPropertiesSnapshot Snapshot { get; } = snapshot;
    public string Token { get; } = token;
    public void Dispose() { stream.Dispose(); seal.Dispose(); directories.Dispose(); }
}

internal sealed class RecordLease(FileStream stream, FileStream seal, OperationRecord record, string hash, string token, DirectoryGuard directories) : IDisposable
{
    public OperationRecord Record { get; } = record;
    public string Hash { get; } = hash;
    public string Token { get; } = token;
    public void Dispose() { stream.Dispose(); seal.Dispose(); directories.Dispose(); }
}

internal sealed class SealedFile(FileStream json, FileStream seal) : IDisposable
{
    public void Dispose() { json.Dispose(); seal.Dispose(); }
}

internal sealed class EventLease(SealedFile file, DirectoryGuard directories, string token) : IDisposable
{
    public string Token { get; } = token;
    public void Dispose() { file.Dispose(); directories.Dispose(); }
}
