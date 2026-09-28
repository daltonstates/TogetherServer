using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TogetherServer;

public static class ServerLogSourceStates
{
    public const string Active = "Active";
    public const string Ended = "Ended";
    public const string Missing = "Missing";
    public const string Unsupported = "Unsupported";
    public const string Unavailable = "Unavailable";
}

public enum ServerLogAudience
{
    Host,
    Friend
}

public sealed record ServerLogQuery(string? Cursor = null, int Limit = 100,
    string? Severity = null, string? Category = null, string? Stream = null,
    string? Contains = null);

public sealed record ServerLogRecord(DateTimeOffset? TimestampUtc, string Severity,
    string Category, string Stream, string Message);

public sealed record ServerLogResult(bool Ok, string Code, string Message,
    string SourceState, string? RunId, IReadOnlyList<ServerLogRecord> Records,
    string? Cursor, bool HasMore);

internal sealed record ManagedServerLogSource(Guid ProfileId, string Kind,
    Guid? OperationId, string? LogPath, string State, DateTimeOffset? EndedUtc = null);

internal static class ServerLogQueryParser
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    { "cursor", "limit", "severity", "category", "stream", "contains" };

    public static bool TryParse(IQueryCollection values, out ServerLogQuery query,
        out ServerLogResult? error)
    {
        query = new();
        error = null;
        if (values.Keys.Any(key => !Allowed.Contains(key)) ||
            values.Any(item => item.Value.Count != 1))
        {
            error = Invalid("Unknown or repeated log query field. Use only cursor, limit, severity, category, stream, and contains.");
            return false;
        }

        var limit = 100;
        if (values.TryGetValue("limit", out var enteredLimit) &&
            (!int.TryParse(enteredLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > ServerLogService.MaximumLimit))
        {
            error = Invalid($"Log limit must be between 1 and {ServerLogService.MaximumLimit}.");
            return false;
        }

        static string? Value(IQueryCollection source, string key) =>
            source.TryGetValue(key, out var value) ? value.ToString() : null;
        query = new(Value(values, "cursor"), limit, Value(values, "severity"),
            Value(values, "category"), Value(values, "stream"), Value(values, "contains"));
        if (!ServerLogService.ValidQuery(query, out var message))
        {
            error = Invalid(message);
            return false;
        }
        return true;
    }

    private static ServerLogResult Invalid(string message) => new(false, "InvalidLogQuery", message,
        ServerLogSourceStates.Unavailable, null, [], null, false);
}

public sealed class ServerLogService(LocalData data, HostManager manager)
{
    public const int MaximumLimit = 200;
    private readonly IReadOnlyDictionary<string, IServerLogAdapter> adapters =
        new Dictionary<string, IServerLogAdapter>(StringComparer.Ordinal)
        {
            [GameKinds.Valheim] = new ValheimServerLogAdapter(),
            [GameKinds.MinecraftJava] = new MinecraftServerLogAdapter(GameKinds.MinecraftJava),
            [GameKinds.MinecraftBedrock] = new MinecraftServerLogAdapter(GameKinds.MinecraftBedrock)
        };

