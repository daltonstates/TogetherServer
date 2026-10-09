using System.Runtime.InteropServices;
using System.Text.Json;

namespace TogetherServer;

public sealed record DesktopNotificationServerPreference(Guid ProfileId, Guid? ConnectionId, string[] AllowedEvents);
public sealed record DesktopNotificationPreferenceView(bool QuietMode, string[] AllowedEvents,
    DesktopNotificationServerPreference[] Servers, string SystemState, bool SystemAllowsNotifications);
public sealed record DesktopNotificationPreferenceChange(bool? QuietMode = null, string[]? AllowedEvents = null,
    Guid? ProfileId = null, Guid? ConnectionId = null, bool ResetServer = false);
public sealed record DesktopNotificationDestination(string Workspace, string Section, Guid? ProfileId = null,
    Guid? ConnectionId = null);
public sealed record DesktopTraySummary(int RunningServers, int ServersNeedingReview, string FriendState);

public sealed class DesktopNotificationPreferences
{
    private const string FileName = "qol-notifications.json";
    public static readonly string[] EventKinds = ["Lifecycle", "Backup", "Access", "Connections", "Network",
        "Recovery", "Countdown", "Players", "Maintenance", "Configuration", "AddOns", "Remote", "Update"];
    private readonly LocalData? data;
    private readonly Func<DesktopUserNotificationState> systemState;
    private readonly object sync = new();
    private Settings settings;

    public DesktopNotificationPreferences(LocalData data)
    {
        this.data = data;
        systemState = WindowsNotificationState.Read;
        settings = data.LoadState(FileName, new Settings());
        if (!Valid(settings)) settings = new Settings();
    }

    internal DesktopNotificationPreferences(Func<DesktopUserNotificationState> systemState)
    {
        this.systemState = systemState;
        settings = new Settings();
    }

    public DesktopNotificationPreferenceView View()
    {
        lock (sync)
        {
            var currentSystemState = systemState();
            return new(settings.QuietMode, settings.AllowedEvents.ToArray(),
                settings.Servers.Select(item => item with { AllowedEvents = item.AllowedEvents.ToArray() }).ToArray(),
                currentSystemState.ToString(), currentSystemState == DesktopUserNotificationState.AcceptsNotifications);
        }
    }

    public bool ShouldNotify(string category, Guid? profileId = null, Guid? connectionId = null)
    {
        lock (sync)
        {
            if (settings.QuietMode || systemState() != DesktopUserNotificationState.AcceptsNotifications) return false;
            if (!EventKinds.Contains(category, StringComparer.Ordinal)) return false;
            var server = settings.Servers.SingleOrDefault(item => item.ProfileId == profileId && item.ConnectionId == connectionId);
            return (server?.AllowedEvents ?? settings.AllowedEvents).Contains(category, StringComparer.Ordinal);
        }
    }

    public DesktopNotificationPreferenceView Apply(DesktopNotificationPreferenceChange change)
    {
        Validate(change);
        lock (sync)
        {
            var next = new Settings { QuietMode = settings.QuietMode, AllowedEvents = settings.AllowedEvents.ToArray(),
                Servers = settings.Servers.ToList() };
            if (change.QuietMode is { } quiet) next.QuietMode = quiet;
            else if (change.ProfileId is { } profile)
            {
                next.Servers.RemoveAll(item => item.ProfileId == profile && item.ConnectionId == change.ConnectionId);
                if (!change.ResetServer)
                {
                    if (next.Servers.Count >= 128) throw new InvalidDataException("Choose notifications for at most 128 saved servers.");
                    next.Servers.Add(new(profile, change.ConnectionId, Ordered(change.AllowedEvents!)));
                }
            }
            else next.AllowedEvents = Ordered(change.AllowedEvents!);
            data?.SaveState(FileName, next);
            settings = next;
            return View();
        }
    }

