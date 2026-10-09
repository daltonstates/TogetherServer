using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

// Registered by the coordinator. This launches packaged apps and a disposable
// fixture only on the owner's explicitly approved separate Windows CI runner.
// Never invoke it on the active desktop, including with hidden-window flags.
internal static class QolApiJourney
{
    public static Task RunAsync(string appPath) => CoreRemoteJourney.RunQolApiAsync(appPath);
}

internal static partial class CoreRemoteJourney
{
    private sealed record QolDraft(string Purpose, Guid ProfileId, Guid? ConnectionId, string Key);
    private sealed record QolDraftReceipt(QolDraft Identity, string Text, long Revision);

    internal static async Task RunQolApiAsync(string appPath)
    {
        var fixturePath = Path.GetFullPath("src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe");
        Require(File.Exists(appPath) && File.Exists(fixturePath), "Build the packaged app and synthetic Valheim fixture before the QoL API journey.");
        var root = Path.GetFullPath(Path.Combine("local-data", "qol-api-journey", Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var peerData = Path.Combine(root, "peer-host");
        var friendData = Path.Combine(root, "friend");
        var hostPort = FreeTcpPort();
        var companionPort = FreeTcpPort(hostPort);
        var friendPort = FreeTcpPort(hostPort, companionPort);
        var peerPort = FreeTcpPort(hostPort, companionPort, friendPort);
        var peerCompanionPort = FreeTcpPort(hostPort, companionPort, friendPort, peerPort);
        var profile = new ServerProfile
        {
            Kind = GameKinds.Valheim,
            Name = "Disposable QoL API",
            ServerName = "Disposable QoL API",
            WorldSource = "New",
            WorldId = "qol-fixture",
            ExecutablePath = fixturePath,
            WorldDirectory = Path.Combine(hostData, "worlds", Guid.NewGuid().ToString("N")),
            GamePort = FreeUdpPair()
        };
        var java = new ServerProfile
        {
            Kind = GameKinds.MinecraftJava,
            Name = "Unlaunched draft scope",
            WorldId = "qol-java",
            WorldSource = "New",
            WorldDirectory = Path.Combine(hostData, "minecraft-servers", Guid.NewGuid().ToString("N")),
            ExecutablePath = Path.Combine(hostData, "minecraft-servers", "never-launched-java.exe"),
            GamePort = FreeTcpPort(hostPort, companionPort, friendPort, peerPort, peerCompanionPort)
        };
        // Equal profile GUIDs on two independent Hosts make accidental selected-
        // Host fallback observable. The peer's fixture is never started.
        var peerProfile = new ServerProfile
        {
            Id = profile.Id,
            Kind = GameKinds.Valheim,
            Name = "Other disposable Host",
            ServerName = "Other disposable Host",
            WorldSource = "New",
            WorldId = "qol-peer",
            ExecutablePath = fixturePath,
            WorldDirectory = Path.Combine(peerData, "worlds", Guid.NewGuid().ToString("N")),
            GamePort = FreeUdpPair()
        };
        var sourceFile = Path.Combine(profile.WorldDirectory, "synthetic-world.bin");
        Process? host = null;
        Process? peer = null;
        Process? friend = null;
        try
        {
            // Staging disables actual updater checks/downloads and separates all
            // state, locks, settings, credentials, worlds and listener ports.
            host = StartApp(appPath, "--host", hostPort, hostData, staging: true);
            peer = StartApp(appPath, "--host", peerPort, peerData, staging: true);
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(peerPort), WaitLocalAsync(friendPort));
            WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host);
            WindowsListenerOwners.RequireTogetherServerOwner(peerPort, peer);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            using var owner = LocalClient(hostPort);
            using var otherOwner = LocalClient(peerPort);
            using var joining = LocalClient(friendPort);
            foreach (var (client, dataPath) in new[] { (owner, hostData), (otherOwner, peerData), (joining, friendData) })
                await QolInstanceAsync(client, dataPath);
            // Create synthetic input only after staging initialized its marker.
            Directory.CreateDirectory(profile.WorldDirectory);
            Directory.CreateDirectory(java.WorldDirectory);
            Directory.CreateDirectory(peerProfile.WorldDirectory);
            await File.WriteAllTextAsync(sourceFile, "Disposable QoL world bytes; no real game or player data.");
            var propertiesPath = Path.Combine(java.WorldDirectory, "server.properties");
            var propertiesText = "level-name=qol-java\nserver-port=" + java.GamePort + "\n";
            await File.WriteAllTextAsync(propertiesPath, propertiesText);
            await File.WriteAllTextAsync(Path.Combine(java.WorldDirectory, "whitelist.json"), "[]");
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceFile));
            var settings = new HostSettings
            {
                Profiles = [profile, java],
                MaxConcurrentServers = 1,
                CompanionEndpoint = $"https://127.0.0.1:{companionPort}",
                CompanionPort = companionPort,
                CompanionBindAddress = "127.0.0.1"
            };
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
                "disposable QoL settings were rejected");
            Require((await PutAsync<HostSettings, ActionResult>(otherOwner, "/api/local/settings", new()
            {
                Profiles = [peerProfile],
                CompanionEndpoint = $"https://127.0.0.1:{peerCompanionPort}",
                CompanionPort = peerCompanionPort,
                CompanionBindAddress = "127.0.0.1"
            })).Ok, "second disposable Host settings were rejected");
            var retained = new List<QolDraftReceipt>();
            var independentFile = new QolDraft("file", java.Id, null, "file:server-properties");
            var independentSettings = new QolDraft("settings", java.Id, null, "settings:properties");
            var beforeFile = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post, QolDraftIdentity(independentFile)));
            var beforeSettings = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post, QolDraftIdentity(independentSettings)));
            var interleavedFile = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put,
                QolDraftSave(independentFile, "Independent HTTP file draft", beforeFile.Revision)));
            var interleavedSettings = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put,
                QolDraftSave(independentSettings, "Independent HTTP settings draft", beforeSettings.Revision)));
            Require(interleavedFile.Ok && interleavedSettings.Ok, "two preloaded absent HTTP editor scopes caused an unrelated CAS refusal");
            Require(QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts/clear", HttpMethod.Post,
                QolDraftClear(independentFile, interleavedFile.Revision))).Ok &&
                QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts/clear", HttpMethod.Post,
                QolDraftClear(independentSettings, interleavedSettings.Revision))).Ok, "independent HTTP draft cleanup failed");
            foreach (var draft in new[]
            {
                new QolDraft("file", java.Id, null, "file:server-properties"),
                new QolDraft("list", java.Id, null, "list:allow-list"),
                new QolDraft("settings", java.Id, null, "settings:properties"),
                new QolDraft("settings", Guid.Empty, null, "host-setup"),
                new QolDraft("chat", profile.Id, null, "compose")
            }) retained.Add(await QolDraftRoundTripAsync(owner, draft));
            await QolDraftRejectionsAsync(owner, joining, profile, java, retained);
            Require(await File.ReadAllTextAsync(propertiesPath) == propertiesText &&
                await File.ReadAllTextAsync(Path.Combine(java.WorldDirectory, "whitelist.json")) == "[]",
                "keeping editor drafts changed a synthetic server file");
            var room = await QolRequestAsync(owner, $"/api/local/profiles/{profile.Id}/chat", HttpMethod.Get);
            Require(room.GetProperty("entries").GetArrayLength() == 0, "keeping a compose draft posted a message");
            Console.WriteLine("PASS packaged protected file/list/settings/setup/Host-chat draft read/save/CAS clear and stale-save fencing");
            await QolNotificationChecksAsync(owner, profile.Id);
            await QolUpdateChecksAsync(owner);
            var backupId = await QolBackupChecksAsync(owner, profile.Id, java.Id, sourceFile, originalHash);
            var primaryConnection = await QolPairAsync(owner, joining, profile.Id);
            WindowsListenerOwners.RequireTogetherServerOwner(companionPort, host);
            var friendDraft = new QolDraft("chat", profile.Id, primaryConnection, "compose");
            retained.Add(await QolDraftRoundTripAsync(joining, friendDraft));
            await QolFriendDraftRejectionsAsync(joining, friendDraft, java.Id);
            var peerConnection = await QolPairAsync(otherOwner, joining, peerProfile.Id);
            WindowsListenerOwners.RequireTogetherServerOwner(peerCompanionPort, peer);
            Require(primaryConnection != peerConnection, "different pinned Hosts collapsed into one saved connection");
            await QolProbeChecksAsync(owner, joining, profile, java.Id, primaryConnection, peerConnection);
            await QolFriendNotificationChecksAsync(joining, profile.Id, primaryConnection, peerConnection);

            // Stop the one exact fixture before restarting app instances; no
            // production path, process-name kill or real updater is involved.
            await TryStopManagedRunAsync(hostPort, profile.Id, host);
            _ = await WaitForRunStateAsync(owner, profile.Id, "Offline");
            StopApp(friend); friend = null;
            StopApp(peer); peer = null;
            StopApp(host); host = null;
            host = StartApp(appPath, "--host", hostPort, hostData, staging: true);
            peer = StartApp(appPath, "--host", peerPort, peerData, staging: true);
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(peerPort), WaitLocalAsync(friendPort));
            WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host);
            WindowsListenerOwners.RequireTogetherServerOwner(peerPort, peer);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            var restored = await WaitForConnectedAsync(joining, allowDisabled: true);
            Require(restored.ConnectionId == primaryConnection, "selected saved Host did not survive packaged restart");
            foreach (var receipt in retained)
            {
                var client = receipt.Identity.ConnectionId is null ? owner : joining;
                var persisted = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts/read", HttpMethod.Post,
                    QolDraftIdentity(receipt.Identity)));
                Require(persisted.Ok && persisted.Text == receipt.Text && persisted.Revision == receipt.Revision,
                    "protected draft scope/text/revision did not survive packaged restart");
            }
            await QolAssertNotificationPersistenceAsync(owner, joining, profile.Id, primaryConnection, peerConnection);
            await QolAssertBackupEvidenceAsync(owner, profile.Id, backupId);
            var restartedWorldHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceFile));
            Require(originalHash.SequenceEqual(restartedWorldHash),
                "QoL checks changed the original synthetic world bytes");
            Console.WriteLine("PASS packaged restart retains exact draft scopes/revisions, notification choices and independently measured backup evidence");
            // Manufacture the old single-Host layout from this journey's own
            // generated test pairing only, while its Friend process is stopped.
            // No external credentials or production data are opened or copied.
            StopApp(friend); friend = null;
            using (var legacyData = new LocalData(friendData))
            {
                var legacyConfig = legacyData.LoadProtected($"friend-{primaryConnection:N}.protected");
                Require(legacyConfig is not null, "disposable legacy configuration source was missing");
                legacyData.SaveProtected("friend.protected", legacyConfig!);
                legacyData.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new
                { ids = new[] { Guid.Empty, peerConnection }, selectedId = Guid.Empty }, Json));
            }
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await WaitLocalAsync(friendPort);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            var legacyView = await WaitForConnectedAsync(joining, allowDisabled: true);
            Require(legacyView.ConnectionId == Guid.Empty && legacyView.Profiles.Single().Id == profile.Id,
                "actual protected legacy index did not preserve its selected link and assigned room");
            var legacyCompose = await QolDraftRoundTripAsync(joining, new("chat", profile.Id, Guid.Empty, "compose"));
            var legacyChat = $"/api/local/friend/connections/{Guid.Empty}/servers/{profile.Id}/chat";
            var legacySummary = await QolRequestAsync(joining, legacyChat + "/summary", HttpMethod.Get);
            Require(legacySummary.GetProperty("ok").GetBoolean() && legacySummary.GetProperty("hostId").GetGuid() == legacyView.HostId,
                "scoped legacy summary rejected its exact current pinned Host");
            Require((await QolRequestAsync(joining, legacyChat + "/sync", HttpMethod.Post)).GetProperty("ok").GetBoolean(),
                "scoped legacy room could not sync through its actual saved link");
            QolCode(await QolRequestAsync(joining, $"/api/local/friend/connections/{peerConnection}/servers/{profile.Id}/chat/summary", HttpMethod.Get),
                "ConnectionChanged");
            QolCode(await QolRequestAsync(joining, $"/api/local/friend/connections/{Guid.Empty}/servers/{Guid.NewGuid()}/chat/sync", HttpMethod.Post),
                "UnknownProfile");
            QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
                QolDraftIdentity(legacyCompose.Identity with { ConnectionId = null }), HttpStatusCode.Conflict), "FriendMode");
            Require((await QolRequestAsync(joining, $"/api/local/friend/connections/{peerConnection}/select", HttpMethod.Post, new { })).GetProperty("ok").GetBoolean(),
                "legacy fixture peer selection failed");
            QolCode(await QolRequestAsync(joining, legacyChat + "/summary", HttpMethod.Get), "ConnectionChanged");
            QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
                QolDraftIdentity(legacyCompose.Identity), HttpStatusCode.Conflict), "DraftScopeChanged");
            Require((await QolRequestAsync(joining, $"/api/local/friend/connections/{Guid.Empty}/select", HttpMethod.Post, new { })).GetProperty("ok").GetBoolean(),
                "legacy fixture exact reselect failed");
            StopApp(friend); friend = null;
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await WaitLocalAsync(friendPort);
            Require((await WaitForConnectedAsync(joining, allowDisabled: true)).ConnectionId == Guid.Empty,
                "legacy link selection did not survive its restart");
            var legacyRecovered = QolDraftResult(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
                QolDraftIdentity(legacyCompose.Identity)));
            Require(legacyRecovered.Ok && legacyRecovered.Text == legacyCompose.Text && legacyRecovered.Revision == legacyCompose.Revision,
                "legacy zero-CID compose draft lost its exact protected scope or revision across restart");
            Console.WriteLine("PASS packaged legacy-index chat/draft round trip, exact selected-link/room checks, null Host-role separation and restart persistence");
            Console.WriteLine("QoL API journey: disposable loopback/package/fixture evidence only. Native pickers/popups, a successful updater download, real game joins/saves and Friend-PC/WAN acceptance remain UNVERIFIED.");
        }
        finally
        {
            await TryStopManagedRunAsync(hostPort, profile.Id, host);
            StopApp(friend);
            StopApp(peer);
            StopApp(host);
        }
    }

    private static object QolDraftIdentity(QolDraft draft) => new
    {
        purpose = draft.Purpose,
        profileId = draft.ProfileId,
        connectionId = draft.ConnectionId,
        key = draft.Key
    };

    private static object QolDraftSave(QolDraft draft, string text, long expectedRevision) => new
    {
        purpose = draft.Purpose,
        profileId = draft.ProfileId,
        connectionId = draft.ConnectionId,
        key = draft.Key,
        text,
        expectedRevision
    };

    private static object QolDraftClear(QolDraft draft, long expectedRevision) => new
    {
        purpose = draft.Purpose,
        profileId = draft.ProfileId,
        connectionId = draft.ConnectionId,
        key = draft.Key,
        expectedRevision
    };

    private static async Task<QolDraftReceipt> QolDraftRoundTripAsync(HttpClient client, QolDraft identity)
    {
        var before = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts/read", HttpMethod.Post, QolDraftIdentity(identity)));
        Require(before.Ok && before.Text is null, "new disposable draft scope was not empty");
        var firstText = "Synthetic " + identity.Purpose + " draft A";
        var first = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(identity, firstText, before.Revision)));
        Require(first.Ok && first.Text == firstText && first.Revision > before.Revision, "draft save receipt was not canonical");
        var read = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts/read", HttpMethod.Post, QolDraftIdentity(identity)));
        Require(read.Ok && read.Text == firstText && read.Revision == first.Revision, "saved draft did not read back in its exact scope");
        var stale = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(identity, "Stale overwrite must not land", before.Revision)));
        Require(!stale.Ok && stale.Text == firstText && stale.Revision == first.Revision, "stale draft save changed canonical text");
        var secondText = "Synthetic " + identity.Purpose + " newer draft B";
        var second = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(identity, secondText, first.Revision)));
        Require(second.Ok && second.Text == secondText && second.Revision > first.Revision, "newer draft save failed");
        var staleClear = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts/clear", HttpMethod.Post,
            QolDraftClear(identity, first.Revision)));
        Require(!staleClear.Ok && staleClear.Text == secondText && staleClear.Revision == second.Revision,
            "stale draft clear discarded a newer edit");
        var cleared = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts/clear", HttpMethod.Post,
            QolDraftClear(identity, second.Revision)));
        Require(cleared.Ok && cleared.Text is null && cleared.Revision > second.Revision, "confirmed draft clear did not advance its tombstone");
        var resurrection = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(identity, secondText, second.Revision)));
        Require(!resurrection.Ok && resurrection.Text is null && resurrection.Revision == cleared.Revision,
            "stale draft save resurrected a cleared value");
        var final = QolDraftResult(await QolRequestAsync(client, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(identity, secondText, cleared.Revision)));
        Require(final.Ok && final.Text == secondText && final.Revision > cleared.Revision, "reviewed post-clear draft save failed");
        return new(identity, secondText, final.Revision);
    }

    private static async Task QolInstanceAsync(HttpClient client, string expectedRoot)
    {
        var instance = await QolRequestAsync(client, "/api/local/instance", HttpMethod.Get);
        Require(instance.GetProperty("isStaging").GetBoolean() && !instance.GetProperty("updatesAvailable").GetBoolean() &&
            string.Equals(Path.GetFullPath(instance.GetProperty("dataRoot").GetString()!).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(expectedRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
            "QoL journey did not bind to its own update-disabled staging instance");
    }

    private static async Task QolDraftRejectionsAsync(HttpClient owner, HttpClient joining,
        ServerProfile profile, ServerProfile java, List<QolDraftReceipt> receipts)
    {
        var file = receipts.Single(item => item.Identity.Purpose == "file");
        await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post, QolDraftIdentity(file.Identity),
            HttpStatusCode.Forbidden, localHeader: false);
        await QolRequestAsync(owner, "/api/local/ui-drafts/read?ignored=1", HttpMethod.Post, QolDraftIdentity(file.Identity),
            HttpStatusCode.Forbidden);
        var duplicate = JsonSerializer.Serialize(QolDraftIdentity(file.Identity), Json);
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post,
            expectedStatus: HttpStatusCode.BadRequest, raw: duplicate[..^1] + ",\"key\":\"compose\"}"), "InvalidDraft");
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put, new
        {
            purpose = "file",
            profileId = java.Id,
            connectionId = (Guid?)null,
            key = "file:server-properties",
            text = "Unreviewed extra field",
            expectedRevision = file.Revision,
            path = "C:\\not-an-authority"
        }, HttpStatusCode.BadRequest), "InvalidDraft");
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put, new
        {
            purpose = "file",
            profileId = java.Id,
            key = "file:server-properties",
            text = "Invalid fractional CAS",
            expectedRevision = 1.5
        }, HttpStatusCode.BadRequest), "InvalidDraft");
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(new("file", profile.Id, null, "file:server-properties")), HttpStatusCode.BadRequest), "DraftScopeChanged");
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(new("chat", Guid.NewGuid(), null, "compose")), HttpStatusCode.NotFound), "UnknownProfile");
        QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(file.Identity), HttpStatusCode.Conflict), "FriendMode");
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(new("chat", profile.Id, Guid.NewGuid(), "compose")), HttpStatusCode.Conflict), "HostMode");
        foreach (var bounded in receipts.Where(item => item.Identity.Purpose is "list" or "settings" or "chat"))
        {
            // UTF-8 bytes, rather than .NET character count, are authoritative.
            QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put,
                QolDraftSave(bounded.Identity, new string('\u00e9', 32_769), bounded.Revision), HttpStatusCode.BadRequest), "InvalidDraft");
        }
        var maximumFile = new string('x', ProtectedUiDraftStore.MaximumFileDraftBytes);
        var maximum = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(file.Identity, maximumFile, file.Revision)));
        Require(maximum.Ok && maximum.Text == maximumFile && maximum.Revision > file.Revision,
            "a reviewed 2 MiB raw-file draft was incorrectly limited to the smaller editor cap");
        QolCode(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(file.Identity, maximumFile + "x", maximum.Revision), HttpStatusCode.BadRequest), "InvalidDraft");
        var restored = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts", HttpMethod.Put,
            QolDraftSave(file.Identity, file.Text, maximum.Revision)));
        Require(restored.Ok && restored.Text == file.Text, "oversized draft refusal changed the current revision");
        receipts[receipts.IndexOf(file)] = file with { Revision = restored.Revision };
        foreach (var receipt in receipts)
        {
            var current = QolDraftResult(await QolRequestAsync(owner, "/api/local/ui-drafts/read", HttpMethod.Post, QolDraftIdentity(receipt.Identity)));
            Require(current.Ok && current.Text == receipt.Text && current.Revision == receipt.Revision,
                "a refused draft request changed another draft purpose/key/profile");
        }
    }

    private static async Task QolFriendDraftRejectionsAsync(HttpClient joining, QolDraft chat, Guid unassignedProfile)
    {
        QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(chat with { ConnectionId = Guid.NewGuid() }), HttpStatusCode.Conflict), "DraftScopeChanged");
        QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(chat with { ProfileId = unassignedProfile }), HttpStatusCode.Conflict), "DraftScopeChanged");
        QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(chat with { Purpose = "file", Key = "file:server-properties" }), HttpStatusCode.Conflict), "DraftScopeChanged");
        QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(chat with { ConnectionId = Guid.Empty }), HttpStatusCode.Conflict), "DraftScopeChanged");
        Console.WriteLine("PASS packaged Friend compose drafts enforce the selected saved connection, assigned room and chat-only purpose");
    }

    private static async Task QolNotificationChecksAsync(HttpClient owner, Guid profileId)
    {
        await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Get, expectedStatus: HttpStatusCode.Forbidden, localHeader: false);
        QolCode(await QolRequestAsync(owner, "/api/local/desktop/notifications?ignored=1", HttpMethod.Get,
            expectedStatus: HttpStatusCode.BadRequest), "InvalidFeatureRequest");
        var quiet = QolNotification(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put, new { quietMode = true }));
        Require(quiet.QuietMode, "quiet mode did not persist through the local notification endpoint");
        var defaults = QolNotification(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { allowedEvents = new[] { "Backup", "Lifecycle", "Update" } }));
        Require(defaults.AllowedEvents.SequenceEqual(new[] { "Lifecycle", "Backup", "Update" }), "fixed notification events were not canonical");
        var specific = QolNotification(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, connectionId = (Guid?)null, allowedEvents = new[] { "Backup" } }));
        Require(specific.Servers.Single(item => item.ProfileId == profileId).AllowedEvents.SequenceEqual(new[] { "Backup" }),
            "Host-specific notification choices were not scoped to their server");
        QolCode(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId = Guid.NewGuid(), allowedEvents = Array.Empty<string>() }, HttpStatusCode.Conflict), "NotificationScopeChanged");
        QolCode(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, connectionId = Guid.NewGuid(), allowedEvents = Array.Empty<string>() }, HttpStatusCode.Conflict), "NotificationScopeChanged");
        QolCode(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { allowedEvents = new[] { "Backup", "Backup" } }, HttpStatusCode.BadRequest), "InvalidNotificationPreferences");
        QolCode(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { allowedEvents = new[] { "Execute" } }, HttpStatusCode.BadRequest), "InvalidNotificationPreferences");
        QolCode(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            expectedStatus: HttpStatusCode.BadRequest, raw: "{\"quietMode\":false,\"quietMode\":true}"), "InvalidNotificationPreferences");
        var reset = QolNotification(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, allowedEvents = (string[]?)null }));
        Require(reset.Servers.All(item => item.ProfileId != profileId), "reset did not restore default notification choices");
        _ = await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, allowedEvents = new[] { "Backup" } });
        Console.WriteLine("PASS packaged notification schema, quiet/default/server choices, reset, strict fields and ownership rejects");
    }

    private static async Task QolFriendNotificationChecksAsync(HttpClient joining, Guid profileId, Guid primary, Guid peer)
    {
        _ = await QolRequestAsync(joining, "/api/local/desktop/notifications", HttpMethod.Put, new { quietMode = true });
        var view = QolNotification(await QolRequestAsync(joining, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, connectionId = primary, allowedEvents = new[] { "Backup" } }));
        view = QolNotification(await QolRequestAsync(joining, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, connectionId = peer, allowedEvents = new[] { "Network" } }));
        Require(view.Servers.Single(item => item.ConnectionId == primary && item.ProfileId == profileId).AllowedEvents.SequenceEqual(new[] { "Backup" }) &&
            view.Servers.Single(item => item.ConnectionId == peer && item.ProfileId == profileId).AllowedEvents.SequenceEqual(new[] { "Network" }),
            "same-GUID servers on different saved Hosts shared notification overrides");
        QolCode(await QolRequestAsync(joining, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, connectionId = Guid.NewGuid(), allowedEvents = new[] { "Backup" } }, HttpStatusCode.Conflict), "NotificationScopeChanged");
        QolCode(await QolRequestAsync(joining, "/api/local/desktop/notifications", HttpMethod.Put,
            new { profileId, allowedEvents = new[] { "Backup" } }, HttpStatusCode.Conflict), "NotificationScopeChanged");
    }

    private static async Task QolAssertNotificationPersistenceAsync(HttpClient owner, HttpClient joining,
        Guid profileId, Guid primary, Guid peer)
    {
        var host = QolNotification(await QolRequestAsync(owner, "/api/local/desktop/notifications", HttpMethod.Get));
        Require(host.QuietMode && host.AllowedEvents.SequenceEqual(new[] { "Lifecycle", "Backup", "Update" }) &&
            host.Servers.Single(item => item.ProfileId == profileId && item.ConnectionId is null).AllowedEvents.SequenceEqual(new[] { "Backup" }),
            "Host notification preferences did not survive packaged restart");
        var friend = QolNotification(await QolRequestAsync(joining, "/api/local/desktop/notifications", HttpMethod.Get));
        Require(friend.QuietMode &&
            friend.Servers.Single(item => item.ProfileId == profileId && item.ConnectionId == primary).AllowedEvents.SequenceEqual(new[] { "Backup" }) &&
            friend.Servers.Single(item => item.ProfileId == profileId && item.ConnectionId == peer).AllowedEvents.SequenceEqual(new[] { "Network" }),
            "per-saved-Host Friend notification overrides did not survive packaged restart");
    }

    private static DesktopNotificationPreferenceView QolNotification(JsonElement value)
    {
        Require(value.GetProperty("quietMode").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetProperty("allowedEvents").ValueKind == JsonValueKind.Array && value.GetProperty("servers").ValueKind == JsonValueKind.Array &&
            value.GetProperty("systemState").ValueKind == JsonValueKind.String &&
            value.GetProperty("systemAllowsNotifications").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "notification preference JSON schema drifted");
        return value.Deserialize<DesktopNotificationPreferenceView>(Json) ?? throw new Exception("empty notification preference result");
    }

    private static async Task QolUpdateChecksAsync(HttpClient owner)
    {
        await QolRequestAsync(owner, "/api/local/update/preparation", HttpMethod.Get,
            expectedStatus: HttpStatusCode.Forbidden, localHeader: false);
        QolCode(await QolRequestAsync(owner, "/api/local/update/preparation?path=ignored", HttpMethod.Get,
            expectedStatus: HttpStatusCode.BadRequest), "InvalidDiagnosticsRequest");
        var preparation = await QolRequestAsync(owner, "/api/local/update/preparation", HttpMethod.Get);
        Require(preparation.GetProperty("stage").GetString() == "Idle" &&
            preparation.GetProperty("message").ValueKind == JsonValueKind.String && preparation.GetProperty("downloadedBytes").GetInt64() == 0 &&
            preparation.GetProperty("totalBytes").ValueKind == JsonValueKind.Null && preparation.GetProperty("blocker").ValueKind == JsonValueKind.Null,
            "idle update preparation JSON schema or download state drifted");
        foreach (var duration in new[] { "OneHour", "Tomorrow", "ThreeDays", "SevenDays", "SkipVersion", "Clear" })
        {
            var result = await QolRequestAsync(owner, "/api/local/update/snooze", HttpMethod.Post, new { version = "999.0.0", duration });
            Require(!result.GetProperty("ok").GetBoolean(), "staging enabled a real updater reminder");
            QolCode(result, "UpdatesDisabled");
        }
        QolCode(await QolRequestAsync(owner, "/api/local/update/snooze", HttpMethod.Post,
            new { version = "999.0.0", duration = "Forever" }, HttpStatusCode.BadRequest), "InvalidUpdateReminder");
        QolCode(await QolRequestAsync(owner, "/api/local/update/snooze", HttpMethod.Post,
            new { version = "../unreviewed", duration = "OneHour" }, HttpStatusCode.BadRequest), "InvalidUpdateReminder");
        QolCode(await QolRequestAsync(owner, "/api/local/update/snooze", HttpMethod.Post,
            expectedStatus: HttpStatusCode.BadRequest, raw: "{\"version\":\"999.0.0\",\"version\":\"999.0.1\",\"duration\":\"OneHour\"}"), "InvalidUpdateReminder");
        var after = await QolRequestAsync(owner, "/api/local/update/preparation", HttpMethod.Get);
        Require(after.GetProperty("stage").GetString() == "Idle" && after.GetProperty("downloadedBytes").GetInt64() == 0,
            "a reminder or rejected update input started a download");
        Console.WriteLine("PASS packaged update preparation schema and fixed snooze parser/disabled guard; no update check, download or install requested");
    }

    private static async Task<Guid> QolBackupChecksAsync(HttpClient owner, Guid profileId, Guid otherProfileId,
        string sourceFile, byte[] originalHash)
    {
        var path = $"/api/local/profiles/{profileId}/backup-catalog";
        await QolRequestAsync(owner, path, HttpMethod.Get, expectedStatus: HttpStatusCode.Forbidden, localHeader: false);
        QolCode(await QolRequestAsync(owner, path + "?path=ignored", HttpMethod.Get,
            expectedStatus: HttpStatusCode.BadRequest), "InvalidFeatureRequest");
        var empty = QolCatalog(await QolRequestAsync(owner, path, HttpMethod.Get));
        Require(empty.Ok && empty.ProfileId == profileId && empty.Backups.Count == 0 &&
            empty.MaximumPinnedCount == 20 && empty.MaximumLabelLength == 64 && empty.MaximumPinnedSizeBytes == 50L * 1024 * 1024 * 1024,
            "new backup catalog JSON bindings or pin bounds drifted");
        var missing = QolCatalog(await QolRequestAsync(owner, $"/api/local/profiles/{Guid.NewGuid()}/backup-catalog", HttpMethod.Get));
        Require(!missing.Ok && missing.Code == "UnknownProfile" && missing.Backups.Count == 0,
            "unknown-profile backup catalog fell back to another saved server");
        var manual = await QolRequestAsync(owner, $"/api/local/profiles/{profileId}/backups/manual", HttpMethod.Post, new { });
        Require(manual.GetProperty("ok").GetBoolean(), "disposable offline backup failed");
        var catalog = QolCatalog(await QolRequestAsync(owner, path, HttpMethod.Get));
        var backup = catalog.Backups.Single();
        Require(backup.ProfileId == profileId && backup.GameKind == GameKinds.Valheim && backup.WorldId == "qol-fixture" &&
            backup.BackupKind == BackupKinds.Manual && backup.MetadataAvailable && backup.PayloadSha256 is { Length: 64 } &&
            backup.SizeBytes > 0 && backup.FileCount > 0 && backup.CreatedUtc != default && backup.Evidence.Count == 0,
            "completed catalog omitted exact metadata or inferred unmeasured protection evidence");
        var verified = await QolRequestAsync(owner, $"/api/local/profiles/{profileId}/backups/{backup.BackupId}/verify", HttpMethod.Post, new { });
        Require(verified.GetProperty("ok").GetBoolean() && verified.GetProperty("backupId").GetGuid() == backup.BackupId,
            "actual local backup verification did not bind its selected copy");
        QolCode(verified, "BackupVerified");
        var rehearsed = await QolRequestAsync(owner, $"/api/local/profiles/{profileId}/backups/{backup.BackupId}/rehearse", HttpMethod.Post, new { });
        Require(rehearsed.GetProperty("ok").GetBoolean() && rehearsed.GetProperty("backupId").GetGuid() == backup.BackupId,
            "disposable hash restore rehearsal did not bind its selected copy");
        QolCode(rehearsed, "RestoreRehearsalCompleted");
        var wrongProfile = await QolRequestAsync(owner, $"/api/local/profiles/{otherProfileId}/backups/{backup.BackupId}/verify", HttpMethod.Post, new { });
        Require(!wrongProfile.GetProperty("ok").GetBoolean(), "verification accepted another saved profile's backup ID");
        await QolAssertBackupEvidenceAsync(owner, profileId, backup.BackupId);
        var rehearsedWorldHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceFile));
        Require(originalHash.SequenceEqual(rehearsedWorldHash),
            "catalog verification or scratch rehearsal changed synthetic live-world bytes");
        Console.WriteLine("PASS packaged backup catalog schemas, exact profile/copy binding and separately retained integrity/hash-restore evidence");
        return backup.BackupId;
    }

    private static async Task QolAssertBackupEvidenceAsync(HttpClient owner, Guid profileId, Guid backupId)
    {
        var catalog = QolCatalog(await QolRequestAsync(owner, $"/api/local/profiles/{profileId}/backup-catalog", HttpMethod.Get));
        Require(catalog.Ok && catalog.ProfileId == profileId && catalog.EvidenceAvailable, "backup evidence storage was unavailable");
        var backup = catalog.Backups.Single(item => item.BackupId == backupId);
        Require(backup.Evidence.Count == 2 &&
            backup.Evidence.Single(item => item.Kind == BackupEvidenceKinds.Integrity) is { Outcome: "Passed", Code: "BackupVerified" } &&
            backup.Evidence.Single(item => item.Kind == BackupEvidenceKinds.HashRehearsal) is { Outcome: "Passed", Code: "RestoreRehearsalCompleted" } &&
            backup.Evidence.All(item => item.BackupId == backupId && item.CheckedUtc != default && item.CheckedUtc <= DateTimeOffset.UtcNow),
            "measured evidence was not retained independently for its exact completed backup");
        Require(backup.Evidence.All(item => item.Kind is not (BackupEvidenceKinds.Vault or BackupEvidenceKinds.OwnerGameRehearsal)),
            "a local hash check fabricated a vault transfer or owner game-acceptance result");
    }

    private static BackupCatalogView QolCatalog(JsonElement value)
    {
        foreach (var field in new[] { "ok", "code", "message", "profileId", "backups", "pinnedCount", "pinnedSizeBytes",
            "maximumLabelLength", "maximumPinnedCount", "maximumPinnedSizeBytes", "moreBackupsAvailable", "retention",
            "retainedSizeBytes", "availableSpaceBytes", "evidenceAvailable" })
            Require(value.TryGetProperty(field, out _), "backup catalog JSON omitted " + field);
        foreach (var backup in value.GetProperty("backups").EnumerateArray())
        {
            foreach (var field in new[] { "backupId", "profileId", "createdUtc", "backupKind", "sizeBytes", "label", "pinned", "gameKind",
                "worldId", "fileCount", "setupIncluded", "payloadSha256", "setupSha256", "metadataAvailable", "evidence" })
                Require(backup.TryGetProperty(field, out _), "backup summary JSON omitted " + field);
            foreach (var fact in backup.GetProperty("evidence").EnumerateArray())
                foreach (var field in new[] { "backupId", "kind", "outcome", "checkedUtc", "code" })
                    Require(fact.TryGetProperty(field, out _), "backup evidence JSON omitted " + field);
        }
        return value.Deserialize<BackupCatalogView>(Json) ?? throw new Exception("empty backup catalog result");
    }

    private static async Task<Guid> QolPairAsync(HttpClient owner, HttpClient joining, Guid profileId)
    {
        var invite = await QolRequestAsync(owner, $"/api/local/servers/{profileId}/invite", HttpMethod.Post,
            new ServerInviteRequest(false, false, true));
        Require(invite.GetProperty("ok").GetBoolean() && invite.GetProperty("listenerActive").GetBoolean(),
            "disposable Host invite did not start its loopback pinned listener");
        var code = invite.GetProperty("password").GetString();
        Require(!string.IsNullOrEmpty(code), "disposable Host invite was empty");
        var paired = await PostAsync<FriendPairRequest, FriendActionResult>(joining, "/api/local/friend/pair", new(code!));
        Require(paired.Ok, "disposable Friend normal pairing failed: " + paired.Code);
        var connected = await WaitForConnectedAsync(joining, allowDisabled: true);
        Require(connected.HostId != Guid.Empty && connected.ConnectionId != Guid.Empty && connected.Profiles.Single().Id == profileId &&
            connected.Connections?.Any(item => item.ConnectionId == connected.ConnectionId) == true,
            "paired Host/profile did not bind to its saved connection ID");
        return connected.ConnectionId;
    }

    private static async Task QolProbeChecksAsync(HttpClient owner, HttpClient joining, ServerProfile profile,
        Guid unassignedProfileId, Guid primary, Guid peer)
    {
        var path = $"/api/local/friend/{profile.Id}/probe-game";
        var friendDraft = new QolDraft("chat", profile.Id, primary, "compose");
        QolCode(await QolRequestAsync(joining, "/api/local/ui-drafts/read", HttpMethod.Post,
            QolDraftIdentity(friendDraft), HttpStatusCode.Conflict), "DraftScopeChanged");
        var wrongConnection = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post,
            new { connectionId = primary, runOperationId = (Guid?)null }));
        Require(!wrongConnection.Answered && wrongConnection.Code == "GameProbeScopeChanged",
            "an unselected saved Host probe fell back to the selected equal-GUID server");
        var wrongRun = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post,
            new { connectionId = peer, runOperationId = Guid.NewGuid() }));
        Require(!wrongRun.Answered && wrongRun.Code == "GameProbeScopeChanged", "unknown run ID passed the selected Host scope gate");
        await QolRequestAsync(joining, path, HttpMethod.Post, new { connectionId = peer, runOperationId = (Guid?)null },
            HttpStatusCode.Forbidden, localHeader: false);
        QolCode(await QolRequestAsync(joining, path + "?host=ignored", HttpMethod.Post,
            new { connectionId = peer, runOperationId = (Guid?)null }, HttpStatusCode.BadRequest), "InvalidFeatureRequest");
        QolCode(await QolRequestAsync(joining, path, HttpMethod.Post, new { connectionId = peer }, HttpStatusCode.BadRequest), "InvalidGameProbe");
        QolCode(await QolRequestAsync(joining, path, HttpMethod.Post,
            new { connectionId = peer, runOperationId = (Guid?)null, address = "127.0.0.1:1" }, HttpStatusCode.BadRequest), "InvalidGameProbe");
        QolCode(await QolRequestAsync(joining, path, HttpMethod.Post,
            new { connectionId = peer, runOperationId = Guid.Empty }, HttpStatusCode.BadRequest), "InvalidGameProbe");
        QolCode(await QolRequestAsync(joining, path, HttpMethod.Post, expectedStatus: HttpStatusCode.BadRequest,
            raw: "{\"connectionId\":\"" + peer + "\",\"runOperationId\":null,\"runOperationId\":null}"), "InvalidGameProbe");
        QolCode(await QolRequestAsync(owner, path, HttpMethod.Post,
            new { connectionId = primary, runOperationId = (Guid?)null }, HttpStatusCode.Conflict), "HostMode");

        var selection = await QolRequestAsync(joining, $"/api/local/friend/connections/{primary}/select", HttpMethod.Post, new { });
        Require(selection.GetProperty("ok").GetBoolean(), "primary saved Host selection failed");
        Require((await WaitForConnectedAsync(joining, allowDisabled: true)).ConnectionId == primary, "saved Host selection was not canonical");
        Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/password",
            new("synthetic-qol-fixture-password"))).Ok, "synthetic fixture password was rejected");
        Require((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { })).Ok,
            "owned synthetic fixture did not start");
        _ = await WaitForRunStateAsync(owner, profile.Id, "Ready");
        var ready = await WaitForConnectedAsync(joining, allowDisabled: true);
        var operation = ready.Profiles.Single(item => item.Id == profile.Id).RunOperationId;
        Require(operation is not null && operation != Guid.Empty, "running fixture omitted canonical run operation ID");
        var staleNull = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post,
            new { connectionId = primary, runOperationId = (Guid?)null }));
        Require(!staleNull.Answered && staleNull.Code == "GameProbeScopeChanged", "null legacy run matched a known current operation");
        var actual = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post, new { connectionId = primary, runOperationId = operation }));
        // Valheim's product join address requires public IPv4. This loopback-only
        // journey never supplies one or sends a query outside its own instances.
        // The typed endpoint-unavailable result proves this exact request passed
        // the connection/run scope gate, without inventing route or join proof.
        Require(!actual.Answered && actual.Code == "GameEndpointUnavailable" && actual.OnlinePlayers is null,
            "the exact loopback-only fixture run did not preserve its absent game-route evidence");
        var otherProfile = QolProbe(await QolRequestAsync(joining, $"/api/local/friend/{unassignedProfileId}/probe-game", HttpMethod.Post,
            new { connectionId = primary, runOperationId = (Guid?)null }));
        Require(!otherProfile.Answered && otherProfile.Code == "UnknownProfile", "an unassigned server was probed by profile ID");
        var devices = await QolRequestAsync(owner, "/api/local/companion", HttpMethod.Get);
        var device = devices.GetProperty("devices").EnumerateArray().Single().GetProperty("id").GetGuid();
        Require((await PutAsync<DeviceServerAccessRequest, PairingDecision>(owner,
            $"/api/local/devices/{device}/servers", new([unassignedProfileId]))).Ok, "disposable assignment removal failed");
        _ = await WaitForConnectedAsync(joining, allowDisabled: true);
        var removed = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post, new { connectionId = primary, runOperationId = operation }));
        Require(!removed.Answered && removed.Code == "UnknownProfile", "removed assignment still authorized a game probe");
        Require((await PutAsync<DeviceServerAccessRequest, PairingDecision>(owner,
            $"/api/local/devices/{device}/servers", new([profile.Id]))).Ok, "disposable assignment restore failed");
        _ = await WaitForConnectedAsync(joining, allowDisabled: true);
        Require((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { })).Ok,
            "owned synthetic fixture did not stop gracefully");
        _ = await WaitForRunStateAsync(owner, profile.Id, "Offline");
        Require((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { })).Ok,
            "owned synthetic fixture did not restart");
        _ = await WaitForRunStateAsync(owner, profile.Id, "Ready");
        var restarted = await WaitForConnectedAsync(joining, allowDisabled: true);
        var newOperation = restarted.Profiles.Single(item => item.Id == profile.Id).RunOperationId;
        Require(newOperation is not null && newOperation != operation, "fixture restart reused the old run observation ID");
        var oldRun = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post, new { connectionId = primary, runOperationId = operation }));
        Require(!oldRun.Answered && oldRun.Code == "GameProbeScopeChanged", "prior run probe survived fixture restart");
        var newRun = QolProbe(await QolRequestAsync(joining, path, HttpMethod.Post, new { connectionId = primary, runOperationId = newOperation }));
        Require(!newRun.Answered && newRun.Code == "GameEndpointUnavailable", "current restarted fixture run failed its exact probe binding");
        Console.WriteLine("PASS packaged selected-Host probes reject unselected connection, stale/null run, removed assignment and caller targets; current fixture run retains unavailable-route evidence");
    }

    private static GameEndpointProbeResult QolProbe(JsonElement value)
    {
        foreach (var field in new[] { "answered", "code", "message", "checkedUtc", "onlinePlayers", "maxPlayers" })
            Require(value.TryGetProperty(field, out _), "game probe JSON omitted " + field);
        var result = value.Deserialize<GameEndpointProbeResult>(Json) ?? throw new Exception("empty game probe result");
        Require(result.CheckedUtc != default && result.CheckedUtc <= DateTimeOffset.UtcNow, "game probe omitted a real check time");
        return result;
    }

    private static void QolCode(JsonElement value, string expected) =>
        Require(value.ValueKind == JsonValueKind.Object && value.GetProperty("code").GetString() == expected,
            "QoL endpoint returned a different typed outcome than " + expected);

    private static ProtectedUiDraftResult QolDraftResult(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.GetProperty("ok").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetProperty("text").ValueKind is JsonValueKind.String or JsonValueKind.Null &&
            value.GetProperty("revision").TryGetInt64(out var revision) && revision is >= 0 and <= 9_007_199_254_740_991 &&
            value.GetProperty("message").ValueKind == JsonValueKind.String,
            "protected draft JSON schema drifted");
        return value.Deserialize<ProtectedUiDraftResult>(Json) ?? throw new Exception("empty protected draft result");
    }

    private static async Task<JsonElement> QolRequestAsync(HttpClient client, string path, HttpMethod method,
        object? body = null, HttpStatusCode expectedStatus = HttpStatusCode.OK, bool localHeader = true, string? raw = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (raw is not null) request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
        if (localHeader) request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == expectedStatus, $"QoL local {method} {path} returned {(int)response.StatusCode}; expected {(int)expectedStatus}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Require(bytes.Length <= 8 * 1024 * 1024, "QoL local response exceeded its journey bound");
        if (bytes.Length == 0) return default;
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
}
