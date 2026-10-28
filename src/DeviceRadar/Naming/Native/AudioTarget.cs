namespace DeviceRadar.Naming.Native;

internal sealed class AudioTarget : TargetSession
{
    private readonly DeviceCatalog catalog;
    private readonly string endpointId;
    private readonly int flow;
    public override IReadOnlyList<DeviceSnapshot> Candidates { get; }

    public AudioTarget(string endpointId, int flow, DeviceIdentity? bound = null)
    {
        if (!Runtime.IsSupportedWindows) throw new ProbeException("audio-platform-unsupported");
        this.endpointId = endpointId;
        this.flow = flow;
        var endpoint = CoreAudio.Open(endpointId, flow);
        catalog = new DeviceCatalog();
        try
        {
            var matches = catalog.Nodes.Where(n => string.Equals(n.InstanceId, endpoint.PnpInstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ProbeException(matches.Length == 0 ? "audio-pnp-binding-unavailable" : "audio-pnp-binding-ambiguous");
            var node = matches[0];
            var identity = node.Identity() with { EndpointId = endpoint.Id, EndpointContainerId = endpoint.ContainerId };
            ValidateBinding(identity);
            if (bound is not null && (!string.Equals(bound.InstanceId, identity.InstanceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(bound.EndpointId, identity.EndpointId, StringComparison.Ordinal)
                || !string.Equals(bound.EndpointContainerId, identity.EndpointContainerId, StringComparison.OrdinalIgnoreCase)))
                throw new ProbeException("audio-binding-changed");
            var policyValue = AudioPolicy.ReadDescription(endpoint.Id);
            if (!endpoint.Description.Same(policyValue)) throw new ProbeException("audio-description-read-mismatch");
            // The policy ABI does not establish lossless deletion of a previously absent value.
            if (!policyValue.Exists) throw new ProbeException("audio-policy-missing-description-not-restorable");
            var sources = new AudioNameSources(Format.Text(endpoint.Description, Format.RegistryString)!, endpoint.InterfaceName,
                ReadName(node, Native.DeviceDescription), ReadName(node, Native.FriendlyName), ReadName(node, Native.Name));
            var suffix = ResolvedSuffix(endpoint.Name, Format.Text(endpoint.Description, Format.RegistryString)!);
            Candidates = [new(identity, policyValue, endpoint.Name, suffix, sources)];
        }
        catch { catalog.Dispose(); throw; }
    }

    internal static void ValidateBinding(DeviceIdentity identity)
    {
        if (identity.ClassGuid == Guid.Empty || identity.InstanceId is not { Length: > 0 and <= 4096 }
            || identity.InstanceId.Any(char.IsControl) || identity.EndpointId is not { Length: > 0 and <= 4096 }
            || identity.EndpointId.Any(char.IsControl) || identity.HardwareIds is null || identity.Ancestors is null
            || !Guid.TryParseExact(identity.EndpointContainerId, "D", out var container) || container == Guid.Empty)
            throw new ProbeException("invalid-record-target");
    }

    internal static string ResolvedSuffix(string name, string description)
    {
        if (!name.StartsWith(description, StringComparison.Ordinal)) throw new ProbeException("audio-name-format-unsupported");
        return name[description.Length..];
    }

    private static string? ReadName(DeviceNode node, Native.PropertyKey property)
    {
        var value = Format.Text(node.Read(property));
        if (value is not null && (value.Length > 512 || value.Any(char.IsControl)))
            throw new ProbeException("invalid-audio-name-source");
        return value;
    }

    public override TargetWriteLease OpenWriter(RawValue value)
    {
        var before = Unique();
        if (!value.Exists || value.Type != Format.RegistryString)
            throw new ProbeException("audio-policy-missing-description-not-restorable");
        _ = Format.Text(value, Format.RegistryString);
        var current = CoreAudio.Open(endpointId, flow);
        using var currentCatalog = new DeviceCatalog();
        var matches = currentCatalog.Nodes.Where(n => string.Equals(n.InstanceId, current.PnpInstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ProbeException(matches.Length == 0 ? "audio-pnp-binding-unavailable" : "audio-pnp-binding-ambiguous");
        var currentIdentity = matches[0].Identity() with { EndpointId = current.Id, EndpointContainerId = current.ContainerId };
        if (!string.Equals(currentIdentity.EndpointContainerId, before.Identity.EndpointContainerId, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("audio-endpoint-container-changed");
        Safety.MatchIdentity(before.Identity, "", "", currentIdentity);
        if (!string.Equals(current.Id, before.Identity.EndpointId, StringComparison.Ordinal)
            || !current.Description.Same(before.FriendlyName)
            || !AudioPolicy.ReadDescription(endpointId).Same(before.FriendlyName))
            throw new ProbeException("value-changed-before-write");
        return AudioPolicy.OpenWriter(endpointId, before.FriendlyName, value);
    }
    public override void Dispose() => catalog.Dispose();
}