    public static DesktopNotificationPreferenceChange ParseChange(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Notification preference must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            if (property.Name is not ("quietMode" or "allowedEvents" or "profileId" or "connectionId") || !names.Add(property.Name))
                throw new InvalidDataException("Notification preference contains unknown or duplicate fields.");
        if (input.TryGetProperty("quietMode", out var quiet))
        {
            if (names.Count != 1 || quiet.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Change quiet mode with one boolean preference.");
            return new(QuietMode: quiet.GetBoolean());
        }
        if (!input.TryGetProperty("allowedEvents", out var events)) throw new InvalidDataException("Choose the allowed notification events.");
        Guid? profile = null;
        Guid? connection = null;
        if (input.TryGetProperty("profileId", out var profileField)) profile = ReadGuid(profileField);
        if (input.TryGetProperty("connectionId", out var connectionField) && connectionField.ValueKind != JsonValueKind.Null)
            connection = ReadGuid(connectionField);
        if (events.ValueKind == JsonValueKind.Null)
        {
            var reset = new DesktopNotificationPreferenceChange(ProfileId: profile, ConnectionId: connection, ResetServer: true);
            Validate(reset);
            return reset;
        }
        if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > EventKinds.Length)
            throw new InvalidDataException("Choose only the fixed notification events.");
        var allowed = events.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { } value
            ? value : throw new InvalidDataException("Notification event must be a fixed name.")).ToArray();
        var change = new DesktopNotificationPreferenceChange(AllowedEvents: allowed, ProfileId: profile, ConnectionId: connection);
        Validate(change);
        return change;
    }

    public static DesktopNotificationDestination Destination(ActivityEvent item, bool friendMode, Guid? connectionId = null)
    {
        if (friendMode)
            return new("join", item.Category == "Backup" ? "shared-saves" : item.Category == "Network" ? "network" : "overview",
                item.ProfileId, connectionId);
        return item.Category switch
        {
            "Backup" => new("host", "backups", item.ProfileId),
            "Countdown" or "Players" => new("host", "players", item.ProfileId),
            "Lifecycle" or "Maintenance" => new("host", "overview", item.ProfileId),
            "Remote" => new("host", "sessions", item.ProfileId),
            "Configuration" or "AddOns" => new("host", "files", item.ProfileId),
            "Connections" or "Access" => new("settings", "access", item.ProfileId),
            "Network" => new("settings", "network", item.ProfileId),
            "Recovery" => new("settings", "diagnostics", item.ProfileId),
            "Update" => new("settings", "app"),
            _ => new("settings", "diagnostics")
        };
    }

    public static bool IsDestination(DesktopNotificationDestination destination) =>
        destination.ProfileId != Guid.Empty && destination.ConnectionId != Guid.Empty && (destination.Workspace switch
        {
            "host" => destination.ConnectionId is null && destination.Section is "overview" or "backups" or "players" or "sessions" or "files" or "chat" or "logs",
            "join" => destination.Section is "overview" or "network" or "shared-saves" or "chat" or "logs",
            "settings" => destination.ConnectionId is null && destination.Section is "access" or "network" or "diagnostics" or "app" or "stop",
            _ => false
        });

    private static void Validate(DesktopNotificationPreferenceChange change)
    {
        if (change.QuietMode.HasValue)
        {
            if (change.AllowedEvents is not null || change.ProfileId is not null || change.ConnectionId is not null || change.ResetServer)
                throw new InvalidDataException("Change one notification preference at a time.");
            return;
        }
        if (change.ProfileId == Guid.Empty || change.ConnectionId == Guid.Empty ||
            (change.ConnectionId is not null && change.ProfileId is null) ||
            (change.ResetServer && (change.ProfileId is null || change.AllowedEvents is not null)) ||
            (!change.ResetServer && (change.AllowedEvents is null || !ValidEvents(change.AllowedEvents))))
            throw new InvalidDataException("Choose notifications for a saved server and fixed event list.");
    }
    private static bool Valid(Settings value) => value is not null && value.AllowedEvents is not null &&
        ValidEvents(value.AllowedEvents) && value.Servers is not null && value.Servers.Count <= 128 &&
        value.Servers.All(item => item is not null && item.ProfileId != Guid.Empty && item.ConnectionId != Guid.Empty &&
            item.AllowedEvents is not null && ValidEvents(item.AllowedEvents)) &&
        value.Servers.Select(item => (item.ProfileId, item.ConnectionId)).Distinct().Count() == value.Servers.Count;
    private static bool ValidEvents(string[] values) => values.Length <= EventKinds.Length &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Length &&
        values.All(value => EventKinds.Contains(value, StringComparer.Ordinal));
    private static string[] Ordered(string[] values) => EventKinds.Where(values.Contains).ToArray();
    private static Guid ReadGuid(JsonElement field) => field.ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(field.GetString(), "D", out var value) && value != Guid.Empty ? value :
        throw new InvalidDataException("Notification server scope is invalid.");
    public sealed class Settings
    {
        public bool QuietMode { get; set; }
        public string[] AllowedEvents { get; set; } = EventKinds.ToArray();
        public List<DesktopNotificationServerPreference> Servers { get; set; } = [];
    }
}

internal enum DesktopUserNotificationState
{
    Unknown = 0, NotPresent = 1, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4,
    AcceptsNotifications = 5, QuietTime = 6, App = 7
}

internal static class WindowsNotificationState
{
    // Microsoft recommends this fixed Shell API before displaying a notification. No settings are changed.
    // https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
    internal static DesktopUserNotificationState Read()
    {
        if (!OperatingSystem.IsWindows()) return DesktopUserNotificationState.Unknown;
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 && Enum.IsDefined(state)
                ? state : DesktopUserNotificationState.Unknown;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { return DesktopUserNotificationState.Unknown; }
    }
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out DesktopUserNotificationState state);
}
