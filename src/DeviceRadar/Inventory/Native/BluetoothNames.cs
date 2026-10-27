using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceRadar.Inventory.Native;

internal static partial class BluetoothNames
{
    // The association is made only for an exact address-bearing Bluetooth root node.
    internal static NamingLocation? Read(SetupApi.Node node)
    {
        var match = RootId().Match(node.Id);
        if (!match.Success) return null;
        var kind = match.Groups[1].Value.Equals("BTHLE", StringComparison.OrdinalIgnoreCase)
            ? NamingLocationKind.BluetoothLe : NamingLocationKind.BluetoothClassic;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\" + match.Groups[2].Value, false);
            if (key is null) return null;
            var alias = Decode(key.GetValue("FriendlyName"));
            var deviceName = Decode(key.GetValue(kind == NamingLocationKind.BluetoothLe ? "LEName" : "Name")) ?? Decode(key.GetValue("Name"));
            var display = string.IsNullOrEmpty(alias) ? deviceName : alias;
            return display is null ? null : new("bluetooth:" + node.Id, kind, display, node.Id, node.Present);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException or DecoderFallbackException) { return null; }
    }
    private static string? Decode(object? value)
    {
        if (value is not byte[] bytes || bytes.Length == 0 || bytes[^1] != 0) return null;
        var text = new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - 1);
        return text.Contains('\0') ? null : text;
    }
    [GeneratedRegex(@"\A(BTHLE|BTHENUM)\\DEV_([0-9A-F]{12})\\[^\\]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootId();
}
