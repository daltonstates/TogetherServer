using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

public static class BackupEvidenceKinds
{
    public const string Integrity = "Integrity";
    public const string Vault = "Vault";
    public const string HashRehearsal = "HashRehearsal";
    public const string OwnerGameRehearsal = "OwnerGameRehearsal";
    internal static bool Valid(string value) => value is Integrity or Vault or HashRehearsal or OwnerGameRehearsal;
}

public sealed record BackupEvidenceView(Guid BackupId, string Kind, string Outcome, DateTimeOffset CheckedUtc, string Code);
public sealed record BackupCatalogSummaryView(Guid BackupId, Guid ProfileId, DateTimeOffset CreatedUtc,
    string BackupKind, long SizeBytes, string Label, bool Pinned, string? GameKind, string? WorldId,
    int? FileCount, bool? SetupIncluded, string? PayloadSha256, string? SetupSha256,
    bool MetadataAvailable, IReadOnlyList<BackupEvidenceView> Evidence);
public sealed record BackupRetentionView(int RetentionCount, long MinimumFreeSpaceBytes, bool RollingEnabled);
public sealed record BackupCatalogView(bool Ok, string Code, string Message, Guid ProfileId,
    IReadOnlyList<BackupCatalogSummaryView> Backups, int PinnedCount, long PinnedSizeBytes,
    int MaximumLabelLength, int MaximumPinnedCount, long MaximumPinnedSizeBytes,
    bool MoreBackupsAvailable, BackupRetentionView? Retention, long? RetainedSizeBytes,
    long? AvailableSpaceBytes, bool EvidenceAvailable);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SavedBackupEvidence(Guid ProfileId, Guid BackupId, string CompletionIdentity,
    string Kind, string Outcome, DateTimeOffset CheckedUtc, string Code);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupEvidenceState(int Schema, IReadOnlyList<SavedBackupEvidence> Results);
internal sealed record BackupCompletionSummary(string CompletionIdentity, string PayloadSha256, string? SetupSha256);

// Keep the fixed marker and its anchored ancestors immutable across the measured
// operation and its optional evidence write, rather than comparing two snapshots.
internal sealed class BackupEvidenceLease(BackupBookmarkCompletion marker, string completionIdentity) : IDisposable
{
    internal string CompletionIdentity { get; } = completionIdentity;
    public void Dispose() => marker.Dispose();
}

internal sealed partial class WorldBackupService
{
    private const string BackupEvidenceName = "backup-evidence-v1.protected";
    internal const int MaximumBackupEvidenceResults = 400;
    private const int MaximumBackupEvidenceBytes = 256 * 1024;
    // Bounds completion reads for one catalog projection. Exhaustion is Unknown,
    // never an integrity failure and never a request to inspect the live world.
    private const long MaximumCatalogCompletionBytes = 32L * 1024 * 1024;
    private const long MaximumUiInteger = 9_007_199_254_740_991;

    internal static BackupCatalogView EmptyCatalog(Guid profileId, string code, string message) =>
        new(false, code, message, profileId, [], 0, 0, MaximumBackupLabelLength,
            MaximumPinnedBackups, MaximumPinnedBackupBytes, false, null, null, null, false);

