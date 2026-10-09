using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// No app, process, native UI, listener, console or production state is used by these checks.
internal static class DesktopQolChecks
{
    internal static void RunDrafts()
    {
        byte[]? persisted = null;
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var store = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        var identity = new ProtectedUiDraftIdentity("chat", Guid.NewGuid(), Guid.NewGuid(), "composer");
        var empty = store.Read(identity);
        Require(empty.Ok && empty.Text is null && empty.Revision == 0, "Empty draft scope");
        var first = store.Save(identity, "unfinished private edit", empty.Revision);
        Require(first.Ok && first.Revision > empty.Revision, "Draft persists");
        var restarted = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        Require(restarted.Read(identity).Text == "unfinished private edit", "Restart recovery");
        var stale = restarted.Save(identity, "stale edit", empty.Revision);
        Require(!stale.Ok && stale.Text == first.Text && stale.Revision == first.Revision, "CAS protects newer content");
        var cleared = restarted.Clear(identity, first.Revision);
        Require(cleared.Ok && cleared.Text is null && cleared.Revision > first.Revision, "Clear tombstone");
        Require(!restarted.Save(identity, "resurrection", first.Revision).Ok, "Stale save cannot undo clear");
        now += TimeSpan.FromDays(31);
        var absent = restarted.Read(identity);
        Require(absent.Text is null && absent.Revision > cleared.Revision, "Pruning advances absence floor");
        Require(!restarted.Save(identity, "old cleared edit", cleared.Revision).Ok, "Pruned clear remains stale");
        var other = identity with { ProfileId = Guid.NewGuid() };
        var afterRetention = restarted.Save(identity, "reviewed fresh edit", absent.Revision);
        Require(afterRetention.Ok && restarted.Read(other).Text is null, "Scope isolation");
        var reserved = new ProtectedUiDraftIdentity("settings", Guid.Empty, null, "host-setup");
        Require(restarted.Save(reserved, "{\"step\":2}", restarted.Read(reserved).Revision).Ok, "Reserved local setup key");
        Reject(() => ProtectedUiDraftStore.ValidateIdentity(reserved with { Purpose = "file" }), "Empty profile denied");
        Reject(() => ProtectedUiDraftStore.ValidateIdentity(identity with { Key = "../secret" }), "Path denied");
        Reject(() => restarted.Save(identity, new string('界', 22000), afterRetention.Revision), "UTF-8 byte cap");
        var file = identity with { Purpose = "file", ConnectionId = null, Key = "properties" };
        Require(restarted.Save(file, new string('a', 512 * 1024), restarted.Read(file).Revision).Ok,
            "Large reviewed file drafts use their separate 2 MiB cap");
        Reject(() => restarted.Save(file, new string('a', ProtectedUiDraftStore.MaximumFileDraftBytes + 1),
            restarted.Read(file).Revision), "File draft remains bounded");
        foreach (var invalid in new[]
                 {
                     "{\"purpose\":\"chat\",\"purpose\":\"file\",\"profileId\":\"" + identity.ProfileId + "\",\"key\":\"composer\",\"text\":\"x\",\"expectedRevision\":0}",
                     "{\"purpose\":\"chat\",\"profileId\":\"" + identity.ProfileId + "\",\"key\":\"composer\",\"path\":\"private\",\"text\":\"x\",\"expectedRevision\":0}",
                     "{\"purpose\":\"chat\",\"profileId\":\"" + identity.ProfileId + "\",\"key\":\"composer\",\"text\":\"x\",\"expectedRevision\":\"1\"}"
                 })
            Reject(() => { using var input = JsonDocument.Parse(invalid); ProtectedUiDraftStore.ParseSave(input.RootElement); },
                "Strict fixed draft schema");

        byte[]? boundedBytes = null;
        var bounded = new ProtectedUiDraftStore(() => boundedBytes, bytes => boundedBytes = bytes, () => now);
        for (var index = 0; index < ProtectedUiDraftStore.MaximumEntries; index++)
        {
            var scope = identity with { Key = "draft-" + index };
            Require(bounded.Save(scope, "draft", bounded.Read(scope).Revision).Ok, "128 bounded entries");
        }
        var excess = identity with { Key = "excess" };
        Require(!bounded.Save(excess, "draft", bounded.Read(excess).Revision).Ok, "Unfinished drafts never silently evicted");
        var oldest = identity with { Key = "draft-0" };
        var oldestRevision = bounded.Read(oldest).Revision;
        var oldestClear = bounded.Clear(oldest, oldestRevision);
        Require(oldestClear.Ok, "Full store clear");
        Require(bounded.Save(excess, "draft", bounded.Read(excess).Revision).Ok, "Cleared slot compacted");
        var compacted = bounded.Read(oldest);
        Require(compacted.Text is null && compacted.Revision > oldestClear.Revision &&
            !bounded.Save(oldest, "stale", oldestRevision).Ok &&
            !bounded.Save(oldest, "stale cleared value", oldestClear.Revision).Ok, "Compacted tombstone cannot resurrect");
        var compactedRestart = new ProtectedUiDraftStore(() => boundedBytes, bytes => boundedBytes = bytes, () => now);
        Require(compactedRestart.Read(oldest).Revision == compacted.Revision &&
            !compactedRestart.Save(oldest, "restart replay", oldestClear.Revision).Ok, "Compacted floor survives restart");
        var unrelatedMissing = identity with { Key = "still-missing" };
        var missingRevision = compactedRestart.Read(unrelatedMissing).Revision;
        var existing = identity with { Key = "draft-1" };
        Require(compactedRestart.Save(existing, "later ordinary edit", compactedRestart.Read(existing).Revision).Ok &&
            compactedRestart.Read(unrelatedMissing).Revision == missingRevision,
            "Ordinary writes after compaction leave absent identities unchanged");
        var fullClear = compactedRestart.Clear(unrelatedMissing, missingRevision);
        Require(fullClear.Ok && fullClear.Text is null && fullClear.Revision > missingRevision &&
            !compactedRestart.Save(unrelatedMissing, "replay after absence-only clear", missingRevision).Ok,
            "A full store can compact an absence-only tombstone without permitting replay");
        Require(boundedBytes?.Length <= ProtectedUiDraftStore.MaximumStoreBytes, "Total protected bytes bounded");

        byte[]? hugeBytes = null;
        var huge = new ProtectedUiDraftStore(() => hugeBytes, bytes => hugeBytes = bytes, () => now);
        var stoppedByTotal = false;
        for (var index = 0; index < ProtectedUiDraftStore.MaximumEntries; index++)
        {
            var scope = identity with { Key = "large-" + index };
            if (!huge.Save(scope, new string('a', ProtectedUiDraftStore.MaximumDraftBytes), huge.Read(scope).Revision).Ok)
            { stoppedByTotal = true; break; }
        }
        Require(stoppedByTotal && hugeBytes?.Length <= ProtectedUiDraftStore.MaximumStoreBytes, "4 MiB store cap");
        var malformed = new ProtectedUiDraftStore(() => Encoding.UTF8.GetBytes("{\"schema\":1,\"counter\":0,\"entries\":[{}]}"),
            _ => throw new Exception("Corrupt drafts must not be overwritten"), () => now);
        Require(!malformed.Read(identity).Ok, "Malformed protected state fails closed");
        byte[]? uncertainWrite = null;
        var failOnce = true;
        var uncertain = new ProtectedUiDraftStore(() => uncertainWrite, bytes =>
        {
            uncertainWrite = bytes;
            if (failOnce) { failOnce = false; throw new IOException("Synthetic receipt failure after storage commit"); }
        }, () => now);
        Require(!uncertain.Save(identity, "committed edit", uncertain.Read(identity).Revision).Ok,
            "Uncertain persistence returns failure");
        var recoveredWrite = uncertain.Read(identity);
        Require(recoveredWrite.Ok && recoveredWrite.Text == "committed edit" && recoveredWrite.Revision > 0 &&
            !uncertain.Save(identity, "old empty-state overwrite", 0).Ok,
            "A failed write receipt reloads canonical revision before retry");
        CheckAbsentDraftRevisions();
        CheckDraftStateMigration();
        CheckLegacyChatScopes();
    }

