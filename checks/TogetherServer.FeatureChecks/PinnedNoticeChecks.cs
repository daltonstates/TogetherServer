using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Safe on the owner's active desktop: synthetic protected state/crypto only.
// No listeners, app/game/fixture processes, console attachment or native UI.
internal static class PinnedNoticeChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Run(string root)
    {
        var hostId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var otherProfile = Guid.NewGuid();
        var friendId = Guid.NewGuid();
        var hostRoot = Path.Combine(root, "notice-host-" + Guid.NewGuid().ToString("N"));
        var peerRoot = Path.Combine(root, "notice-peer-" + Guid.NewGuid().ToString("N"));
        PinnedServerNotice first;
        PinnedServerNotice changed;
        PinnedServerNotice cleared;
        string key;
        using (var hostData = new LocalData(hostRoot))
        using (var peerData = new LocalData(peerRoot))
        {
            var host = new ServerChat(hostData);
            var peer = new ServerChat(peerData);
            Require(host.ReadOwnerNotice(hostId, profileId) is null, "a new room invented a notice");
            first = host.SetOwnerNotice(hostId, profileId, "Rules\nPlease leave the spawn clear.", 0);
            key = host.OwnerPublicKey();
            Require(first.Revision == 1 && ServerChat.VerifyNotice(first, key, hostId, profileId),
                "the owner notice was not signed and room-scoped");
            Require(peer.AcceptNoticeCopy(hostId, profileId, first, key) &&
                peer.ReadNoticeCopy(hostId, profileId, key) == first, "a valid notice copy was not durable");
            Require(peer.AcceptNoticeCopy(hostId, profileId, first, key), "retrying an immutable notice was rejected");
            Require(host.SetOwnerNotice(hostId, profileId, first.Text, 1) == first,
                "retrying an owner edit created a different revision");
            RequireThrows<PinnedNoticeConflictException>(() => host.SetOwnerNotice(hostId, profileId,
                "Stale editor", 0), "an outdated owner editor overwrote the notice");
            Require(!peer.AcceptNoticeCopy(hostId, otherProfile, first, key) &&
                !peer.AcceptNoticeCopy(Guid.NewGuid(), profileId, first, key),
                "a notice crossed its signed Host or profile scope");
            Require(!peer.AcceptNoticeCopy(hostId, profileId, first with { Text = "Forged rules" }, key) &&
                !peer.AcceptNoticeCopy(hostId, profileId, first with { Revision = 2 }, key) &&
                !peer.AcceptNoticeCopy(hostId, profileId, first with { Text = null }, key) &&
                !peer.AcceptNoticeCopy(hostId, profileId, first with { Signature = "not-base64" }, key),
                "a changed or malformed revision passed signature checks");
            using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var otherPublicKey = Convert.ToBase64String(unrelatedKey.ExportSubjectPublicKeyInfo());
            Require(!peer.AcceptNoticeCopy(hostId, profileId, first, otherPublicKey),
                "a different signing identity replaced the pinned owner");
            RequireThrows<InvalidDataException>(() => peer.ReadNoticeCopy(hostId, profileId, otherPublicKey),
                "a cache was shown under a different pinned identity");
            Require(peer.ReadNoticeCopy(hostId, profileId, key) == first,
                "a rejected external identity damaged the legitimate cache");
            var sameRevisionEdit = Sign(hostData, first with { Text = "Signed but reused revision" });
            Require(!peer.AcceptNoticeCopy(hostId, profileId, sameRevisionEdit, key),
                "an immutable revision accepted different signed content");
            var oldSignedNotice = Sign(hostData, first with
            {
                ProfileId = otherProfile,
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-90)
            });
            Require(ServerChat.VerifyNotice(oldSignedNotice, key, hostId, otherProfile) &&
                peer.AcceptNoticeCopy(hostId, otherProfile, oldSignedNotice, key),
                "chat's 30-day retention removed a still-current pinned notice");
            changed = host.SetOwnerNotice(hostId, profileId, "Maintenance tonight at 8.", 1);
            Require(changed.Revision == 2 && peer.AcceptNoticeCopy(hostId, profileId, changed, key),
                "a newer owner revision was not accepted");
            Require(!peer.AcceptNoticeCopy(hostId, profileId, first, key) &&
                !peer.AcceptNoticeCopy(hostId, profileId, null, key),
                "an older or missing notice rolled the cache back");
            var reversedTime = Sign(hostData, changed with
            {
                Revision = 3,
                UpdatedUtc = first.UpdatedUtc.AddTicks(-1)
            });
            Require(!peer.AcceptNoticeCopy(hostId, profileId, reversedTime, key),
                "a revision with a backwards timestamp replaced the cache");
            cleared = host.SetOwnerNotice(hostId, profileId, null, 2);
            Require(cleared.Text is null && cleared.Revision == 3 &&
                ServerChat.VerifyNotice(cleared, key, hostId, profileId) &&
                peer.AcceptNoticeCopy(hostId, profileId, cleared, key), "clear did not store a signed tombstone");
            Require(!peer.AcceptNoticeCopy(hostId, profileId, first, key) &&
                !peer.AcceptNoticeCopy(hostId, profileId, changed, key) &&
                !peer.AcceptNoticeCopy(hostId, profileId, null, key), "a deleted notice was resurrected");
            var entry = host.Post(hostId, profileId, Guid.Empty, "Host", "An ordinary chat message");
            Require(peer.Merge(hostId, profileId, [entry], key), "notice state broke ordinary chat sync");
            hostData.DeleteProtected($"chat-{hostId:N}-{profileId:N}.protected");
            Require(host.Merge(hostId, profileId, peer.Read(hostId, profileId), key) &&
                host.ReadOwnerNotice(hostId, profileId) == cleared,
                "restoring a lost chat log replaced the owner's latest notice authority");
            host.SetMember(profileId, friendId, false);
            Require(!host.IsMember(profileId, friendId) && host.ReadOwnerNotice(hostId, profileId) == cleared,
                "room removal changed the owner's persistent notice");
            peer.Forget(hostId, friendId, [profileId], preserveSharedRoomCopies: true);
            Require(peer.ReadNoticeCopy(hostId, profileId, key) == cleared,
                "forgetting one shared route removed another route's notice cache");
            var syncInput = JsonSerializer.Deserialize<ChatSyncRequest>(JsonSerializer.Serialize(new
            {
                entries = new[] { entry },
                drafts = Array.Empty<ChatDraft>(),
                notice = first
            }, Json), Json);
            Require(syncInput is not null && !typeof(ChatSyncRequest).GetProperties()
                .Any(property => property.Name.Contains("Notice", StringComparison.OrdinalIgnoreCase)) &&
                host.Merge(hostId, profileId, syncInput.Entries, key) &&
                host.ReadOwnerNotice(hostId, profileId) == cleared,
                "Friend sync input exposed notice mutation or replaced a clear");
            RequireThrows<ArgumentException>(() => host.SetOwnerNotice(hostId, profileId,
                new string('x', ServerChat.MaximumNoticeTextLength + 1)), "an oversized notice was accepted");
            foreach (var invalid in new[] { "", "  \n\t", "rules\0", "rules\rhidden", "rules\u202e" })
                RequireThrows<ArgumentException>(() => host.SetOwnerNotice(hostId, profileId, invalid),
                    "a non-plain notice was accepted");
            Require(ServerChat.ValidNoticeText(new string('x', ServerChat.MaximumNoticeTextLength)),
                "the documented text boundary was rejected");
            Require(host.ReadOwnerNotice(hostId, profileId) == cleared,
                "invalid owner edits changed the existing revision");
        }
        // Dispose and reopen the same disposable stores: signatures, monotonic
        // state and signed clears must survive an app-state restart.
        using (var hostData = new LocalData(hostRoot))
        using (var peerData = new LocalData(peerRoot))
        {
            var host = new ServerChat(hostData);
            var peer = new ServerChat(peerData);
            Require(host.OwnerPublicKey() == key && host.ReadOwnerNotice(hostId, profileId) == cleared &&
                peer.ReadNoticeCopy(hostId, profileId, key) == cleared &&
                !peer.AcceptNoticeCopy(hostId, profileId, changed, key),
                "a restart lost the signing key or signed clear high-water mark");
            hostData.SaveProtected($"chat-notice-owner-{hostId:N}-{profileId:N}.protected",
                Encoding.UTF8.GetBytes("{broken"));
            RequireThrows<InvalidDataException>(() => host.ReadOwnerNotice(hostId, profileId),
                "damaged owner notice state looked like no notice");
            RequireThrows<InvalidDataException>(() => host.SetOwnerNotice(hostId, profileId, "Silent reset"),
                "a quarantined owner notice reset its revision");
            peerData.SaveProtected($"chat-notice-copy-{hostId:N}-{profileId:N}.protected",
                Encoding.UTF8.GetBytes("{broken"));
            RequireThrows<InvalidDataException>(() => peer.ReadNoticeCopy(hostId, profileId, key),
                "damaged cache state looked like no notice");
            RequireThrows<InvalidDataException>(() => peer.AcceptNoticeCopy(hostId, profileId, first, key),
                "a quarantined cache silently accepted an older revision");
            peer.Forget(hostId, friendId, [profileId]);
            Require(peer.ReadNoticeCopy(hostId, profileId, key) is null,
                "forgetting the last room copy did not clear its isolated local notice cache");
        }
    }

    private static PinnedServerNotice Sign(LocalData data, PinnedServerNotice notice)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(data.LoadProtected("chat-owner-key.protected")!, out _);
        var basis = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            purpose = ServerChat.PinnedNoticeCapability,
            notice.HostId,
            notice.ProfileId,
            notice.Revision,
            notice.UpdatedUtc,
            notice.Text
        }, Json);
        return notice with { Signature = Convert.ToBase64String(key.SignData(basis, HashAlgorithmName.SHA256)) };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
