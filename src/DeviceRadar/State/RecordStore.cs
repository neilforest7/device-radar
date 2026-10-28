using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeviceRadar.Naming.Native;

internal sealed partial class RecordStore
{
    private const int MaxFileBytes = 65536;
    public string Machine { get; }
    internal string Root { get; }

    public RecordStore()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var value = key?.GetValue("MachineGuid", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!Guid.TryParse(value, out var guid) || guid == Guid.Empty) throw new ProbeException("machine-identity-unavailable");
        Machine = Format.Hash(guid.ToString("D"));
        Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "v1", Machine));
        RequireLocalRoot(Root);
    }

    internal RecordStore(string localRoot, string machine)
    {
        Root = Path.GetFullPath(localRoot);
        RequireLocalRoot(Root);
        Machine = machine;
    }

    private static void RequireLocalRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new ProbeException("local-data-directory-required");
    }

    internal IReadOnlyList<(string Token, OperationRecord? Record, string Status, string? Reason)> History()
    {
        var items = new List<(string, OperationRecord?, string, string?)>();
        if (!Directory.Exists(Root)) return items;
        using var directories = NativeFiles.LockDirectories(Root, create: false);
        var files = Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"\A[a-f0-9]{32}\.json\z", RegexOptions.CultureInvariant))
            .Take(4097).ToArray();
        if (files.Length > 4096) throw new ProbeException("history-count-limit");
        foreach (var path in files)
        {
            var token = Path.GetFileName(path);
            try
            {
                using var lease = Load(token);
                var events = ReadEvents(lease);
                var latest = events.OrderByDescending(entry => entry.TimeUtc).FirstOrDefault();
                var pending = latest?.Stage == "intent";
                items.Add((token, lease.Record, pending ? "interrupted" : latest?.Status ?? "prepared", latest?.Reason));
            }
            catch (Exception error) { items.Add((token, null, "unavailable", NamingService.ErrorCode(error))); }
        }
        return items;
    }

    internal OperationEvent[] ReadEvents(RecordLease lease)
    {
        var directory = EventDirectory(lease.Record.OperationId);
        if (!Directory.Exists(directory)) return [];
        using var directories = NativeFiles.LockDirectories(directory, create: false);
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(513).ToArray();
        if (files.Length > 512) throw new ProbeException("event-count-limit");
        var entries = new List<OperationEvent>();
        foreach (var path in files)
        {
            var sealPath = path + ".sha256";
            if (!File.Exists(sealPath)) continue;
            using var seal = NativeFiles.OpenRead(sealPath);
            if (seal.Length < 64) continue;
            using var stream = NativeFiles.OpenRead(path);
            var bytes = ReadBounded(stream);
            ValidateSeal(bytes, ReadBounded(seal));
            var entry = Deserialize<OperationEvent>(bytes);
            if (entry.FormatVersion != Format.Version || entry.OperationId != lease.Record.OperationId || entry.RecordHash != lease.Hash
                || entry.TimeUtc == default || entry.Command is not ("apply" or "restore" or "verify")
                || entry.Stage is not ("intent" or "completed")) throw new ProbeException("event-record-mismatch");
            entries.Add(entry);
        }
        return entries.ToArray();
    }

    internal SettingsLoadResult LoadSettings()
    {
        if (!Directory.Exists(Root)) return new(new());
        using var directories = NativeFiles.LockDirectories(Root, create: false);
        var paths = Directory.EnumerateFiles(Root, "settings-*.json", SearchOption.TopDirectoryOnly).Take(1025)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (paths.Length > 1024) return new(new(), "settings-count-limit");
        if (paths.Length == 0) return new(new());
        try
        {
            var path = paths[0];
            if (!Regex.IsMatch(Path.GetFileName(path), @"\Asettings-[0-9]{19}-[a-f0-9]{32}\.json\z", RegexOptions.CultureInvariant))
                throw new ProbeException("invalid-settings-filename");
            using var file = NativeFiles.OpenRead(path);
            using var seal = NativeFiles.OpenRead(path + ".sha256");
            var bytes = ReadBounded(file);
            ValidateSeal(bytes, ReadBounded(seal));
            var settings = Deserialize<SettingsRecord>(bytes);
            if (settings.Version != 1 || settings.Machine != Machine || settings.CreatedAtUtc == default || settings.Settings is null)
                throw new ProbeException("invalid-settings");
            ValidateSettings(settings.Settings);
            return new(settings.Settings);
        }
        catch (Exception error) { return new(new(), NamingService.ErrorCode(error)); }
    }

    internal void SaveSettings(UserSettings settings)
    {
        ValidateSettings(settings);
        using var mutationLock = LockMutations();
        using var directories = NativeFiles.LockDirectories(Root, create: true);
        var time = DateTimeOffset.UtcNow;
        var filename = $"settings-{time.UtcTicks:D19}-{Guid.NewGuid():N}.json";
        using var committed = SaveSealed(Path.Combine(Root, filename), JsonSerializer.SerializeToUtf8Bytes(
            new SettingsRecord(1, Machine, time, settings), Format.Json));
    }

    private static void ValidateSettings(UserSettings settings)
    {
        if (settings is null || settings.Language is not ("system" or "zh" or "en")) throw new ProbeException("invalid-settings");
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
        => ReadEvents(lease).Any(entry => Safety.IsApplyEvidence(entry, lease.Record, lease.Hash));

    public bool HasClassicEmptyAliasIntent(RecordLease lease)
        => lease.Record.TargetKind == Targets.BluetoothClassic
            && ReadEvents(lease).Any(entry => Safety.IsClassicEmptyAliasIntent(entry, lease.Record, lease.Hash));

    private string EventDirectory(string operationId) => Path.Combine(Root, operationId + ".events");

    internal string Resolve(string supplied)
    {
        // Callers choose an opaque token, never a path or a native write address.
        if (supplied is null || !Regex.IsMatch(supplied, @"\A[a-f0-9]{32}\.json\z", RegexOptions.CultureInvariant))
            throw new ProbeException("invalid-record-token");
        return Path.Combine(Root, supplied);
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

internal sealed record SettingsRecord(int Version, string Machine, DateTimeOffset CreatedAtUtc, UserSettings Settings);
