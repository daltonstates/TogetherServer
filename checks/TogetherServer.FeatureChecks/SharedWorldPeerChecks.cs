using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Signatures, protected synthetic files and pure projections only. No app,
// process, browser, listener, native dialog, game or production data is used.
internal static class SharedWorldPeerChecks
{
    internal static void Run(string checkRoot)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static void Invalid(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new Exception(message);
        }
        static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        static string Hash(int value) => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(value)));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var friend = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var joiner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var receiver = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var friendId = Guid.NewGuid();
        var joinerId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var rosterDraft = new SharedWorldRoster(3, root.GroupId, root.ProfileId, 1, 1, true,
            root.OwnerPublicKey,
            [new(friendId, Public(friend), new(true, true, true, true, true), false),
             new(joinerId, Public(joiner), new(), false),
             new(receiverId, Public(receiver), new(Receive: true), false)], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: root.OwnerPublicKey);
        SharedWorldRoster SignRoster(SharedWorldRoster draft) => draft with
        {
            Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        var roster = SignRoster(rosterDraft);
        Require(SharedWorldRosterTrust.Verify(roster), "synthetic owner roster did not verify");
        var trust = new SharedWorldPeerTrust(root,
            (id, key) => SharedWorldPeerTrust.AccessFromRoster(root, roster, id, key));
        var route = new SharedWorldPeerRoute("https://192.0.2.10:5131/", Hash(10));
        var movedRoute = route with { Address = "https://198.51.100.22:5131/" };
        var branch = new SharedWorldPeerBranch(Hash(20), Hash(21), Hash(21), null, 1);
        var session = new SharedWorldPeerSession(Guid.NewGuid(), branch.BranchHash, 1,
            "Hosting", branch.BaseVersionHash, branch.HeadVersionHash,
            DateTimeOffset.Parse("2001-01-01T00:00:00Z"));
        SharedWorldPeerAnnouncement Sign(ECDsa key, Guid id, long revision = 1, string? previous = null,
            IReadOnlyList<SharedWorldPeerRoute>? routes = null,
            IReadOnlyList<SharedWorldPeerBranch>? branches = null,
            IReadOnlyList<SharedWorldPeerSession>? sessions = null,
            IReadOnlyList<string>? copies = null) => SharedWorldPeerTrust.Sign(new(1, root, id, Public(key),
                revision, previous, routes ?? [], sessions ?? [], branches ?? [], copies ?? [],
                DateTimeOffset.Parse("2001-01-01T00:00:00Z"), "", ""), key);

        var old = Sign(owner, Guid.Empty, routes: [route]);
        Require(trust.Verify(old), "old metadata was expired by an age cutoff");
        Require(root.GroupKey == (root with { }).GroupKey && root.GroupKey !=
            (root with { GroupId = Guid.NewGuid() }).GroupKey && root.GroupKey !=
            (root with { ProfileId = Guid.NewGuid() }).GroupKey && root.GroupKey !=
            (root with { OwnerPublicKey = Public(friend) }).GroupKey,
            "root identity was not bound to owner, source profile and source group");
        foreach (var changed in new[]
        {
            old with { DeviceId = friendId }, old with { Revision = 2 },
            old with { Routes = [movedRoute] }, old with { AnnouncementHash = Hash(999) },
            old with { Root = root with { GroupId = Guid.NewGuid() } },
            old with { Signature = Convert.ToBase64String(new byte[64]) }
        }) Require(!trust.Verify(changed), "altered peer metadata verified");
        foreach (var address in new[]
        {
            "http://192.0.2.10:5131/", "https://example.test:5131/", "https://127.0.0.1:5131/",
            "https://169.254.169.254/", "https://0.0.0.0/", "https://224.0.0.1/",
            "https://192.0.2.10/private", "https://192.0.2.10/?path=secret",
            "https://user@192.0.2.10/", "https://192.0.2.10/#fragment", "https://[::1]/"
        }) Require(!SharedWorldPeerTrust.ValidRoute(route with { Address = address }), "an arbitrary or local proxy route was accepted");
        Require(!SharedWorldPeerTrust.ValidRoute(route with { CertificateSha256 = "invalid" }), "unpinned peer route was accepted");
        Require(trust.Verify(Sign(joiner, joinerId, routes: [route])) &&
            !trust.Verify(Sign(joiner, joinerId, copies: [branch.HeadVersionHash])) &&
            !trust.Verify(Sign(joiner, joinerId, branches: [branch], sessions: [session])) &&
            trust.Verify(Sign(receiver, receiverId, copies: [branch.HeadVersionHash])) &&
            !trust.Verify(Sign(receiver, receiverId, branches: [branch])),
            "metadata following, Receive and EligibleHost permissions were conflated");

        var basePath = Path.Combine(checkRoot, "signed-peers-" + Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(basePath, "first");
        var secondPath = Path.Combine(basePath, "second");
        var thirdPath = Path.Combine(basePath, "offline-return");
        SharedWorldPeerAnnouncement first;
        SharedWorldPeerAnnouncement second;
        SharedWorldPeerAnnouncement friendAnnouncement;
        using (var firstData = new LocalData(firstPath))
        using (var secondData = new LocalData(secondPath))
        using (var thirdData = new LocalData(thirdPath))
        {
            var firstStore = new SharedWorldPeerStore(firstData, root, trust);
            var secondStore = new SharedWorldPeerStore(secondData, root, trust);
            var thirdStore = new SharedWorldPeerStore(thirdData, root, trust);
            first = firstStore.Publish(Guid.Empty, owner, [route], [], []);
            Require(firstStore.Publish(Guid.Empty, owner, [route], [], []).AnnouncementHash == first.AnnouncementHash &&
                firstStore.Project().AnnouncementCount == 1, "unchanged polling metadata consumed a new journal revision");
            second = firstStore.Publish(Guid.Empty, owner, [movedRoute], [session], [branch], [branch.HeadVersionHash]);
            Require(secondStore.Import(second).Code == "PeerCatchUpRequired" &&
                secondStore.Project().AnnouncementCount == 0, "a missing signed parent was silently skipped");
            Require(secondStore.ImportPage(firstStore.ReadPage()).Accepted == 2, "the first peer did not catch up");
            var otherBranch = branch with
            {
                BranchHash = Hash(30),
                HeadVersionHash = Hash(31),
                ParentVersionHash = branch.HeadVersionHash,
                VersionNumber = 2
            };
            friendAnnouncement = secondStore.Publish(friendId, friend, [route], [], [otherBranch], [otherBranch.HeadVersionHash]);
            Require(thirdStore.ImportPage(secondStore.ReadPage()).Accepted == 3,
                "an offline PC could not catch up through a peer while the original owner was absent");
            var learned = thirdStore.Project();
            Require(learned.GroupKey == root.GroupKey && learned.Peers.Count == 2 && learned.Branches.Count == 2 &&
                learned.Sessions.Count == 1 && learned.NeedsChoice && learned.DefaultChoices.Count == 0,
                "multiple playable histories were collapsed into a timestamp-selected default");
            Require(thirdStore.Import(first).Code == "AlreadyKnown" && thirdStore.Project().AnnouncementCount == 3,
                "an old replay advanced or replaced the signed head");
            var conflict = SharedWorldPeerTrust.Sign(friendAnnouncement with { Routes = [movedRoute], Signature = "" }, friend);
            Require(thirdStore.Import(conflict).Code == "PeerConflictPreserved" &&
                thirdStore.Project().Peers.Single(value => value.DeviceId == friendId) is { HasConflict: true, Routes.Count: 0 } &&
                thirdStore.Project().Branches.Count == 2,
                "signer equivocation replaced an endpoint or deleted a branch");
            thirdStore.RecordSync(friendId, Public(friend), true, secondStore.ReadPage().Total);
            thirdStore.RecordSync(friendId, Public(friend), false, secondStore.ReadPage().Total);
            var health = thirdStore.Project().Replication;
            Require(health.LastSuccessfulSyncUtc is not null && health.PeersLastSyncSucceeded == 0 && health.HasConflicts,
                "a failed sync erased history or presented old contact as current connectivity");
            var revokedDraft = rosterDraft with
            {
                Members = rosterDraft.Members.Select(value =>
                value.DeviceId == friendId ? value with { Revoked = true } : value).ToArray()
            };
            roster = SignRoster(revokedDraft);
            Require(!thirdStore.Project().Peers.Single(value => value.DeviceId == friendId).Authorized &&
                thirdStore.Project().Branches.Count == 2 &&
                thirdStore.Import(Sign(friend, friendId, 2, friendAnnouncement.AnnouncementHash, routes: [route])).Code == "PeerRejected",
                "revocation allowed new metadata or erased a preserved competing history");
            roster = SignRoster(rosterDraft);

            var pageBytes = JsonSerializer.SerializeToUtf8Bytes(firstStore.ReadPage(), SharedWorldPeerTrust.Json);
            Require(SharedWorldPeerTrust.DecodePage(pageBytes).Total == 2, "valid bounded peer page did not decode");
            var pageText = Encoding.UTF8.GetString(pageBytes);
            Invalid(() => SharedWorldPeerTrust.DecodePage(Encoding.UTF8.GetBytes(pageText.Replace(
                "\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal))), "duplicate peer fields decoded");
            Invalid(() => SharedWorldPeerTrust.DecodePage(Encoding.UTF8.GetBytes(pageText.Replace(
                "\"offset\":0", "\"offset\":0,\"path\":\"arbitrary\"", StringComparison.Ordinal))), "unknown peer fields decoded");
            Invalid(() => SharedWorldPeerTrust.DecodePage(new byte[SharedWorldPeerTrust.MaximumPageBytes + 1]), "oversized peer page decoded");
            Invalid(() => thirdStore.ReadPage(-1), "a negative metadata cursor was accepted");
        }
        using (var reopenedData = new LocalData(thirdPath))
        {
            var reopened = new SharedWorldPeerStore(reopenedData, root, trust);
            Require(reopened.Project().AnnouncementCount == 4 && reopened.Project().Replication.HasConflicts &&
                reopened.Import(first).Code == "AlreadyKnown" && reopened.ReadPage(4).Announcements.Count == 0,
                "durable peer history, revision replay protection or its append cursor was lost on restart");
            reopenedData.SaveProtected(reopened.StateName, [1, 2, 3]);
            Invalid(() => reopened.Project(), "damaged protected metadata was replaced with an empty world state");
        }

        using var capData = new LocalData(Path.Combine(basePath, "capacity"));
        var capStore = new SharedWorldPeerStore(capData, root, trust);
        string? previous = null;
        for (var revision = 1; revision <= SharedWorldPeerStore.MaximumKnownBranches / SharedWorldPeerTrust.MaximumBranches; revision++)
        {
            var hints = Enumerable.Range((revision - 1) * SharedWorldPeerTrust.MaximumBranches, SharedWorldPeerTrust.MaximumBranches)
                .Select(index => new SharedWorldPeerBranch(Hash(1000 + index), Hash(2000 + index), Hash(2000 + index), null, 1)).ToArray();
            var value = Sign(owner, Guid.Empty, revision, previous, branches: hints);
            Require(capStore.Import(value).Changed, "bounded branch metadata was refused before its capacity");
            previous = value.AnnouncementHash;
        }
        var countBefore = capStore.Project().AnnouncementCount;
        Invalid(() => capStore.Import(Sign(owner, Guid.Empty, countBefore + 1, previous,
            branches: [new(Hash(9000), Hash(9001), Hash(9001), null, 1)])),
            "new branches evicted unresolved history instead of refusing the capacity overflow");
        Require(capStore.Project().Branches.Count == SharedWorldPeerStore.MaximumKnownBranches &&
            capStore.Project().AnnouncementCount == countBefore, "capacity refusal changed existing histories");
    }
}