    internal BackupCatalogView Catalog(ServerProfile profile)
    {
        lock (sync)
        {
            var records = data.LoadBackupCatalog().Records.Where(item => item.ProfileId == profile.Id).ToList();
            if (records.Any(item => item.Id == Guid.Empty || item.CreatedUtc == default ||
                    item.SizeBytes is < 0 or > MaximumUiInteger) ||
                records.Select(item => item.Id).Distinct().Count() != records.Count)
                return EmptyCatalog(profile.Id, "BackupCatalogUnavailable", "The completed catalog needs local review.");
            var bookmarks = Bookmarks(profile.Id);
            var saved = LoadBackupEvidence(out var evidenceAvailable);
            var shown = new List<BackupCatalogSummaryView>();
            var completionBudget = MaximumCatalogCompletionBytes;
            foreach (var bookmark in bookmarks.Backups)
            {
                var record = records.Single(item => item.Id == bookmark.BackupId);
                var summary = ReadCatalogCompletion(record, ref completionBudget);
                BackupEvidenceView[] facts = summary is null ? [] : saved.Where(item => item.ProfileId == profile.Id && item.BackupId == record.Id &&
                        item.CompletionIdentity == summary.CompletionIdentity)
                    .OrderBy(item => item.Kind).Select(item => new BackupEvidenceView(item.BackupId, item.Kind,
                        item.Outcome, item.CheckedUtc, item.Code)).ToArray();
                shown.Add(new(record.Id, profile.Id, record.CreatedUtc, bookmark.BackupKind, record.SizeBytes,
                    bookmark.Label, bookmark.Pinned,
                    CatalogGame(record.Kind) ? record.Kind : null, CatalogWorld(record.WorldId) ? record.WorldId : null,
                    summary is null ? null : record.FileCount, summary is null ? null : record.SetupIncluded,
                    summary?.PayloadSha256, summary?.SetupSha256, summary is not null, facts));
            }
            var retention = profile.Backups.RetentionCount is >= 1 and <= 50 &&
                            profile.Backups.MinimumFreeSpaceMb is >= 0 and <= 1_048_576
                ? new BackupRetentionView(profile.Backups.RetentionCount, profile.Backups.MinimumFreeSpaceMb * 1024 * 1024,
                    profile.Backups.Enabled) : null;
            long? retainedBytes;
            try
            {
                var sum = records.Aggregate(0L, (total, item) => checked(total + item.SizeBytes));
                retainedBytes = sum <= MaximumUiInteger ? sum : null;
            }
            catch (OverflowException) { retainedBytes = null; }
            long? freeBytes = null;
            try { var measured = availableSpace(data.BackupsRoot); if (measured is >= 0 and <= MaximumUiInteger) freeBytes = measured; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                NotSupportedException or System.ComponentModel.Win32Exception)
            { }
            if (bookmarks.PinnedSizeBytes > MaximumUiInteger)
                return EmptyCatalog(profile.Id, "BackupCatalogUnavailable", "Pinned capacity needs local review.");
            return new(true, "BackupCatalog", "Completed backup metadata and separate dated outcomes on this Host.",
                profile.Id, shown, bookmarks.PinnedCount, bookmarks.PinnedSizeBytes, MaximumBackupLabelLength,
                MaximumPinnedBackups, MaximumPinnedBackupBytes, bookmarks.MoreBackupsAvailable, retention,
                retainedBytes, freeBytes, evidenceAvailable);
        }
    }

    // Uses anchored completion handles only. This does not hash payload bytes,
    // inspect setup contents, invoke retention, launch a game, or claim integrity.
    private BackupCompletionSummary? ReadCatalogCompletion(WorldBackupRecord record, ref long remainingBytes)
    {
        try
        {
            using var marker = BackupBookmarkCompletion.Open(data, record.ProfileId, record.Id);
            if (marker.Stream.Length > remainingBytes) return null;
            remainingBytes -= marker.Stream.Length;
            return ReadCompletionSummary(record, marker.Stream);
        }
        catch (Exception ex) when (BackupEvidenceFailure(ex)) { return null; }
    }

