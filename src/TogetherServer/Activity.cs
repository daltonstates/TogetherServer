namespace TogetherServer;

public static class ActivityVisibility
{
    public const string Local = "Local";
    public const string AssignedFriends = "AssignedFriends";
    public const string Device = "Device";
}

public static class ActivitySeverity
{
    public const string Info = "Info";
    public const string Important = "Important";
    public const string Warning = "Warning";
}

// Structured activity is deliberately redacted. Callers provide bounded human
// text only; credentials, invite codes, scripts, passwords, paths, arguments,
// and player/network identifiers are never event fields.
public sealed record ActivityEvent(Guid Id, DateTimeOffset OccurredUtc, string Category,
    string Action, string Message, string Severity = ActivitySeverity.Info,
    Guid? ProfileId = null, Guid? DeviceId = null,
    string Visibility = ActivityVisibility.Local);
