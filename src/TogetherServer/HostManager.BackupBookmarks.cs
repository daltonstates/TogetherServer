using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateBackupBookmarkRequest(string? Label, bool? Pinned);

public sealed record BackupBookmarkView(Guid BackupId, Guid ProfileId, DateTimeOffset CreatedUtc,
    string BackupKind, long SizeBytes, string Label, bool Pinned);

public sealed record BackupBookmarksResult(bool Ok, string Code, string Message, Guid ProfileId,
    IReadOnlyList<BackupBookmarkView> Backups, int PinnedCount, long PinnedSizeBytes,
    int MaximumLabelLength, int MaximumPinnedCount, long MaximumPinnedSizeBytes,
    bool MoreBackupsAvailable = false);

public sealed record BackupBookmarkResult(bool Ok, string Code, string Message, Guid ProfileId,
    Guid BackupId, BackupBookmarkView? Backup = null);

public sealed partial class HostManager
{
    public async Task<BackupBookmarksResult> BackupBookmarksAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            if (!settings.Profiles.Any(item => item.Id == profileId))
                return WorldBackupService.EmptyBookmarks(profileId, "UnknownProfile", "Choose a saved Host server.");
            return backups.Bookmarks(profileId);
        }
        finally { gate.Release(); }
    }

    public async Task<BackupBookmarkResult> UpdateBackupBookmarkAsync(Guid profileId, Guid backupId,
        UpdateBackupBookmarkRequest request)
    {
        await gate.WaitAsync();
        try
        {
            if (!settings.Profiles.Any(item => item.Id == profileId))
                return new(false, "UnknownProfile", "Choose a saved Host server.", profileId, backupId);
            var result = backups.UpdateBookmark(profileId, backupId, request);
            if (result.Ok)
            {
                Activity("Backup", "BookmarkUpdated", "A completed backup's name or retention pin was updated.",
                    ActivitySeverity.Info, profileId);
                AdvanceReadModel();
            }
            return result;
        }
        finally { gate.Release(); }
    }
}
