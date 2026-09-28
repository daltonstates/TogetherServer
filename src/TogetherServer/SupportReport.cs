using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record SupportLogMetadata(string State, int AuditFileCount, long AuditBytes,
    DateTimeOffset? AuditLastWriteUtc, int RunLogCount, long RunLogBytes,
    DateTimeOffset? RunLogLastWriteUtc, bool Truncated);

public sealed record SupportReportExport(string FileName, string ContentType, string Content,
    int SizeBytes);

internal static class SupportReportRedactor
{
    private static readonly Regex Endpoint = new(
        "(?i)\\b(?:https?|wss?)://[^\\s,;]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex ExtendedWindowsPath = new(
        "(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^,\\r\\n;|]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static string Redact(string? value, int maximum = 400, string fallback = "Unavailable")
    {
        var clean = ServerLogSanitizer.Clean(value ?? "");
        clean = Endpoint.Replace(clean, "[redacted-endpoint]");
        clean = ExtendedWindowsPath.Replace(clean, "[redacted-path]");
        clean = ServerLogSanitizer.ProtectSupportValue(clean);
        if (clean.Length == 0) clean = fallback;
        return clean.Length <= maximum ? clean : clean[..Math.Max(1, maximum - 14)] + "… [truncated]";
    }

    public static string Token(string? value, int maximum = 64, string fallback = "Unknown")
    {
        var protectedValue = Redact(value, Math.Max(maximum * 2, 128), fallback);
        var clean = new string(protectedValue.Where(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ' ').ToArray()).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length <= maximum ? clean : clean[..maximum];
    }
}

public static class SupportReportExporter
{
    public const int ReportSchemaVersion = 1;
    public const int MaximumReportBytes = 128 * 1024;
    public const string FileName = "TogetherServer-support-report.json";
    public const string ContentType = "application/json; charset=utf-8";
    private const int MaximumServers = 16;
    private const int MaximumEvents = 24;
    private const int MaximumOperations = 24;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static SupportReportExport Create(OwnerDiagnosticsView diagnostics, HostSnapshot snapshot,
        AppInstance instance, UpdateView update, IReadOnlyList<ActivityEvent> activity,
        IReadOnlyList<RemoteOperationView> operations, SupportLogMetadata logs)
    {
        var document = BuildDocument(diagnostics, snapshot, instance, update, activity, operations, logs,
            compact: false);
        var content = Serialize(document);
        if (Encoding.UTF8.GetByteCount(content) > MaximumReportBytes)
        {
            document = BuildDocument(diagnostics, snapshot, instance, update, activity, operations, logs,
                compact: true);
            content = Serialize(document);
        }
        var size = Encoding.UTF8.GetByteCount(content);
        if (size > MaximumReportBytes)
            throw new InvalidOperationException("The bounded support report could not be serialized safely.");
        return new(FileName, ContentType, content, size);
    }

