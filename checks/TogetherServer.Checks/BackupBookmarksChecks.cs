using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

// Callable from a separate code/file-only harness. This never launches an app,
// game, fixture, console helper, listener, dialog, browser, or desktop window.
internal static class BackupBookmarksChecks
{
    internal static void Run(string root)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        var checkRoot = Path.Combine(Path.GetFullPath(root), "backup-bookmarks-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(checkRoot, "synthetic-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "synthetic.bin"), "disposable bytes");
        var clock = new BookmarkClock();
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Fixture,
            Name = "Disposable backup bookmarks",
            WorldId = "disposable",
            WorldDirectory = source,
            Backups = new() { MinimumFreeSpaceMb = 0, RetentionCount = 1 }
        };
        var dataRoot = Path.Combine(checkRoot, "app-data");
        Guid firstId;
        Guid lastId;
        byte[] firstMarker;
        byte[] firstPayloadHash;
        string DirectoryFor(LocalData local, Guid id) => Path.Combine(local.BackupsRoot, profile.Id.ToString("N"), id.ToString("N") + ".backup");
        using (var data = new LocalData(dataRoot))
        {
            data.SaveSettings(new HostSettings { Profiles = [profile] });
            var service = new WorldBackupService(data, clock, _ => long.MaxValue);
            WorldBackupRecord MakeBackup()
            {
                clock.Advance();
                var result = service.Create(profile, BackupKinds.Manual);
                Require(result.Ok && result.Backup is not null, "disposable backup did not complete");
                return result.Backup!;
            }
            var first = MakeBackup();
            firstId = first.Id;
            var directory = DirectoryFor(data, firstId);
            firstMarker = File.ReadAllBytes(Path.Combine(directory, "complete.json"));
            firstPayloadHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "payload", "synthetic.bin")));
            Require(service.UpdateBookmark(profile.Id, firstId, new("Before a game update", true)).Ok, "completed backup could not be named and pinned");
            Require(!service.UpdateBookmark(Guid.NewGuid(), firstId, new("Other server", false)).Ok,
                "another profile changed a pinned backup");
            Require(!service.UpdateBookmark(profile.Id, Guid.NewGuid(), new("Other backup", false)).Ok,
                "a missing backup ID was accepted");
            foreach (var unsafeName in new[] { "..\\world", "/world", "bad\nname", "bad\u202Ename", "---", new string('x', 65) })
                Require(service.UpdateBookmark(profile.Id, firstId, new(unsafeName, false)).Code == "InvalidBackupBookmark",
                    "an unsafe/unbounded name changed a pinned backup");
            Require(!service.UpdateBookmark(profile.Id, firstId, new(null, false)).Ok &&
                    !service.UpdateBookmark(profile.Id, firstId, new("Name", null)).Ok,
                "missing bookmark fields were accepted");
            Require(File.ReadAllBytes(Path.Combine(directory, "complete.json")).SequenceEqual(firstMarker) &&
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "payload", "synthetic.bin"))).SequenceEqual(firstPayloadHash),
                "a bookmark changed immutable backup bytes or hashes");

            var catalogPath = Path.Combine(dataRoot, "backups.json");
            using (var hold = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(!service.UpdateBookmark(profile.Id, firstId, new("Failed unpin", false)).Ok,
                    "a failed catalog replacement reported success");
                Require(service.Bookmarks(profile.Id).Backups.Single().Pinned &&
                        service.Bookmarks(profile.Id).Backups.Single().Label == "Before a game update",
                    "failed persistence silently dropped a pin or changed its name");
            }
            File.WriteAllText(Path.Combine(directory, "complete.json"), "{}");
            Require(!service.UpdateBookmark(profile.Id, firstId, new("Partial completion", false)).Ok &&
                    service.Bookmarks(profile.Id).Backups.Single().Pinned,
                "partial completion allowed unpin or pin loss");
            File.WriteAllBytes(Path.Combine(directory, "complete.json"), firstMarker);
            var payload = Path.Combine(directory, "payload");
            var preservedPayload = Path.Combine(directory, "payload-preserved");
            Directory.Move(payload, preservedPayload);
            Require(!service.UpdateBookmark(profile.Id, firstId, new("Missing payload", false)).Ok,
                "a missing completed payload was accepted");
            Directory.Move(preservedPayload, payload);

            for (var index = 1; index < WorldBackupService.MaximumPinnedBackups; index++)
            {
                var backup = MakeBackup();
                Require(service.UpdateBookmark(profile.Id, backup.Id, new("Saved checkpoint " + index, true)).Ok,
                    "a pin below the count and capacity limits failed");
            }
            var candidate = MakeBackup();
            lastId = candidate.Id;
            Require(service.UpdateBookmark(profile.Id, candidate.Id, new("Over limit", true)).Code == "PinnedBackupLimitReached",
                "the per-profile pin count limit was not enforced");
            Require(service.List(profile.Id).Count == WorldBackupService.MaximumPinnedBackups + 1 && Directory.Exists(directory),
                "automatic retention removed a pinned backup or changed the unpinned retention count");
        }
        using (var reopened = new LocalData(dataRoot))
        {
            var service = new WorldBackupService(reopened, clock, _ => long.MaxValue);
            var first = service.Bookmarks(profile.Id).Backups.Single(item => item.BackupId == firstId);
            Require(first is { Label: "Before a game update", Pinned: true }, "name/pin did not survive storage reopening");
            Require(service.UpdateBookmark(profile.Id, firstId, new("", false)).Ok && Directory.Exists(DirectoryFor(reopened, firstId)),
                "unpin deleted a backup immediately or could not clear its name");

            var catalog = reopened.LoadBackupCatalog();
            var other = catalog.Records.First(item => item.Pinned);
            other.SizeBytes = WorldBackupService.MaximumPinnedBackupBytes;
            reopened.SaveBackupCatalog(catalog);
            Require(service.UpdateBookmark(profile.Id, lastId, new("Capacity", true)).Code == "PinnedBackupCapacityReached",
                "the aggregate pin byte limit was not enforced");
            other.SizeBytes = -1;
            reopened.SaveBackupCatalog(catalog);
            Require(service.UpdateBookmark(profile.Id, lastId, new("Corrupt capacity", true)).Code == "PinnedBackupCapacityUnavailable",
                "a corrupt pinned size was treated as spare capacity");
            catalog.Records.Add(new WorldBackupRecord
            {
                Id = other.Id,
                ProfileId = other.ProfileId,
                Kind = other.Kind,
                WorldId = other.WorldId,
                CreatedUtc = other.CreatedUtc - TimeSpan.FromDays(1),
                SizeBytes = 0,
                Pinned = false
            });
            reopened.SaveBackupCatalog(catalog);
            clock.Advance();
            Require(service.Create(profile, BackupKinds.Manual).Ok && Directory.Exists(DirectoryFor(reopened, other.Id)),
                "retention deleted a pinned record through corrupt size or duplicate unpinned metadata");
            Require(!Directory.Exists(DirectoryFor(reopened, firstId)), "later retention did not reclaim a deliberately unpinned backup");
        }

        // Bad typed pin state quarantines the catalog. Existing completed copies
        // become unlisted, but automatic retention must never delete them.
        var corruptRoot = Path.Combine(checkRoot, "corrupt-catalog");
        using (var data = new LocalData(corruptRoot))
        {
            var service = new WorldBackupService(data, clock, _ => long.MaxValue);
            clock.Advance();
            var backup = service.Create(profile, BackupKinds.Manual).Backup!;
            Require(service.UpdateBookmark(profile.Id, backup.Id, new("Protected evidence", true)).Ok, "corruption baseline pin failed");
            var json = File.ReadAllText(Path.Combine(corruptRoot, "backups.json"));
            File.WriteAllText(Path.Combine(corruptRoot, "backups.json"), json.Replace("\"pinned\": true", "\"pinned\": \"invalid\"", StringComparison.Ordinal));
            clock.Advance();
            Require(service.Create(profile, BackupKinds.Manual).Ok && Directory.Exists(DirectoryFor(data, backup.Id)),
                "an unreadable pin catalog let retention delete protected completed evidence");
        }
        var encoded = JsonSerializer.Serialize(new UpdateBackupBookmarkRequest("Small name", true));
        Require(encoded.Contains("Small name", StringComparison.Ordinal), "bookmark request did not serialize");
        var unknownFieldRejected = false;
        try
        {
            JsonSerializer.Deserialize<UpdateBackupBookmarkRequest>(
                "{\"label\":\"Safe name\",\"pinned\":true,\"path\":\"arbitrary\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException) { unknownFieldRejected = true; }
        Require(unknownFieldRejected, "a bookmark request accepted an arbitrary path field");
    }

    private sealed class BookmarkClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-08T15:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance() => now += TimeSpan.FromSeconds(30);
    }
}
