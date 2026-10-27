using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeviceRadar.Naming.Native;

namespace DeviceRadar.Identification;

public enum IdentificationConfidence { SystemProvided, PublicIdDescription }
public sealed record IdentificationCandidate(string Name, string Source, IdentificationConfidence Confidence);
public sealed record DatabaseUpdateResult(string Database, bool Success, string Message);

// The network updater accepts no device information and only fetches two fixed complete files.
public sealed partial class IdentificationService
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None
    }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string cacheDirectory;
    private Dictionary<string, IdEntry> usb;
    private Dictionary<string, IdEntry> pci;

    static IdentificationService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("DeviceRadar/1.0");

    public IdentificationService(string toolDirectory)
    {
        cacheDirectory = Path.Combine(toolDirectory, "cache", "identification");
        usb = Load("usb");
        pci = Load("pci");
    }

    public IReadOnlyList<IdentificationCandidate> Identify(IEnumerable<string> hardwareIds, IEnumerable<string> localNames)
    {
        var candidates = new List<IdentificationCandidate>();
        foreach (var name in localNames)
        {
            var safe = SafeText(name);
            if (safe.Length > 0) candidates.Add(new(safe, "Windows", IdentificationConfidence.SystemProvided));
        }
        foreach (var id in hardwareIds)
        {
            var match = UsbId().Match(id);
            var database = usb;
            var source = "USB ID Repository";
            if (!match.Success)
            {
                match = PciId().Match(id);
                database = pci;
                source = "PCI ID Repository";
            }
            if (!match.Success) continue;
            var key = (match.Groups[1].Value + ":" + match.Groups[2].Value).ToUpperInvariant();
            if (database.TryGetValue(key, out var item))
            {
                var name = SafeText(item.Vendor + " " + item.Device);
                if (name.Length > 0) candidates.Add(new(name, source, IdentificationConfidence.PublicIdDescription));
            }
        }
        return candidates.DistinctBy(c => (c.Name.ToUpperInvariant(), c.Source)).ToArray();
    }

    public static void SelfCheck()
    {
        // A missing directory guarantees that this check exercises the bundled snapshots.
        var service = new IdentificationService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var found = service.Identify([@"USB\VID_046D&PID_0082", @"PCI\VEN_8086&DEV_0007"], ["Local\u001b[31mName"]);
        if (!found.Any(c => c.Source == "USB ID Repository") || !found.Any(c => c.Source == "PCI ID Repository") ||
            found.Any(c => c.Name.Contains('\u001b'))) throw new InvalidOperationException("Identification self-check failed.");
        try { _ = Parse(Encoding.UTF8.GetBytes("broken\n"), "usb"); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Identification corrupt data check failed.");
    }

    public async Task<IReadOnlyList<DatabaseUpdateResult>> UpdateAsync(CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            var results = new List<DatabaseUpdateResult>();
            try
            {
                using var directories = NativeFiles.LockDirectories(cacheDirectory, create: true);
                using var processLock = NativeFiles.OpenLock(Path.Combine(cacheDirectory, ".update.lock"));
                foreach (var (name, url) in new[] {
                    ("usb", "https://usb-ids.gowdy.us/usb.ids"),
                    ("pci", "https://pci-ids.ucw.cz/v2.2/pci.ids") })
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidDataException("File exceeds size limit.");
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                        using var bytes = new MemoryStream();
                        var buffer = new byte[16384];
                        int count;
                        while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                        {
                            if (bytes.Length + count > MaxBytes) throw new InvalidDataException("File exceeds size limit.");
                            bytes.Write(buffer, 0, count);
                        }
                        var data = bytes.ToArray();
                        var parsed = Parse(data, name);
                        var path = Path.Combine(cacheDirectory, $"{name}-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.ids");
                        using (var file = NativeFiles.CreateNew(path)) WriteAndCheck(file, data);
                        // The hash file is the commit marker. A crash during either create leaves an ignored entry.
                        using (var marker = NativeFiles.CreateNew(path + ".sha256"))
                            WriteAndCheck(marker, Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(data))));
                        if (name == "usb") usb = parsed; else pci = parsed;
                        results.Add(new(name, true, "Updated"));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or ProbeException)
                    {
                        results.Add(new(name, false, "Update failed; existing data retained."));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProbeException)
            {
                return [new("usb", false, "Update failed; existing data retained."),
                    new("pci", false, "Update failed; existing data retained.")];
            }
            return results;
        }
        finally { UpdateLock.Release(); }
    }

    private Dictionary<string, IdEntry> Load(string name)
    {
        try
        {
            using var directories = NativeFiles.LockDirectories(cacheDirectory, create: false);
            foreach (var path in Directory.EnumerateFiles(cacheDirectory, name + "-*.ids")
                .Where(path => CacheName().IsMatch(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).Take(1000))
            {
                try
                {
                    using var file = NativeFiles.OpenRead(path);
                    using var marker = NativeFiles.OpenRead(path + ".sha256");
                    var data = ReadBounded(file);
                    var hash = ReadBounded(marker, 64);
                    if (hash.Length != 64 || Encoding.ASCII.GetString(hash) != Convert.ToHexString(SHA256.HashData(data))) continue;
                    return Parse(data, name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ProbeException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ProbeException) { }
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DeviceRadar.{name}.ids")
            ?? throw new InvalidOperationException("Bundled identification database is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Parse(bytes.ToArray(), name);
    }

    private static byte[] ReadBounded(FileStream file, int limit = MaxBytes)
    {
        if (file.Length <= 0 || file.Length > limit) throw new InvalidDataException("Invalid database size.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.Position != file.Length) throw new InvalidDataException("Database changed during read.");
        return bytes;
    }

    private static void WriteAndCheck(FileStream file, byte[] data)
    {
        file.Write(data);
        file.Flush(true);
        file.Position = 0;
        if (!ReadBounded(file).AsSpan().SequenceEqual(data)) throw new InvalidDataException("Cache verification failed.");
    }

    private static Dictionary<string, IdEntry> Parse(byte[] bytes, string name)
    {
        // Upstream USB includes a few non-UTF-8 bytes; replacement avoids rejecting the complete official file.
        if (bytes.Length < (name == "usb" ? 400_000 : 1_000_000) || bytes.Length > MaxBytes || bytes[^1] != (byte)'\n')
            throw new InvalidDataException("Invalid database size or ending.");
        var text = Encoding.UTF8.GetString(bytes);
        var result = new Dictionary<string, IdEntry>(StringComparer.OrdinalIgnoreCase);
        string? vendorId = null, vendor = null;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            // Class sections and other unindented sections end the vendor/device list.
            if (line[0] != '\t')
            {
                var m = IdLine().Match(line);
                if (!m.Success) { vendorId = vendor = null; continue; }
                vendorId = m.Groups[1].Value;
                vendor = SafeText(m.Groups[2].Value);
            }
            else if (vendorId is not null && vendor is not null && !line.StartsWith("\t\t"))
            {
                var m = IdLine().Match(line[1..]);
                if (m.Success)
                {
                    var device = SafeText(m.Groups[2].Value);
                    if (vendor.Length > 0 && device.Length > 0) result[vendorId + ":" + m.Groups[1].Value] = new(vendor, device);
                }
            }
        }
        if (result.Count < 1000) throw new InvalidDataException("Incomplete database.");
        return result;
    }

    public static string SafeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(Math.Min(value.Length, 256));
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length >= 256) break;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator) continue;
            builder.Append(rune.ToString());
        }
        return builder.ToString().Trim();
    }

    private sealed record IdEntry(string Vendor, string Device);
    [GeneratedRegex(@"^(?:USB|HID)\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbId();
    [GeneratedRegex(@"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PciId();
    [GeneratedRegex(@"^([0-9A-Fa-f]{4})\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IdLine();
    [GeneratedRegex(@"\A(?:usb|pci)-[0-9]{19}-[a-f0-9]{32}\.ids\z", RegexOptions.CultureInvariant)]
    private static partial Regex CacheName();
}