    private static SupportReportDocument BuildDocument(OwnerDiagnosticsView diagnostics,
        HostSnapshot snapshot, AppInstance instance, UpdateView update,
        IReadOnlyList<ActivityEvent> activity, IReadOnlyList<RemoteOperationView> operations,
        SupportLogMetadata logs, bool compact)
    {
        var serverLimit = compact ? 4 : MaximumServers;
        var eventLimit = compact ? 0 : MaximumEvents;
        var operationLimit = compact ? 0 : MaximumOperations;
        var profileNumbers = snapshot.Settings.Profiles.Take(serverLimit)
            .Select((profile, index) => (profile.Id, Number: index + 1))
            .ToDictionary(item => item.Id, item => item.Number);
        string ServerLabel(Guid profileId) => profileNumbers.TryGetValue(profileId, out var number)
            ? $"Server {number}" : "Unconfigured server";

        var serverDiagnostics = diagnostics.Servers.Take(serverLimit).Select((server, index) =>
            new SupportServerDiagnostics($"Server {index + 1}",
                SupportReportRedactor.Token(server.Kind, 40),
                server.Checks.Take(8).Select(check => SupportCheck(check, compact)).ToList())).ToList();
        var sharedDiagnostics = diagnostics.SharedChecks.Take(12)
            .Select(check => SupportCheck(check, compact)).ToList();
        var profiles = snapshot.Settings.Profiles.Take(serverLimit).Select((profile, index) =>
            new SupportProfileSummary($"Server {index + 1}",
                SupportReportRedactor.Token(profile.Kind, 40),
                SupportReportRedactor.Token(profile.WorldSource, 24), profile.Crossplay,
                profile.Maintenance?.Enabled == true, profile.CrashRecovery?.Enabled == true,
                profile.Backups?.Enabled == true,
                profile.Kind == GameKinds.Valheim && snapshot.PasswordConfigured.GetValueOrDefault(profile.Id)))
            .ToList();
        var eventSummaries = activity.OrderByDescending(item => item.OccurredUtc).Take(eventLimit)
            .Select(item => new SupportActivitySummary(item.OccurredUtc,
                SupportReportRedactor.Token(item.Category, 40),
                SupportReportRedactor.Token(item.Action, 64),
                SupportReportRedactor.Token(item.Severity, 16),
                item.ProfileId is { } profileId ? ServerLabel(profileId) : "App",
                SupportReportRedactor.Token(item.Visibility, 24))).ToList();
        var operationSummaries = operations.OrderByDescending(item => item.RequestedUtc).Take(operationLimit)
            .Select(item => new SupportOperationSummary(item.RequestedUtc, item.StartedUtc,
                item.CompletedUtc, ServerLabel(item.ProfileId),
                SupportReportRedactor.Token(item.Action, 32),
                SupportReportRedactor.Token(item.State, 24), item.Ok,
                SupportReportRedactor.Token(item.Code, 64))).ToList();
        var settings = snapshot.Settings;

        return new SupportReportDocument(
            ReportSchemaVersion,
            diagnostics.GeneratedUtc,
            new SupportVersionSummary(
                SupportReportRedactor.Token(CompanionProtocol.AppVersion, 32),
                CompanionProtocol.Current, CompanionProtocol.Minimum,
                LocalData.CurrentStorageSchemaVersion, instance.IsStaging),
            new SupportSettingsSummary(settings.MaxConcurrentServers, settings.AutoShutdownEnabled,
                settings.IdleMinutes, settings.RemoteControlsEnabled, settings.CompanionListeningEnabled,
                settings.CompanionPort, SupportReportRedactor.Token(
                    ConnectionRoutes.Normalize(settings.ConnectionRoute).Mode, 32), settings.Profiles.Count,
                profiles, settings.Profiles.Count > serverLimit),
            new SupportDiagnosticsSummary(
                SupportReportRedactor.Redact(diagnostics.EvidenceBoundary, compact ? 220 : 400),
                serverDiagnostics, sharedDiagnostics,
                diagnostics.Truncated || diagnostics.Servers.Count > serverLimit),
            new SupportRecoverySummary(snapshot.Recovery?.LifecycleBlocked == true,
                snapshot.Recovery?.Notices.Count ?? 0),
            new SupportUpdateSummary(SupportReportRedactor.Token(update.State, 32),
                SupportReportRedactor.Token(update.CurrentVersion, 32),
                update.LatestVersion is null ? null : SupportReportRedactor.Token(update.LatestVersion, 32),
                SupportReportRedactor.Redact(update.Message, compact ? 160 : 300)),
            eventSummaries, activity.Count > eventLimit,
            operationSummaries, operations.Count > operationLimit,
            new SupportLogSummary(SupportReportRedactor.Token(logs.State, 32),
                Math.Clamp(logs.AuditFileCount, 0, 4), Math.Clamp(logs.AuditBytes, 0, 20L * 1024 * 1024),
                logs.AuditLastWriteUtc, Math.Clamp(logs.RunLogCount, 0, 500),
                Math.Clamp(logs.RunLogBytes, 0, 20L * 1024 * 1024 * 1024),
                logs.RunLogLastWriteUtc, logs.Truncated),
            "Raw server logs are not included. Values are bounded and redacted before serialization.");
    }

