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
        KeyParsingCacheChecks();
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
        var friendPublicKey = Public(friend);
        Require(SharedWorldRosterTrust.ValidKey(root.OwnerPublicKey) &&
            SharedWorldRosterTrust.ValidKey(friendPublicKey) &&
            SharedWorldRosterTrust.ValidKey(friendPublicKey) &&
            WorldGroupAccess.MayReceive(roster, friendId, friendPublicKey),
            "valid parsing cache inputs did not retain current roster access");
        foreach (var deniedMember in new[]
        {
            rosterDraft.Members[0] with { Revoked = true },
            rosterDraft.Members[0] with { AccessExpiresUtc = DateTimeOffset.UnixEpoch }
        })
        {
            var denied = SignRoster(rosterDraft with
            {
                Members = rosterDraft.Members.Select(value => value.DeviceId == friendId ? deniedMember : value).ToArray()
            });
            Require(SharedWorldRosterTrust.Verify(denied) &&
                !WorldGroupAccess.MayFollow(denied, friendId, friendPublicKey) &&
                !WorldGroupAccess.MayReceive(denied, friendId, friendPublicKey) &&
                SharedWorldPeerTrust.AccessFromRoster(root, denied, friendId, friendPublicKey) is null,
                "warm canonical keys bypassed current revocation or access expiry");
        }
        var withdrawn = SignRoster(rosterDraft with
        {
            Members = rosterDraft.Members.Select(value => value.DeviceId == friendId
                ? value with { Grants = new() } : value).ToArray()
        });
        Require(WorldGroupAccess.MayFollow(withdrawn, friendId, friendPublicKey) &&
            !WorldGroupAccess.MayReceive(withdrawn, friendId, friendPublicKey) &&
            !SharedWorldRosterTrust.VerifySignature(roster with { Revision = roster.Revision + 1 }),
            "warm canonical keys cached Receive grants or a prior signature result");
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

    private static void KeyParsingCacheChecks()
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = Public(firstKey);
        var second = Public(secondKey);
        var validations = 0;
        var cache = new CanonicalPublicKeyParsingCache(() => validations++);
        Require(cache.IsValid(first) && cache.IsValid(new string(first.ToCharArray())) &&
            validations == 1 && cache.Count == 1,
            "an exact canonical key string was reparsed instead of reused");
        Require(first != second && cache.IsValid(second) && validations == 2 && cache.Count == 2,
            "different canonical key bytes did not receive independent validation");

        var bytes = Convert.FromBase64String(first);
        Require(bytes[0] == 0x30 && bytes[1] < 128, "the generated SPKI did not use the expected short outer length");
        var noncanonicalDer = new byte[bytes.Length + 1];
        noncanonicalDer[0] = bytes[0];
        noncanonicalDer[1] = 0x81;
        noncanonicalDer[2] = bytes[1];
        bytes.AsSpan(2).CopyTo(noncanonicalDer.AsSpan(3));
        var trailingBytes = new byte[bytes.Length + 1];
        bytes.CopyTo(trailingBytes, 0);
        using var wrongSize384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var wrongSize521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        var malformed = "!" + first[1..];
        foreach (var invalid in new string?[]
        {
            null, "", new string('A', 79), new string('A', 513), malformed, first.ToLowerInvariant(),
            "\r\n" + first, first.Insert(40, " "),
            Convert.ToBase64String(trailingBytes), Convert.ToBase64String(noncanonicalDer),
            Public(wrongSize384), Public(wrongSize521)
        }) Require(!cache.IsValid(invalid), "a malformed, noncanonical, trailing-byte or wrong-size key was cached as valid");
        var beforeRepeatedInvalid = validations;
        Require(!cache.IsValid(malformed) && !cache.IsValid(malformed) &&
            validations == beforeRepeatedInvalid + 2 && cache.Count == 2,
            "invalid parsing results were retained or changed positive cache membership");
        Require(cache.IsValid(first) && cache.IsValid(second) && validations == beforeRepeatedInvalid + 2,
            "invalid inputs evicted an existing positive parsing result");

        using var validationEntered = new ManualResetEventSlim();
        using var releaseValidation = new ManualResetEventSlim();
        var concurrentValidations = 0;
        var concurrent = new CanonicalPublicKeyParsingCache(() =>
        {
            if (Interlocked.Increment(ref concurrentValidations) != 1) return;
            validationEntered.Set();
            if (!releaseValidation.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("the generated key validation barrier was not released");
        });
        var held = Task.Run(() => concurrent.IsValid(first));
        Task<bool>? duplicate = null;
        try
        {
            Require(validationEntered.Wait(TimeSpan.FromSeconds(5)), "the generated cache miss did not begin validation");
            duplicate = Task.Run(() => concurrent.IsValid(new string(first.ToCharArray())));
            Require(duplicate.Wait(TimeSpan.FromSeconds(5)) && duplicate.Result,
                "a concurrent cache miss held the cache lock across key validation");
        }
        finally
        {
            releaseValidation.Set();
            Require(held.GetAwaiter().GetResult(), "a concurrent canonical key validation failed");
            if (duplicate is not null) Require(duplicate.GetAwaiter().GetResult(), "a duplicate canonical validation failed");
        }
        Require(concurrentValidations == 2 && concurrent.Count == 1,
            "concurrent positive insertions did not coalesce to one exact key");
        var warmHits = Enumerable.Range(0, 16).Select(_ => Task.Run(() => concurrent.IsValid(first))).ToArray();
        Require(Task.WhenAll(warmHits).GetAwaiter().GetResult().All(value => value) && concurrentValidations == 2,
            "concurrent warm key hits repeated validation");

        var capacityValidations = 0;
        var bounded = new CanonicalPublicKeyParsingCache(() => capacityValidations++);
        var generated = new List<string>();
        for (var index = 0; index <= CanonicalPublicKeyParsingCache.MaximumEntries; index++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var value = Public(key);
            generated.Add(value);
            Require(bounded.IsValid(value) && bounded.Count <= CanonicalPublicKeyParsingCache.MaximumEntries,
                "positive parsing cache exceeded its fixed capacity");
        }
        Require(generated.Distinct(StringComparer.Ordinal).Count() == generated.Count &&
            bounded.Count == CanonicalPublicKeyParsingCache.MaximumEntries &&
            capacityValidations == generated.Count && bounded.IsValid(generated[^1]) &&
            capacityValidations == generated.Count && bounded.IsValid(generated[0]) &&
            capacityValidations == generated.Count + 1 &&
            bounded.Count == CanonicalPublicKeyParsingCache.MaximumEntries,
            "bounded positive cache did not reuse retained keys and revalidate an evicted key");
    }
}
