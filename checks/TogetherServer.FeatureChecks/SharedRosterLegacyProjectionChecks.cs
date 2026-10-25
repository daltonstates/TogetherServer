using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated identities and fake authenticated HTTP only. No listener, app or game process.
internal static class SharedRosterLegacyProjectionChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string root)
    {
        await JoinsEquivalentOwnerStateAsync(root);
        await ParentAcrossPagesAsync(root);
        await EqualCounterConnectionFloorAsync(root);
        await PreservesExactNewerFloorAsync(root);
        RejectsNonEquivalentStates(root);
        RecoversProjectionCommit(root);
        RecoversOrdinaryPendingWithoutProjectionFields(root);
        RejectsDamagedProjectionEvidence(root);
        ReplayPreservesLaterRevocation(root);
        PortableProjectionHistory(root);
        PeerChallengeImportsProjection(root);
        CanonicalPeerExportsProjectionProof(root);
        await ProofParentAnchorAcrossPagesAsync(root);
    }

    private static async Task JoinsEquivalentOwnerStateAsync(string root)
    {
        using var fixture = new Fixture(root);
        var originalBytes = File.ReadAllBytes(fixture.RevisionPath(fixture.Legacy));
        var originalLookup = fixture.Data.LoadProtected(fixture.LookupName(fixture.Legacy))!;
        var sharing = await fixture.CheckAsync();
        Require(sharing.Available && sharing.Revision == fixture.Current.Revision,
            "equivalent legacy owner state could not join its exact signed schema-3 history: " +
            sharing.Code + "; " + sharing.Message);
        Require(fixture.RootFallbacks == 1 && fixture.PageRequests == 2,
            "legacy projection catch-up did not use the echoed unknown-anchor root fallback");
        Require(File.ReadAllBytes(fixture.RevisionPath(fixture.Legacy)).AsSpan().SequenceEqual(originalBytes) &&
            fixture.Data.LoadProtected(fixture.LookupName(fixture.Legacy))!.AsSpan().SequenceEqual(originalLookup),
            "legacy projection replaced the retained immutable snapshot or protected lookup");
        var saved = fixture.Chain.Read(fixture.ProfileId);
        Require(saved.Count == 11 && SharedWorldRosterTrust.Hash(saved[0]) == SharedWorldRosterTrust.Hash(fixture.Legacy) &&
            saved.Skip(1).Select(SharedWorldRosterTrust.Hash).SequenceEqual(
                fixture.History.Skip(1).Select(SharedWorldRosterTrust.Hash)) &&
            fixture.Chain.Heads(fixture.ProfileId).Single().Signature == fixture.Current.Signature,
            "legacy projection did not retain its old signed state and complete exact-parent continuation");
        fixture.AssertFloor(fixture.Current);
        Require(fixture.Chain.ContainsFloor(fixture.ProfileId, new(fixture.Trusted.GroupId,
            fixture.Trusted.Epoch, fixture.Trusted.Revision, fixture.Trusted.Signature)),
            "legacy projection omitted the exact protected newer connection floor");
        Require((await fixture.CheckHostingAsync()).Allowed && fixture.UnexpectedRequests == 0 &&
            fixture.Data.LoadSettings().Profiles.Count == 0 && !fixture.Data.LoadSettings().CompanionListeningEnabled,
            "permission-only legacy projection required a payload, listener, world or local server");
        AssertProof(fixture.Data, fixture.Projection, fixture.Parent);
        var before = fixture.PageRequests;
        Require((await fixture.CheckAsync()).Available && fixture.PageRequests == before,
            "a completed legacy projection fetched its history again");
    }

    private static async Task ParentAcrossPagesAsync(string root)
    {
        using var fixture = new Fixture(root, precedingLegacyCount: 12, lastRevision: 22);
        var sharing = await fixture.CheckAsync();
        Require(sharing.Available && fixture.RootFallbacks == 1 && fixture.PageRequests == 3 &&
            fixture.Chain.Read(fixture.ProfileId).Count == 11 &&
            fixture.Chain.Heads(fixture.ProfileId).Single().Signature == fixture.Current.Signature,
            "a signed legacy parent at the preceding page boundary was lost during projection catch-up");
        fixture.AssertFloor(fixture.Current);
        AssertProof(fixture.Data, fixture.Projection, fixture.Parent);
    }

    private static async Task PreservesExactNewerFloorAsync(string root)
    {
        foreach (var fault in new[] { "omitted", "changed" })
        {
            using var fixture = new Fixture(root);
            var originalCache = fixture.Data.LoadProtected(fixture.CacheName)!;
            fixture.History = fault == "omitted"
                ? fixture.History.Where(item => item.Revision != fixture.Trusted.Revision).ToArray()
                : fixture.History.Select(item => item.Revision == fixture.Trusted.Revision
                    ? fixture.Sign(item with { OwnerOverride = !item.OwnerOverride }) : item).ToArray();
            var sharing = await fixture.CheckAsync();
            var containsCurrent = fixture.Chain.Contains(fixture.ProfileId, fixture.Current);
            var cacheSame = fixture.Data.LoadProtected(fixture.CacheName)!.AsSpan().SequenceEqual(originalCache);
            Require(!sharing.Available && sharing.Code == "RosterRollback" &&
                !containsCurrent && cacheSame,
                "legacy projection crossed an omitted or changed exact newer trusted floor: " + fault +
                "; available=" + sharing.Available + "; code=" + sharing.Code +
                "; containsCurrent=" + containsCurrent + "; cacheSame=" + cacheSame + "; message=" + sharing.Message);
            fixture.AssertFloor(fixture.Trusted);
        }
    }

    private static async Task EqualCounterConnectionFloorAsync(string root)
    {
        foreach (var seedJournal in new[] { true, false })
        {
            using var fixture = new Fixture(root, seedJournal: seedJournal);
            fixture.Trusted = fixture.Legacy;
            fixture.Current = fixture.Projection;
            fixture.History = [fixture.Parent, fixture.Projection];
            fixture.Data.SaveProtected(fixture.CacheName, JsonSerializer.SerializeToUtf8Bytes(fixture.Legacy, Json));
            Require(fixture.Chain.HasState(fixture.ProfileId) == seedJournal,
                "equal-counter fixture did not establish its intended saved-history boundary");
            Require((await fixture.CheckAsync()).Available && fixture.Chain.Read(fixture.ProfileId).Count == 2,
                "an exact protected legacy floor could not advance to its same-counter proved owner projection; seeded=" + seedJournal);
            fixture.AssertFloor(fixture.Projection);
            AssertProof(fixture.Data, fixture.Projection, fixture.Parent);
            var requests = fixture.PageRequests;
            Require((await fixture.CheckAsync()).Available && fixture.PageRequests == requests,
                "a proved same-counter owner projection became a rollback on the next check; seeded=" + seedJournal);
        }
    }

    private static void RejectsNonEquivalentStates(string root)
    {
        foreach (var fault in new[]
        {
            "schema2", "delegated", "fork", "member-added", "member-removed", "member-order",
            "grant", "management", "revoked", "deadline", "key", "device-id", "override",
            "epoch", "revision", "profile", "group", "owner", "baseline-missing", "baseline-changed",
            "baseline-order", "accepted-time", "acceptance-signature", "bad-signature",
            "parent-missing", "parent-schema3", "parent-bad-signature", "parent-profile",
            "parent-group", "parent-owner", "parent-unsigned-field", "legacy-unsigned-field"
        })
        {
            using var fixture = new Fixture(root, unsignedLegacyField: fault == "legacy-unsigned-field");
            var candidate = fixture.Projection;
            SharedWorldRoster? parent = fixture.Parent;
            var changedMember = candidate.Members[0];
            SharedWorldRosterMember[] ChangeMember(Func<SharedWorldRosterMember, SharedWorldRosterMember> change) =>
                candidate.Members.Select((item, index) => index == 0 ? change(item) : item).ToArray();
            switch (fault)
            {
                case "schema2": candidate = fixture.Legacy; break;
                case "delegated": candidate = fixture.SignAsDevice(candidate); break;
                case "fork": candidate = fixture.Sign(candidate with { PreviousRosterHash = new string('A', 64) }); break;
                case "member-added":
                    candidate = fixture.Sign(candidate with
                    { Members = [.. candidate.Members, changedMember with { DeviceId = Guid.NewGuid(), PublicKey = candidate.OwnerPublicKey }] }); break;
                case "member-removed": candidate = fixture.Sign(candidate with { Members = candidate.Members.Skip(1).ToArray() }); break;
                case "member-order": candidate = fixture.Sign(candidate with { Members = candidate.Members.Reverse().ToArray() }); break;
                case "grant":
                    candidate = fixture.Sign(candidate with
                    {
                        Members = ChangeMember(item => item with
                        { Grants = item.Grants with { Receive = !item.Grants.Receive } })
                    }); break;
                case "management":
                    candidate = fixture.Sign(candidate with
                    {
                        Members = ChangeMember(item => item with
                        { Grants = item.Grants with { ManageSharing = !item.Grants.ManageSharing } })
                    }); break;
                case "revoked": candidate = fixture.Sign(candidate with { Members = ChangeMember(item => item with { Revoked = !item.Revoked }) }); break;
                case "deadline":
                    candidate = fixture.Sign(candidate with
                    {
                        Members = ChangeMember(item => item with
                        { AccessExpiresUtc = (item.AccessExpiresUtc ?? DateTimeOffset.UtcNow).AddMinutes(1) })
                    }); break;
                case "key": candidate = fixture.Sign(candidate with { Members = ChangeMember(item => item with { PublicKey = candidate.OwnerPublicKey }) }); break;
                case "device-id": candidate = fixture.Sign(candidate with { Members = ChangeMember(item => item with { DeviceId = Guid.NewGuid() }) }); break;
                case "override": candidate = fixture.Sign(candidate with { OwnerOverride = !candidate.OwnerOverride }); break;
                case "epoch": candidate = fixture.Sign(candidate with { Epoch = candidate.Epoch + 1 }); break;
                case "revision": candidate = fixture.Sign(candidate with { Revision = candidate.Revision + 1 }); break;
                case "profile": candidate = fixture.Sign(candidate with { ProfileId = Guid.NewGuid() }); break;
                case "group": candidate = fixture.Sign(candidate with { GroupId = Guid.NewGuid() }); break;
                case "owner": candidate = fixture.OtherOwner(candidate); break;
                case "baseline-missing": candidate = fixture.Sign(candidate with { OwnerLocalBaselineMembers = null }); break;
                case "baseline-changed": candidate = fixture.Sign(candidate with { OwnerLocalBaselineMembers = ChangeMember(item => item with { Revoked = true }) }); break;
                case "baseline-order": candidate = fixture.Sign(candidate with { OwnerLocalBaselineMembers = candidate.Members.Reverse().ToArray() }); break;
                case "accepted-time": candidate = candidate with { HostAcceptedUtc = DateTimeOffset.UtcNow }; break;
                case "acceptance-signature": candidate = candidate with { HostAcceptanceSignature = new string('A', 88) }; break;
                case "bad-signature": candidate = candidate with { Signature = new string('A', 88) }; break;
                case "parent-missing": parent = null; break;
                case "parent-schema3": parent = fixture.Child(fixture.Parent); break;
                case "parent-bad-signature": parent = parent! with { Signature = new string('A', 88) }; break;
                case "parent-profile": parent = fixture.Sign(parent! with { ProfileId = Guid.NewGuid() }); break;
                case "parent-group": parent = fixture.Sign(parent! with { GroupId = Guid.NewGuid() }); break;
                case "parent-owner": parent = fixture.OtherOwner(parent!); break;
                case "parent-unsigned-field": parent = parent! with { SignerDeviceId = Guid.Empty }; break;
            }
            var before = CaptureFiles(fixture.Data.RootPath);
            Require(!fixture.Chain.TryAppendLegacyOwnerProjection(candidate, parent!, fixture.Legacy.OwnerPublicKey),
                "a non-equivalent or unproved legacy state was accepted as a projection: " + fault);
            AssertFilesEqual(before, CaptureFiles(fixture.Data.RootPath),
                "a rejected legacy projection changed synthetic durable state: " + fault);
        }
    }

    private static void RecoversProjectionCommit(string root)
    {
        foreach (var stage in new[] { "committed", "pending", "file" })
        {
            using var fixture = new Fixture(root);
            var oldRevision = File.ReadAllBytes(fixture.RevisionPath(fixture.Legacy));
            var oldLookup = fixture.Data.LoadProtected(fixture.LookupName(fixture.Legacy))!;
            Require(fixture.Chain.TryAppendLegacyOwnerProjection(fixture.Projection, fixture.Parent,
                fixture.Legacy.OwnerPublicKey, stopAfterPendingForChecks: stage == "pending",
                stopAfterFileForChecks: stage == "file"), "valid projection staging failed: " + stage);
            if (stage != "committed")
            {
                var bytes = fixture.Data.LoadProtected($"shared-roster-pending-{fixture.ProfileId:N}.protected")!;
                using var pending = JsonDocument.Parse(bytes);
                Require(pending.RootElement.GetProperty("legacyParent").Deserialize<SharedWorldRoster>(Json) is { } proof &&
                    SharedWorldRosterTrust.Hash(proof) == SharedWorldRosterTrust.Hash(fixture.Parent),
                    "pending legacy projection did not retain its exact signed parent: " + stage);
            }
            fixture.Data.Dispose();
            using var reopenedData = new LocalData(fixture.Data.RootPath);
            var reopened = new SharedWorldRosterChainStore(reopenedData);
            Require(reopened.Read(fixture.ProfileId).Count == 2 &&
                reopened.Heads(fixture.ProfileId).Single().Signature == fixture.Projection.Signature &&
                !reopenedData.HasProtected($"shared-roster-pending-{fixture.ProfileId:N}.protected") &&
                File.ReadAllBytes(fixture.RevisionPath(fixture.Legacy)).AsSpan().SequenceEqual(oldRevision) &&
                reopenedData.LoadProtected(fixture.LookupName(fixture.Legacy))!.AsSpan().SequenceEqual(oldLookup),
                "reopened projection recovery lost evidence or rewrote the original protected anchor: " + stage);
            AssertProof(reopenedData, fixture.Projection, fixture.Parent);
        }

        using (var fixture = new Fixture(root))
        {
            Require(fixture.Chain.TryAppendLegacyOwnerProjection(fixture.Projection, fixture.Parent,
                fixture.Legacy.OwnerPublicKey, stopAfterPendingForChecks: true), "could not stage tamper-negative projection");
            var name = $"shared-roster-pending-{fixture.ProfileId:N}.protected";
            var pending = JsonNode.Parse(fixture.Data.LoadProtected(name)!)!;
            pending["legacyParent"]!["signature"] = new string('A', 88);
            fixture.Data.SaveProtected(name, JsonSerializer.SerializeToUtf8Bytes(pending, Json));
            ExpectInvalid(() => fixture.Chain.Read(fixture.ProfileId),
                "pending recovery accepted a changed signed projection parent");
        }
    }

    private static void RejectsDamagedProjectionEvidence(string root)
    {
        foreach (var fault in new[] { "missing-proof", "changed-proof", "missing-projection-lookup",
            "missing-anchor-lookup", "missing-index", "changed-index", "changed-anchor-file",
            "missing-position", "changed-position" })
        {
            using var fixture = new Fixture(root);
            Require(fixture.Chain.TryAppendLegacyOwnerProjection(fixture.Projection, fixture.Parent,
                fixture.Legacy.OwnerPublicKey), "could not prepare proof-integrity negative");
            fixture.Chain.Append(fixture.Child(fixture.Projection), fixture.Legacy.OwnerPublicKey);
            switch (fault)
            {
                case "missing-proof":
                case "changed-proof":
                    var lookup = JsonNode.Parse(fixture.Data.LoadProtected(fixture.LookupName(fixture.Projection))!)!.AsObject();
                    if (fault == "missing-proof") lookup.Remove("legacyParent");
                    else lookup["legacyParent"]!["signature"] = new string('A', 88);
                    fixture.Data.SaveProtected(fixture.LookupName(fixture.Projection), JsonSerializer.SerializeToUtf8Bytes(lookup, Json));
                    break;
                case "missing-projection-lookup": fixture.Data.DeleteProtected(fixture.LookupName(fixture.Projection)); break;
                case "missing-anchor-lookup": fixture.Data.DeleteProtected(fixture.LookupName(fixture.Legacy)); break;
                case "missing-index": fixture.Data.DeleteProtected($"shared-roster-index-{fixture.ProfileId:N}-{0:D12}.protected"); break;
                case "changed-index":
                    var indexName = $"shared-roster-index-{fixture.ProfileId:N}-{0:D12}.protected";
                    var index = JsonNode.Parse(fixture.Data.LoadProtected(indexName)!)!;
                    index["hashes"]![0] = new string('A', 64);
                    fixture.Data.SaveProtected(indexName, JsonSerializer.SerializeToUtf8Bytes(index, Json));
                    break;
                case "changed-anchor-file":
                    File.WriteAllBytes(fixture.RevisionPath(fixture.Legacy), JsonSerializer.SerializeToUtf8Bytes(
                        fixture.Sign(fixture.Legacy with { OwnerOverride = !fixture.Legacy.OwnerOverride }), Json));
                    break;
                case "missing-position":
                case "changed-position":
                    var floorName = $"shared-roster-chain-{fixture.ProfileId:N}.protected";
                    var floor = JsonNode.Parse(fixture.Data.LoadProtected(floorName)!)!.AsObject();
                    if (fault == "missing-position") floor.Remove("legacyProjectionPosition");
                    else floor["legacyProjectionPosition"] = 2;
                    fixture.Data.SaveProtected(floorName, JsonSerializer.SerializeToUtf8Bytes(floor, Json));
                    break;
            }
            ExpectInvalid(() => fixture.Chain.Read(fixture.ProfileId),
                "damaged projection evidence was accepted on full read: " + fault);
            if (fault != "missing-projection-lookup")
                ExpectInvalid(() => fixture.Chain.Contains(fixture.ProfileId, fixture.Projection),
                    "damaged projection proof was accepted by committed lookup: " + fault);
            var before = CaptureFiles(fixture.Data.RootPath);
            ExpectInvalid(() => fixture.Chain.Append(fixture.Projection, fixture.Legacy.OwnerPublicKey),
                "projection replay rebuilt or bypassed damaged retained evidence: " + fault);
            AssertFilesEqual(before, CaptureFiles(fixture.Data.RootPath),
                "rejected projection replay changed damaged evidence: " + fault);
        }
    }

    private static void RecoversOrdinaryPendingWithoutProjectionFields(string root)
    {
        using var fixture = new Fixture(root);
        var ordinary = fixture.Child(fixture.Legacy);
        fixture.Chain.Append(ordinary, ordinary.OwnerPublicKey, stopAfterPendingForChecks: true);
        var pendingName = $"shared-roster-pending-{fixture.ProfileId:N}.protected";
        var floorName = $"shared-roster-chain-{fixture.ProfileId:N}.protected";
        using (var pending = JsonDocument.Parse(fixture.Data.LoadProtected(pendingName)!))
        using (var floor = JsonDocument.Parse(fixture.Data.LoadProtected(floorName)!))
            Require(!pending.RootElement.TryGetProperty("legacyParent", out _) &&
                !floor.RootElement.TryGetProperty("legacyProjectionPosition", out _),
                "ordinary pending metadata acquired projection fields instead of preserving its existing encoding");
        fixture.Data.Dispose();
        using var reopenedData = new LocalData(fixture.Data.RootPath);
        var reopened = new SharedWorldRosterChainStore(reopenedData);
        Require(reopened.Read(fixture.ProfileId).Count == 2 &&
            reopened.Heads(fixture.ProfileId).Single().Signature == ordinary.Signature &&
            !reopenedData.HasProtected(pendingName),
            "ordinary pending transaction without projection metadata no longer recovered");
        using var recoveredFloor = JsonDocument.Parse(reopenedData.LoadProtected(floorName)!);
        using var ordinaryLookup = JsonDocument.Parse(reopenedData.LoadProtected(fixture.LookupName(ordinary))!);
        Require(!recoveredFloor.RootElement.TryGetProperty("legacyProjectionPosition", out _) &&
            !ordinaryLookup.RootElement.TryGetProperty("legacyParent", out _),
            "ordinary pending recovery invented projection evidence");
    }

    private static void ReplayPreservesLaterRevocation(string root)
    {
        using var fixture = new Fixture(root);
        Require(fixture.Chain.TryAppendLegacyOwnerProjection(fixture.Projection, fixture.Parent,
            fixture.Legacy.OwnerPublicKey), "could not prepare replay-negative projection");
        var revoked = fixture.Sign(fixture.Child(fixture.Projection) with
        {
            Members = fixture.Projection.Members.Select(item => item with { Revoked = true }).ToArray()
        });
        fixture.Chain.Append(revoked, revoked.OwnerPublicKey);
        var before = CaptureFiles(fixture.Data.RootPath);
        if (!fixture.Chain.TryAppendLegacyOwnerProjection(fixture.Projection, fixture.Parent, fixture.Legacy.OwnerPublicKey))
            fixture.Chain.Append(fixture.Projection, fixture.Legacy.OwnerPublicKey);
        var latest = fixture.Chain.Heads(fixture.ProfileId).Single();
        Require(latest.Signature == revoked.Signature && fixture.Chain.Read(fixture.ProfileId).Count == 3 &&
            !SharedWorldRosterTrust.HasRole(latest, fixture.DeviceId, fixture.DevicePublicKey, grants => grants.Receive),
            "historical projection replay rewound the head or revived revoked sharing access");
        AssertFilesEqual(before, CaptureFiles(fixture.Data.RootPath),
            "historical projection replay rewrote protected evidence");
    }

    private static void PortableProjectionHistory(string root)
    {
        using var fixture = new Fixture(root);
        Require(fixture.Chain.TryAppendLegacyOwnerProjection(fixture.Projection, fixture.Parent,
            fixture.Legacy.OwnerPublicKey), "could not prepare portable projection history");
        foreach (var child in fixture.History.Skip(2)) fixture.Chain.Append(child, child.OwnerPublicKey);
        var page = fixture.Chain.ReadPage(fixture.ProfileId, 0);
        var anchored = fixture.Chain.ReadAfter(fixture.ProfileId, SharedWorldRosterTrust.Hash(fixture.Legacy))!;
        var byRevision = fixture.Chain.ReadAfterRevision(fixture.ProfileId, fixture.Legacy.Revision);
        foreach (var exported in new[] { page, anchored, byRevision })
        {
            Require(exported.Count == 11 && SharedWorldRosterTrust.Hash(exported[0]) == SharedWorldRosterTrust.Hash(fixture.Parent) &&
                SharedWorldRosterTrust.Hash(exported[1]) == SharedWorldRosterTrust.Hash(fixture.Projection),
                "a public roster page omitted the exact signed parent of an equivalent legacy projection");
            foreach (var receiver in new[] { "empty", "legacy", "canonical" })
            {
                using var receiverData = new LocalData(Path.Combine(root, "projection-receiver-" + Guid.NewGuid().ToString("N")));
                var chain = new SharedWorldRosterChainStore(receiverData);
                if (receiver == "legacy") chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
                if (receiver == "canonical")
                {
                    chain.Append(fixture.Parent, fixture.Parent.OwnerPublicKey);
                    chain.Append(fixture.Projection, fixture.Projection.OwnerPublicKey);
                }
                ImportPage(chain, fixture.ProfileId, exported);
                Require(chain.Read(fixture.ProfileId).Count == 11 &&
                    chain.Heads(fixture.ProfileId).Single().Signature == fixture.Current.Signature,
                    "exported projection proof failed on a disposable receiving journal: " + receiver);
            }
        }
        Require(fixture.Chain.Read(fixture.ProfileId)[0].Signature == fixture.Legacy.Signature &&
            fixture.Chain.ReadAfter(fixture.ProfileId, SharedWorldRosterTrust.Hash(fixture.Current))!.Count == 0 &&
            fixture.Chain.ReadAfterRevision(fixture.ProfileId, fixture.Current.Revision).Count == 0 &&
            fixture.Chain.ReadAfter(fixture.ProfileId, SharedWorldRosterTrust.Hash(fixture.Projection))![0].Revision == 3 &&
            fixture.Chain.ReadAfterRevision(fixture.ProfileId, 3)[0].Revision == 4,
            "portable projection pages altered local history or replayed an unrelated completed cursor");
    }

    private static void PeerChallengeImportsProjection(string root)
    {
        using var fixture = new Fixture(root);
        var peerRoot = new SharedWorldPeerRoot(fixture.Legacy.OwnerPublicKey, fixture.ProfileId, fixture.Legacy.GroupId);
        SavePeerMembership(fixture.Data, peerRoot, fixture.Legacy, fixture.DeviceId);
        using var friends = new FriendService(fixture.Data);
        using var service = new WorldPeerService(fixture.Data, new HostManager(fixture.Data), friends,
            new PairingService(fixture.Data), (_, _) => throw new InvalidOperationException("Peer challenge opened a real route."));
        var pin = new string('A', 64);
        var request = fixture.OwnerChallenge(peerRoot, pin, [fixture.Parent, fixture.Projection]);
        var response = service.PrepareChallenge(peerRoot.GroupKey, request, pin);
        Require(response.Challenge is not null && response.KnownRosterRevision == 2 &&
            fixture.Chain.Read(fixture.ProfileId).Count == 2 &&
            fixture.Chain.Heads(fixture.ProfileId).Single().Signature == fixture.Projection.Signature,
            "proved fixed peer challenge could not import an equivalent same-counter legacy owner projection");
        AssertProof(fixture.Data, fixture.Projection, fixture.Parent);
    }

    private static void CanonicalPeerExportsProjectionProof(string root)
    {
        using var fixture = new Fixture(root, seedJournal: false);
        foreach (var revision in fixture.History) fixture.Chain.Append(revision, revision.OwnerPublicKey);
        var exported = fixture.Chain.ReadAfterRevision(fixture.ProfileId, fixture.Legacy.Revision);
        Require(exported.Count == 11 && exported[0].Signature == fixture.Parent.Signature &&
            exported[1].Signature == fixture.Projection.Signature,
            "a canonical journal skipped the exact parent and same-counter owner revision needed by a legacy peer");
        using var receiverData = new LocalData(Path.Combine(root, "canonical-projection-peer-" + Guid.NewGuid().ToString("N")));
        var receiverChain = new SharedWorldRosterChainStore(receiverData);
        receiverChain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
        fixture.SaveMemberKeyTo(receiverData);
        var peerRoot = new SharedWorldPeerRoot(fixture.Legacy.OwnerPublicKey, fixture.ProfileId, fixture.Legacy.GroupId);
        SavePeerMembership(fixture.Data, peerRoot, fixture.Current, fixture.DeviceId);
        SavePeerMembership(receiverData, peerRoot, fixture.Legacy, fixture.DeviceId);
        using var sourceFriends = new FriendService(fixture.Data);
        using var receiverFriends = new FriendService(receiverData);
        using var source = new WorldPeerService(fixture.Data, new HostManager(fixture.Data), sourceFriends,
            new PairingService(fixture.Data), (_, _) => throw new InvalidOperationException("Canonical peer opened a real route."));
        using var receiver = new WorldPeerService(receiverData, new HostManager(receiverData), receiverFriends,
            new PairingService(receiverData), (_, _) => throw new InvalidOperationException("Legacy peer opened a real route."));
        var sourcePin = new string('A', 64);
        var emptyOffer = source.PrepareChallenge(peerRoot.GroupKey,
            fixture.OwnerChallenge(peerRoot, sourcePin, [], fixture.Legacy.Revision), sourcePin);
        Require(emptyOffer.RosterRevisions is { Count: 8 } emptyPage &&
            emptyPage[0].Signature == fixture.Parent.Signature && emptyPage[1].Signature == fixture.Projection.Signature,
            "canonical peer omitted projection proof for an empty incoming roster offer");
        Require(fixture.Chain.SupersededLegacySnapshot(fixture.ProfileId, fixture.Legacy),
            "canonical signed history did not recognize its exact equivalent legacy state");
        var selected = source.PrepareChallenge(peerRoot.GroupKey,
            fixture.MemberChallenge(peerRoot, sourcePin, [fixture.Legacy]), sourcePin);
        var selectedPage = selected.RosterRevisions ?? [];
        Require(selected.Challenge is null && selected.KnownRosterRevision == fixture.Current.Revision &&
            selectedPage.Count == 8 && selectedPage[0].Signature == fixture.Parent.Signature &&
            selectedPage[1].Signature == fixture.Projection.Signature,
            "fixed challenge response selection omitted the canonical legacy projection proof");
        var receiverPin = new string('B', 64);
        var imported = receiver.PrepareChallenge(peerRoot.GroupKey,
            fixture.OwnerChallenge(peerRoot, receiverPin, selectedPage), receiverPin);
        Require(imported.Challenge is not null && imported.KnownRosterRevision == selectedPage[^1].Revision &&
            receiverChain.Read(fixture.ProfileId).Count == selectedPage.Count,
            "a legacy peer could not import the canonical peer's selected bounded projection proof");
        AssertProof(receiverData, fixture.Projection, fixture.Parent);
        ImportPage(receiverChain, fixture.ProfileId, exported);
        Require(receiverChain.Heads(fixture.ProfileId).Single().Signature == fixture.Current.Signature,
            "the canonical projection export failed to finish on a legacy receiver");
    }

    private static async Task ProofParentAnchorAcrossPagesAsync(string root)
    {
        using var fixture = new Fixture(root, seedJournal: false);
        for (var revision = 1; revision <= 35; revision++)
            fixture.Chain.Append(fixture.Sign(fixture.Parent with { Epoch = revision, Revision = revision }), fixture.Legacy.OwnerPublicKey);
        var legacy = fixture.Sign(fixture.Legacy with { Epoch = 100, Revision = 100 });
        var parent = fixture.Sign(fixture.Parent with { Epoch = 99, Revision = 99 });
        var projection = fixture.Sign(fixture.Child(parent) with
        {
            Members = legacy.Members,
            OwnerLocalBaselineMembers = legacy.Members
        });
        fixture.Chain.Append(legacy, legacy.OwnerPublicKey);
        Require(fixture.Chain.TryAppendLegacyOwnerProjection(projection, parent, legacy.OwnerPublicKey),
            "could not prepare a long journal with a non-indexed projection parent");
        var page = fixture.Chain.ReadPage(fixture.ProfileId, 24);
        Require(page.Count == 12 && page[^1].Signature == parent.Signature &&
            !fixture.Data.HasProtected(fixture.LookupName(parent)) &&
            fixture.Chain.Read(fixture.ProfileId)[35].Signature == legacy.Signature,
            "long projected wire page did not end at its protected proof-only parent");
        var afterParent = fixture.Chain.ReadAfter(fixture.ProfileId, SharedWorldRosterTrust.Hash(parent));
        Require(afterParent is { Count: 1 } && afterParent[0].Signature == projection.Signature,
            "a proof-only parent anchor could not resume at its exact schema-3 child");
        fixture.Current = projection;
        fixture.ExportChain = fixture.Chain;
        using var receiverData = new LocalData(Path.Combine(root, "fresh-projection-pages-" + Guid.NewGuid().ToString("N")));
        var received = await fixture.CheckFreshReceiverAsync(receiverData);
        var receivedChain = new SharedWorldRosterChainStore(receiverData);
        Require(received.Available && receivedChain.Read(fixture.ProfileId).Count == 37 &&
            receivedChain.Heads(fixture.ProfileId).Single().Signature == projection.Signature &&
            fixture.PageRequests == 4 && fixture.RootFallbacks == 0,
            "fresh fixed Friend catch-up looped on the proof-only parent at its third page boundary");
    }

    private static void SavePeerMembership(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster, Guid deviceId) =>
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = root, Roster = roster, DeviceId = deviceId,
                Name = "Synthetic projected roster", ReceiveConsented = false, SharingEnabled = true,
                Bootstrap = (SharedWorldPeerRoute?)null } }
        }, Json));

    private static void ImportPage(SharedWorldRosterChainStore chain, Guid profileId, IReadOnlyList<SharedWorldRoster> page)
    {
        SharedWorldRoster? parent = null;
        foreach (var revision in page)
        {
            if (revision.Schema == 2) parent = revision;
            if (chain.Contains(profileId, revision) || chain.SupersededLegacySnapshot(profileId, revision)) continue;
            if (parent is null || !chain.TryAppendLegacyOwnerProjection(revision, parent, revision.OwnerPublicKey))
                chain.Append(revision, revision.OwnerPublicKey);
        }
    }

    private static void AssertProof(LocalData data, SharedWorldRoster projection, SharedWorldRoster parent)
    {
        var bytes = data.LoadProtected($"shared-roster-lookup-{projection.ProfileId:N}-{SharedWorldRosterTrust.Hash(projection)}.protected")!;
        using var lookup = JsonDocument.Parse(bytes);
        Require(lookup.RootElement.GetProperty("legacyParent").Deserialize<SharedWorldRoster>(Json) is { } proof &&
            SharedWorldRosterTrust.Hash(proof) == SharedWorldRosterTrust.Hash(parent) &&
            SharedWorldRosterTrust.VerifyRevision(projection, proof, projection.OwnerPublicKey, DateTimeOffset.UtcNow, false),
            "committed legacy projection lost its exact signed parent proof");
    }

    private static Dictionary<string, byte[]> CaptureFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "host.lock").ToDictionary(
            path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertFilesEqual(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after, string message)
    {
        Require(before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var bytes) &&
            item.Value.AsSpan().SequenceEqual(bytes)), message);
    }

    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or CryptographicException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa otherDevice = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly Guid deviceId = Guid.NewGuid();
        private readonly string credential = new('B', 64);
        private FriendLink? link;
        public Guid ProfileId { get; } = Guid.NewGuid();
        public Guid DeviceId => deviceId;
        public string DevicePublicKey => Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
        public LocalData Data { get; }
        public SharedWorldRosterChainStore Chain { get; }
        public SharedWorldRoster Parent { get; }
        public SharedWorldRoster Legacy { get; private set; }
        public SharedWorldRoster Projection { get; }
        public SharedWorldRoster Trusted { get; set; }
        public SharedWorldRoster Current { get; set; }
        public IReadOnlyList<SharedWorldRoster> History { get; set; }
        public SharedWorldRosterChainStore? ExportChain { get; set; }
        public int RootFallbacks { get; private set; }
        public int PageRequests { get; private set; }
        public int UnexpectedRequests { get; private set; }
        public string CacheName => $"shared-world-roster-{deviceId:N}-{ProfileId:N}.protected";
        public string LookupName(SharedWorldRoster roster) =>
            $"shared-roster-lookup-{ProfileId:N}-{SharedWorldRosterTrust.Hash(roster)}.protected";
        public string RevisionPath(SharedWorldRoster roster) => Path.Combine(Data.RootPath,
            "shared-worlds", ProfileId.ToString("N"), "roster-chain", SharedWorldRosterTrust.Hash(roster) + ".json");

        public Fixture(string root, int precedingLegacyCount = 1, int lastRevision = 11,
            bool unsignedLegacyField = false, bool seedJournal = true)
        {
            Data = new LocalData(Path.Combine(root, "legacy-owner-projection-" + Guid.NewGuid().ToString("N")));
            Chain = new(Data);
            Parent = Sign(new(2, Guid.NewGuid(), ProfileId, 1, 1, false,
                Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()), [], ""));
            var history = new List<SharedWorldRoster> { Parent };
            while (history.Count < precedingLegacyCount)
                history.Add(Sign(Parent with { Epoch = history.Count + 1, Revision = history.Count + 1 }));
            Parent = history[^1];
            var members = new SharedWorldRosterMember[]
            {
                new(deviceId, Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()),
                    new(Receive: true, EligibleHost: true, RecoveryVoter: true, ManageSharing: true, Reshare: true), false),
                new(Guid.NewGuid(), Convert.ToBase64String(otherDevice.ExportSubjectPublicKeyInfo()),
                    new(Receive: true, EligibleHost: true, Reshare: true), false,
                    DateTimeOffset.UtcNow.AddDays(10))
            }.OrderBy(item => item.DeviceId).ToArray();
            Legacy = Sign(Parent with { Epoch = Parent.Epoch + 1, Revision = Parent.Revision + 1, Members = members });
            if (unsignedLegacyField) Legacy = Legacy with { SignerDeviceId = Guid.Empty };
            Projection = Sign(Child(Parent) with
            {
                Members = members,
                OwnerLocalBaselineMembers = members
            });
            history.Add(Projection);
            while (history[^1].Revision < lastRevision) history.Add(Child(history[^1]));
            History = history;
            Current = history[^1];
            Trusted = history.Single(item => item.Revision == precedingLegacyCount + 6);
            if (seedJournal) Chain.Append(Legacy, Legacy.OwnerPublicKey);
            Data.SaveProtected(CacheName, JsonSerializer.SerializeToUtf8Bytes(Trusted, Json));
            Data.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", device.ExportPkcs8PrivateKey());
        }

        public SharedWorldRoster Sign(SharedWorldRoster roster) => roster with
        {
            Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256))
        };

        public SharedWorldRoster SignAsDevice(SharedWorldRoster roster)
        {
            var draft = roster with { SignerDeviceId = deviceId, SignerPublicKey = DevicePublicKey };
            return draft with
            {
                Signature = Convert.ToBase64String(device.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
        }

        public SharedWorldRoster OtherOwner(SharedWorldRoster roster)
        {
            using var another = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = Convert.ToBase64String(another.ExportSubjectPublicKeyInfo());
            var draft = roster with { OwnerPublicKey = key, SignerPublicKey = roster.Schema == 3 ? key : null };
            return draft with
            {
                Signature = Convert.ToBase64String(another.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
        }

        public WorldPeerChallengeRequest OwnerChallenge(SharedWorldPeerRoot root, string pin,
            IReadOnlyList<SharedWorldRoster> rosters, long? knownRevision = null) => WorldPeerService.SignChallengeRequest(
                root, Guid.Empty, owner, pin, knownRevision ?? rosters[^1].Revision, rosters);

        public WorldPeerChallengeRequest MemberChallenge(SharedWorldPeerRoot root, string pin,
            IReadOnlyList<SharedWorldRoster> rosters) => WorldPeerService.SignChallengeRequest(
                root, deviceId, device, pin, rosters[^1].Revision, rosters);

        public void SaveMemberKeyTo(LocalData data) =>
            data.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", device.ExportPkcs8PrivateKey());

        public async Task<SharedWorldSharingView> CheckFreshReceiverAsync(LocalData receiverData)
        {
            const string endpoint = "https://127.0.0.1:5131";
            receiverData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DeviceId = deviceId,
                HostId = Guid.NewGuid(),
                Endpoint = endpoint,
                Fingerprint = new string('A', 64),
                Credential = credential,
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
                SharedWorldSigningKeys = new() { [ProfileId] = Legacy.OwnerPublicKey }
            }, Json));
            SaveMemberKeyTo(receiverData);
            using var receiver = new FriendLink(receiverData, "friend.protected", (address, pins) =>
            {
                Require(address == endpoint && pins.Contains(new string('A', 64)), "fresh receiver escaped pinned fake transport");
                return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(address) };
            });
            Require((await receiver.PollAsync()).Profiles.Single().Id == ProfileId, "fresh synthetic profile unavailable");
            return await receiver.CheckSharedWorldSharingAsync(ProfileId);
        }


        public SharedWorldRoster Child(SharedWorldRoster parent) => Sign(parent with
        {
            Schema = 3,
            Epoch = parent.Epoch + 1,
            Revision = parent.Revision + 1,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(parent),
            SignerDeviceId = Guid.Empty,
            SignerPublicKey = parent.OwnerPublicKey,
            Signature = ""
        });

        public async Task<SharedWorldSharingView> CheckAsync()
        {
            if (link is null)
            {
                const string endpoint = "https://127.0.0.1:5131";
                Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
                {
                    DeviceId = deviceId,
                    HostId = Guid.NewGuid(),
                    Endpoint = endpoint,
                    Fingerprint = new string('A', 64),
                    Credential = credential,
                    CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
                    SharedWorldSigningKeys = new() { [ProfileId] = Legacy.OwnerPublicKey },
                    SharedRosterFloors = new()
                    {
                        [ProfileId] = new(Trusted.GroupId, Trusted.Epoch, Trusted.Revision, Trusted.Signature)
                    }
                }, Json));
                link = new FriendLink(Data, "friend.protected", (address, pins) =>
                {
                    Require(address == endpoint && pins.Contains(new string('A', 64)), "escaped pinned fake transport");
                    return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(address) };
                });
                Require((await link.PollAsync()).Profiles.Single().Id == ProfileId, "synthetic profile unavailable");
            }
            return await link.CheckSharedWorldSharingAsync(ProfileId);
        }

        public Task<LocalHostingAccess> CheckHostingAsync() =>
            (link ?? throw new InvalidOperationException("Check synthetic sharing first."))
                .LocalHostingAccessAsync(ProfileId, true, CancellationToken.None, permissionOnly: true);

        public void AssertFloor(SharedWorldRoster expected)
        {
            var saved = JsonSerializer.Deserialize<FriendConfiguration>(Data.LoadProtected("friend.protected")!, Json)!;
            Require(saved.SharedRosterFloors[ProfileId] == new SharedRosterFloor(expected.GroupId,
                expected.Epoch, expected.Revision, expected.Signature) &&
                saved.SharedWorldSigningKeys[ProfileId] == Legacy.OwnerPublicKey,
                "legacy projection changed the connection's exact trust floor or owner");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Require(request.Headers.Authorization?.Parameter == credential &&
                request.Headers.GetValues("X-Device-Id").Single() == deviceId.ToString(), "lost device authentication");
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/companion/heartbeat") body = new CompanionStatus(false, null,
                [new(ProfileId, "Synthetic legacy owner", "Offline", null, Kind: GameKinds.Valheim)], false, false,
                DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                    [CompanionProtocol.SharedWorldsCapability]));
            else if (path.EndsWith("/enrollment", StringComparison.Ordinal))
                body = request.Method == HttpMethod.Get
                    ? new SharedWorldEnrollmentChallenge(new string('N', 44)) : new { ok = true };
            else if (path.EndsWith("/roster", StringComparison.Ordinal) ||
                path.EndsWith("/revisions/current", StringComparison.Ordinal)) body = Current;
            else if (path.EndsWith("/revisions", StringComparison.Ordinal))
            {
                PageRequests++;
                Require(long.TryParse(request.RequestUri.Query.AsSpan("?offset=".Length), out var offset), "invalid offset");
                var anchor = request.Headers.TryGetValues(SharedWorldRosterProtocol.AfterHeader, out var anchors)
                    ? anchors.Single() : null;
                if (ExportChain is { } export)
                {
                    var exported = anchor is null ? export.ReadPage(ProfileId, offset) : export.ReadAfter(ProfileId, anchor);
                    if (exported is not null)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(exported, Json))
                        });
                    RootFallbacks++;
                    var unknown = new HttpResponseMessage(HttpStatusCode.Conflict);
                    unknown.Headers.Add(SharedWorldRosterProtocol.AnchorHeader, anchor!);
                    return Task.FromResult(unknown);
                }
                var position = anchor is null ? -1 : Array.FindIndex(History.ToArray(),
                    item => SharedWorldRosterTrust.Hash(item) == anchor);
                if (anchor is not null && position < 0)
                {
                    RootFallbacks++;
                    var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                    conflict.Headers.Add(SharedWorldRosterProtocol.AnchorHeader, anchor);
                    return Task.FromResult(conflict);
                }
                body = History.Skip(anchor is null ? checked((int)offset) : position + 1)
                    .Take(SharedWorldRosterChainStore.PageSize).ToArray();
            }
            else
            {
                UnexpectedRequests++;
                throw new InvalidOperationException("Legacy permission check requested unrelated synthetic data.");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Json))
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                link?.Dispose();
                Data.Dispose();
                owner.Dispose();
                device.Dispose();
                otherDevice.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
