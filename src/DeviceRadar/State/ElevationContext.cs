using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeviceRadar.Naming.Native;

namespace DeviceRadar;

// This context only restores navigation. It never contains an action or a path.
internal sealed record ElevationContext(int Version, DateTimeOffset CreatedAtUtc, string? DeviceId,
    string Search, bool External, bool Offline, bool Internal, bool Virtual)
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static string DirectoryPath => Path.Combine(Runtime.BaseDirectory, "data", "session");
    private static bool ValidToken(string? token) => token is not null && Regex.IsMatch(token, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);
    private bool Valid() => Version == 1 && CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-10) &&
        CreatedAtUtc < DateTimeOffset.UtcNow.AddMinutes(1) && DeviceId is null or { Length: <= 4096 } &&
        Search is { Length: <= 1024 };

    internal static string Save(ElevationContext context)
    {
        if (!context.Valid()) throw new InvalidOperationException("invalid-resume-context");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(context, Json);
        if (bytes.Length > 16384) throw new InvalidOperationException("invalid-resume-context");
        using var guard = NativeFiles.LockDirectories(DirectoryPath, create: true);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var stream = NativeFiles.CreateNew(Path.Combine(DirectoryPath, token + ".json"));
        stream.Write(bytes);
        stream.Flush(true);
        return token;
    }

    internal static ElevationContext? Consume(string? token)
    {
        if (!ValidToken(token)) return null;
        using var guard = NativeFiles.LockDirectories(DirectoryPath, create: false);
        var path = Path.Combine(DirectoryPath, token + ".json");
        try
        {
            byte[] bytes;
            using (var stream = NativeFiles.OpenRead(path))
            {
                if (stream.Length is <= 0 or > 16384) return null;
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }
            var context = JsonSerializer.Deserialize<ElevationContext>(bytes, Json);
            return context is not null && context.Valid() ? context : null;
        }
        finally { File.Delete(path); }
    }

    internal static void Discard(string token)
    {
        if (!ValidToken(token)) return;
        using var guard = NativeFiles.LockDirectories(DirectoryPath, create: false);
        File.Delete(Path.Combine(DirectoryPath, token + ".json"));
    }

    internal static void SelfCheck()
    {
        var context = new ElevationContext(1, DateTimeOffset.UtcNow, "device", "search", true, false, true, false);
        var copy = JsonSerializer.Deserialize<ElevationContext>(JsonSerializer.SerializeToUtf8Bytes(context, Json), Json);
        if (copy != context || !context.Valid() || (context with { Version = 2 }).Valid() ||
            (context with { Search = new string('x', 1025) }).Valid() ||
            ValidToken("../x") || !ValidToken(new string('a', 64)))
            throw new InvalidOperationException("Resume context validation failed");
    }
}
