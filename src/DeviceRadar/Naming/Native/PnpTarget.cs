using System.Security.Principal;

namespace DeviceRadar.Naming.Native;

internal sealed class PnpTarget : TargetSession
{
    private readonly SelectedDevice selected;
    private readonly DeviceSnapshot initial;
    public override IReadOnlyList<DeviceSnapshot> Candidates { get; }

    public PnpTarget(string instanceId, DeviceIdentity? bound = null)
    {
        ValidateInstanceId(instanceId);
        if (bound is not null)
        {
            ValidateBinding(bound);
            if (!string.Equals(instanceId, bound.InstanceId, StringComparison.OrdinalIgnoreCase))
                throw new ProbeException("target-mismatch");
        }

        selected = new SelectedDevice(instanceId);
        try
        {
            initial = selected.Node.Snapshot();
            ValidateBinding(initial.Identity);
            if (bound is not null) MatchBinding(bound, initial.Identity);
            Candidates = [initial];
        }
        catch { selected.Dispose(); throw; }
    }

    public override TargetWriteLease OpenWriter(RawValue value)
    {
        RequireAdministrator();
        Safety.ValidateOriginal(value);
        ValidateWriteBaseline();
        return new(() =>
        {
            ValidateWriteBaseline();
            selected.Node.SetFriendlyName(value);
        });
    }

    private void ValidateWriteBaseline()
    {
        selected.EnsurePresent();
        var current = selected.Node.Snapshot();
        MatchBinding(initial.Identity, current.Identity);
        if (!current.FriendlyName.Same(initial.FriendlyName)) throw new ProbeException("value-changed-before-write");
    }

    public override void Dispose() => selected.Dispose();

    internal static void ValidateBinding(DeviceIdentity identity)
    {
        ValidateInstanceId(identity.InstanceId);
        if (identity.ClassGuid == Targets.BluetoothClass || identity.ClassGuid == Targets.AudioEndpointClass)
            throw new ProbeException("managed-pnp-target");
        if (identity.ClassGuid == Guid.Empty || IsBluetoothManagedId(identity.InstanceId)
            || identity.HardwareIds is not { Length: <= 32 }
            || identity.HardwareIds.Any(id => id is not { Length: > 0 and <= 4096 } || id.Any(char.IsControl))
            || (identity.ContainerId is not null
                && (!Guid.TryParseExact(identity.ContainerId, "D", out var container) || container == Guid.Empty))
            || identity.Ancestors is not { Length: 0 })
            throw new ProbeException("invalid-record-target");
    }

    private static void ValidateInstanceId(string instanceId)
    {
        if (instanceId is not { Length: > 0 and <= 4096 } || instanceId.Any(char.IsControl))
            throw new ProbeException("invalid-instance-id");
        if (IsBluetoothManagedId(instanceId)) throw new ProbeException("managed-pnp-target");
    }

    private static bool IsBluetoothManagedId(string instanceId)
        => instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)
            || instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);

    private static void MatchBinding(DeviceIdentity expected, DeviceIdentity current)
    {
        if (expected.ClassGuid != current.ClassGuid
            || !string.Equals(expected.InstanceId, current.InstanceId, StringComparison.Ordinal)
            || !string.Equals(expected.ContainerId, current.ContainerId, StringComparison.OrdinalIgnoreCase)
            || !expected.HardwareIds.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                current.HardwareIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new ProbeException("target-mismatch");
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new ProbeException("administrator-required");
    }

    internal static int SelfCheck()
    {
        var checks = 0;
        void Accept(DeviceIdentity identity)
        {
            ValidateBinding(identity);
            checks++;
        }
        void Refuse(DeviceIdentity identity)
        {
            try { ValidateBinding(identity); }
            catch (ProbeException error) when (error.Code is "invalid-record-target" or "managed-pnp-target") { checks++; return; }
            throw new InvalidOperationException("pnp-self-check-failed");
        }

        var usb = new DeviceIdentity("USB\\VID_1234&PID_5678\\DEVICE", new Guid("e6eb0d7f-9f52-4651-ae39-76367b878d8f"),
            ["USB\\VID_1234&PID_5678"], null, []);
        var system = new DeviceIdentity("ROOT\\SOMEDEVICE\\0000", new Guid("6af9c814-a6d8-407d-a5b7-17d8a0d2a8d3"), [], null, []);
        Accept(usb);
        Accept(system);
        var missingFriendlyName = RawValue.Missing();
        Safety.ValidateOriginal(missingFriendlyName);
        var write = missingFriendlyName.ForWrite();
        if (write.Type != Format.Empty || write.Bytes is not null) throw new InvalidOperationException("pnp-self-check-failed");
        checks++;
        Refuse(usb with { InstanceId = "BTHENUM\\DEV_AABBCCDDEEFF\\7&123&0&BLUETOOTHDEVICE_AABBCCDDEEFF" });
        Refuse(system with { InstanceId = "BTHLEDEVICE\\{00000000-0000-0000-0000-000000000001}" });
        Refuse(system with { ClassGuid = Targets.AudioEndpointClass });
        return checks;
    }
}
