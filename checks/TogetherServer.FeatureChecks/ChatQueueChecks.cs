using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Protected synthetic files and an injected HTTP handler only. No listener,
// app, game, process, console, browser, dialog or real credential is used.
internal static class ChatQueueChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "chat-queue-" + Guid.NewGuid().ToString("N")));
        var chat = new ServerChat(data);
        var hostId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var draft = chat.Queue(hostId, profileId, deviceId, "Unsent original");
        Require(draft.Submitted == false && chat.EditQueued(hostId, profileId, deviceId, draft.Id,
            new("Reviewed original", "Unsent original")).Ok, "a proven unsent draft could not be edited");
        Require(!chat.EditQueued(hostId, profileId, deviceId, draft.Id,
            new("Stale edit", "Unsent original")).Ok &&
            chat.Pending(hostId, profileId, deviceId).Single().Text == "Reviewed original",
            "a stale editor overwrote a newer local draft");
        Require(!chat.CancelQueued(hostId, profileId, Guid.NewGuid(), draft.Id, new("Reviewed original")).Ok &&
            chat.Pending(hostId, profileId, deviceId).Count == 1, "a different device canceled another queue");
        Require(chat.CancelQueued(hostId, profileId, deviceId, draft.Id, new("Reviewed original")).Ok &&
            chat.Read(hostId, profileId).Count == 0, "cancel changed accepted history or retained the unsent draft");

        for (var index = 0; index < ServerChat.MaximumDrafts; index++)
            chat.Queue(hostId, profileId, deviceId, "Bounded draft " + index);
        Throws<InvalidOperationException>(() => chat.Queue(hostId, profileId, deviceId, "Overflow"),
            "the twenty-message queue bound was not enforced");
        var bounded = chat.Pending(hostId, profileId, deviceId)[0];
        Require(!chat.EditQueued(hostId, profileId, deviceId, bounded.Id,
            new(new string('x', 501), bounded.Text)).Ok &&
            chat.Pending(hostId, profileId, deviceId).Count == 20, "editing bypassed text or queue limits");
        chat.MarkPendingSubmitted(hostId, profileId, deviceId);
        var restarted = new ServerChat(data);
        Require(restarted.Pending(hostId, profileId, deviceId).All(item => item.Submitted == true) &&
            !restarted.CancelQueued(hostId, profileId, deviceId, bounded.Id, new(bounded.Text)).Ok,
            "restart forgot that dispatched messages may already be accepted");
        var signed = chat.Post(hostId, profileId, deviceId, "Synthetic Friend", bounded.Text, bounded.Id);
        Require(!chat.EditQueued(hostId, profileId, deviceId, bounded.Id, new("Changed accepted text", bounded.Text)).Ok &&
            chat.Read(hostId, profileId).Single() == signed && ServerChat.Verify(signed, chat.OwnerPublicKey(), hostId, profileId),
            "a queue edit altered the immutable accepted signed entry");
        chat.Confirm(hostId, profileId, deviceId);
        Require(chat.Pending(hostId, profileId, deviceId).Count == 19, "confirmation did not remove exactly the accepted ID");

        var legacyDevice = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        data.SaveProtected($"chat-pending-{hostId:N}-{profileId:N}-{legacyDevice:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(new[] { new { id = legacyId, text = "Legacy uncertain draft" } }, Json));
        Require(!chat.CancelQueued(hostId, profileId, legacyDevice, legacyId, new("Legacy uncertain draft")).Ok,
            "an old queue without dispatch evidence was treated as safely editable");

        CheckParsers();
        await CheckLinkGateAsync(root);
    }

    private static void CheckParsers()
    {
        static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        Require(ServerChat.TryQueueEdit(Bytes("{\"text\":\"new\",\"expectedText\":\"old\"}"), out var edit) &&
            edit == new ChatQueueEditRequest("new", "old"), "a valid strict edit was not decoded");
        Require(ServerChat.TryQueueCancel(Bytes("{\"expectedText\":\"old\"}"), out _), "a valid strict cancel was not decoded");
        Require(ServerChat.TryChatPost(Bytes("{\"text\":\"message\"}"), out var post) && post!.Text == "message",
            "a valid strict post was not decoded");
        foreach (var value in new[] { "{}", "{\"text\":null}", "{\"text\":\" \"}",
            "{\"text\":\"message\",\"text\":\"second\"}", "{\"text\":\"message\",\"path\":\"world\"}" })
            Require(!ServerChat.TryChatPost(Bytes(value), out _), "an invalid scoped chat post was accepted");
        foreach (var value in new[]
        {
            "{}", "[]", "null", "{\"text\":null,\"expectedText\":\"old\"}",
            "{\"text\":\"new\",\"expectedText\":1}", "{\"text\":\"\",\"expectedText\":\"old\"}",
            "{\"text\":\"new\",\"expectedText\":\"old\",\"path\":\"world\"}",
            "{\"text\":\"new\",\"text\":\"second\",\"expectedText\":\"old\"}",
            "{\"Text\":\"new\",\"expectedText\":\"old\"}",
            "{\"text\":\"new\\u0000\",\"expectedText\":\"old\"}"
        }) Require(!ServerChat.TryQueueEdit(Bytes(value), out _), "an invalid queue edit schema was accepted");
        foreach (var value in new[]
        {
            "{}", "{\"expectedText\":null}", "{\"expectedText\":\" \"}",
            "{\"expectedText\":\"old\",\"expectedText\":\"old\"}",
            "{\"expectedText\":\"old\",\"text\":\"new\"}"
        }) Require(!ServerChat.TryQueueCancel(Bytes(value), out _), "an invalid queue cancel schema was accepted");
        Require(!ServerChat.TryQueueEdit(new byte[ServerChat.MaximumQueueMutationBytes + 1], out _),
            "an oversized queue mutation was decoded");
    }

    private static async Task CheckLinkGateAsync(string root)
    {
        using var hostData = new LocalData(Path.Combine(root, "queue-host-" + Guid.NewGuid().ToString("N")));
        using var peerData = new LocalData(Path.Combine(root, "queue-peer-" + Guid.NewGuid().ToString("N")));
        var hostId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var host = new ServerChat(hostData);
        var peer = new ServerChat(peerData);
        const string file = "queue-friend.protected";
        var configuration = new FriendConfiguration
        {
            HostId = hostId, DeviceId = deviceId, Credential = new string('Q', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
            Endpoint = "https://127.0.0.1:5131", Fingerprint = new string('A', 64),
            CachedProfiles = [new(profileId, "Synthetic server", "Offline", null)]
        };
        peerData.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        using var handler = new FakeHost(host, hostId, profileId, deviceId);
        using var link = new FriendLink(peerData, file, (endpoint, pins) =>
        {
            Require(endpoint == configuration.Endpoint && pins.Contains(configuration.Fingerprint),
                "the queue escaped the saved pinned transport");
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
        });
        var queued = await link.PostChatAsync(profileId, "Offline question");
        var id = queued.Pending.Single().Id;
        Require((await link.EditQueuedChatAsync(profileId, id, new("Reviewed question", "Offline question"))).Ok &&
            handler.SyncCount == 0, "an offline local edit dispatched a Host request");

        handler.LoseReply = true;
        await link.PollAsync(); // Its automatic sync accepts the message, then loses the reply.
        Require(peer.Pending(hostId, profileId, deviceId).Single().Submitted == true &&
            host.Read(hostId, profileId).Single().Text == "Reviewed question" && !handler.InputHadSubmitted,
            "lost-reply dispatch was not recorded durably or local delivery evidence escaped to the Host");
        Require(!(await link.CancelQueuedChatAsync(profileId, id, new("Reviewed question"))).Ok &&
            !(await link.EditQueuedChatAsync(profileId, id, new("Late edit", "Reviewed question"))).Ok,
            "an uncertain accepted message remained editable or cancelable");
        handler.LoseReply = false;
        await link.SyncChatAsync(profileId);
        Require(peer.Pending(hostId, profileId, deviceId).Count == 0 && host.Read(hostId, profileId).Count == 1,
            "a lost-reply retry duplicated an accepted ID or kept a confirmed draft");

        handler.Hold = true;
        var sync = link.SyncChatAsync(profileId);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var mutation = link.CancelQueuedChatAsync(profileId, id, new("Reviewed question"));
        Require(!mutation.IsCompleted, "queue mutation raced the in-flight automatic/manual sync gate");
        using (var canceled = new CancellationTokenSource())
        {
            var canceledEdit = link.EditQueuedChatAsync(profileId, id, new("Canceled edit", "Reviewed question"), canceled.Token);
            canceled.Cancel();
            try { await canceledEdit; throw new InvalidOperationException("a scope cancellation still executed queue work"); }
            catch (OperationCanceledException) { }
            Require(!sync.IsCompleted, "canceling a waiting mutation released another operation's sync gate");
        }
        handler.Release.TrySetResult(true);
        await sync;
        Require(!(await mutation).Ok && host.Read(hostId, profileId).Count == 1,
            "serialization let a post-acceptance cancel change immutable history");

        var notice = host.SetOwnerNotice(hostId, profileId, "Synthetic notice", 0);
        handler.Notice = notice;
        await link.SyncChatAsync(profileId);
        var before = handler.SyncCount;
        var summary = await link.ChatSummaryAsync(profileId);
        Require(summary.Ok && summary.Notice == notice && summary.MessageIds.Count == 1 && handler.SyncCount == before,
            "closed-room summary dispatched a remote request or failed to project authorized IDs/notice");
        handler.Denial = "ChatAccessDenied";
        await link.SyncChatAsync(profileId);
        var denied = await link.ChatSummaryAsync(profileId);
        Require(!denied.Ok && denied.Notice is null && denied.MessageIds.Count == 0,
            "a removed room exposed summary content");
        var held = peer.Queue(hostId, profileId, deviceId, "Protected unsent question in removed room");
        await link.SyncChatAsync(profileId);
        Require(handler.LastDraftCount == 0 && peer.Pending(hostId, profileId, deviceId).Single().Id == held.Id &&
            peer.Pending(hostId, profileId, deviceId).Single().Submitted == false,
            "an access-restoration probe disclosed or marked drafts from a removed room");
        handler.Denial = "AccessExpired";
        await link.SyncChatAsync(profileId);
        Require(!(await link.ChatSummaryAsync(profileId)).Ok,
            "owner access expiry left closed-room summary accessible");
    }

    private sealed class FakeHost(ServerChat chat, Guid hostId, Guid profileId, Guid deviceId) : HttpMessageHandler
    {
        public bool LoseReply { get; set; }
        public bool Hold { get; set; }
        public int SyncCount { get; private set; }
        public int LastDraftCount { get; private set; }
        public bool InputHadSubmitted { get; private set; }
        public string? Denial { get; set; }
        public PinnedServerNotice? Notice { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/companion/heartbeat")
                return Reply(new CompanionStatus(true, null, [new(profileId, "Synthetic server", "Offline", null)],
                    false, false, DateTimeOffset.UtcNow,
                    new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        [CompanionProtocol.ServerChatCapability, ServerChat.PinnedNoticeCapability])));
            Require(request.RequestUri.AbsolutePath == $"/api/companion/servers/{profileId}/chat/sync",
                "an unreviewed request escaped the chat transport");
            SyncCount++;
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            InputHadSubmitted |= document.RootElement.GetProperty("drafts").EnumerateArray()
                .Any(draft => draft.TryGetProperty("submitted", out _));
            var body = JsonSerializer.Deserialize<ChatSyncRequest>(bytes, Json);
            LastDraftCount = body?.Drafts?.Count ?? 0;
            if (Hold) { Entered.TrySetResult(true); await Release.Task.WaitAsync(cancellationToken); }
            if (Denial is not null) return Reply(new { code = Denial }, HttpStatusCode.Forbidden);
            foreach (var draft in body?.Drafts ?? []) chat.Post(hostId, profileId, deviceId, "Synthetic Friend", draft.Text, draft.Id);
            if (LoseReply) throw new HttpRequestException("Synthetic lost reply after acceptance.");
            return Reply(new ChatSyncResponse(true, "ChatSynced", "Synced", hostId, profileId,
                chat.OwnerPublicKey(), chat.Read(hostId, profileId), Notice));
        }

        private static HttpResponseMessage Reply<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(body, options: Json) };
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException(message); }
}