    public async Task<ServerLogResult> ReadAsync(Guid profileId, ServerLogQuery query,
        ServerLogAudience audience)
    {
        if (profileId == Guid.Empty)
            return new(false, "InvalidLogQuery", "Profile ID is invalid.", ServerLogSourceStates.Unavailable,
                null, [], null, false);
        if (!ValidQuery(query, out var validation))
            return new(false, "InvalidLogQuery", validation, ServerLogSourceStates.Unavailable,
                null, [], null, false);

        // Retention is enforced on every read as well as app startup and new
        // log creation. It is display-only cleanup and never changes run
        // identity or lifecycle authority.
        data.PruneRunLogs();
        var source = await manager.ResolveServerLogSourceAsync(profileId);
        if (source is null)
            return new(false, "UnknownProfile", "Saved server was not found.",
                ServerLogSourceStates.Missing, null, [], null, false);
        if (audience == ServerLogAudience.Friend && source.Kind == GameKinds.Custom)
            return new(false, "CustomRemoteLogsUnavailable",
                "Custom game logs are local-owner-only in this version.",
                ServerLogSourceStates.Unsupported, null, [], null, false);
        if (source.State == ServerLogSourceStates.Unavailable)
            return new(false, "LogSourceIdentityMismatch",
                "The saved profile and exact managed-run identity do not agree.",
                ServerLogSourceStates.Unavailable, source.OperationId?.ToString("N"), [], null, false);
        if (!adapters.TryGetValue(source.Kind, out var adapter))
            return new(false, "UnsupportedLogSource",
                $"{source.Kind} logs are not supported in this version.",
                ServerLogSourceStates.Unsupported, source.OperationId?.ToString("N"), [], null, false);
        if (audience == ServerLogAudience.Friend && source.State == ServerLogSourceStates.Ended)
            return new(false, "FriendRetainedLogsUnavailable",
                "Friends can view only the exact active managed run. Retained ended-run logs remain Host-only.",
                ServerLogSourceStates.Ended, null, [], null, false);
        if (source.OperationId is null || source.LogPath is null)
            return new(false, "NoManagedRunLog", "This server has no active or retained managed-run log.",
                ServerLogSourceStates.Missing, null, [], null, false);

        var expected = data.RunLogPath(source.OperationId.Value);
        if (!PathsEqual(source.LogPath, expected))
            return new(false, "LogSourceIdentityMismatch",
                "The recorded run log is not an owned log for this exact managed run.",
                ServerLogSourceStates.Unavailable, source.OperationId.Value.ToString("N"), [], null, false);
        if (!File.Exists(expected))
        {
            var ended = source.State == ServerLogSourceStates.Ended;
            return new(false, ended ? "EndedLogUnavailable" : "LogNotCreated",
                ended ? "The recent managed run ended and its owned log is no longer retained."
                    : "The active managed run has not created its owned log yet.",
                ended ? ServerLogSourceStates.Ended : ServerLogSourceStates.Missing,
                source.OperationId.Value.ToString("N"), [], null, false);
        }
        try
        {
            var directory = Path.GetDirectoryName(expected)!;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
                return new(false, "LogSourceIdentityMismatch",
                    "The owned run log resolves through an unsupported filesystem link and was not read.",
                    ServerLogSourceStates.Unavailable, source.OperationId.Value.ToString("N"), [], null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(false, "LogReadUnavailable", "The owned run log could not be verified right now.",
                ServerLogSourceStates.Unavailable, source.OperationId.Value.ToString("N"), [], null, false);
        }

        return adapter.Read(source, query, audience);
    }

    internal static bool ValidQuery(ServerLogQuery query, out string message)
    {
        if (query.Limit is < 1 or > MaximumLimit)
        {
            message = $"Log limit must be between 1 and {MaximumLimit}.";
            return false;
        }
        if (query.Cursor is { Length: > 160 })
        {
            message = "Log cursor is too long.";
            return false;
        }
        foreach (var (name, value, maximum) in new[]
        {
            ("severity", query.Severity, 16), ("category", query.Category, 32),
            ("stream", query.Stream, 16), ("contains", query.Contains, 80)
        })
        {
            if (value is null) continue;
            if (value.Length is 0 || value.Length > maximum || value.Any(char.IsControl))
            {
                message = $"Log {name} filter is invalid.";
                return false;
            }
        }
        message = "";
        return true;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static string QueryString(ServerLogQuery query)
    {
        var fields = new List<string> { "limit=" + query.Limit.ToString(CultureInfo.InvariantCulture) };
        static void Add(List<string> target, string name, string? value)
        {
            if (value is not null) target.Add(name + "=" + Uri.EscapeDataString(value));
        }
        Add(fields, "cursor", query.Cursor);
        Add(fields, "severity", query.Severity);
        Add(fields, "category", query.Category);
        Add(fields, "stream", query.Stream);
        Add(fields, "contains", query.Contains);
        return string.Join('&', fields);
    }
}

internal interface IServerLogAdapter
{
    ServerLogResult Read(ManagedServerLogSource source, ServerLogQuery query,
        ServerLogAudience audience);
}

internal abstract class OwnedRunLogAdapter : IServerLogAdapter
{
    private const int TailBytes = 512 * 1024;
    private const int MaximumScanBytes = 1024 * 1024;
    private const int MaximumRawLineBytes = 64 * 1024;

    public ServerLogResult Read(ManagedServerLogSource source, ServerLogQuery query,
        ServerLogAudience audience)
    {
        var operationId = source.OperationId!.Value;
        try
        {
            using var stream = new FileStream(source.LogPath!, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!TryOffset(query.Cursor, operationId, stream.Length, out var offset))
                return new(false, "InvalidLogCursor",
                    "The log cursor is invalid, stale, or belongs to another managed run.",
                    ServerLogSourceStates.Unavailable, operationId.ToString("N"), [], null, false);
            if (query.Cursor is null)
                offset = AlignTail(stream, Math.Max(0, stream.Length - TailBytes));

            stream.Seek(offset, SeekOrigin.Begin);
            var records = new List<ServerLogRecord>(query.Limit);
            var line = new List<byte>(256);
            var lineTruncated = false;
            var scanned = 0;
            var committedOffset = offset;
            var stop = false;
            var buffer = new byte[8192];
            while (!stop && scanned < MaximumScanBytes)
            {
                var wanted = Math.Min(buffer.Length, MaximumScanBytes - scanned);
                var read = stream.Read(buffer, 0, wanted);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    scanned++;
                    if (value == (byte)'\n')
                    {
                        committedOffset = stream.Position - read + index + 1;
                        AddRecord(line, lineTruncated, query, audience, records);
                        line.Clear();
                        lineTruncated = false;
                        if (records.Count >= query.Limit) { stop = true; break; }
                    }
                    else if (line.Count < MaximumRawLineBytes)
                    {
                        line.Add(value);
                    }
                    else
                    {
                        lineTruncated = true;
                    }
                }
            }

            if (!stop && source.State == ServerLogSourceStates.Ended && line.Count > 0)
            {
                committedOffset = stream.Length;
                AddRecord(line, lineTruncated, query, audience, records);
            }
            else if (!stop && scanned >= MaximumScanBytes && line.Count > 0)
            {
                committedOffset = stream.Position;
                AddRecord(line, true, query, audience, records);
            }
            var cursor = EncodeCursor(operationId, committedOffset);
            var hasMore = committedOffset < stream.Length;
            return new(true, source.State == ServerLogSourceStates.Ended ? "EndedLog" : "LogAvailable",
                source.State == ServerLogSourceStates.Ended
                    ? "Showing retained records from the most recent ended managed run."
                    : "Showing records from the exact active managed run.",
                source.State, operationId.ToString("N"), records, cursor, hasMore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or
                                   RegexMatchTimeoutException)
        {
            return new(false, "LogReadUnavailable", "The owned run log could not be read right now.",
                ServerLogSourceStates.Unavailable, operationId.ToString("N"), [], null, false);
        }
    }

    private static long AlignTail(FileStream stream, long proposed)
    {
        if (proposed <= 0) return 0;
        stream.Seek(proposed - 1, SeekOrigin.Begin);
        if (stream.ReadByte() == (byte)'\n') return proposed;
        stream.Seek(proposed, SeekOrigin.Begin);
        var remaining = MaximumRawLineBytes + 1;
        while (remaining-- > 0)
        {
            var value = stream.ReadByte();
            if (value < 0) return stream.Length;
            if (value == (byte)'\n') return stream.Position;
        }
        return stream.Position;
    }

    private void AddRecord(List<byte> line, bool truncated, ServerLogQuery query,
        ServerLogAudience audience, List<ServerLogRecord> records)
    {
        if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
        var parsed = Parse(line.ToArray(), truncated, audience);
        if (query.Severity is not null && !parsed.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase) ||
            query.Category is not null && !parsed.Category.Equals(query.Category, StringComparison.OrdinalIgnoreCase) ||
            query.Stream is not null && !parsed.Stream.Equals(query.Stream, StringComparison.OrdinalIgnoreCase) ||
            query.Contains is not null && !parsed.Message.Contains(query.Contains, StringComparison.OrdinalIgnoreCase))
            return;
        records.Add(parsed);
    }

    protected abstract ServerLogRecord Parse(ReadOnlySpan<byte> line, bool truncated,
        ServerLogAudience audience);

    private static bool TryOffset(string? cursor, Guid operationId, long length, out long offset)
    {
        offset = 0;
        if (cursor is null) return true;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(
                encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            var pieces = text.Split(':');
            return pieces.Length == 3 && pieces[0] == "1" &&
                Guid.TryParseExact(pieces[1], "N", out var cursorRun) && cursorRun == operationId &&
                long.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) &&
                offset >= 0 && offset <= length;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeCursor(Guid operationId, long offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"1:{operationId:N}:{offset}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class ValheimServerLogAdapter : OwnedRunLogAdapter
{
    protected override ServerLogRecord Parse(ReadOnlySpan<byte> line, bool truncated,
        ServerLogAudience audience) =>
        ServerLogSanitizer.ParseValheim(Encoding.UTF8.GetString(line), truncated, audience);
}

internal sealed class MinecraftServerLogAdapter(string kind) : OwnedRunLogAdapter
{
    protected override ServerLogRecord Parse(ReadOnlySpan<byte> line, bool truncated,
        ServerLogAudience audience)
    {
        if (!MinecraftCapturedLogFrame.TryDecode(line, out var captured) || captured is null)
            return MinecraftLogPresentation.Invalid(truncated, audience);
        return MinecraftLogPresentation.Parse(captured, kind, truncated, audience);
    }
}

internal static class ServerLogSanitizer
{
    private const int MaximumMessageCharacters = 2048;
    private const string TruncatedSuffix = " ... [truncated]";
    private static readonly Regex Ansi = new("\\x1B(?:\\][^\\x07]*(?:\\x07|\\x1B\\\\)|[@-_]|\\[[0-?]*[ -/]*[@-~])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Secret = new(
        "(?i)\\b(password|passwd|token|bearer|authorization|credential|invite|pairing(?:\\s+code)?|secret)\\b\\s*[:=]?\\s*(?:bearer\\s+\\S+|\\\"[^\\\"]*\\\"|'[^']*'|\\S+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex InviteCode = new("\\bTS[123]-[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Jwt = new("\\b[A-Za-z0-9_-]{12,}\\.[A-Za-z0-9_-]{12,}\\.[A-Za-z0-9_-]{12,}\\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex OpaqueToken = new("(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/_-]{32,}={0,2}(?![A-Za-z0-9+/=_-])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Ipv4 = new("(?<![0-9])(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})(?:\\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})){3}(?![0-9])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Ipv6 = new("(?i)(?<![0-9A-F:])(?:(?:[0-9A-F]{1,4}:){2,7}[0-9A-F]{0,4}|::(?:[0-9A-F]{1,4}:?){1,7})(?![0-9A-F:])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex WindowsPath = new("(?i)(?:\\\"(?:[A-Z]:\\\\|\\\\\\\\)[^\\\"]*\\\"|'(?:[A-Z]:\\\\|\\\\\\\\)[^']*'|(?:[A-Z]:\\\\|\\\\\\\\)[^\\s\\\"']+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex UnixPath = new("(?<![A-Za-z0-9])(?:/[A-Za-z0-9._-]+){2,}",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex LongIdentifier = new("(?<![0-9])(?:[0-9]{6,20}|[0-9A-Fa-f]{16,64})(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Uuid = new("(?i)\\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Email = new("(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,63}\\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex HostAddress = new(
        "(?i)(?<![A-Z0-9._-])(?:localhost|(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\\.)+[A-Z]{2,63})(?::[0-9]{1,5})?(?![A-Z0-9._-])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex PlayerLine = new(
        "(?i)\\b(chat|player(?:\\s+name)?|character|peer|username|SteamID|PlayFab|XUID|Got connection|Closing socket|RPC_Disconnect)\\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static ServerLogRecord ParseValheim(string raw, bool truncated, ServerLogAudience audience)
    {
        var clean = Clean(raw);
        DateTimeOffset? timestamp = null;
        if (clean.Length >= 21 && clean[19] == ':' &&
            DateTime.TryParseExact(clean.AsSpan(0, 19), "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var local))
        {
            timestamp = new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero);
            clean = clean[20..].TrimStart();
        }

        var severity = clean.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "Error" :
            clean.Contains("warn", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Info";
        var category = PlayerLine.IsMatch(clean) ? "Player" :
            clean.Contains("Game server connected", StringComparison.OrdinalIgnoreCase) ? "Lifecycle" :
            clean.Contains("Connections ", StringComparison.Ordinal) ? "Connections" : "Server";

        return new(timestamp, severity, category, "Server",
            Protect(clean, category, audience, truncated));
    }

    internal static string Clean(string value)
    {
        value = Ansi.Replace(value, "");
        var builder = new StringBuilder(Math.Min(value.Length, MaximumMessageCharacters + 256));
        var lastWasSpace = false;
        foreach (var character in value)
        {
            var category = char.GetUnicodeCategory(character);
            var next = char.IsControl(character) || category is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : character;
            if (next == ' ' && lastWasSpace) continue;
            builder.Append(next);
            lastWasSpace = next == ' ';
        }
        return builder.ToString().Trim();
    }

    internal static string Protect(string clean, string category, ServerLogAudience audience,
        bool truncated)
    {
        var protectedMessage = RedactSecrets(clean);
        if (audience == ServerLogAudience.Friend)
        {
            if (category is "Player" or "Chat")
            {
                protectedMessage = "Player activity redacted.";
            }
            else
            {
                protectedMessage = Ipv4.Replace(protectedMessage, "[redacted-address]");
                protectedMessage = Ipv6.Replace(protectedMessage, "[redacted-address]");
                protectedMessage = HostAddress.Replace(protectedMessage, "[redacted-address]");
                protectedMessage = WindowsPath.Replace(protectedMessage, "[redacted-path]");
                protectedMessage = UnixPath.Replace(protectedMessage, "[redacted-path]");
                protectedMessage = Uuid.Replace(protectedMessage, "[redacted-id]");
                protectedMessage = Email.Replace(protectedMessage, "[redacted-id]");
                protectedMessage = LongIdentifier.Replace(protectedMessage, "[redacted-id]");
            }
        }
        if (truncated) protectedMessage += TruncatedSuffix;
        if (protectedMessage.Length > MaximumMessageCharacters)
            protectedMessage = protectedMessage[..(MaximumMessageCharacters - TruncatedSuffix.Length)] + TruncatedSuffix;
        return protectedMessage;
    }

    private static string RedactSecrets(string value)
    {
        value = Secret.Replace(value, match => match.Groups[1].Value + " [redacted]");
        value = InviteCode.Replace(value, "[redacted-code]");
        value = Jwt.Replace(value, "[redacted-token]");
        value = OpaqueToken.Replace(value, "[redacted-token]");
        return value;
    }

}

internal static class MinecraftLogPresentation
{
    private static readonly Regex Chat = new(
        "(?i)(?:^|\\s)(?:<[^>]{1,64}>|\\[chat\\]|chat(?:ted)?\\b)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Player = new(
        "(?i)\\b(?:player|username|xuid|uuid|joined the game|left the game|connected:|disconnected:)\\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static ServerLogRecord Parse(MinecraftCapturedLine captured, string kind,
        bool rawTruncated, ServerLogAudience audience)
    {
        var clean = ServerLogSanitizer.Clean(captured.Message);
        var severity = clean.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "Error" :
            clean.Contains("warn", StringComparison.OrdinalIgnoreCase) || captured.Stream == "Stderr"
                ? "Warning" : "Info";
        var category = captured.Stream == "Capture" ? "Capture" :
            Chat.IsMatch(clean) ? "Chat" :
            Player.IsMatch(clean) ? "Player" :
            clean.Contains("Done (", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("server started", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("starting minecraft", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("shutdown", StringComparison.OrdinalIgnoreCase) ? "Lifecycle" :
            kind == GameKinds.MinecraftJava ? "Java" : "Bedrock";
        return new(captured.CapturedUtc, severity, category, captured.Stream,
            ServerLogSanitizer.Protect(clean, category, audience,
                rawTruncated || captured.Truncated));
    }

    public static ServerLogRecord Invalid(bool truncated, ServerLogAudience audience) =>
        new(null, "Warning", "Capture", "Capture", ServerLogSanitizer.Protect(
            "A captured console record was incomplete or invalid.", "Capture", audience, truncated));
}