    private static void CheckAbsentDraftRevisions()
    {
        byte[]? persisted = null;
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var store = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        var a = new ProtectedUiDraftIdentity("file", Guid.NewGuid(), null, "file:server-properties");
        var b = new ProtectedUiDraftIdentity("list", Guid.NewGuid(), null, "list:allow-list");
        var c = new ProtectedUiDraftIdentity("settings", Guid.NewGuid(), null, "settings:properties");
        var beforeA = store.Read(a);
        var beforeB = store.Read(b);
        var beforeC = store.Read(c);
        var savedA = store.Save(a, "File draft A", beforeA.Revision);
        var savedB = store.Save(b, "List draft B", beforeB.Revision);
        Require(savedA.Ok && savedB.Ok && savedB.Revision > savedA.Revision && store.Read(c).Revision == beforeC.Revision,
            "Reading two absent editors before saving either does not cause an unrelated CAS refusal");
        var clearedA = store.Clear(a, savedA.Revision);
        var restarted = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        Require(clearedA.Ok && restarted.Read(c).Revision == beforeC.Revision &&
            restarted.Save(c, "Settings draft C", beforeC.Revision).Ok,
            "Ordinary clear and restart do not invalidate another absent editor");
        Require(!restarted.Save(a, "old file edit", savedA.Revision).Ok, "Stable absence floor does not weaken an existing tombstone");
        now += ProtectedUiDraftStore.Retention + TimeSpan.FromTicks(1);
        var afterPrune = restarted.Read(a);
        Require(afterPrune.Ok && afterPrune.Text is null && afterPrune.Revision > clearedA.Revision &&
            !restarted.Save(a, "old cleared file", clearedA.Revision).Ok &&
            !restarted.Save(b, "old pruned list", savedB.Revision).Ok, "Prune fences old content and tombstones independently of later allocations");
        var d = c with { ProfileId = Guid.NewGuid() };
        var beforeD = restarted.Read(d);
        Require(restarted.Save(a, "Reviewed after pruning", afterPrune.Revision).Ok &&
            restarted.Read(d).Revision == beforeD.Revision, "Post-prune ordinary saves do not keep moving the absence floor");
        var afterRestart = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        Require(afterRestart.Read(d).Revision == beforeD.Revision && afterRestart.Save(d, "Reviewed settings draft D", beforeD.Revision).Ok &&
            !afterRestart.Save(b, "pruned replay after restart", savedB.Revision).Ok, "Restart retains the prune floor without unrelated false CAS");
        using var serialized = JsonDocument.Parse(persisted!);
        Require(serialized.RootElement.GetProperty("schema").GetInt32() == 2 &&
            serialized.RootElement.GetProperty("absenceFloor").GetInt64() == afterPrune.Revision &&
            serialized.RootElement.GetProperty("counter").GetInt64() > afterPrune.Revision,
            "Revision allocation and the durable absence floor are separate state fields");
    }

