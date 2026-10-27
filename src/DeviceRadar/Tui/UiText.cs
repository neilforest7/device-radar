using System.Globalization;
using System.Text;

namespace DeviceRadar.Tui;

internal sealed class UiText(string language)
{
    public bool Chinese { get; set; } = language == "zh" || language == "system" && CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public string Get(string zh, string en) => Chinese ? zh : en;
    public string Kind(NamingLocationKind kind) => kind switch
    {
        NamingLocationKind.Pnp => Get("PnP 设备", "PnP device"),
        NamingLocationKind.AudioRecording => Get("录音端点", "Recording endpoint"),
        NamingLocationKind.AudioPlayback => Get("播放端点", "Playback endpoint"),
        NamingLocationKind.BluetoothClassic => Get("Classic 蓝牙", "Classic Bluetooth"),
        NamingLocationKind.BluetoothLe => Get("BLE 蓝牙", "BLE Bluetooth"),
        _ => kind.ToString()
    };
    // Raw identifiers and raw property bytes never go through this presentation helper.
    public static string Safe(string? value)
    {
        if (value is null) return "";
        var b = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
                b.Append('�');
            else b.Append(rune);
            if (b.Length > 4096) { b.Append('…'); break; }
        }
        return b.ToString();
    }
    public string Reason(string? reason) => reason is null ? "" : reason switch
    {
        "administrator-required" => Get("需要管理员权限；请从主界面提权", "Administrator required; elevate from the main screen"),
        "device-and-location-unverified" => Get("该设备与位置尚未验证", "Device and location are not yet validated"),
        "validated-build-required" => Get("当前 Windows 版本尚未验证", "This Windows build is not validated"),
        "unverified-device-not-validated" => Get("缺少该实物的本机验证配置；仅可只读使用", "No local validation profile for this physical device; read-only"),
        "validation-profile-mismatch" or "invalid-validation-profile" => Get("本机验证配置与当前设备或系统不符", "Local validation profile does not match the current device or system"),
        "validated-driver-changed" => Get("驱动指纹已变化，需要重新验收", "Driver fingerprint changed; validation required"),
        "ble-reversible-path-unverified" => Get("BLE 可恢复写入尚未验证", "Reversible BLE writing is not validated"),
        "classic-settings-and-persistence-unverified" => Get("蓝牙设置与持久性尚未验证", "Bluetooth settings and persistence are not validated"),
        "audio-playback-unverified" => Get("播放端点写入尚未验证", "Playback endpoint writing is not validated"),
        "audio-endpoint-inactive" => Get("声音端点未激活，请核对禁用或连接状态", "Audio endpoint is inactive; check its disabled or connection state"),
        "external-value-changed" => Get("当前名称已被外部修改", "Current name changed externally"),
        "target-offline" => Get("设备已断开", "Device disconnected"),
        "target-ambiguous" => Get("身份有歧义，停止写入", "Ambiguous identity; write stopped"),
        "current-value-changed" => Get("现名已变化，请重新预览", "Current value changed; preview again"),
        "machine-mismatch" => Get("恢复点属于另一台电脑", "Recovery point belongs to another computer"),
        "target-mismatch" => Get("设备身份与恢复点不符", "Device identity differs from recovery point"),
        "result-log-failed" => Get("结果未能保存，请独立核验后再操作", "Result could not be saved; verify independently"),
        "audio-baseline-inconsistent" => Get("声音名称来源不一致，需要调查", "Audio name sources differ; investigation required"),
        "preview-stale" or "value-changed-before-write" or "validation-evidence-baseline-changed" => Get("当前值已变化，请重新预览", "Current value changed; preview again"),
        "selection-identity-changed" or "location-not-found" => Get("选中设备或位置已变化，请重新选择", "The selected device or location changed; select again"),
        "invalid-name" => Get("名称无效，请检查长度和字符", "Invalid name; check its length and characters"),
        "invalid-selection" => Get("请选择有效的命名位置", "Select valid naming locations"),
        "file-access-denied" => Get("数据目录不可写，请使用可写的本地目录", "Data directory is not writable; use a writable local directory"),
        "file-io-error" or "persistence-verification-failed" => Get("保存数据失败，请检查本地目录", "Could not save data; check the local directory"),
        "local-data-directory-required" => Get("数据目录必须位于本地磁盘", "The data directory must be on a local disk"),
        "validated-target-unavailable" => Get("该设备的写入验证记录不可用", "This device has no available write validation record"),
        "audio-name-source-changed" => Get("声音名称来源已变化，请重新检查", "Audio name source changed; check again"),
        "observation-unconfirmed" or "write-unconfirmed" => Get("写入结果未能通过回读确认", "Write could not be confirmed by readback"),
        "security-check-failed" => Get("安全校验未通过，操作已停止", "Safety check failed; operation stopped"),
        "invalid-record" or "invalid-record-json" or "invalid-file-seal" or "machine-identity-unavailable" => Get("记录或本机身份无法验证", "Record or local machine identity could not be validated"),
        _ => Get("操作未完成，请检查设备及本地数据：", "Operation could not complete; check the device and local data: ") + Safe(reason)
    };
}
