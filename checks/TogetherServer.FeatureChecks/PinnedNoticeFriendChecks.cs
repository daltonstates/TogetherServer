using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Injected HTTP only: does not open a listener or use the network/desktop.
internal static class PinnedNoticeFriendChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string root)
    {
        using var hostData = new LocalData(Path.Combine(root, "notice-link-host-" + Guid.NewGuid().ToString("N")));
        using var peerData = new LocalData(Path.Combine(root, "notice-link-peer-" + Guid.NewGuid().ToString("N")));
        var hostId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var host = new ServerChat(hostData);
        var first = host.SetOwnerNotice(hostId, profileId, "Rules for this server", 0);
        var key = host.OwnerPublicKey();
        const string configFile = "notice-link.protected";
        var configuration = new FriendConfiguration
        {
            HostId = hostId,
            DeviceId = Guid.NewGuid(),
            Credential = new string('C', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('A', 64),
            CachedProfiles = [new(profileId, "Synthetic server", "Offline", null)]
        };
        peerData.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        var handler = new FakeHost(hostId, profileId, key, first);
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == configuration.Endpoint && pins.Contains(configuration.Fingerprint),
                "the Friend did not use its saved endpoint/pin transport");
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
        }
        PinnedServerNotice cleared;
        using (var link = new FriendLink(peerData, configFile, Client))
        {
            await link.PollAsync();
            var synced = await link.SyncChatAsync(profileId);
            Require(synced.Ok && synced.Notice == first && synced.NoticeSupported && !synced.NoticeCached,
                "an authenticated current notice was not shown as current");
            Require(peerData.LoadProtectedJson<FriendConfiguration>(configFile)?.ChatOwnerKeys[profileId] == key &&
                !handler.InputHadNotice, "the Friend did not persist the signing pin or sent notice mutation input");
            handler.Offline = true;
            var offline = await link.SyncChatAsync(profileId);
            Require(offline.Notice == first && offline.NoticeCached && offline.Code == "ChatOffline",
                "transport failure lost or mislabelled the verified cached notice");
            handler.Offline = false;
            handler.Notice = cleared = host.SetOwnerNotice(hostId, profileId, null, 1);
            Require((await link.SyncChatAsync(profileId)).Notice == cleared, "a signed clear did not reach the Friend");
            handler.Notice = first;
            var replay = await link.SyncChatAsync(profileId);
            Require(!replay.Ok && replay.Code == "NoticeCopyRejected" && replay.Notice == cleared && replay.NoticeCached,
                "a stale notice replaced or hid the signed clear");
            handler.Notice = null;
            Require((await link.SyncChatAsync(profileId)).Code == "NoticeCopyRejected",
                "a missing response silently cleared the observed revision floor");
            handler.NoticeSupported = false;
            await link.PollAsync();
            var olderPeer = await link.SyncChatAsync(profileId);
            Require(olderPeer.Ok && !olderPeer.NoticeSupported && olderPeer.NoticeCached && olderPeer.Notice == cleared,
                "a notice-unaware Host broke chat or made the old notice look current");
            handler.ChatSupported = false;
            await link.PollAsync();
            var unsupported = await link.SyncChatAsync(profileId);
            Require(!unsupported.Ok && unsupported.Code == "ChatUpdateRequired" && unsupported.Notice == cleared &&
                unsupported.NoticeCached, "a protocol-old Host was confused with current supported notices");
            handler.ChatSupported = handler.NoticeSupported = true;
            handler.Notice = cleared;
            await link.PollAsync();
            handler.PublicKey = new ServerChat(hostData).OwnerPublicKey() + "bad";
            Require((await link.SyncChatAsync(profileId)).Code == "ChatCopyRejected",
                "a different signing key was accepted for a pinned connection");
            handler.PublicKey = key;
            handler.ResponseHostId = Guid.NewGuid();
            Require((await link.SyncChatAsync(profileId)).Code == "ChatCopyRejected",
                "a different Host identity was accepted for a saved room");
            handler.ResponseHostId = hostId;
            handler.DeniedCode = "ChatAccessDenied";
            var removed = await link.SyncChatAsync(profileId);
            Require(!removed.Ok && removed.Notice is null && removed.Entries.Count == 0,
                "room removal returned chat or pinned notice content");
            handler.DeniedCode = null;
            Require((await link.SyncChatAsync(profileId)).Notice == cleared, "restored room membership could not sync");
            handler.Assigned = false;
            await link.PollAsync();
            Require(link.ChatRoom(profileId).Notice is null && !link.ChatRoom(profileId).Ok,
                "an unassigned server still exposed its notice");
            handler.Assigned = true;
            await link.PollAsync();
            handler.DeniedCode = "AccessExpired";
            Require((await link.SyncChatAsync(profileId)).Notice is null && link.View().State == "Access expired",
                "typed owner expiry retained accessible notice content");
            handler.DeniedCode = null;
            await link.PollAsync();
            handler.DeniedCode = "Revoked";
            Require((await link.SyncChatAsync(profileId)).Notice is null && link.View().State == "Revoked",
                "typed revocation retained accessible notice content");
        }
        handler.DeniedCode = null;
        using (var restarted = new FriendLink(peerData, configFile, Client))
        {
            var cached = restarted.ChatRoom(profileId);
            Require(cached.Notice == cleared && cached.NoticeCached && !cached.NoticeSupported,
                "restart lost the signed clear cache or labelled it current before authentication");
            await restarted.PollAsync();
            Require((await restarted.SyncChatAsync(profileId)).Notice == cleared,
                "restart changed the durable signing pin or current revision");
        }

        await CheckPinPersistenceFailureAsync(root, hostId, profileId, key, first);
    }

    private static async Task CheckPinPersistenceFailureAsync(string root, Guid hostId, Guid profileId,
        string key, PinnedServerNotice notice)
    {
        using var peerData = new LocalData(Path.Combine(root, "notice-pin-fault-" + Guid.NewGuid().ToString("N")));
        const string configFile = "notice-pin-fault.protected";
        var configuration = new FriendConfiguration
        {
            HostId = hostId,
            DeviceId = Guid.NewGuid(),
            Credential = new string('D', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('B', 64),
            CachedProfiles = [new(profileId, "Synthetic server", "Offline", null)]
        };
        peerData.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        using var handler = new FakeHost(hostId, profileId, key, notice);
        var blockedPath = Path.Combine(peerData.RootPath, configFile);
        handler.BeforeFirstSync = () =>
        {
            peerData.DeleteProtected(configFile);
            Directory.CreateDirectory(blockedPath);
        };
        using var link = new FriendLink(peerData, configFile,
            (endpoint, _) => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(endpoint) });
        await link.PollAsync();
        Require(!peerData.HasProtected($"chat-notice-copy-{hostId:N}-{profileId:N}.protected"),
            "a notice was cached before its signing pin could be durably saved");
        Directory.Delete(blockedPath, recursive: false);
        peerData.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        var retried = await link.SyncChatAsync(profileId);
        Require(retried.Notice == notice &&
            peerData.LoadProtectedJson<FriendConfiguration>(configFile)?.ChatOwnerKeys[profileId] == key,
            "a failed pin write was not retried before caching the notice");
    }

    private sealed class FakeHost(Guid hostId, Guid profileId, string publicKey, PinnedServerNotice notice)
        : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public bool ChatSupported { get; set; } = true;
        public bool NoticeSupported { get; set; } = true;
        public bool Assigned { get; set; } = true;
        public string? DeniedCode { get; set; }
        public string PublicKey { get; set; } = publicKey;
        public Guid ResponseHostId { get; set; } = hostId;
        public PinnedServerNotice? Notice { get; set; } = notice;
        public bool InputHadNotice { get; private set; }
        public Action? BeforeFirstSync { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("Synthetic unavailable transport.");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/companion/heartbeat")
            {
                IReadOnlyList<string> capabilities = ChatSupported ? NoticeSupported
                    ? [CompanionProtocol.ServerChatCapability, ServerChat.PinnedNoticeCapability]
                    : [CompanionProtocol.ServerChatCapability] : [];
                return Reply(new CompanionStatus(true, null,
                    Assigned ? [new(profileId, "Synthetic server", "Offline", null)] : [],
                    false, false, DateTimeOffset.UtcNow,
                    new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum, capabilities)));
            }
            if (path == $"/api/companion/servers/{profileId}/chat/sync")
            {
                if (DeniedCode is not null) return Reply(new { code = DeniedCode }, HttpStatusCode.Forbidden);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                InputHadNotice |= document.RootElement.TryGetProperty("notice", out _);
                var before = BeforeFirstSync;
                BeforeFirstSync = null;
                before?.Invoke();
                return Reply(new ChatSyncResponse(true, "ChatSynced", "Synthetic authenticated response.",
                    ResponseHostId, profileId, PublicKey, [], NoticeSupported ? Notice : null));
            }
            throw new InvalidOperationException("The notice check attempted an unexpected HTTP route.");
        }
    }

    private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: Json) };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
