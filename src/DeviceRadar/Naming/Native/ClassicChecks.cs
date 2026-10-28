using System.Text;

namespace DeviceRadar.Naming.Native;

internal static class ClassicChecks
{
    internal static int SelfCheck()
    {
        var count = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("classic-self-check-failed");
            count++;
        }
        void Refuse(Action action, string code)
        {
            try { action(); }
            catch (ProbeException error) when (error.Code == code) { count++; return; }
            throw new InvalidOperationException("classic-boundary-check-failed: " + code);
        }
        BluetoothClassicNative.ValidateLayout();
        count++;
        var missing = RawValue.Missing();
        var zeroLength = new RawValue(true, Format.RegistryBinary, []);
        var empty = new RawValue(true, Format.RegistryBinary, [0]);
        ClassicTarget.ValidateValue(missing);
        ClassicTarget.ValidateValue(zeroLength);
        ClassicTarget.ValidateValue(empty);
        Check(!missing.Same(empty) && !zeroLength.Same(empty) && !missing.Same(zeroLength));
        Check(ClassicTarget.AliasText(missing) is null && ClassicTarget.AliasText(empty) is null && ClassicTarget.AliasText(zeroLength) is null);
        var alias = ClassicTarget.EncodeAlias("我的耳机");
        Check(alias.Type == Format.RegistryBinary && ClassicTarget.AliasText(alias) == "我的耳机" && alias.Bytes[^1] == 0);
        Check(ClassicTarget.DeviceName(new(true, Format.RegistryBinary, Encoding.UTF8.GetBytes("Original\0"))) == "Original");
        Refuse(() => ClassicTarget.EncodeAlias("bad\0name"), "invalid-bluetooth-alias");
        Refuse(() => ClassicTarget.EncodeAlias("\ud800"), "invalid-name");
        Refuse(() => ClassicTarget.ValidateValue(new(true, Format.RegistryString, [0])), "invalid-bluetooth-alias");
        Refuse(() => ClassicTarget.ValidateValue(new(false, Format.RegistryBinary, [])), "invalid-bluetooth-alias");
        Refuse(() => ClassicTarget.ValidateValue(new(true, Format.RegistryBinary, [0xc3, 0x28, 0])), "invalid-bluetooth-alias");
        Refuse(() => ClassicTarget.ValidateValue(new(true, Format.RegistryBinary, [65])), "invalid-bluetooth-alias");
        Check(ClassicTarget.AliasText(ClassicTarget.EncodeAlias(new string('a', 123)))?.Length == 123);
        Refuse(() => ClassicTarget.EncodeAlias(new string('a', 124)), "bluetooth-alias-length-unsupported");
        Refuse(() => ClassicTarget.EncodeAlias(new string('中', 63)), "bluetooth-alias-length-unsupported");
        var target = new DeviceIdentity(@"BTHENUM\DEV_AABBCCDDEEFF\7&LOCAL&0&BLUETOOTHDEVICE_AABBCCDDEEFF",
            Targets.BluetoothClass, [], null, [], RadioAddress: "112233445566");
        ClassicTarget.ValidateBinding(target);
        count++;
        Refuse(() => ClassicTarget.ValidateBinding(target with { InstanceId = target.InstanceId.Replace("BTHENUM", "BTHLE") }), "invalid-classic-target");
        Refuse(() => ClassicTarget.ValidateBinding(target with { InstanceId = target.InstanceId + @"\FUNCTION" }), "invalid-classic-target");
        Refuse(() => ClassicTarget.ValidateBinding(target with { RadioAddress = "000000000000" }), "invalid-bluetooth-address");
        Refuse(() => ClassicTarget.ValidateBinding(target with { EndpointId = "another-surface" }), "invalid-record-target");
        Check(ClassicTarget.DeviceAddress(target.InstanceId) == 0xaabbccddeeff);
        return count;
    }
}
