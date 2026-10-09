using System.Text.Json;

namespace TogetherServer;

internal sealed partial class WorldBackupService
{
    internal const int MaximumBackupLabelLength = 64;
    internal const int MaximumPinnedBackups = 20;
    internal const long MaximumPinnedBackupBytes = 50L * 1024 * 1024 * 1024;
    internal const int MaximumBookmarkListCount = 100;

    internal static BackupBookmarksResult EmptyBookmarks(Guid profileId, string code, string message) =>
        new(false, code, message, profileId, [], 0, 0, MaximumBackupLabelLength,
            MaximumPinnedBackups, MaximumPinnedBackupBytes);

    internal BackupBookmarksResult Bookmarks(Guid profileId)
    {
        lock (sync)
        {
            var records = data.LoadBackupCatalog().Records.Where(item => item.ProfileId == profileId).ToList();
            var pinned = records.Where(item => item.Pinned).ToList();
            long pinnedBytes;
            try { pinnedBytes = pinned.Aggregate(0L, (total, item) => checked(total + Math.Max(0, item.SizeBytes))); }
            catch (OverflowException) { pinnedBytes = long.MaxValue; }
            var views = records.OrderByDescending(item => item.Pinned).ThenByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.Id).Take(MaximumBookmarkListCount).Select(BookmarkView).ToList();
            return new(true, "BackupBookmarks", "Completed backups kept on this Host.", profileId,
                views, pinned.Count, pinnedBytes, MaximumBackupLabelLength, MaximumPinnedBackups,
                MaximumPinnedBackupBytes, records.Count > views.Count);
        }
    }

    internal BackupBookmarkResult UpdateBookmark(Guid profileId, Guid backupId, UpdateBackupBookmarkRequest request)
    {
        lock (sync)
        {
            BackupBookmarkResult Denied(string code, string message) => new(false, code, message, profileId, backupId);
            if (request is null || request.Label is null || request.Pinned is null ||
                !TryBackupLabel(request.Label, out var label))
                return Denied("InvalidBackupBookmark",
                    "Use a name of up to 64 English letters, numbers, spaces or simple punctuation, and choose whether to pin it.");
            var catalog = data.LoadBackupCatalog();
            var matches = catalog.Records.Where(item => item.ProfileId == profileId && item.Id == backupId).ToList();
            if (backupId == Guid.Empty || matches.Count != 1)
                return Denied("BackupNotFound", "Choose one completed backup for this server.");
            var record = matches[0];
            try
            {
                // Metadata is opened below the anchored local-data directory with
                // native no-reparse handles. No live world or payload file is opened.
                using var marker = BackupBookmarkCompletion.Open(data, profileId, backupId);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(marker.Stream, Json);
                if (manifest is null || manifest.Files is null || manifest.Files.Any(item => item is null) ||
                    manifest.BackupId != record.Id || manifest.ProfileId != record.ProfileId ||
                    manifest.Kind != record.Kind || manifest.WorldId != record.WorldId ||
                    manifest.BackupKind != record.BackupKind || manifest.CreatedUtc != record.CreatedUtc ||
                    record.CreatedUtc == default || record.FileCount < 0 ||
                    manifest.Files.Count != record.FileCount || record.SizeBytes < 0 ||
                    manifest.Files.Any(item => item.Length < 0 || string.IsNullOrEmpty(item.Path) ||
                        item.Path.StartsWith('/') || item.Path.Contains('\\') || item.Path.Contains(':') ||
                        item.Path.Split('/').Any(part => part is "" or "." or "..") ||
                        item.Sha256 is not { Length: 64 } || !item.Sha256.All(char.IsAsciiHexDigit)) ||
                    manifest.Files.Aggregate(0L, (total, item) => checked(total + item.Length)) != record.SizeBytes ||
                    record.SetupIncluded != (manifest.SetupSha256 is not null) ||
                    manifest.SetupSha256 is { } setupSha && (setupSha.Length != 64 || !setupSha.All(char.IsAsciiHexDigit)))
                    return Denied("BackupCompletionInvalid", "That completed backup's saved metadata could not be verified.");

                if (request.Pinned.Value && !record.Pinned)
                {
                    var pinned = catalog.Records.Where(item => item.ProfileId == profileId && item.Pinned).ToList();
                    if (pinned.Count >= MaximumPinnedBackups)
                        return Denied("PinnedBackupLimitReached", "This server already has 20 pinned backups. Unpin one before pinning another.");
                    if (pinned.Any(item => item.SizeBytes < 0))
                        return Denied("PinnedBackupCapacityUnavailable", "Pinned backup sizes could not be verified. Review the backup catalog before pinning another.");
                    long total;
                    try { total = pinned.Aggregate(record.SizeBytes, (value, item) => checked(value + item.SizeBytes)); }
                    catch (OverflowException) { total = long.MaxValue; }
                    if (total > MaximumPinnedBackupBytes)
                        return Denied("PinnedBackupCapacityReached", "Pinned backups for this server can total at most 50 GB. Unpin a backup or copy it to a vault first.");
                }
                record.Label = label;
                record.Pinned = request.Pinned.Value;
                data.SaveBackupCatalog(catalog);
                data.TryAudit($"backup-bookmark {profileId} {backupId} pinned={record.Pinned} {clock.GetUtcNow():O}");
                return new(true, "BackupBookmarkUpdated", record.Pinned
                    ? "Backup named and protected from automatic retention."
                    : "Backup name saved. Automatic retention can remove this unpinned backup after a later backup.",
                    profileId, backupId, BookmarkView(record));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or
                                       JsonException or OverflowException)
            {
                return Denied("BackupBookmarkUnavailable", "The backup name and pin could not be saved. Refresh and try again.");
            }
        }
    }

    private static BackupBookmarkView BookmarkView(WorldBackupRecord item) =>
        new(item.Id, item.ProfileId, item.CreatedUtc,
            item.BackupKind is BackupKinds.Rolling or BackupKinds.Manual or BackupKinds.PreRestore
                ? item.BackupKind : "Unavailable",
            Math.Max(0, item.SizeBytes), TryBackupLabel(item.Label, out var label) ? label : "", item.Pinned);

    internal static bool TryBackupLabel(string? value, out string label)
    {
        label = value?.Trim(' ') ?? "";
        return value is not null && value.Length <= MaximumBackupLabelLength &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or ',' or
                '\'' or '(' or ')' or '-' or '_' or '!') &&
            (label.Length == 0 || label.Any(char.IsAsciiLetterOrDigit));
    }
}
