namespace DeviceRadar.Tui;

// A reconnect is evidence only for the current Windows entry, not physical identity.
internal sealed class LocateSession(IEnumerable<DeviceSnapshot> baseline)
{
    private readonly Dictionary<string, DeviceSnapshot> known = baseline.Where(d => d.IsPresent)
        .ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> disconnected = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> previousPresent = baseline.Where(d => d.IsPresent)
        .Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public DeviceSnapshot? Match { get; private set; }
    public IReadOnlyList<string> DisconnectedNames => disconnected.Select(id => known[id].Name).ToArray();

    public void Update(IEnumerable<DeviceSnapshot> devices)
    {
        var present = devices.Where(d => d.IsPresent).ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);
        foreach (var id in previousPresent)
            if (!present.ContainsKey(id)) disconnected.Add(id);
        if (Match is not null && !present.ContainsKey(Match.DeviceId)) Match = null;
        Match ??= present.Values.FirstOrDefault(d => disconnected.Contains(d.DeviceId));
        disconnected.ExceptWith(present.Keys);
        previousPresent = present.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in present.Values) known[device.DeviceId] = device;
    }

    public static void SelfCheck()
    {
        var first = new DeviceSnapshot("first", "First device", "USB", DeviceClassification.External, true,
            new("first", null, IdentityStrength.Weak), null, [], [], []);
        var second = first with { DeviceId = "second", Name = "Second device" };
        var session = new LocateSession([first, second]);
        session.Update([first, second]);
        if (session.Match is not null) throw new InvalidOperationException("Locate matched without a reconnect");
        session.Update([first, second with { IsPresent = false }]);
        if (session.Match is not null || !session.DisconnectedNames.Contains(second.Name))
            throw new InvalidOperationException("Locate did not wait for the unselected device");
        session.Update([first, second]);
        if (session.Match?.DeviceId != second.DeviceId)
            throw new InvalidOperationException("Locate failed to match a reconnected device");
        session.Update([first]);
        if (session.Match is not null) throw new InvalidOperationException("Locate kept a disconnected result");
        session.Update([]);
        session.Update([first]);
        session.Update([first, second]);
        session.Update([second]);
        if (session.Match is not null) throw new InvalidOperationException("Locate reused a consumed reconnect event");
    }
}
