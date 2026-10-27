using DeviceRadar.Inventory.Native;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceRadar.Inventory;

public sealed class InventoryService
{
    public static void SelfCheck()
    {
        const string container = "11223344-5566-7788-99aa-bbccddeeff00";
        var nodes = new List<SetupApi.Node>
        {
            new(@"USB\VID_1234&PID_0001\unit-a", Guid.Empty, "A", ["USB\\VID_1234&PID_0001"], container, null, true, 2),
            new(@"HID\unit-a-function", Guid.Empty, "A function", [], container, @"USB\VID_1234&PID_0001\unit-a", true),
            new(@"USB\VID_1234&PID_0001\unit-b", Guid.Empty, "B", ["USB\\VID_1234&PID_0001"], container, null, true, 2)
        };
        var devices = Build(nodes, []);
        if (devices.Count != 2 || devices.Count(x => x.Functions.Length == 2) != 1
            || devices.Select(x => x.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2
            || devices.Any(x => x.Identity.Strength != IdentityStrength.Weak))
            throw new InvalidOperationException("Inventory aggregation or identity check failed");
        var composite = new SetupApi.Node(@"USB\VID_1234&PID_5678\unit", Guid.Empty, "USB Composite Device", [], container, null, true, 2, "Example USB Audio");
        var named = Build([composite], []).Single();
        if (named.Name != "Example USB Audio" || named.NameSource != DeviceNameSource.BusReported
            || named.NameSourceTargetId != composite.Id || named.Functions.Single().Name != "USB Composite Device")
            throw new InvalidOperationException("Inventory name source check failed");
    }

    public IReadOnlyList<DeviceSnapshot> Scan(bool includeOffline = true)
    {
        var nodes = SetupApi.Enumerate();
        var audio = CoreAudio.Enumerate();
        var devices = Build(nodes, audio);
        return (includeOffline ? devices : devices.Where(x => x.IsPresent)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    // A periodic complete rescan reconciles all changes, including Core Audio property changes.
    public IDisposable Watch(Action<IReadOnlyList<DeviceSnapshot>> onChanged, Action<Exception>? onError = null, TimeSpan? interval = null)
        => new Watcher(this, onChanged, onError, interval ?? TimeSpan.FromSeconds(3));

    private static List<DeviceSnapshot> Build(List<SetupApi.Node> nodes, List<CoreAudio.Endpoint> endpoints)
    {
        var byId = nodes.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // The local computer's container spans unrelated built-in devices and cannot identify a peripheral.
        var machineContainers = nodes.Where(n => n.Id.Equals("HTREE\\ROOT\\0", StringComparison.OrdinalIgnoreCase)
                || n.Id.Equals("ROOT\\SYSTEM\\0000", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.ContainerId).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ValidContainer(string? id) => id is not null && !machineContainers.Contains(id)
            && !id.Equals("00000000-0000-0000-ffff-ffffffffffff", StringComparison.OrdinalIgnoreCase);
        string Key(SetupApi.Node n)
        {
            var root = n;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
            while (ValidContainer(root.ContainerId) && root.ParentId is not null && visited.Add(root.ParentId)
                && byId.TryGetValue(root.ParentId, out var parent)
                && string.Equals(parent.ContainerId, root.ContainerId, StringComparison.OrdinalIgnoreCase)) root = parent;
            return "instance:" + root.Id.ToUpperInvariant();
        }
        var groups = nodes.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var containerGroupCounts = groups.Where(g => ValidContainer(g.First().ContainerId)).GroupBy(g => g.First().ContainerId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceSnapshot>();
        var usedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var members = group.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            var memberIds = members.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var root = members.OrderBy(n => n.ParentId is not null && memberIds.Contains(n.ParentId) ? 1 : 0)
                .ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase).First();
            var ancestry = new List<SetupApi.Node>(members);
            foreach (var n in members)
            {
                var parent = n.ParentId;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (parent is not null && seen.Add(parent) && byId.TryGetValue(parent, out var ancestor)) { ancestry.Add(ancestor); parent = ancestor.ParentId; }
            }
            var connection = Connection(ancestry);
            var classification = Classification(ancestry, connection);
            var functions = members.Select(n => new DeviceFunction(n.Id, n.Name, n.ClassGuid, n.HardwareIds, n.ContainerId, n.ParentId, n.Present)).ToArray();
            var locations = members.Select(n => new NamingLocation("pnp:" + n.Id, NamingLocationKind.Pnp, n.Name, n.Id, n.Present)).ToList();
            foreach (var n in members.Where(n => n.Id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) || IsBle(n.Id)))
            {
                var location = BluetoothNames.Read(n);
                if (location is not null) locations.Add(location);
            }
            if (ValidContainer(root.ContainerId) && containerGroupCounts[root.ContainerId!] == 1)
            foreach (var endpoint in endpoints.Where(e => string.Equals(e.ContainerId, root.ContainerId, StringComparison.OrdinalIgnoreCase)))
            {
                locations.Add(AudioLocation(endpoint)); usedEndpoints.Add(endpoint.Id);
            }
            // Instance IDs identify a Windows node, but do not by themselves prove the same physical unit across reconnection.
            var identity = new DeviceIdentity(root.Id, root.ContainerId, IdentityStrength.Weak);
            var (displayName, nameSource, nameTarget) = DisplayName(root, members);
            result.Add(new DeviceSnapshot(group.Key, displayName, connection, classification, members.Any(n => n.Present), identity, root.ContainerId,
                members.SelectMany(n => n.HardwareIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), functions, locations.ToArray())
                { NameSource = nameSource, NameSourceTargetId = nameTarget });
        }
        foreach (var endpoint in endpoints.Where(e => !usedEndpoints.Contains(e.Id)))
        {
            var id = "audio:" + endpoint.Id;
            result.Add(new DeviceSnapshot(id, endpoint.Name, "Audio", DeviceClassification.Unknown, endpoint.Present,
                new(endpoint.Id, endpoint.ContainerId, IdentityStrength.Weak), endpoint.ContainerId, [], [], [AudioLocation(endpoint)])
                { NameSource = DeviceNameSource.AudioEndpoint, NameSourceTargetId = endpoint.Id });
        }
        return result;
    }
    private static NamingLocation AudioLocation(CoreAudio.Endpoint endpoint) => new("audio:" + endpoint.Id,
        endpoint.Capture ? NamingLocationKind.AudioRecording : NamingLocationKind.AudioPlayback, endpoint.Name, endpoint.Id, endpoint.Present);
    private static (string Name, DeviceNameSource Source, string TargetId) DisplayName(SetupApi.Node root, SetupApi.Node[] members)
    {
        if (!IsGeneric(root.Name)) return (root.Name, DeviceNameSource.PnpSystemName, root.Id);
        if (!IsGeneric(root.BusReportedName)) return (root.BusReportedName!, DeviceNameSource.BusReported, root.Id);
        // If descendants disagree, keep the generic root name instead of guessing which function describes the device.
        var busNames = members.Where(n => n.Id != root.Id && !IsGeneric(n.BusReportedName))
            .GroupBy(n => n.BusReportedName!, StringComparer.OrdinalIgnoreCase).ToArray();
        if (busNames.Length == 1)
        {
            var chosen = busNames[0].First();
            return (chosen.BusReportedName!, DeviceNameSource.BusReported, chosen.Id);
        }
        return (root.Name, DeviceNameSource.PnpSystemName, root.Id);
    }
    private static bool IsGeneric(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim() switch
    {
        var n when n.Equals("USB Composite Device", StringComparison.OrdinalIgnoreCase)
            || n.Equals("USB Input Device", StringComparison.OrdinalIgnoreCase)
            || n.Equals("USB Audio Device", StringComparison.OrdinalIgnoreCase)
            || n.Equals("HID-compliant device", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Unknown device", StringComparison.OrdinalIgnoreCase)
            || n is "USB 复合设备" or "USB 输入设备" or "USB 音频设备" or "未知设备" => true,
        _ => false
    };
    private static string Connection(IEnumerable<SetupApi.Node> nodes)
    {
        var ids = nodes.Select(n => n.Id).ToArray();
        if (ids.Any(IsBle)) return "Bluetooth LE";
        if (ids.Any(s => s.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase))) return "Bluetooth";
        if (ids.Any(s => s.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))) return "USB";
        if (ids.Any(s => s.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))) return "PCI";
        if (ids.Any(s => s.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))) return "ACPI";
        return "Unknown";
    }
    private static DeviceClassification Classification(IEnumerable<SetupApi.Node> nodes, string connection)
    {
        if (connection is "Bluetooth" or "Bluetooth LE") return DeviceClassification.External;
        if (connection == "USB")
        {
            var policy = nodes.Where(n => n.Id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)).Select(n => n.RemovalPolicy).FirstOrDefault(p => p is not null);
            return policy is 2 or 3 ? DeviceClassification.External : policy == 1 ? DeviceClassification.Internal : DeviceClassification.Unknown;
        }
        if (connection is "PCI" or "ACPI") return DeviceClassification.Internal;
        if (nodes.Any(n => n.Id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || n.Id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase))) return DeviceClassification.Virtual;
        return DeviceClassification.Unknown;
    }
    private static bool IsBle(string id) => id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase);
    private sealed class Watcher : IDisposable
    {
        private readonly InventoryService service;
        private readonly Action<IReadOnlyList<DeviceSnapshot>> onChanged;
        private readonly Action<Exception>? onError;
        private readonly Timer timer;
        private readonly DeviceNotifications? notifications;
        private readonly TimeSpan interval;
        private byte[]? previous;
        private int running;
        internal Watcher(InventoryService service, Action<IReadOnlyList<DeviceSnapshot>> onChanged, Action<Exception>? onError, TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            this.service = service; this.onChanged = onChanged; this.onError = onError; this.interval = interval;
            timer = new Timer(Tick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try { notifications = new DeviceNotifications(() => timer.Change(TimeSpan.FromMilliseconds(300), this.interval)); }
            catch (Exception e) { onError?.Invoke(e); }
            timer.Change(TimeSpan.Zero, interval);
        }
        private void Tick(object? state)
        {
            if (Interlocked.Exchange(ref running, 1) != 0) return;
            try
            {
                var snapshot = service.Scan();
                var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot));
                if (previous is null || !hash.AsSpan().SequenceEqual(previous)) { previous = hash; onChanged(snapshot); }
            }
            catch (Exception e) { onError?.Invoke(e); }
            finally { Volatile.Write(ref running, 0); }
        }
        public void Dispose() { notifications?.Dispose(); timer.Dispose(); }
    }
}