    private static void CheckDraftStateMigration()
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var existing = new ProtectedUiDraftIdentity("chat", Guid.NewGuid(), null, "compose");
        var missing = existing with { ProfileId = Guid.NewGuid() };
        byte[]? persisted = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            counter = 7,
            entries = new[] { new { identity = existing, text = "Retained version one compose", revision = 2, updatedUtc = now } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var migrated = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        Require(migrated.Read(existing) is { Ok: true, Revision: 2, Text: "Retained version one compose" } &&
            migrated.Read(missing) is { Ok: true, Revision: 7, Text: null }, "Version one entry and absent revision survive migration");
        Require(migrated.Save(existing, "Reviewed migrated compose", 2).Ok && migrated.Read(missing).Revision == 7 &&
            !migrated.Save(missing, "old pre-migration replay", 6).Ok, "Migration keeps prior replay floors and releases unrelated allocation coupling");
        var restarted = new ProtectedUiDraftStore(() => persisted, bytes => persisted = bytes, () => now);
        Require(restarted.Read(missing).Revision == 7 && restarted.Save(missing, "Reviewed missing compose", 7).Ok,
            "Migrated absence floor survives a new-format restart");
        foreach (var malformed in new[]
        {
            "null", "[]", "{}", "{\"schema\":2,\"counter\":1,\"entries\":[]}",
            "{\"schema\":2,\"counter\":1,\"absenceFloor\":2,\"entries\":[]}",
            "{\"schema\":2,\"counter\":1,\"absenceFloor\":-1,\"entries\":[]}",
            "{\"schema\":2,\"counter\":1,\"absenceFloor\":\"0\",\"entries\":[]}",
            "{\"schema\":2,\"counter\":1,\"absenceFloor\":0,\"absenceFloor\":1,\"entries\":[]}",
            "{\"schema\":1,\"counter\":1,\"absenceFloor\":0,\"entries\":[]}",
            "{\"schema\":3,\"counter\":1,\"absenceFloor\":0,\"entries\":[]}",
            "{\"schema\":2,\"counter\":9007199254740992,\"absenceFloor\":0,\"entries\":[]}"
        })
        {
            var invalid = new ProtectedUiDraftStore(() => Encoding.UTF8.GetBytes(malformed),
                _ => throw new InvalidOperationException("Invalid draft state must never be overwritten"), () => now);
            Require(!invalid.Read(existing).Ok && !invalid.Save(existing, "unreviewed overwrite", 0).Ok,
                "Unknown, duplicate or out-of-bound floor state remains unavailable and untouched");
        }
        using var legacyInput = JsonDocument.Parse(JsonSerializer.Serialize(new
        { purpose = "chat", profileId = existing.ProfileId, connectionId = Guid.Empty, key = "compose", text = "Legacy draft", expectedRevision = 0 }));
        var legacy = ProtectedUiDraftStore.ParseSave(legacyInput.RootElement).Identity;
        Require(legacy.ConnectionId == Guid.Empty && legacy.Purpose == "chat" && legacy.Key == "compose",
            "Exact zero legacy connection schema is distinct from a null Host scope");
        Reject(() => ProtectedUiDraftStore.ValidateIdentity(legacy with { Purpose = "file" }), "Legacy scope is chat-only");
        Reject(() => ProtectedUiDraftStore.ValidateIdentity(legacy with { Key = "composer" }), "Legacy scope is the reviewed compose key only");
        Reject(() => ProtectedUiDraftStore.ValidateIdentity(legacy with { ProfileId = Guid.Empty }), "Legacy connection does not acquire the reserved setup profile");
    }