    private static SupportDiagnosticCheck SupportCheck(OwnerDiagnosticCheck check, bool compact) =>
        new(SupportReportRedactor.Token(check.Id, 64),
            SupportReportRedactor.Redact(check.Label, 100),
            SupportReportRedactor.Redact(check.State, 100),
            SupportReportRedactor.Redact(check.Detail, compact ? 160 : 400),
            SupportReportRedactor.Redact(check.NextAction, compact ? 140 : 300),
            SupportReportRedactor.Redact(check.Location, 100),
            SupportReportRedactor.Token(check.Tone, 16), check.ObservedUtc);

    private static string Serialize(SupportReportDocument document) =>
        JsonSerializer.Serialize(document, Json) + "\n";

    private sealed record SupportReportDocument(int ReportSchemaVersion, DateTimeOffset GeneratedUtc,
        SupportVersionSummary Versions, SupportSettingsSummary Settings,
        SupportDiagnosticsSummary Diagnostics, SupportRecoverySummary Recovery,
        SupportUpdateSummary Update, IReadOnlyList<SupportActivitySummary> RecentActivity,
        bool ActivityTruncated, IReadOnlyList<SupportOperationSummary> RecentOperations,
        bool OperationsTruncated, SupportLogSummary LogMetadata, string SecurityNote);
    private sealed record SupportVersionSummary(string AppVersion, int CompanionProtocolVersion,
        int MinimumCompanionProtocolVersion, int StorageSchemaVersion, bool Staging);
    private sealed record SupportSettingsSummary(int MaximumConcurrentServers,
        bool AutomaticShutdownEnabled, int IdleMinutes, bool RemoteControlsEnabled,
        bool CompanionListenerEnabled, int CompanionPort, string RouteMode, int SavedServerCount,
        IReadOnlyList<SupportProfileSummary> Profiles, bool ProfilesTruncated);
    private sealed record SupportProfileSummary(string Label, string Kind, string WorldSource,
        bool Crossplay, bool MaintenanceEnabled, bool CrashRecoveryEnabled, bool BackupsEnabled,
        bool ProtectedGamePasswordConfigured);
    private sealed record SupportDiagnosticsSummary(string EvidenceBoundary,
        IReadOnlyList<SupportServerDiagnostics> Servers,
        IReadOnlyList<SupportDiagnosticCheck> SharedChecks, bool Truncated);
    private sealed record SupportServerDiagnostics(string Label, string Kind,
        IReadOnlyList<SupportDiagnosticCheck> Checks);
    private sealed record SupportDiagnosticCheck(string Id, string Label, string State,
        string Detail, string NextAction, string Location, string Tone, DateTimeOffset? ObservedUtc);
    private sealed record SupportRecoverySummary(bool LifecycleBlocked, int NoticeCount);
    private sealed record SupportUpdateSummary(string State, string CurrentVersion,
        string? LatestVersion, string Message);
    private sealed record SupportActivitySummary(DateTimeOffset OccurredUtc, string Category,
        string Action, string Severity, string Scope, string Visibility);
    private sealed record SupportOperationSummary(DateTimeOffset RequestedUtc,
        DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc, string Server,
        string Action, string State, bool? Ok, string Code);
    private sealed record SupportLogSummary(string State, int AuditFileCount, long AuditBytes,
        DateTimeOffset? AuditLastWriteUtc, int RunLogCount, long RunLogBytes,
        DateTimeOffset? RunLogLastWriteUtc, bool Truncated);
}
