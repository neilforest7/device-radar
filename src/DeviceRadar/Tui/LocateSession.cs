namespace DeviceRadar.Tui;

// A reconnect identifies only the current Windows entry, not a physical unit.
internal sealed class LocateSession(IEnumerable<DeviceSnapshot> baseline)
{
    private readonly Dictionary<string, DeviceSnapshot> known = baseline.Where(d => d.IsPresent)
        .ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> disconnected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceSnapshot> candidates = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> previousPresent = baseline.Where(d => d.IsPresent)
        .Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public DeviceSnapshot? Match => candidates.Count == 1 ? candidates.Values.Single() : null;
    public IReadOnlyList<DeviceSnapshot> Candidates => candidates.Values.ToArray();
    public IReadOnlyList<string> DisconnectedNames => disconnected.Select(id => known[id].Name).ToArray();

    public void Update(IEnumerable<DeviceSnapshot> devices)
    {
        var present = devices.Where(d => d.IsPresent).ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
        foreach (var id in previousPresent)
            if (!present.ContainsKey(id)) { disconnected.Add(id); candidates.Remove(id); }
        foreach (var device in present.Values)
        {
            if (disconnected.Remove(device.DeviceId)) candidates[device.DeviceId] = device;
            else if (candidates.ContainsKey(device.DeviceId)) candidates[device.DeviceId] = device;
            known[device.DeviceId] = device;
        }
        previousPresent = present.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void SelfCheck()
    {
        var first = new DeviceSnapshot("first", "First device", "USB", DeviceClassification.External, true,
            new("first", null, IdentityStrength.Weak), null, [], [], []);
        var second = first with { DeviceId = "second", Name = "Second device" };
        var session = new LocateSession([first, second]);
        session.Update([first, second]);
        if (session.Match is not null) throw new InvalidOperationException("Locate matched without a reconnect");
        session.Update([]);
        if (session.DisconnectedNames.Count != 2) throw new InvalidOperationException("Locate did not wait for reconnect");
        session.Update([first, second]);
        if (session.Match is not null || session.Candidates.Count != 2)
            throw new InvalidOperationException("Locate accepted ambiguous reconnects");
        session.Update([second]);
        if (session.Match?.DeviceId != second.DeviceId) throw new InvalidOperationException("Locate retained an offline candidate");
        session.Update([]);
        if (session.Match is not null) throw new InvalidOperationException("Locate kept an offline result");
        session.Update([first]);
        session.Update([first, second]);
        session.Update([second]);
        if (session.Match?.DeviceId != second.DeviceId) throw new InvalidOperationException("Locate reused a consumed reconnect event");
        var consumed = new LocateSession([first, second]);
        consumed.Update([second]);
        consumed.Update([first, second]);
        consumed.Update([second]);
        if (consumed.Match is not null) throw new InvalidOperationException("Locate reused a consumed reconnect event");
    }
}