    private static void CheckLegacyChatScopes()
    {
        // Synthetic protected configuration and authorized local copies only.
        // The injected transport throws on every send, including loopback.
        using var data = new LocalData(Path.GetFullPath(Path.Combine("local-data", "feature-checks", "legacy-chat-" + Guid.NewGuid().ToString("N"))));
        var profile = Guid.NewGuid();
        var deniedProfile = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var legacyHost = Guid.NewGuid();
        var peerHost = Guid.NewGuid();
        FriendConfiguration Configuration(Guid host, char credential) => new()
        {
            HostId = host,
            DeviceId = Guid.NewGuid(),
            Credential = new string(credential, 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('A', 64),
            CachedProfiles = [new(profile, "Synthetic assigned room", "Offline", null), new(deniedProfile, "Synthetic removed room", "Offline", null)],
            ChatDeniedProfiles = [deniedProfile]
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(Configuration(legacyHost, 'L'), options));
        data.SaveProtected($"friend-{peer:N}.protected", JsonSerializer.SerializeToUtf8Bytes(Configuration(peerHost, 'P'), options));
        data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new { ids = new[] { Guid.Empty, peer }, selectedId = Guid.Empty }, options));
        var requests = 0;
        using var friend = new FriendService(data, (endpoint, pins) => new HttpClient(new MetadataHandler(request =>
        {
            requests++;
            throw new InvalidOperationException("Legacy scope checks cannot send network traffic");
        }))
        { BaseAddress = new Uri(endpoint) });
        Require(friend.View().ConnectionId == Guid.Empty && friend.View().Connections?.Count == 2,
            "The protected legacy link remains selected when it is present in the saved index");
        var summary = friend.ChatSummaryAsync(Guid.Empty, profile, CancellationToken.None).GetAwaiter().GetResult();
        Require(summary.Ok && summary.HostId == legacyHost && summary.ProfileId == profile,
            "A selected actual legacy link can read its assigned local room summary");
        var missing = friend.ChatSummaryAsync(Guid.Empty, Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult();
        var denied = friend.ChatSummaryAsync(Guid.Empty, deniedProfile, CancellationToken.None).GetAwaiter().GetResult();
        Require(!missing.Ok && missing.Code == "UnknownProfile" && !denied.Ok && denied.Code == "ChatAccessDenied",
            "Legacy selection never bypasses assignment or room removal");
        var queued = friend.PostChatAsync(Guid.Empty, profile, "Synthetic legacy unsent text", CancellationToken.None).GetAwaiter().GetResult();
        Require(queued.Ok && queued.Code == "ChatQueued" && queued.Pending.Count == 1, "Selected legacy offline room can queue deliberately");
        var draft = queued.Pending.Single();
        Require(friend.EditQueuedChatAsync(Guid.Empty, profile, draft.Id, new("Reviewed legacy text", draft.Text), CancellationToken.None).GetAwaiter().GetResult().Ok,
            "Selected legacy room can edit a proven unsent queue entry");
        Require(friend.Select(peer).Ok &&
            friend.ChatSummaryAsync(Guid.Empty, profile, CancellationToken.None).GetAwaiter().GetResult().Code == "ConnectionChanged" &&
            friend.SyncChatAsync(Guid.Empty, profile, CancellationToken.None).GetAwaiter().GetResult().Code == "ConnectionChanged",
            "An unselected legacy link cannot be used when another equal-profile Host is selected");
        Require(friend.ChatSummaryAsync(peer, profile, CancellationToken.None).GetAwaiter().GetResult().HostId == peerHost,
            "The same profile ID on another saved Host remains a different chat authority");
        Require(friend.Select(Guid.Empty).Ok &&
            friend.CancelQueuedChatAsync(Guid.Empty, profile, draft.Id, new("Reviewed legacy text"), CancellationToken.None).GetAwaiter().GetResult().Ok,
            "Reselecting the legacy link restores only its own queue authority");
        friend.Dispose();
        Require(friend.ChatSummaryAsync(Guid.Empty, profile, CancellationToken.None).GetAwaiter().GetResult().Code == "ConnectionChanged" && requests == 0,
            "Disposed legacy connections remain closed and every regression stays local");
        data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new { ids = new[] { peer }, selectedId = Guid.Empty }, options));
        using var withoutLegacy = new FriendService(data, (endpoint, pins) => new HttpClient(new MetadataHandler(request =>
        {
            requests++;
            throw new InvalidOperationException("Unindexed legacy scope checks cannot send network traffic");
        }))
        { BaseAddress = new Uri(endpoint) });
        Require(withoutLegacy.View().ConnectionId == peer &&
            withoutLegacy.ChatSummaryAsync(Guid.Empty, profile, CancellationToken.None).GetAwaiter().GetResult().Code == "ConnectionChanged" && requests == 0,
            "A leftover protected legacy file cannot become an active link when the current index excludes it");
    }

    internal static void RunPreferences()
    {
        CheckNativeDraftFlushGate();
        var system = DesktopUserNotificationState.AcceptsNotifications;
        var preferences = new DesktopNotificationPreferences(() => system);
        var profile = Guid.NewGuid();
        var connection = Guid.NewGuid();
        Require(preferences.ShouldNotify("Backup", profile, connection), "Native notification default preserved");
        preferences.Apply(new(QuietMode: true));
        Require(!preferences.ShouldNotify("Backup", profile, connection), "Quiet mode gates native popups");
        preferences.Apply(new(QuietMode: false));
        foreach (var suppressed in new[] { DesktopUserNotificationState.Unknown, DesktopUserNotificationState.Busy,
                     DesktopUserNotificationState.PresentationMode, DesktopUserNotificationState.RunningD3DFullScreen,
                     DesktopUserNotificationState.QuietTime, DesktopUserNotificationState.NotPresent })
        {
            system = suppressed;
            Require(!preferences.ShouldNotify("Backup", profile, connection), "Windows suppression respected");
        }
        system = DesktopUserNotificationState.AcceptsNotifications;
        preferences.Apply(new(AllowedEvents: ["Backup"], ProfileId: profile, ConnectionId: connection));
        Require(preferences.ShouldNotify("Backup", profile, connection) && !preferences.ShouldNotify("Lifecycle", profile, connection),
            "Per-server event allowlist");
        Require(preferences.ShouldNotify("Lifecycle", profile, Guid.NewGuid()), "Another Host connection remains independent");
        preferences.Apply(new(ProfileId: profile, ConnectionId: connection, ResetServer: true));
        Require(preferences.ShouldNotify("Lifecycle", profile, connection), "Reset restores local defaults");
        foreach (var json in new[] { "{\"quietMode\":true,\"profileId\":\"" + profile + "\"}",
                     "{\"allowedEvents\":[\"Backup\",\"Backup\"]}", "{\"allowedEvents\":[\"RunCommand\"]}",
                     "{\"quietMode\":true,\"quietMode\":false}", "{\"allowedEvents\":null}" })
            Reject(() => { using var input = JsonDocument.Parse(json); DesktopNotificationPreferences.ParseChange(input.RootElement); },
                "Fixed notification input");
        var activity = new ActivityEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "Backup", "BackupFailed", "Review the backup.",
            ActivitySeverity.Warning, profile);
        Require(DesktopNotificationPreferences.Destination(activity, false) == new DesktopNotificationDestination("host", "backups", profile),
            "Notification opens its owning server");
        Require(DesktopNotificationPreferences.Destination(activity, true, connection) == new DesktopNotificationDestination("join", "shared-saves", profile, connection),
            "Friend notification preserves connection scope");
        Require(!DesktopNotificationPreferences.IsDestination(new("host", "start", profile)) &&
            !DesktopNotificationPreferences.IsDestination(new("settings", "https://example.com")), "Bridge cannot request commands or URLs");

        var monitor = new Rectangle(-1920, 0, 1920, 1040);
        var other = new Rectangle(0, 0, 1366, 728);
        var clamped = QolLocalPreferences.ClampWindowBounds(new(-2400, -50, 1180, 820), [monitor, other]);
        Require(monitor.Contains(clamped) && clamped.Size == new Size(1180, 820), "Negative monitor bounds clamped");
        var detached = QolLocalPreferences.ClampWindowBounds(new(9000, 9000, 3000, 2000), [other]);
        Require(other.Contains(detached) && detached.Size == other.Size, "Disconnected monitor and oversize window recover");
        var small = new Rectangle(0, 0, 320, 480);
        Require(small.Contains(QolLocalPreferences.ClampWindowBounds(new(50, 50, 1180, 820), [small])),
            "Small work area remains reachable");
        Require(DesktopWindow.TraySummaryText(new string('A', 200), new(2, 1, "Connected")).Length <= 127,
            "Tray text fits the Windows limit");
        Require(DesktopWindow.IsApprovedExternal("https://github.com/daltonstates/TogetherServer/releases/tag/v0.4.0") &&
            !DesktopWindow.IsApprovedExternal("https://github.com/other/repo/releases/tag/v0.4.0") &&
            !DesktopWindow.IsApprovedExternal("https://github.com/daltonstates/TogetherServer/releases/tag/v0.4.0?command=start"),
            "Release page links remain fixed");

        UpdateUiPreferences.State? saved = null;
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var reminders = new UpdateUiPreferences(save: value => saved = value, clock: () => now);
        var until = reminders.Snooze(new("0.4.0", "Tomorrow"));
        Require(until == now + TimeSpan.FromDays(1), "Fixed reminder duration");
        var restored = new UpdateUiPreferences(saved, clock: () => now);
        Require(restored.SnoozedUntil("0.4.0") == until && restored.SnoozedUntil("0.4.1") is null,
            "Reminder survives restart and stays version-scoped");
        now = until!.Value;
        Require(restored.SnoozedUntil("0.4.0") is null, "Reminder expires at exact equality");
        restored.Snooze(new("0.4.0", "SkipVersion"));
        now += TimeSpan.FromDays(400);
        Require(restored.IsVersionSkipped("0.4.0") && restored.SnoozedUntil("0.4.0") is not null &&
            !restored.IsVersionSkipped("0.4.1") && restored.SnoozedUntil("0.4.1") is null,
            "Skip suppresses this version only across future runs");
        restored.Snooze(new("0.4.0", "Clear"));
        Require(restored.SnoozedUntil("0.4.0") is null && !restored.IsVersionSkipped("0.4.0"), "Skip can be cleared");
        for (var index = 0; index < 24; index++) reminders.Snooze(new("0.4." + index, "SkipVersion"));
        Require(saved?.Snoozes.Count == 16, "Reminder state bounded");
        Reject(() =>
        {
            using var input = JsonDocument.Parse("{\"version\":\"0.4.0\",\"duration\":\"Forever\",\"url\":\"private\"}");
            UpdateUiPreferences.ParseSnooze(input.RootElement);
        }, "Snooze cannot set arbitrary policy or URL");
    }

    private static void CheckNativeDraftFlushGate()
    {
        var address = new Uri("http://127.0.0.1:5127/");
        var gate = new DesktopDraftFlushGate(address);
        static string Reply(Guid id, bool ok = true) => JsonSerializer.Serialize(new
        { type = "together-drafts-flushed", requestId = id, ok });
        Require(!gate.TryComplete(address.AbsoluteUri, Reply(Guid.NewGuid())),
            "An unsolicited acknowledgement cannot authorize Quit");
        Require(gate.TryBegin(out var firstId, out var first) && firstId != Guid.Empty && !first.IsCompleted,
            "Manual Quit creates one pending draft flush request");
        using (var document = JsonDocument.Parse(DesktopDraftFlushGate.RequestJson(firstId)))
        {
            var request = document.RootElement;
            Require(request.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "requestId", "type" }) &&
                    request.GetProperty("type").GetString() == "together-flush-drafts" &&
                    request.GetProperty("requestId").GetGuid() == firstId,
                "Native draft flush includes only its fixed type and nonce");
        }
        Require(!gate.TryBegin(out var busyId, out var busy) && busyId == Guid.Empty && busy.IsCompletedSuccessfully && !busy.Result,
            "A second native request cannot replace the pending draft flush");
        foreach (var source in new[]
        {
            "https://127.0.0.1:5127/", "http://127.0.0.1:5128/", "http://localhost:5127/",
            "http://example.test:5127/", "http://127.0.0.1:5127/other-page", "http://127.0.0.1:5127/?command=quit",
            "http://owner@127.0.0.1:5127/", "not-a-page"
        }) Require(!gate.TryComplete(source, Reply(firstId)) && !first.IsCompleted,
            "An unapproved response source consumed a native draft acknowledgement");
        foreach (var json in new[]
        {
            "null", "[]", "{}", "{",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:D}\",\"ok\":true,\"draft\":\"private text\"}}",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:D}\",\"ok\":true,\"path\":\"private.exe\"}}",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:D}\",\"ok\":true,\"ok\":false}}",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:D}\",\"requestId\":\"{firstId:D}\",\"ok\":true}}",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:N}\",\"ok\":true}}",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:D}\",\"ok\":\"true\"}}",
            $"{{\"type\":\"together-drafts-flushed\",\"requestId\":\"{firstId:D}\",\"ok\":null}}",
            $"{{\"type\":\"quit\",\"requestId\":\"{firstId:D}\",\"ok\":true}}",
            "{\"type\":\"together-drafts-flushed\",\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"ok\":true}",
            new string(' ', DesktopDraftFlushGate.MaximumReplyCharacters + 1)
        }) Require(!gate.TryComplete(address.AbsoluteUri, json) && !first.IsCompleted,
            "Malformed, ambiguous or content-bearing replies cannot authorize Quit");
        Require(!gate.TryComplete(address.AbsoluteUri, Reply(Guid.NewGuid())) && !first.IsCompleted,
            "A response for another request consumed the pending flush");
        Require(gate.TryComplete(address.AbsoluteUri, Reply(firstId)) && first.IsCompletedSuccessfully && first.Result &&
                !gate.TryComplete(address.AbsoluteUri, Reply(firstId)),
            "Only one matching approved acknowledgement can confirm draft completion");

        Require(gate.TryBegin(out var failedId, out var failed) &&
                gate.TryComplete(address.AbsoluteUri, Reply(failedId, false)) && failed.IsCompletedSuccessfully && !failed.Result,
            "A failed draft save keeps native Quit blocked");
        Require(gate.TryBegin(out var expiredId, out var expired) && gate.Cancel(expiredId) &&
                expired.IsCompletedSuccessfully && !expired.Result &&
                !gate.TryComplete(address.AbsoluteUri, Reply(expiredId)),
            "A timeout expires its exact request and rejects late success");
        Require(gate.TryBegin(out var retryId, out var retry) && retryId != expiredId &&
                !gate.Cancel(expiredId) && !gate.TryComplete(address.AbsoluteUri, Reply(expiredId)) && !retry.IsCompleted,
            "An expired request cannot cancel or complete a manual retry");
        gate.CancelAll();
        Require(retry.IsCompletedSuccessfully && !retry.Result && !gate.TryComplete(address.AbsoluteUri, Reply(retryId)),
            "Window shutdown cancels pending acknowledgement and ignores late replies");
    }

    internal static async Task RunUpdateMetadataAsync(string root)
    {
        var directory = Path.Combine(root, "desktop-update-metadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "TogetherServer.exe");
        File.WriteAllText(executable, "synthetic installed file; never executed");
        string Metadata(string tag, string notesLink) => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            body = new string('a', 5000) + "\0",
            html_url = notesLink,
            assets = new[] { new { name = AppUpdater.AssetName, state = "uploaded", size = 4,
                digest = "sha256:" + new string('A', 64),
                browser_download_url = $"https://github.com/daltonstates/TogetherServer/releases/download/{tag}/{AppUpdater.AssetName}" } }
        });
        var version = "v0.4.0";
        var requests = 0;
        using var client = new HttpClient(new MetadataHandler(request =>
        {
            Require(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/daltonstates/TogetherServer/releases/latest",
                "Notes use only the existing metadata request");
            requests++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(Metadata(version,
                "https://github.com/daltonstates/TogetherServer/releases/tag/" + version))
            };
        }));
        UpdateUiPreferences.State? persisted = null;
        var preferences = new UpdateUiPreferences(save: value => persisted = value);
        var updater = new AppUpdater(client, directory, executable, new Version(0, 3, 0), new SyntheticUnsignedVerifier(),
            uiPreferences: preferences);
        var available = await updater.CheckAsync(true);
        Require(available.ReleaseNotes.Length == AppUpdater.MaximumReleaseNotesCharacters && requests == 1 &&
            available.ReleaseNotesUrl == "https://github.com/daltonstates/TogetherServer/releases/tag/v0.4.0",
            "Bounded notes from trusted release metadata");
        Require((await updater.SnoozeAsync(new("0.4.0", "SkipVersion"))).Ok && updater.View.PromptSnoozed && updater.View.VersionSkipped,
            "Canonical updater honors version skip");
        var restartedPreferences = new UpdateUiPreferences(persisted);
        Require(restartedPreferences.NotesFor("0.4.0")?.Text == available.ReleaseNotes && restartedPreferences.IsVersionSkipped("0.4.0"),
            "Notes and reminder retained durably");
        version = "v0.4.1";
        Require(!(await updater.CheckAsync(true)).PromptSnoozed, "A different release is not suppressed");
        Require(!(await updater.SnoozeAsync(new("0.4.0", "Tomorrow"))).Ok, "Stale version cannot modify reminder");
        var foreign = AppUpdater.ParseRelease(Metadata("v0.4.0", "https://example.com/release"));
        Require(foreign is not null && foreign.ReleaseNotesUrl is null, "Foreign release link discarded");
        updater.ReportPreparation("Checkpoint", "Protecting local settings.");
        Require(updater.Preparation.Stage == "Checkpoint", "Checkpoint stage is visible without a gate wait");
        updater.ReportPreparation("Blocked", "Stop hosted servers first.", "Stop hosted servers first.");
        Require(updater.View.Preparation?.Blocker == "Stop hosted servers first.", "Lifecycle blocker remains visible");
        Require(requests == 2, "No extra notes network request or update execution");
    }

    private sealed class SyntheticUnsignedVerifier : IAuthenticodeVerifier
    {
        public AuthenticodeVerification Verify(string path) => new(false, null, "Synthetic unsigned file", IsUnsigned: true);
    }
    private sealed class MetadataHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(label);
    }
    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }
}
