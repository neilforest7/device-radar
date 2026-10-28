using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceRadar.Identification;
using DeviceRadar.Inventory;
using DeviceRadar.Tui;

namespace DeviceRadar;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (!Runtime.IsSupportedWindows) { Output(new { status = "error", error = "Windows 10 build 19045 or later, x64, is required." }); return 1; }
            if (args.Length == 0) return RadarApp.Run();
            if (args is ["--resume", var resumeToken]) return RadarApp.Run(resumeToken);
            if (args is ["--file-check"])
            {
                var count = NamingService.FileCheck();
                Output(new { status = "passed", fileChecks = count, systemWrites = 0 });
                return 0;
            }
            if (args is ["--self-check"])
            {
                InventoryService.SelfCheck();
                IdentificationService.SelfCheck();
                var count = NamingService.SelfCheck();
                RadarApp.SelfCheck();
                Output(new { status = "passed", namingChecks = count, systemWrites = 0 });
                return 0;
            }
            if (args is ["--snapshot-dir", var dir]) return RadarApp.WriteSnapshots(dir);
            if (args is ["inventory"])
            {
                Output(new { devices = Scan().Select(DeviceView).ToArray() });
                return 0;
            }
            if (args is ["capabilities", "--device", var capabilityDevice])
            {
                var device = FindDevice(capabilityDevice);
                var capabilities = new NamingService().Capabilities(device);
                Output(new { device = Token(device.DeviceId), capabilities = capabilities.Select(c => new { location = Token(c.LocationId), c.Location.Kind, c.CurrentName, c.IsSupported, c.CanWrite, c.Reason, c.RequiresAdministrator, c.Evidence, c.SettingsUri }).ToArray() });
                return 0;
            }
            if (args is ["history", "--device", var historyDevice])
            {
                var history = new NamingService().History(FindDevice(historyDevice));
                Output(new { history = history.Select(h => new { h.RecordToken, h.CreatedAtUtc, location = Token(h.LocationId), h.OriginalName, h.ProposedName, h.Status, h.Reason }).ToArray() });
                return 0;
            }
            if (args is ["prepare", "--device", var prepareDevice, "--location", var prepareLocation, "--name", var name])
            {
                var device = FindDevice(prepareDevice);
                var matches = device.Locations.Where(l => Token(l.Id) == prepareLocation).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Location token is invalid or ambiguous.");
                var naming = new NamingService();
                var preview = naming.Preview(device, [new NameEdit(matches[0].Id, name)]);
                var prepared = naming.Prepare(preview);
                Output(new { status = "prepared", prepared.RecordTokens });
                return 0;
            }
            if (args is ["restore-preview", "--record", var previewRecord])
            {
                var preview = new NamingService().PreviewRestore(previewRecord);
                Output(new { entries = preview.Entries.Select(e => new { location = Token(e.LocationId), e.Kind, e.OriginalName, e.NewName, e.ExpectedDisplayName, e.Method, e.Evidence }).ToArray() });
                return 0;
            }
            if (args is ["restore-history", "--record", var historyRecord])
            {
                var naming = new NamingService();
                return OutputResult(naming.Restore(naming.PreviewRestore(historyRecord)));
            }
            if (args is [var command, "--record", var record] && command is "apply" or "verify" or "restore")
            {
                var naming = new NamingService();
                var result = command switch { "apply" => naming.Apply(record), "restore" => naming.Restore(record), _ => naming.Verify(record) };
                return OutputResult(result);
            }
            if (args is ["update-identification"])
            {
                var results = new IdentificationService(Runtime.BaseDirectory).UpdateAsync().GetAwaiter().GetResult();
                Output(new { results });
                return results.All(r => r.Success) ? 0 : 1;
            }
            Output(new { status = "usage", commands = new[] { "inventory", "capabilities --device <token>", "history --device <token>", "prepare --device <token> --location <token> --name <name>", "apply --record <token>", "verify --record <token>", "restore --record <token>", "restore-preview --record <token>", "restore-history --record <token>", "update-identification", "--self-check", "--snapshot-dir <directory>" } });
            return 2;
        }
        catch (Exception error)
        {
            var message = error is UnauthorizedAccessException ? "Portable data directory is not writable. Move the application to a writable directory." : NamingService.ErrorCode(error);
            Output(new { status = "error", error = message });
            return 1;
        }
    }

    private static int OutputResult(NamingResult result)
    {
        Output(new { result.Status, result.Succeeded, locations = result.Locations.Select(l => new { l.RecordToken, location = Token(l.LocationId), l.Command, l.Status, l.WriteAttempted, l.ApiSucceeded, l.ObservedState, l.ResolvedState, l.SynchronizedState, l.ObservedName, l.Reason, l.NativeError, l.EventToken, l.InterfaceVerification, l.ReconnectVerification, l.RebootVerification }).ToArray() });
        return result.Succeeded ? 0 : 1;
    }
    private static IReadOnlyList<DeviceSnapshot> Scan() => new InventoryService().Scan();
    private static DeviceSnapshot FindDevice(string token)
    {
        var matches = Scan().Where(d => Token(d.DeviceId) == token).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Device token is invalid or ambiguous.");
    }
    private static object DeviceView(DeviceSnapshot device) => new { device = Token(device.DeviceId), device.Name, device.Connection, device.Classification, device.IsPresent,
        locations = device.Locations.Select(l => new { location = Token(l.Id), l.Kind, l.Name, l.IsPresent }).ToArray() };
    private static string Token(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).ToLowerInvariant();
    private static void Output(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