    private static BackupCompletionSummary? ReadCompletionSummary(WorldBackupRecord record, Stream stream)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
        if (manifest is null || !CatalogGame(record.Kind) || !CatalogWorld(record.WorldId) ||
            manifest.BackupId != record.Id || manifest.ProfileId != record.ProfileId ||
            manifest.Kind != record.Kind || manifest.WorldId != record.WorldId ||
            manifest.BackupKind != record.BackupKind || manifest.CreatedUtc != record.CreatedUtc ||
            record.BackupKind is not (BackupKinds.Rolling or BackupKinds.Manual or BackupKinds.PreRestore) ||
            record.CreatedUtc == default || record.FileCount < 0 || record.SizeBytes < 0 ||
            manifest.Files is null || manifest.Files.Any(item => item is null) || manifest.Files.Count != record.FileCount ||
            manifest.Files.Any(item => item.Length < 0 || string.IsNullOrEmpty(item.Path) ||
                item.Path.StartsWith('/') || item.Path.Contains('\\') || item.Path.Contains(':') ||
                item.Path.Split('/').Any(part => part is "" or "." or "..") || !ValidEvidenceHash(item.Sha256)) ||
            manifest.Files.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
            manifest.Files.Aggregate(0L, (total, item) => checked(total + item.Length)) != record.SizeBytes ||
            record.SetupIncluded != (manifest.SetupSha256 is not null) ||
            manifest.SetupSha256 is { } setup && !ValidEvidenceHash(setup)) return null;
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest.Files.OrderBy(item => item.Path, StringComparer.Ordinal)
            .Select(item => new BackupManifestFile(item.Path, item.Length, item.Sha256.ToUpperInvariant())).ToArray());
        // Same canonical completion identity as ReadVerifiedWorldLoadSource.
        return new(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest))),
            Convert.ToHexString(SHA256.HashData(payload)), manifest.SetupSha256?.ToUpperInvariant());
    }

    internal BackupEvidenceLease? LeaseBackupEvidence(Guid profileId, Guid backupId)
    {
        lock (sync)
        {
            BackupBookmarkCompletion? marker = null;
            try
            {
                var matches = data.LoadBackupCatalog().Records.Where(item => item.ProfileId == profileId && item.Id == backupId).ToArray();
                if (matches.Length != 1) return null;
                marker = BackupBookmarkCompletion.Open(data, profileId, backupId);
                var summary = ReadCompletionSummary(matches[0], marker.Stream);
                if (summary is null) return null;
                var retained = new BackupEvidenceLease(marker, summary.CompletionIdentity);
                marker = null; // Ownership moves to the caller through measurement and recording.
                return retained;
            }
            catch (Exception ex) when (BackupEvidenceFailure(ex)) { return null; }
            finally { marker?.Dispose(); }
        }
    }

    internal string? BackupEvidenceIdentity(Guid profileId, Guid backupId)
    {
        lock (sync)
        {
            var matches = data.LoadBackupCatalog().Records.Where(item => item.ProfileId == profileId && item.Id == backupId).ToArray();
            if (matches.Length != 1) return null;
            var budget = MaximumCatalogCompletionBytes;
            return ReadCatalogCompletion(matches[0], ref budget)?.CompletionIdentity;
        }
    }

    internal Guid? BackupIdForEvidenceIdentity(Guid profileId, string expectedCompletion)
    {
        lock (sync)
        {
            var budget = MaximumCatalogCompletionBytes;
            var matches = new List<Guid>();
            foreach (var item in data.LoadBackupCatalog().Records.Where(item => item.ProfileId == profileId)
                         .OrderByDescending(item => item.Pinned).ThenByDescending(item => item.CreatedUtc).Take(MaximumBookmarkListCount))
                if (ReadCatalogCompletion(item, ref budget)?.CompletionIdentity == expectedCompletion) matches.Add(item.Id);
            return matches.Count == 1 ? matches[0] : null;
        }
    }

    internal bool RecordBackupEvidence(Guid profileId, Guid backupId, string? expectedCompletion,
        string kind, string outcome, DateTimeOffset checkedUtc, string code)
    {
        lock (sync)
        {
            try
            {
                if (!ValidEvidenceHash(expectedCompletion) || !BackupEvidenceKinds.Valid(kind) ||
                    outcome is not ("Passed" or "Failed" or "Incomplete") || checkedUtc == default ||
                    checkedUtc > clock.GetUtcNow() || !ValidEvidenceCode(code) || !ValidEvidenceOutcome(kind, outcome, code)) return false;
                if (BackupEvidenceIdentity(profileId, backupId) != expectedCompletion) return false;
                var saved = LoadBackupEvidence(out var available);
                if (!available) return false;
                if (saved.Any(item => item.ProfileId == profileId && item.BackupId == backupId && item.Kind == kind &&
                    item.CompletionIdentity == expectedCompletion && (item.CheckedUtc > checkedUtc ||
                        item.CheckedUtc == checkedUtc && item.Outcome == "Failed" && outcome != "Failed"))) return true;
                // A changed completion drops prior credit. The newest outcome of
                // each stage replaces that stage only, including failed retries.
                saved.RemoveAll(item => item.ProfileId == profileId && item.BackupId == backupId &&
                    (item.CompletionIdentity != expectedCompletion || item.Kind == kind));
                saved.Add(new(profileId, backupId, expectedCompletion!, kind, outcome, checkedUtc, code));
                var retained = data.LoadBackupCatalog().Records.Select(item => (item.ProfileId, item.Id)).ToHashSet();
                saved = saved.Where(item => retained.Contains((item.ProfileId, item.BackupId)))
                    .OrderByDescending(item => item.CheckedUtc).ThenBy(item => item.BackupId).ThenBy(item => item.Kind, StringComparer.Ordinal)
                    .Take(MaximumBackupEvidenceResults).ToList();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new BackupEvidenceState(1, saved));
                if (bytes.Length > MaximumBackupEvidenceBytes) return false;
                data.SaveProtected(BackupEvidenceName, bytes);
                return true;
            }
            catch (Exception ex) when (BackupEvidenceFailure(ex)) { return false; }
        }
    }

    private List<SavedBackupEvidence> LoadBackupEvidence(out bool available)
    {
        available = false;
        try
        {
            var existed = data.HasProtected(BackupEvidenceName);
            var bytes = data.LoadProtected(BackupEvidenceName);
            if (bytes is null)
            {
                available = !existed && !data.Recovery.Notices.Any(item => item.StateFile == BackupEvidenceName);
                return [];
            }
            if (bytes.Length is <= 0 or > MaximumBackupEvidenceBytes) return [];
            var state = JsonSerializer.Deserialize<BackupEvidenceState>(bytes);
            if (state is null || state.Schema != 1 || state.Results is null || state.Results.Count > MaximumBackupEvidenceResults ||
                state.Results.Any(item => item is null || item.ProfileId == Guid.Empty || item.BackupId == Guid.Empty ||
                    !ValidEvidenceHash(item.CompletionIdentity) || !BackupEvidenceKinds.Valid(item.Kind) ||
                    item.Outcome is not ("Passed" or "Failed" or "Incomplete") || item.CheckedUtc == default ||
                    item.CheckedUtc > clock.GetUtcNow() || !ValidEvidenceCode(item.Code) || !ValidEvidenceOutcome(item.Kind, item.Outcome, item.Code)) ||
                state.Results.Select(item => (item.ProfileId, item.BackupId, item.Kind)).Distinct().Count() != state.Results.Count)
                return [];
            available = true;
            return state.Results.ToList();
        }
        catch (Exception ex) when (BackupEvidenceFailure(ex)) { return []; }
    }

    private static bool ValidEvidenceHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    private static bool ValidEvidenceCode(string? code) => code is { Length: >= 1 and <= 80 } &&
        char.IsAsciiLetter(code[0]) && code.All(char.IsAsciiLetterOrDigit);
    private static bool ValidEvidenceOutcome(string kind, string outcome, string code) => (kind, outcome, code) is
        (BackupEvidenceKinds.Integrity, "Passed", "BackupVerified") or
        (BackupEvidenceKinds.Integrity, "Failed", "BackupIntegrityFailed") or
        (BackupEvidenceKinds.Vault, "Passed", "VaultCopyVerified") or
        (BackupEvidenceKinds.Vault, "Failed", "VaultCopyFailed") or
        (BackupEvidenceKinds.HashRehearsal, "Passed", "RestoreRehearsalCompleted") or
        (BackupEvidenceKinds.HashRehearsal, "Failed", "RestoreRehearsalFailed") or
        (BackupEvidenceKinds.OwnerGameRehearsal, "Passed", "OwnerGameRehearsalConfirmed") or
        (BackupEvidenceKinds.OwnerGameRehearsal, "Failed", "OwnerGameRehearsalFailed") or
        (BackupEvidenceKinds.OwnerGameRehearsal, "Incomplete", "OwnerGameRehearsalIncomplete");
    private static bool CatalogGame(string? game) => game is GameKinds.Valheim or GameKinds.MinecraftJava or
        GameKinds.MinecraftBedrock or GameKinds.Factorio or GameKinds.Terraria or GameKinds.Fixture;
    private static bool CatalogWorld(string? world) => world is { Length: >= 1 and <= 128 } &&
        !world.Any(character => character is '\\' or '/' or ':' || char.IsControl(character) ||
            char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);
    internal static bool BackupEvidenceFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or
        CryptographicException or JsonException or OverflowException or ArgumentException;
}
