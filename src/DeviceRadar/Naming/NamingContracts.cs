namespace DeviceRadar;

public sealed record NamingCapability(NamingLocation Location, bool CanWrite, string Reason,
    string CurrentName, string? SettingsUri, bool RequiresAdministrator, string Evidence)
{
    public string LocationId => Location.Id;
}

public sealed record NameEdit(string LocationId, string NewName);
public sealed record NamingPreviewEntry(string LocationId, NamingLocationKind Kind, string OriginalName,
    string NewName, string ExpectedDisplayName, string Method, string Evidence);

public sealed class NamingPreview
{
    internal NamingPreview(string deviceId, NamingPreviewEntry[] entries, Naming.Native.OperationRecord[] records)
        => (DeviceId, Entries, Records) = (deviceId, entries, records);
    public string DeviceId { get; }
    public IReadOnlyList<NamingPreviewEntry> Entries { get; }
    internal Naming.Native.OperationRecord[] Records { get; }
}

public sealed record PreparedNaming(string[] RecordTokens);
public sealed record NamingLocationResult(string RecordToken, string LocationId, string Command, string Status,
    bool WriteAttempted, bool? ApiSucceeded, string ObservedState, string? ResolvedState,
    string? SynchronizedState, string? ObservedName, string? Reason, int? NativeError,
    int? NotificationCount, string? EventToken, string InterfaceVerification = "unverified",
    string ReconnectVerification = "unverified", string RebootVerification = "unverified")
{
    public bool Succeeded => Status is "applied" or "restored" or "already-at-value" or "verified";
}
public sealed record NamingResult(string Status, NamingLocationResult[] Locations)
{
    public bool Succeeded => Locations.Length > 0 && Locations.All(item => item.Succeeded);
}
public sealed record HistoryEntry(string RecordToken, DateTimeOffset CreatedAtUtc, string LocationId,
    string OriginalName, string ProposedName, string Status, string? Reason = null, bool IsNickname = true);

public sealed record UserSettings(string Language = "system", bool ExternalOnly = true);
public sealed record SettingsLoadResult(UserSettings Settings, string? Warning = null);
public sealed record ValidationImportResult(string ProfileToken, string TargetKind, string TargetHash);
