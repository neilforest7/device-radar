using DeviceRadar.Naming.Native;

namespace DeviceRadar;

public sealed class PortableState
{
    public SettingsLoadResult LoadSettings()
    {
        try { return new RecordStore().LoadSettings(); }
        catch (Exception error) { return new(new(), NamingService.ErrorCode(error)); }
    }

    public void SaveSettings(UserSettings settings) => new RecordStore().SaveSettings(settings);
}
