using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Real FriendLink calls over fake authenticated HTTP; no listener or process.
internal static class SharedRosterBootstrapChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string root)
    {
        await RepairedLegacyCatchUpAsync(root);
        await ExactCurrentHeadAsync(root);
        await InitialGroupReviewAsync(root);
        await ReviewedSchema3FloorAsync(root);
        await FencedHistoryReviewAsync(root);
        await FencedRosterCatchUpAsync(root);
        await ConcurrentLegacyBootstrapAsync(root);
        await ConcurrentAnchoredLegacyBootstrapAsync(root);
        await RecoveredFloorReceivesSaveAsync(root);
        await TransferProgressCancellationAsync(root);
        using (var fixture = new Fixture(root))
        {
            fixture.Current = fixture.Legacy;
            fixture.Page = [fixture.Bootstrap];
            var raced = await fixture.CheckAsync();
            Require(!raced.Available && raced.Code == "RosterCatchUpPending" &&
                !fixture.Chain.HasState(fixture.ProfileId), "roster race committed a mismatched root");
            fixture.AssertFloor(fixture.Legacy);
            fixture.Current = fixture.Bootstrap;
            var retried = await fixture.CheckAsync();
            Require(retried.Available && retried.Revision == fixture.Bootstrap.Revision,
                "matching retry did not recover from a concurrent legacy roster update");
            fixture.AssertFloor(fixture.Bootstrap);
        }

        using (var fixture = new Fixture(root))
        {
            fixture.Seed();
            var recovered = await fixture.CheckAsync();
            Require(recovered.Available && recovered.Revision == fixture.Current.Revision &&
                fixture.Chain.Count(fixture.ProfileId) == 2 && fixture.LastOffset == 2,
                "valid persisted legacy bootstrap did not recover at its committed offset");
            fixture.AssertFloor(fixture.Current);
        }

        foreach (var fault in new[] { "missing-cache", "invalid-cache", "invalid-cache-json", "wrong-cache-profile",
            "wrong-cache-group", "wrong-cache-owner", "schema3-floor", "same-epoch", "same-revision",
            "sibling", "wrong-group", "wrong-owner", "missing-middle", "competing-heads", "missing-lookup" })
        {
            using var fixture = new Fixture(root);
            var expected = "RosterRollback";
            switch (fault)
            {
                case "missing-cache": fixture.Data.DeleteProtected(fixture.CacheName); break;
                case "invalid-cache": fixture.SaveCache(fixture.Legacy with { Signature = new string('A', 88) }); break;
                case "invalid-cache-json":
                    fixture.Data.SaveProtected(fixture.CacheName, [0x7b]);
                    expected = "RosterRejected";
                    break;
                case "wrong-cache-profile": fixture.SaveCache(fixture.Sign(fixture.Legacy with { ProfileId = Guid.NewGuid() })); break;
                case "wrong-cache-group": fixture.SaveCache(fixture.Sign(fixture.Legacy with { GroupId = Guid.NewGuid() })); break;
                case "wrong-cache-owner": fixture.SaveCache(fixture.OtherOwnerRoster(fixture.Legacy)); break;
                case "schema3-floor":
                    fixture.Legacy = fixture.Sign(fixture.Legacy with
                    { Schema = 3, SignerDeviceId = Guid.Empty, SignerPublicKey = fixture.Legacy.OwnerPublicKey });
                    fixture.SaveCache(fixture.Legacy);
                    break;
                case "same-epoch": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { Epoch = fixture.Legacy.Epoch }); break;
                case "same-revision": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { Revision = fixture.Legacy.Revision }); break;
                case "sibling": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { Epoch = 1, Revision = 1, OwnerOverride = true }); break;
                case "wrong-group": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { GroupId = Guid.NewGuid() }); break;
                case "wrong-owner":
                    fixture.Bootstrap = fixture.OtherOwnerRoster(fixture.Bootstrap);
                    expected = "SigningIdentityChanged";
                    break;
                case "missing-middle":
                case "missing-lookup": expected = "RosterRejected"; break;
            }
            fixture.Seed(includeChild: fault != "missing-middle");
            if (fault == "missing-middle")
            {
                fixture.Current = fixture.Child(fixture.Child(fixture.Bootstrap));
                fixture.Page = [fixture.Current];
            }
            if (fault == "competing-heads")
            {
                var sibling = fixture.Sign(fixture.Current with { OwnerOverride = !fixture.Current.OwnerOverride });
                fixture.Chain.Append(sibling, sibling.OwnerPublicKey);
            }
            if (fault == "missing-lookup")
                fixture.Data.DeleteProtected($"shared-roster-lookup-{fixture.ProfileId:N}-{SharedWorldRosterTrust.Hash(fixture.Bootstrap)}.protected");
            var rejected = await fixture.CheckAsync();
            Require(!rejected.Available && rejected.Code == expected,
                $"{fault} crossed the legacy floor: {rejected.Code}, expected {expected}");
            fixture.AssertFloor(fixture.Legacy);
        }

        // Mismatched singleton pages must never poison an empty journal.
        foreach (var fault in new[] { "sibling", "older", "wrong-profile", "wrong-group", "wrong-owner" })
        {
            using var fixture = new Fixture(root);
            fixture.Current = fixture.Bootstrap;
            fixture.Page = [fault switch
            {
                "sibling" => fixture.Sign(fixture.Bootstrap with { OwnerOverride = true }),
                "older" => fixture.Legacy,
                "wrong-profile" => fixture.Sign(fixture.Bootstrap with { ProfileId = Guid.NewGuid() }),
                "wrong-group" => fixture.Sign(fixture.Bootstrap with { GroupId = Guid.NewGuid() }),
                _ => fixture.OtherOwnerRoster(fixture.Bootstrap)
            }];
            var rejected = await fixture.CheckAsync();
            Require(!rejected.Available && rejected.Code == "RosterRejected" &&
                !fixture.Chain.HasState(fixture.ProfileId), $"{fault} singleton changed the journal");
            fixture.AssertFloor(fixture.Legacy);
        }

        using (var fixture = new Fixture(root))
        {
            fixture.Data.SaveProtected(fixture.CacheName, [0x7b]);
            fixture.Current = fixture.Legacy;
            fixture.Page = [fixture.Legacy];
            Require((await fixture.CheckAsync()).Available,
                "an exact signed trusted floor acquired a cache dependency");
        }
    }

    private static async Task InitialGroupReviewAsync(string root)
    {
        using (var fixture = new Fixture(root, new(RecoveryVoter: true)))
        {
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            for (var refresh = 0; refresh < 2; refresh++)
            {
                Require((await fixture.CheckAsync()).Available &&
                    !fixture.SavedConfiguration.ApprovedSharedWorldGroups.ContainsKey(fixture.ProfileId) &&
                    !fixture.ReceiveStatus.Consented,
                    "background metadata refresh approved a voter-only group or granted save consent");
                fixture.AssertFloor(fixture.Legacy);
            }

            var pending = await fixture.ReviewAsync();
            Require(!pending.Ok && pending.Code == "GroupReviewRequired" &&
                pending.GroupId == fixture.Current.GroupId && pending.OwnerPublicKey == fixture.Current.OwnerPublicKey,
                "a refreshed voter-only member lost explicit signed group and owner review: " + pending.Code);
            var wrongGroup = await fixture.ReviewAsync(new(Guid.NewGuid(), pending.OwnerPublicKey));
            var wrongOwner = await fixture.ReviewAsync(new(pending.GroupId, "another owner"));
            Require(!wrongGroup.Ok && wrongGroup.Code == "GroupReviewRequired" &&
                !wrongOwner.Ok && wrongOwner.Code == "GroupReviewRequired" &&
                !fixture.SavedConfiguration.ApprovedSharedWorldGroups.ContainsKey(fixture.ProfileId),
                "wrong voter-only owner or group confirmation approved the signed group");
            var reviewed = await fixture.ReviewAsync(new(pending.GroupId, pending.OwnerPublicKey));
            Require(reviewed is { Ok: true, Code: "HistoryReviewed" } &&
                fixture.SavedConfiguration.ApprovedSharedWorldGroups.GetValueOrDefault(fixture.ProfileId) == fixture.Current.GroupId &&
                !fixture.ReceiveStatus.Consented && fixture.SavedConfiguration.ConsentedSharedWorldProfiles.Count == 0 &&
                fixture.Current.Members.Single().Grants is
                { Receive: false, EligibleHost: false, Reshare: false, RecoveryVoter: true, ManageSharing: false },
                "exact voter-only review changed receiving consent or core signed grants");
        }

        foreach (var grants in new[] { new SharedWorldGrants(Receive: true),
            new SharedWorldGrants(Receive: true, EligibleHost: true, Reshare: true) })
        {
            using var fixture = new Fixture(root, grants);
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            Require((await fixture.CheckAsync()).Available &&
                fixture.SavedConfiguration.ApprovedSharedWorldGroups.GetValueOrDefault(fixture.ProfileId) == fixture.Current.GroupId,
                "an active signed Receive member no longer followed its first verified group automatically");
        }

        foreach (var keepOldJournal in new[] { false, true })
        {
            using var fixture = new Fixture(root);
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            Require((await fixture.CheckAsync()).Available, "initial group check failed");
            if (keepOldJournal) fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Sign(fixture.Legacy with { GroupId = Guid.NewGuid(), Epoch = 2, Revision = 2 });
            fixture.RemoteHistory = [fixture.Current];
            Require((await fixture.ReviewAsync()).Code == "GroupReviewRequired",
                "a changed source group skipped explicit owner and group review");
            var reviewed = await fixture.ReviewAsync(new(fixture.Current.GroupId, fixture.Current.OwnerPublicKey));
            if (keepOldJournal)
            {
                Require(!reviewed.Ok && fixture.Chain.Count(fixture.ProfileId) == 1,
                    "new-group review cleared the earlier group's retained journal");
                fixture.AssertFloor(fixture.Legacy);
            }
            else
            {
                Require(reviewed.Ok && (await fixture.CheckAsync()).Available,
                    "confirmed legacy source rotation without a journal acquired an old-group rollback");
                fixture.AssertFloor(fixture.Current);
            }
        }
    }

    private static async Task ReviewedSchema3FloorAsync(string root)
    {
        using (var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true)))
        {
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            Require((await fixture.CheckAsync()).Available, "initial sharing check failed");
            fixture.Current = fixture.Child(fixture.Legacy);
            fixture.RemoteHistory = [fixture.Legacy, fixture.Current];
            var reviewed = await fixture.ReviewAsync();
            Require(reviewed is { Ok: true, Code: "HistoryReviewed" } &&
                fixture.Chain.Count(fixture.ProfileId) == 2 &&
                fixture.Chain.Contains(fixture.ProfileId, fixture.Current),
                "history review advanced its protected floor without retaining the exact signed revision");
            fixture.AssertFloor(fixture.Current);
            Require((await fixture.CheckAsync()).Available && fixture.PageRequests == 1,
                "a reviewed schema-3 permission became a saved-history rollback on the next hosting check");
            Require((await fixture.CheckHostingAsync()).Allowed,
                "the reviewed schema-3 grant did not permit local hosting access");
        }

        // Older builds could persist the newer floor/cache during history review
        // while leaving the journal behind. Recover its complete exact-parent
        // history, rather than lowering the floor or trusting the cache as a root.
        using (var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true)))
        {
            var middle = fixture.Child(fixture.Legacy);
            var trusted = fixture.Child(middle);
            fixture.Trusted = trusted;
            fixture.SaveCache(trusted);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Child(trusted);
            fixture.RemoteHistory = [fixture.Legacy, middle, trusted, fixture.Current];
            Require((await fixture.CheckAsync()).Available && fixture.Chain.Count(fixture.ProfileId) == 4 &&
                fixture.Chain.ContainsFloor(fixture.ProfileId,
                    new(trusted.GroupId, trusted.Epoch, trusted.Revision, trusted.Signature)),
                "an existing reviewed schema-3 floor could not catch up through its exact signed parents");
            fixture.AssertFloor(fixture.Current);
            Require((await fixture.CheckAsync()).Available && fixture.PageRequests == 1,
                "a recovered sharing check repeated history or lost its trusted floor");
            Require((await fixture.CheckHostingAsync()).Allowed,
                "the recovered schema-3 floor still blocked local hosting access");
        }

        using (var fixture = new Fixture(root))
        {
            var history = new List<SharedWorldRoster> { fixture.Legacy };
            for (var revision = 2; revision <= 52; revision++) history.Add(fixture.Child(history[^1]));
            var trusted = history[49];
            fixture.Trusted = trusted;
            fixture.SaveCache(trusted);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = history[^1];
            fixture.RemoteHistory = history;
            var pending = await fixture.CheckAsync();
            Require(!pending.Available && pending.Code == "RosterCatchUpPending" &&
                fixture.Chain.Count(fixture.ProfileId) == 49,
                "bounded history catch-up skipped ahead of the still-missing reviewed floor");
            fixture.AssertFloor(trusted);
            Require((await fixture.CheckAsync()).Available && fixture.Chain.Count(fixture.ProfileId) == 52 &&
                fixture.Chain.ContainsFloor(fixture.ProfileId,
                    new(trusted.GroupId, trusted.Epoch, trusted.Revision, trusted.Signature)),
                "bounded history catch-up could not resume through the exact reviewed floor");
            fixture.AssertFloor(fixture.Current);
        }

        foreach (var fault in new[] { "missing-parent", "changed-floor", "legacy-jump", "invalid-signature" })
        {
            using var fixture = new Fixture(root);
            var trusted = fixture.Child(fixture.Legacy);
            fixture.Trusted = trusted;
            fixture.SaveCache(trusted);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            var sibling = fixture.Sign(trusted with { OwnerOverride = !trusted.OwnerOverride });
            fixture.Current = fault == "legacy-jump"
                ? fixture.Sign(fixture.Bootstrap with { Epoch = trusted.Epoch + 1, Revision = trusted.Revision + 1 })
                : fixture.Child(fault == "changed-floor" ? sibling : trusted);
            fixture.RemoteHistory = fault switch
            {
                "missing-parent" or "legacy-jump" => [fixture.Legacy, fixture.Current],
                "changed-floor" => [fixture.Legacy, sibling, fixture.Current],
                _ => [fixture.Legacy, trusted with { Signature = new string('A', 88) }, fixture.Current]
            };
            var rejected = await fixture.CheckAsync();
            Require(!rejected.Available && rejected.Code is "RosterRejected" or "RosterRollback" &&
                fixture.Chain.Count(fixture.ProfileId) == 1,
                "schema-3 floor recovery skipped, replaced or corrupted its exact protected revision: " + fault);
            fixture.AssertFloor(trusted);
        }

        using (var fixture = new Fixture(root))
        {
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            Require((await fixture.CheckAsync()).Available, "initial sharing check failed");
            fixture.Current = fixture.Child(fixture.Child(fixture.Legacy));
            fixture.RemoteHistory = [fixture.Legacy, fixture.Current];
            var rejected = await fixture.ReviewAsync();
            Require(!rejected.Ok && fixture.Chain.Count(fixture.ProfileId) == 1,
                "history review skipped an unavailable signed parent");
            fixture.AssertFloor(fixture.Legacy);
        }
    }

    private static async Task FencedHistoryReviewAsync(string root)
    {
        using (var fixture = new Fixture(root, new(RecoveryVoter: true)))
        {
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            fixture.RosterPagesUnavailable = true;
            await fixture.ConnectAsync();
            var reviewed = await fixture.ReviewAsync(new(fixture.Current.GroupId, fixture.Current.OwnerPublicKey));
            Require(reviewed is { Ok: true, Code: "HistoryReviewed" } &&
                fixture.PageRequests == 0 && !fixture.Chain.HasState(fixture.ProfileId) &&
                !fixture.ReceiveStatus.Consented,
                "an unchanged voter-only floor could not review fenced history without roster pages: " + reviewed.Code);
            fixture.AssertFloor(fixture.Legacy);

            fixture.Current = fixture.Sign(fixture.Legacy with { OwnerOverride = !fixture.Legacy.OwnerOverride });
            Require((await fixture.ReviewAsync()).Code == "RosterRejected" && fixture.PageRequests == 0,
                "fenced review accepted another signature at the protected floor");
            fixture.AssertFloor(fixture.Legacy);
            fixture.Current = fixture.Bootstrap;
            var advanced = await fixture.ReviewAsync();
            Require(!advanced.Ok && advanced.Code == "RosterUnavailable" && fixture.PageRequests == 2 &&
                fixture.ReviewPageRequests == 1 &&
                !fixture.Chain.HasState(fixture.ProfileId),
                "fenced review advanced its protected legacy floor without signed history");
            fixture.AssertFloor(fixture.Legacy);
        }

        using (var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true)))
        {
            var middle = fixture.Child(fixture.Legacy);
            fixture.Current = fixture.Child(middle);
            fixture.Trusted = fixture.Current;
            fixture.SaveCache(fixture.Current);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.RemoteHistory = [fixture.Legacy, middle, fixture.Current];
            fixture.RosterPagesUnavailable = true;
            await fixture.ConnectAsync();
            var reviewed = await fixture.ReviewAsync(new(fixture.Current.GroupId, fixture.Current.OwnerPublicKey));
            Require(reviewed is { Ok: true, Code: "HistoryReviewed" } &&
                fixture.PageRequests == 0 && fixture.Chain.Count(fixture.ProfileId) == 1,
                "an unchanged schema-3 floor could not review fenced history: " + reviewed.Code);
            fixture.AssertFloor(fixture.Current);
            var hosting = await fixture.CheckHostingAsync();
            Require(!hosting.Allowed && hosting.Code == "RosterUnavailable" &&
                fixture.PageRequests == 2 && fixture.ReviewPageRequests == 0 &&
                fixture.Chain.Count(fixture.ProfileId) == 1,
                "read-only fenced review bypassed the ordinary hosting journal requirement");
            fixture.AssertFloor(fixture.Current);
        }
    }

    private static async Task FencedRosterCatchUpAsync(string root)
    {
        foreach (var fault in new[] { "none", "unavailable", "invalid-signature", "missing-parent" })
        {
            using var fixture = new Fixture(root, new(RecoveryVoter: true));
            var middle = fixture.Child(fixture.Legacy);
            var trusted = fixture.Child(middle);
            fixture.Trusted = trusted;
            fixture.SaveCache(trusted);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Child(trusted);
            fixture.RemoteHistory = fault switch
            {
                "invalid-signature" => [fixture.Legacy, middle with { Signature = new string('A', 88) }, trusted, fixture.Current],
                "missing-parent" => [fixture.Legacy, fixture.Current],
                _ => [fixture.Legacy, middle, trusted, fixture.Current]
            };
            fixture.RosterPagesUnavailable = true;
            fixture.ReviewPagesAvailable = fault != "unavailable";
            await fixture.ConnectAsync();
            var reviewed = await fixture.ReviewAsync(new(fixture.Current.GroupId, fixture.Current.OwnerPublicKey));
            Require(fixture.ReviewPageRequests == 1 && !fixture.ReceiveStatus.Consented &&
                fixture.SavedConfiguration.ConsentedSharedWorldProfiles.Count == 0,
                "fenced roster catch-up bypassed consent or omitted explicit review metadata: " + fault);
            if (fault == "none")
            {
                Require(reviewed is { Ok: true, Code: "HistoryReviewed" } &&
                    fixture.Chain.Count(fixture.ProfileId) == 4 &&
                    fixture.Chain.ContainsFloor(fixture.ProfileId,
                        new(trusted.GroupId, trusted.Epoch, trusted.Revision, trusted.Signature)),
                    "explicit fenced review did not retain its exact signed floor and parents: " + reviewed.Code);
                fixture.AssertFloor(fixture.Current);
            }
            else
            {
                Require(!reviewed.Ok && fixture.Chain.Count(fixture.ProfileId) == 1,
                    "unavailable, damaged or gapped review metadata advanced signed history: " + fault);
                fixture.AssertFloor(trusted);
            }
        }

        // An old Host's echoed unknown anchor must still fall back to its
        // ordinary root page, without requiring support for the new query.
        using (var fixture = new Fixture(root, new(RecoveryVoter: true)))
        {
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Child(fixture.Bootstrap);
            fixture.RemoteHistory = [fixture.Bootstrap, fixture.Current];
            await fixture.ConnectAsync();
            var reviewed = await fixture.ReviewAsync(new(fixture.Current.GroupId, fixture.Current.OwnerPublicKey));
            Require(reviewed.Ok && fixture.RootFallbacks == 1 && fixture.ReviewPageRequests == 0,
                "history review lost the older Host's ordinary unknown-anchor fallback: " + reviewed.Code);
            fixture.AssertFloor(fixture.Current);
        }

        using (var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true)))
        {
            var trusted = fixture.Child(fixture.Legacy);
            fixture.Trusted = trusted;
            fixture.SaveCache(trusted);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Child(trusted);
            fixture.RemoteHistory = [fixture.Legacy, trusted, fixture.Current];
            fixture.RosterPagesUnavailable = fixture.ReviewPagesAvailable = true;
            await fixture.ConnectAsync();
            Require(!(await fixture.CheckHostingAsync()).Allowed && fixture.ReviewPageRequests == 0,
                "ordinary hosting requested fenced review metadata");
            Require((await fixture.SetConsentAsync(true)).Ok &&
                !(await fixture.PullAsync()).Ok && fixture.ReviewPageRequests == 0 &&
                fixture.ManifestRequests == 0 && fixture.ChunkRequests == 0 && fixture.ReceiptRequests == 0,
                "ordinary receiving used review metadata or bypassed the Host's transfer fence");
            fixture.AssertFloor(trusted);
        }
    }

    private static async Task ConcurrentLegacyBootstrapAsync(string root)
    {
        foreach (var fault in new[] { "none", "sibling", "same-epoch", "same-revision", "schema3", "bad-signature" })
        {
            using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
            fixture.Trusted = fixture.Bootstrap;
            fixture.SaveCache(fixture.Bootstrap);
            var nextLegacy = fixture.Sign(fixture.Bootstrap with { Epoch = 3, Revision = 3 });
            fixture.Current = fixture.Child(nextLegacy);
            var older = fault switch
            {
                "sibling" => fixture.Sign(fixture.Bootstrap with { OwnerOverride = !fixture.Bootstrap.OwnerOverride }),
                "same-epoch" => fixture.Sign(fixture.Legacy with { Epoch = fixture.Bootstrap.Epoch }),
                "same-revision" => fixture.Sign(fixture.Legacy with { Revision = fixture.Bootstrap.Revision }),
                "schema3" => fixture.Sign(fixture.Legacy with
                {
                    Schema = 3,
                    SignerDeviceId = Guid.Empty,
                    SignerPublicKey = fixture.Legacy.OwnerPublicKey
                }),
                "bad-signature" => fixture.Legacy with { Signature = new string('A', 88) },
                _ => fixture.Legacy
            };
            fixture.RemoteHistory = [older, nextLegacy, fixture.Current];
            fixture.BeforeRootPage = () =>
            {
                // Peer catch-up shares this protected journal and can finish
                // while the companion's root-fallback HTTP request is in flight.
                Require(fixture.Chain.Count(fixture.ProfileId) == 1 &&
                    fixture.Chain.Contains(fixture.ProfileId, fixture.Bootstrap),
                    "bootstrap did not retain its exact cached legacy floor before the request");
                fixture.Chain.Append(nextLegacy, nextLegacy.OwnerPublicKey);
                fixture.Chain.Append(fixture.Current, fixture.Current.OwnerPublicKey);
            };
            var checkedSharing = await fixture.CheckAsync();
            Require(fixture.RootFallbacks == 1 && fixture.PageRequests == 2 &&
                fixture.Chain.Count(fixture.ProfileId) == 3 &&
                fixture.Chain.Contains(fixture.ProfileId, fixture.Bootstrap),
                "concurrent peer catch-up replaced the protected bootstrap or repeated root requests");
            if (fault == "none")
            {
                Require(checkedSharing.Available &&
                    checkedSharing.Revision == fixture.Current.Revision &&
                    (await fixture.CheckHostingAsync()).Allowed && fixture.PageRequests == 2,
                    "a valid legacy root became a verification failure after concurrent peer catch-up: " +
                        checkedSharing.Code);
                fixture.AssertFloor(fixture.Current);
            }
            else
            {
                Require(!checkedSharing.Available && checkedSharing.Code == "RosterRejected",
                    "concurrent peer catch-up skipped a forked or unverified legacy snapshot: " + fault);
                fixture.AssertFloor(fixture.Bootstrap);
            }
            Require(fixture.ManifestRequests == 0 && fixture.ChunkRequests == 0 && fixture.ReceiptRequests == 0,
                "roster bootstrap alone downloaded or confirmed a save");
        }
    }

    private static async Task ConcurrentAnchoredLegacyBootstrapAsync(string root)
    {
        foreach (var fault in new[] { "none", "sibling", "same-epoch", "same-revision", "schema3", "schema3-gap", "bad-signature" })
        {
            using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            var nextLegacy = fixture.Sign(fixture.Bootstrap with { Epoch = 3, Revision = 3 });
            var peerHead = fixture.Child(nextLegacy);
            fixture.Current = fault == "schema3-gap" ? fixture.Child(fixture.Child(peerHead)) : peerHead;
            var receivedLegacy = fault switch
            {
                "sibling" => fixture.Sign(nextLegacy with { OwnerOverride = !nextLegacy.OwnerOverride }),
                "same-epoch" => fixture.Sign(fixture.Bootstrap with { Epoch = nextLegacy.Epoch }),
                "same-revision" => fixture.Sign(fixture.Bootstrap with { Revision = nextLegacy.Revision }),
                "schema3" => fixture.Child(fixture.Legacy),
                "bad-signature" => fixture.Bootstrap with { Signature = new string('A', 88) },
                _ => fixture.Bootstrap
            };
            fixture.RemoteHistory = [fixture.Legacy, receivedLegacy, nextLegacy, fixture.Current];
            var cachedFloor = fixture.Data.LoadProtected(fixture.CacheName)!;
            byte[]? peerFloor = null;
            var imported = 0;
            fixture.BeforeAnchoredPage = () =>
            {
                Require(fixture.LastAnchor == SharedWorldRosterTrust.Hash(fixture.Legacy) &&
                    fixture.Chain.Count(fixture.ProfileId) == 1,
                    "the anchored race did not begin from the exact retained owner snapshot");
                // An approved peer can have a valid journal beginning at L3.
                // It imports L3/S4 while this request still carries anchor L1;
                // the owner's response also contains the intervening L2.
                fixture.Chain.Append(nextLegacy, nextLegacy.OwnerPublicKey);
                fixture.Chain.Append(peerHead, peerHead.OwnerPublicKey);
                peerFloor = fixture.Data.LoadProtected($"shared-roster-chain-{fixture.ProfileId:N}.protected")!;
                imported++;
            };
            var sharing = await fixture.CheckAsync();
            Require(imported == 1 && fixture.RootFallbacks == 0 && fixture.PageRequests == 1 &&
                fixture.LastAnchor == SharedWorldRosterTrust.Hash(fixture.Legacy) && fixture.LastOffset == 1 &&
                fixture.Chain.Count(fixture.ProfileId) == 3 &&
                fixture.Chain.Contains(fixture.ProfileId, fixture.Legacy) &&
                fixture.Chain.Heads(fixture.ProfileId).Single().Signature == peerHead.Signature &&
                peerFloor is not null && fixture.Data.LoadProtected($"shared-roster-chain-{fixture.ProfileId:N}.protected")!
                    .AsSpan().SequenceEqual(peerFloor),
                "anchored history import changed the peer's complete signed prefix, repeated paging or used root fallback");
            if (fault == "none")
            {
                Require(sharing.Available && sharing.Revision == fixture.Current.Revision &&
                    (await fixture.CheckHostingAsync()).Allowed && fixture.PageRequests == 1,
                    "a valid anchored legacy page became a verification failure after peer catch-up: " +
                    sharing.Code + "; " + sharing.Message);
                fixture.AssertFloor(fixture.Current);
            }
            else
            {
                Require(!sharing.Available && sharing.Code == "RosterRejected",
                    "anchored peer catch-up skipped a fork, unverified snapshot or schema-3 gap: " + fault);
                fixture.AssertFloor(fixture.Legacy);
                Require(fixture.Data.LoadProtected(fixture.CacheName)!.AsSpan().SequenceEqual(cachedFloor),
                    "rejected anchored metadata replaced the exact cached signed floor: " + fault);
            }
            Require(fixture.ManifestRequests == 0 && fixture.ChunkRequests == 0 && fixture.ReceiptRequests == 0 &&
                fixture.AuthorityRequests == 0 && fixture.ConfirmedReceipt is null &&
                !Directory.Exists(fixture.ReceiveRoot) &&
                !new WorldAuthorityStore(fixture.Data).HasState(fixture.ProfileId) &&
                !fixture.Data.HasProtected(WorldPeerReceiveConsentStore.StateName) &&
                fixture.Data.LoadSettings().Profiles.Count == 0 && !fixture.Data.LoadSettings().CompanionListeningEnabled,
                "an anchored permission check changed save, receipt, authority, consent or hosting state");
        }
    }

    private static async Task TransferProgressCancellationAsync(string root)
    {
        using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
        fixture.Current = fixture.Child(fixture.Bootstrap);
        fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, fixture.Current];
        fixture.PrepareFirstSave();
        await fixture.ConnectAsync();
        Require((await fixture.SetConsentAsync(true)).Ok, "synthetic receiving consent was not saved");
        using var cancellation = new CancellationTokenSource();
        var progress = new List<(long Received, long Total)>();
        var canceled = false;
        try
        {
            await fixture.PullAsync(cancellation.Token, (received, total) =>
            {
                progress.Add((received, total));
                // The read callback now reports partial decoded bytes before
                // a complete chunk exists. Cancel after its committed repeat.
                if (received == SharedWorldService.ChunkBytes && progress.Count(item => item.Received == received) == 2)
                    cancellation.Cancel();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { canceled = true; }
        var totalBytes = fixture.PublishedPayload.LongLength;
        Require(canceled && progress[0] == (0L, totalBytes) &&
            progress.Any(item => item.Received > 0 && item.Received < SharedWorldService.ChunkBytes) &&
            progress[^1] == ((long)SharedWorldService.ChunkBytes, totalBytes) &&
            fixture.ChunkRequests == 1 && fixture.ReceiptRequests == 0 &&
            FriendLink.ReadReceivedLatest(fixture.ReceiveRoot) is null,
            "byte progress ignored cancellation or promoted an incomplete save");
        progress.Clear();
        var resumed = await fixture.PullAsync(reportProgress: (received, total) => progress.Add((received, total)));
        Require(resumed is { Ok: true, Code: "SaveReceived", Status.ReceiptConfirmed: true } &&
            progress[0] == ((long)SharedWorldService.ChunkBytes, totalBytes) && progress[^1] == (totalBytes, totalBytes) &&
            progress.All(item => item.Received >= SharedWorldService.ChunkBytes && item.Total == totalBytes) &&
            fixture.ChunkRequests == 2 && fixture.ReceiptRequests == 1 &&
            FriendLink.ReadReceivedLatest(fixture.ReceiveRoot)?.VersionHash == fixture.PublishedVersion!.VersionHash,
            "resumed byte progress lost its partial baseline or bypassed complete verification and receipt");
    }

    internal static async Task RunSlowReceiveProgressAsync(string root)
    {
        await BusyAutomaticConsentObservationAsync(root);
        foreach (var gzip in new[] { false, true })
        {
            using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
            fixture.Current = fixture.Child(fixture.Bootstrap);
            fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, fixture.Current];
            fixture.PrepareFirstSave(randomHalf: gzip);
            await fixture.ConnectAsync();
            Require((await fixture.SetConsentAsync(true)).Ok, "The slow transfer did not obtain synthetic consent.");
            SlowSaveTransferChecks.SlowBody? slow = null;
            fixture.ChunkContent = bytes =>
            {
                if (!gzip) return new StreamContent(slow = new(bytes));
                var compressed = SharedSaveCompression.Compress(bytes) ?? throw new InvalidOperationException("Synthetic gzip payload did not compress.");
                // Same decoded stream delivered by HttpClient's negotiated
                // GZip handler, over deliberately paused compressed transport.
                return new StreamContent(new System.IO.Compression.GZipStream(slow = new(compressed),
                    System.IO.Compression.CompressionMode.Decompress));
            };
            var receive = fixture.PullAsync();
            for (var index = 0; slow is null && index < 100; index++) await Task.Delay(10);
            Require(slow is not null, "The original-Host receive did not reach its actual response body.");
            await slow!.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var measured = fixture.ReceiveStatus;
            Require(!receive.IsCompleted && measured is { ReceivedBytes: > 0, TransferPhase: "Receiving" } &&
                measured.ReceivedBytes < measured.TotalBytes && measured.TotalBytes == fixture.PublishedPayload.LongLength,
                "Original-Host progress waited for a whole chunk or mixed gzip wire bytes with signed payload totals.");
            var job = BackgroundJobs.For(fixture.Data).Read().Jobs.Single(item => item.Operation == "Save download");
            Require(job.ReceivedBytes == measured.ReceivedBytes && job.TotalBytes == measured.TotalBytes,
                "Original-Host streaming bytes did not reach the application job projection.");
            fixture.ChunkContent = bytes => new StreamContent(new SlowSaveTransferChecks.SlowBody(bytes, pause: false));
            slow.Release.TrySetResult();
            Require((await receive.WaitAsync(TimeSpan.FromSeconds(20))) is { Ok: true, Status.ReceiptConfirmed: true },
                "The deliberately slow original-Host transfer failed full verification/receipt.");
        }
        await CorruptCompletedPartialProgressAsync(root);
    }

    private static async Task BusyAutomaticConsentObservationAsync(string root)
    {
        using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
        fixture.Current = fixture.Child(fixture.Bootstrap);
        fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, fixture.Current];
        await fixture.ConnectAsync();
        Require((await fixture.CheckAsync()).Available, "The automatic-consent fixture did not verify its signed roster.");
        var link = fixture.Connection;
        var memberships = await link.PeerMembershipsAsync(false, CancellationToken.None);
        Require(memberships is [{ ReceiveConsented: true }] && fixture.SavedConfiguration.ConsentedSharedWorldProfiles.Count == 0,
            "The fixture did not exercise automatic receiving without an explicit storage choice.");
        link.ObserveSharedCatchUpScheduling(link.View()); // Material observation invalidates the old page cache.
        var gate = (SemaphoreSlim)typeof(FriendLink).GetField("gate", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!.GetValue(link)!;
        await gate.WaitAsync();
        try
        {
            var pending = await Task.Run(() => fixture.ReceiveStatus).WaitAsync(TimeSpan.FromSeconds(5));
            Require(pending is { Consented: true, HostVersion: null, ThisPcVersion: null },
                "A busy link turned verified automatic receiving into false consent-off or invented a received save.");
            var membership = memberships.Single();
            var store = new WorldPeerReceiveConsentStore(fixture.Data);
            Require(store.Set(membership.Root, membership.DeviceId, false) && !fixture.ReceiveStatus.Consented,
                "Cached automatic receiving overrode a later canonical withdrawal.");
            Require(store.Set(membership.Root, membership.DeviceId, true) && fixture.ReceiveStatus.Consented,
                "A later canonical allow was lost while the link gate was busy.");
        }
        finally { gate.Release(); }
    }

    private static async Task CorruptCompletedPartialProgressAsync(string root)
    {
        using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
        fixture.Current = fixture.Child(fixture.Bootstrap);
        fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, fixture.Current];
        fixture.PrepareFirstSave(extraCompletedFile: true);
        await fixture.ConnectAsync();
        Require((await fixture.SetConsentAsync(true)).Ok, "The corrupt staged retry did not obtain synthetic consent.");
        var stage = Path.Combine(fixture.ReceiveRoot, ".partial-" + fixture.PublishedVersion!.VersionHash, SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(stage);
        var changed = Path.Combine(stage, "synthetic.db");
        File.WriteAllBytes(changed, fixture.PublishedPayload);
        File.WriteAllBytes(Path.Combine(stage, "synthetic.fwl"), fixture.ExtraPayload);
        fixture.ChunkContent = bytes => new StreamContent(new SlowSaveTransferChecks.SlowBody(bytes, pause: false));
        var tampered = false;
        var samples = new List<long>();
        var total = fixture.PublishedPayload.LongLength + fixture.ExtraPayload.LongLength;
        var received = await fixture.PullAsync(reportProgress: (bytes, declared) =>
        {
            Require(declared == total && bytes >= 0 && bytes <= declared,
                "Retransmitting a changed completed staged file exceeded the signed logical total.");
            samples.Add(bytes);
            if (!tampered && bytes == total)
            {
                tampered = true;
                File.WriteAllBytes(changed, fixture.PublishedPayload.Select(value => (byte)(value ^ 0x5a)).ToArray());
            }
        });
        Require(tampered && samples[0] == total && samples.Contains(fixture.ExtraPayload.LongLength) && samples[^1] == total &&
            received is { Ok: true, Status.ReceiptConfirmed: true } && fixture.ChunkRequests == 2 &&
            FriendLink.ReadReceivedLatest(fixture.ReceiveRoot)?.VersionHash == fixture.PublishedVersion.VersionHash,
            "The slow multi-file retry lost the intact completed file's baseline or skipped complete verification/receipt.");
    }

    private static async Task RecoveredFloorReceivesSaveAsync(string root)
    {
        using var fixture = new Fixture(root, new(Receive: true, EligibleHost: true, Reshare: true));
        var middle = fixture.Child(fixture.Legacy);
        var trusted = fixture.Child(middle);
        fixture.Trusted = trusted;
        fixture.SaveCache(trusted);
        fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
        fixture.Current = fixture.Child(trusted);
        fixture.RemoteHistory = [fixture.Legacy, middle, trusted, fixture.Current];
        fixture.PrepareFirstSave();
        await fixture.ConnectAsync();

        Require((await fixture.PullAsync()).Code == "ConsentRequired" &&
            fixture.ManifestRequests == 0 && fixture.ChunkRequests == 0 && fixture.ReceiptRequests == 0 &&
            fixture.Chain.Count(fixture.ProfileId) == 1,
            "floor recovery bypassed receiving consent or downloaded before consent");
        fixture.AssertFloor(trusted);
        Require((await fixture.SetConsentAsync(true)).Ok, "synthetic receiving consent was not saved");

        // Exercise the same scheduled receive path used by an existing paired PC.
        // Its first trust check must repair the journal before requesting bytes.
        await fixture.ScheduledPullAsync();
        var status = fixture.ReceiveStatus;
        var version = fixture.PublishedVersion!;
        var receipt = fixture.ConfirmedReceipt;
        Require(status is { Consented: true, ThisPcVersion: 1, ReceiptConfirmed: true } &&
            fixture.Chain.Count(fixture.ProfileId) == 4 && fixture.PageRequests == 1 &&
            fixture.ManifestRequests == 2 && fixture.ChunkRequests == 2 && fixture.ReceiptRequests == 1 &&
            receipt is not null && receipt.VersionHash == version.VersionHash &&
            FriendLink.ReadReceivedLatest(fixture.ReceiveRoot)?.VersionHash == version.VersionHash &&
            File.ReadAllBytes(Path.Combine(fixture.ReceiveRoot, version.VersionHash,
                SharedWorldService.PayloadDirectory, "synthetic.db")).SequenceEqual(fixture.PublishedPayload),
            "scheduled recovery did not verify the complete first save and confirm its signed receipt: " +
                status.State + "; " + status.Error);
        fixture.AssertFloor(fixture.Current);

        var retry = await fixture.PullAsync();
        Require(retry is { Ok: true, Code: "AlreadyReceived", Status.ReceiptConfirmed: true } &&
            fixture.ChunkRequests == 2 && fixture.ReceiptRequests == 2 && fixture.ConfirmedReceipt == receipt,
            "a repaired receive retry redownloaded bytes or replaced the confirmed signed receipt");
        Require((await fixture.SetConsentAsync(false)).Ok &&
            (await fixture.PullAsync()).Code == "ConsentRequired" &&
            fixture.ChunkRequests == 2 && fixture.ReceiptRequests == 2,
            "receipt confirmation bypassed a later receiving withdrawal");
    }

    private static async Task ExactCurrentHeadAsync(string root)
    {
        using var fixture = new Fixture(root);
        fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with
        {
            Members = fixture.Bootstrap.Members.Select(member => member with
            { Grants = new(Receive: true, EligibleHost: true, ManageSharing: true) }).ToArray()
        });
        fixture.Current = fixture.Bootstrap;
        fixture.Trusted = fixture.Bootstrap;
        fixture.SaveCache(fixture.Bootstrap);
        fixture.Chain.Append(fixture.Bootstrap, fixture.Bootstrap.OwnerPublicKey);
        fixture.RemoteHistory = [fixture.Bootstrap];
        var current = await fixture.CheckAsync();
        Require(current.Available && current.CanManage && fixture.CurrentRequests == 1 && fixture.PageRequests == 0,
            "an exact verified current head fetched an empty history page");
        Require((await fixture.CheckAsync()).CanManage && fixture.CurrentRequests == 2 && fixture.PageRequests == 0,
            "an unchanged check skipped the fresh signed current response or requested empty history");

        fixture.Current = fixture.Sign(fixture.Child(fixture.Bootstrap) with
        {
            Members = fixture.Bootstrap.Members.Select(member => member with { Grants = new(Receive: true) }).ToArray()
        });
        fixture.RemoteHistory = [fixture.Bootstrap, fixture.Current];
        var withdrawn = await fixture.CheckHostingAsync();
        Require(!withdrawn.Allowed && withdrawn.Code == "HostingPermissionRequired" &&
            fixture.CurrentRequests == 3 && fixture.PageRequests == 1 && fixture.Chain.Count(fixture.ProfileId) == 2,
            "a fresh signed withdrawal reused the older head or skipped normal history catch-up");
        fixture.AssertFloor(fixture.Current);
        Require(!(await fixture.CheckHostingAsync()).Allowed && fixture.CurrentRequests == 4 && fixture.PageRequests == 1,
            "an exact withdrawn head restored hosting or repeated empty history");
    }

    private static async Task RepairedLegacyCatchUpAsync(string root)
    {
        using (var fixture = new Fixture(root))
        {
            fixture.Trusted = fixture.Bootstrap;
            fixture.SaveCache(fixture.Bootstrap);
            fixture.Current = fixture.Sign(fixture.Bootstrap with { Epoch = 3, Revision = 3 });
            fixture.RemoteHistory = [fixture.Legacy, fixture.Current];
            Require((await fixture.CheckAsync()).Available && fixture.Chain.Count(fixture.ProfileId) == 2 &&
                fixture.Chain.ContainsFloor(fixture.ProfileId, new(fixture.Bootstrap.GroupId,
                    fixture.Bootstrap.Epoch, fixture.Bootstrap.Revision, fixture.Bootstrap.Signature)),
                "a retained legacy journal omitted the exact cached pre-journal floor");
        }
        using (var fixture = new Fixture(root))
        {
            fixture.Trusted = fixture.Bootstrap;
            fixture.SaveCache(fixture.Bootstrap);
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Child(fixture.Bootstrap);
            fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, fixture.Current];
            fixture.PageFailures = 1;
            var interrupted = await fixture.CheckAsync();
            Require(!interrupted.Available && interrupted.Code == "RosterUnavailable" &&
                fixture.Chain.Count(fixture.ProfileId) == 2,
                "a lost page response did not preserve the exact newer legacy floor in the older journal");
            fixture.AssertFloor(fixture.Bootstrap);
            var recovered = await fixture.CheckAsync();
            Require(recovered.Available && recovered.Revision == fixture.Current.Revision &&
                fixture.Chain.Count(fixture.ProfileId) == 3 &&
                fixture.LastAnchor == SharedWorldRosterTrust.Hash(fixture.Bootstrap),
                "a retry could not resume repaired legacy history from its protected signed anchor");
            fixture.AssertFloor(fixture.Current);
            Require((await fixture.CheckAsync()).Available && fixture.Chain.Count(fixture.ProfileId) == 3,
                "an idempotent retry duplicated repaired roster history");
        }
        using (var fixture = new Fixture(root))
        {
            fixture.Trusted = fixture.Bootstrap;
            fixture.SaveCache(fixture.Bootstrap);
            fixture.Chain.Append(fixture.Bootstrap, fixture.Bootstrap.OwnerPublicKey);
            fixture.Current = fixture.Child(fixture.Bootstrap);
            fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, fixture.Current];
            Require((await fixture.CheckAsync()).Available && fixture.LastOffset == 1 &&
                fixture.LastAnchor == SharedWorldRosterTrust.Hash(fixture.Bootstrap) &&
                fixture.Chain.Count(fixture.ProfileId) == 2,
                "different legacy root positions were treated as equal remote offsets");
        }
        using (var fixture = new Fixture(root))
        {
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            fixture.Current = fixture.Child(fixture.Bootstrap);
            fixture.RemoteHistory = [fixture.Bootstrap, fixture.Current];
            Require((await fixture.CheckAsync()).Available && fixture.RootFallbacks == 1 &&
                fixture.Chain.Count(fixture.ProfileId) == 3 &&
                fixture.Chain.ContainsFloor(fixture.ProfileId, new(fixture.Legacy.GroupId,
                    fixture.Legacy.Epoch, fixture.Legacy.Revision, fixture.Legacy.Signature)),
                "an unknown remote legacy anchor could not catch up without discarding the saved floor");
        }
        foreach (var fault in new[] { "none", "no-journal", "bad-signature", "wrong-group", "wrong-profile", "wrong-owner", "schema3", "equal-epoch", "equal-revision", "sibling" })
        {
            using var fixture = new Fixture(root);
            fixture.Trusted = fixture.Bootstrap;
            fixture.SaveCache(fixture.Bootstrap);
            if (fault != "no-journal") fixture.Chain.Append(fixture.Bootstrap, fixture.Bootstrap.OwnerPublicKey);
            var nextLegacy = fixture.Sign(fixture.Bootstrap with { Epoch = 3, Revision = 3 });
            fixture.Current = fixture.Child(nextLegacy);
            var older = fault switch
            {
                "bad-signature" => fixture.Legacy with { Signature = new string('A', 88) },
                "wrong-group" => fixture.Sign(fixture.Legacy with { GroupId = Guid.NewGuid() }),
                "wrong-profile" => fixture.Sign(fixture.Legacy with { ProfileId = Guid.NewGuid() }),
                "wrong-owner" => fixture.OtherOwnerRoster(fixture.Legacy),
                "schema3" => fixture.Sign(fixture.Legacy with
                {
                    Schema = 3,
                    SignerDeviceId = Guid.Empty,
                    SignerPublicKey = fixture.Legacy.OwnerPublicKey
                }),
                "equal-epoch" => fixture.Sign(fixture.Legacy with { Epoch = fixture.Bootstrap.Epoch }),
                "equal-revision" => fixture.Sign(fixture.Legacy with { Revision = fixture.Bootstrap.Revision }),
                "sibling" => fixture.Sign(fixture.Bootstrap with { OwnerOverride = !fixture.Bootstrap.OwnerOverride }),
                _ => fixture.Legacy
            };
            fixture.RemoteHistory = [older, nextLegacy, fixture.Current];
            var result = await fixture.CheckAsync();
            Require(fixture.RootFallbacks == 1 && (fault is "none" or "no-journal"
                ? result.Available && fixture.Chain.Count(fixture.ProfileId) == 3 &&
                    fixture.Chain.ContainsFloor(fixture.ProfileId, new(fixture.Bootstrap.GroupId,
                        fixture.Bootstrap.Epoch, fixture.Bootstrap.Revision, fixture.Bootstrap.Signature))
                : !result.Available && result.Code == "RosterRejected" && fixture.Chain.Count(fixture.ProfileId) == 1),
                "legacy root fallback skipped an unverified/forked revision or lost its exact floor: " + fault);
            if (fault is not ("none" or "no-journal")) fixture.AssertFloor(fixture.Bootstrap);
        }
        using (var fixture = new Fixture(root))
        {
            fixture.Chain.Append(fixture.Legacy, fixture.Legacy.OwnerPublicKey);
            var child = fixture.Child(fixture.Bootstrap);
            fixture.Current = fixture.Bootstrap;
            fixture.RemoteHistory = [fixture.Legacy, fixture.Bootstrap, child];
            var raced = await fixture.CheckAsync();
            Require(!raced.Available && raced.Code == "RosterCatchUpPending" &&
                fixture.Chain.Count(fixture.ProfileId) == 3,
                "a valid signed change during current/page reads became a rollback or permission denial");
            fixture.AssertFloor(fixture.Legacy);
            fixture.Current = child;
            Require((await fixture.CheckAsync()).Available && fixture.Chain.Count(fixture.ProfileId) == 3,
                "the next matching current response could not resume after a signed page race");
        }
        using (var fixture = new Fixture(root))
        {
            fixture.Trusted = fixture.Bootstrap;
            fixture.SaveCache(fixture.Bootstrap);
            fixture.Chain.Append(fixture.Bootstrap, fixture.Bootstrap.OwnerPublicKey);
            fixture.Current = fixture.Legacy;
            fixture.RemoteHistory = [fixture.Legacy];
            var rolledBack = await fixture.CheckAsync();
            Require(!rolledBack.Available && rolledBack.Code == "RosterRollback" &&
                fixture.Chain.Count(fixture.ProfileId) == 1 && fixture.LastOffset == -1,
                "a genuine Host rollback crossed the newer exact saved floor");
            fixture.AssertFloor(fixture.Bootstrap);
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly Guid deviceId = Guid.NewGuid();
        private readonly string credential = new('B', 64);
        private FriendLink? link;
        public Guid ProfileId { get; } = Guid.NewGuid();
        public LocalData Data { get; }
        public SharedWorldRosterChainStore Chain { get; }
        public SharedWorldRoster Legacy { get; set; }
        public SharedWorldRoster Bootstrap { get; set; }
        public SharedWorldRoster Current { get; set; }
        public SharedWorldRoster? Trusted { get; set; }
        public IReadOnlyList<SharedWorldRoster> Page { get; set; } = [];
        public IReadOnlyList<SharedWorldRoster>? RemoteHistory { get; set; }
        public bool RosterPagesUnavailable { get; set; }
        public bool ReviewPagesAvailable { get; set; }
        public Action? BeforeRootPage { get; set; }
        public Action? BeforeAnchoredPage { get; set; }
        public int PageFailures { get; set; }
        public int CurrentRequests { get; private set; }
        public int PageRequests { get; private set; }
        public int ReviewPageRequests { get; private set; }
        public int RootFallbacks { get; private set; }
        public int ManifestRequests { get; private set; }
        public int ChunkRequests { get; private set; }
        public int ReceiptRequests { get; private set; }
        public int AuthorityRequests { get; private set; }
        public SharedWorldVersion? PublishedVersion { get; private set; }
        public byte[] PublishedPayload { get; private set; } = [];
        public byte[] ExtraPayload { get; private set; } = [];
        public SharedWorldReceipt? ConfirmedReceipt { get; private set; }
        public Func<byte[], HttpContent>? ChunkContent { get; set; }
        public string? LastAnchor { get; private set; }
        public long LastOffset { get; private set; } = -1;
        public string CacheName => $"shared-world-roster-{deviceId:N}-{ProfileId:N}.protected";
        public string ReceiveRoot => Path.Combine(Data.RootPath, "received-shared-worlds",
            deviceId.ToString("N"), ProfileId.ToString("N"));
        public FriendConfiguration SavedConfiguration => JsonSerializer.Deserialize<FriendConfiguration>(
            Data.LoadProtected("friend.protected")!, Json)!;
        public ReceivedSharedWorldStatus ReceiveStatus =>
            (link ?? throw new InvalidOperationException("Check the synthetic connection first.")).SharedWorldStatus(ProfileId);
        public FriendLink Connection => link ?? throw new InvalidOperationException("Connect the synthetic PC first.");

        public Fixture(string root, SharedWorldGrants? grants = null)
        {
            Data = new LocalData(Path.Combine(root, "roster-bootstrap-" + Guid.NewGuid().ToString("N")));
            Chain = new(Data);
            Legacy = Sign(new(2, Guid.NewGuid(), ProfileId, 1, 1, false,
                Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()),
                [new(deviceId, Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), grants ?? new(Receive: true), false)], ""));
            Bootstrap = Sign(Legacy with { Epoch = 2, Revision = 2 });
            Current = Bootstrap;
            SaveCache(Legacy);
            Data.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", device.ExportPkcs8PrivateKey());
        }

        public SharedWorldRoster Sign(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };

        public SharedWorldRoster OtherOwnerRoster(SharedWorldRoster roster)
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var draft = roster with { OwnerPublicKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()) };
            return draft with { Signature = Convert.ToBase64String(other.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
        }

        public SharedWorldRoster Child(SharedWorldRoster parent) => Sign(parent with
        {
            Schema = 3,
            Epoch = parent.Epoch + 1,
            Revision = parent.Revision + 1,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(parent),
            SignerDeviceId = Guid.Empty,
            SignerPublicKey = parent.OwnerPublicKey
        });

        public void SaveCache(SharedWorldRoster roster) => Data.SaveProtected(CacheName, JsonSerializer.SerializeToUtf8Bytes(roster, Json));

        public void PrepareFirstSave(bool randomHalf = false, bool extraCompletedFile = false)
        {
            PublishedPayload = Enumerable.Range(0, SharedWorldService.ChunkBytes + 31)
                .Select(index => (byte)(index % 251)).ToArray();
            if (randomHalf)
            {
                var half = RandomNumberGenerator.GetBytes(16384);
                for (var index = 0; index < PublishedPayload.Length; index++) PublishedPayload[index] = half[index % half.Length];
            }
            ExtraPayload = extraCompletedFile ? Enumerable.Range(0, 47).Select(index => (byte)index).ToArray() : [];
            var files = new List<SharedWorldFile>
            {
                new("synthetic.db", PublishedPayload.LongLength, Convert.ToHexString(SHA256.HashData(PublishedPayload)))
            };
            if (extraCompletedFile) files.Add(new("synthetic.fwl", ExtraPayload.LongLength, Convert.ToHexString(SHA256.HashData(ExtraPayload))));
            PublishedVersion = SharedWorldService.SignVersion(new(1, Current.GroupId, 1, null,
                ProfileId, GameKinds.Valheim, "synthetic-roster-repair", DateTimeOffset.UtcNow,
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false),
                files, "", "", ""), owner);
        }

        public void Seed(bool includeChild = true)
        {
            Chain.Append(Bootstrap, Bootstrap.OwnerPublicKey);
            Current = Bootstrap;
            if (includeChild)
            {
                Current = Child(Bootstrap);
                // The wrong-owner refusal is checked before reading the chain;
                // its child still needs a valid owner signature to seed it.
                if (Current.OwnerPublicKey != Legacy.OwnerPublicKey) Current = Bootstrap;
                else Chain.Append(Current, Current.OwnerPublicKey);
            }
        }

        public async Task ConnectAsync()
        {
            if (link is null)
            {
                const string endpoint = "https://127.0.0.1:5131";
                var trusted = Trusted ?? Legacy;
                Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
                {
                    DeviceId = deviceId,
                    HostId = Guid.NewGuid(),
                    Endpoint = endpoint,
                    Fingerprint = new string('A', 64),
                    Credential = credential,
                    CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
                    SharedWorldSigningKeys = new() { [ProfileId] = Legacy.OwnerPublicKey },
                    SharedRosterFloors = new() { [ProfileId] = new(trusted.GroupId, trusted.Epoch, trusted.Revision, trusted.Signature) }
                }, Json));
                link = new FriendLink(Data, "friend.protected", (address, pins) =>
                {
                    Require(address == endpoint && pins.Contains(new string('A', 64)), "escaped the pinned transport");
                    return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(address) };
                });
                Require((await link.PollAsync()).Profiles.Single().Id == ProfileId, "synthetic profile unavailable");
            }
        }

        public async Task<SharedWorldSharingView> CheckAsync()
        {
            await ConnectAsync();
            return await link!.CheckSharedWorldSharingAsync(ProfileId);
        }

        public Task<ReceivedSharedWorldResult> SetConsentAsync(bool enabled) =>
            (link ?? throw new InvalidOperationException("Connect the synthetic PC first."))
                .SetSharedWorldConsentAsync(ProfileId, enabled);

        public Task<ReceivedSharedWorldResult> PullAsync(CancellationToken cancellationToken = default,
            Action<long, long>? reportProgress = null) =>
            (link ?? throw new InvalidOperationException("Connect the synthetic PC first."))
                .PullSharedWorldAsync(ProfileId, cancellationToken, reportProgress);

        public async Task ScheduledPullAsync()
        {
            var connection = link ?? throw new InvalidOperationException("Connect the synthetic PC first.");
            connection.ScheduleSharedCatchUp(CancellationToken.None);
            await connection.ScheduledSharedCatchUp();
        }

        public Task<LocalHostingAccess> CheckHostingAsync() =>
            (link ?? throw new InvalidOperationException("Check the synthetic connection first."))
                .LocalHostingAccessAsync(ProfileId, true, CancellationToken.None, permissionOnly: true);

        public Task<WorldHistoryReviewResult> ReviewAsync(WorldHistoryReviewRequest? request = null) =>
            (link ?? throw new InvalidOperationException("Check the synthetic connection first."))
                .ReviewSharedHistoryAsync(ProfileId, request);

        public void AssertFloor(SharedWorldRoster expected)
        {
            var saved = JsonSerializer.Deserialize<FriendConfiguration>(Data.LoadProtected("friend.protected")!, Json)!;
            Require(saved.SharedRosterFloors[ProfileId] == new SharedRosterFloor(expected.GroupId,
                expected.Epoch, expected.Revision, expected.Signature) &&
                saved.SharedWorldSigningKeys[ProfileId] == Legacy.OwnerPublicKey,
                "verification changed the saved trust floor or owner");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.Headers.Authorization?.Parameter == credential &&
                request.Headers.GetValues("X-Device-Id").Single() == deviceId.ToString(), "lost device authentication");
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/companion/heartbeat") body = new CompanionStatus(false, null,
                [new(ProfileId, "Synthetic roster", "Offline", null, Kind: GameKinds.Valheim)], false, false,
                DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                    [CompanionProtocol.SharedWorldsCapability]));
            else if (path.EndsWith("/enrollment", StringComparison.Ordinal))
                body = request.Method == HttpMethod.Get ? new SharedWorldEnrollmentChallenge(new string('N', 44)) : new { ok = true };
            else if (path.EndsWith("/roster", StringComparison.Ordinal)) body = Current;
            else if (path.EndsWith("/authority", StringComparison.Ordinal))
            {
                AuthorityRequests++;
                body = Array.Empty<WorldAuthorityRecord>();
            }
            else if (path.EndsWith("/revisions/current", StringComparison.Ordinal))
            {
                CurrentRequests++;
                body = Current;
            }
            else if (path.EndsWith("/revisions", StringComparison.Ordinal))
            {
                PageRequests++;
                var query = request.RequestUri.Query;
                var review = query.EndsWith("&review=1", StringComparison.Ordinal);
                Require(long.TryParse(query.AsSpan("?offset=".Length,
                    query.Length - "?offset=".Length - (review ? "&review=1".Length : 0)), out var offset), "invalid offset");
                LastOffset = offset;
                LastAnchor = request.Headers.TryGetValues(SharedWorldRosterProtocol.AfterHeader, out var anchors)
                    ? anchors.Single() : null;
                if (review) ReviewPageRequests++;
                if (review && !ReviewPagesAvailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                if (!review && RosterPagesUnavailable)
                {
                    var fenced = new HttpResponseMessage(LastAnchor is null ? HttpStatusCode.NotFound : HttpStatusCode.Conflict);
                    if (LastAnchor is not null) fenced.Headers.Add(SharedWorldRosterProtocol.AnchorHeader, LastAnchor);
                    return Task.FromResult(fenced);
                }
                if (PageFailures > 0) { PageFailures--; throw new HttpRequestException("synthetic lost roster response"); }
                if (LastAnchor is null)
                {
                    var beforeRootPage = BeforeRootPage;
                    BeforeRootPage = null;
                    beforeRootPage?.Invoke();
                }
                if (RemoteHistory is not null && LastAnchor is not null)
                {
                    var position = Array.FindIndex(RemoteHistory.ToArray(), item => SharedWorldRosterTrust.Hash(item) == LastAnchor);
                    if (position >= 0)
                    {
                        var beforeAnchoredPage = BeforeAnchoredPage;
                        BeforeAnchoredPage = null;
                        beforeAnchoredPage?.Invoke();
                    }
                    var response = new HttpResponseMessage(position < 0 ? HttpStatusCode.Conflict : HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(position < 0 ? [] :
                        RemoteHistory.Skip(position + 1).Take(SharedWorldRosterChainStore.PageSize).ToArray(), Json))
                    };
                    response.Headers.Add(SharedWorldRosterProtocol.AnchorHeader, LastAnchor);
                    if (position < 0) RootFallbacks++;
                    return Task.FromResult(response);
                }
                body = RemoteHistory is null ? Page : RemoteHistory.Skip(checked((int)offset)).Take(SharedWorldRosterChainStore.PageSize).ToArray();
            }
            else if (path == $"/api/companion/servers/{ProfileId}/shared-world")
            {
                AssertFloor(Current);
                Require(Chain.Contains(ProfileId, Current), "save manifest requested before journal repair");
                ManifestRequests++;
                body = PublishedVersion ?? throw new InvalidOperationException("No synthetic save was prepared.");
            }
            else if (PublishedVersion is { } version && path.StartsWith(
                $"/api/companion/servers/{ProfileId}/shared-world/{version.VersionHash}/files/0/chunks/", StringComparison.Ordinal))
            {
                Require(long.TryParse(path.AsSpan(path.LastIndexOf('/') + 1), out var offset) &&
                    offset >= 0 && offset < PublishedPayload.LongLength && offset % SharedWorldService.ChunkBytes == 0,
                    "synthetic save chunk offset was invalid");
                ChunkRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = ChunkContent?.Invoke(PublishedPayload.AsSpan((int)offset,
                        Math.Min(SharedWorldService.ChunkBytes, PublishedPayload.Length - (int)offset)).ToArray()) ??
                        new ByteArrayContent(PublishedPayload.AsSpan((int)offset,
                        Math.Min(SharedWorldService.ChunkBytes, PublishedPayload.Length - (int)offset)).ToArray())
                });
            }
            else if (path == $"/api/companion/servers/{ProfileId}/shared-world/receipts")
                return ConfirmReceiptAsync(request, cancellationToken);
            else throw new InvalidOperationException("Unexpected synthetic roster request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json)) });
        }

        private async Task<HttpResponseMessage> ConfirmReceiptAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Require(request.Method == HttpMethod.Post, "receipt used the wrong HTTP action");
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var receipt = SharedWorldReceiptTrust.Parse(bytes);
            var version = PublishedVersion ?? throw new InvalidOperationException("No synthetic save was prepared.");
            var member = Current.Members.Single(item => item.DeviceId == deviceId);
            Require(bytes.Length <= SharedWorldReceiptTrust.MaximumRequestBytes &&
                member is { Revoked: false, Grants.Receive: true } &&
                SharedWorldReceiptTrust.Verify(receipt, member.PublicKey) &&
                receipt is not null && receipt.DeviceId == deviceId && receipt.ProfileId == ProfileId &&
                receipt.GroupId == Current.GroupId && receipt.VersionHash == version.VersionHash &&
                receipt.RosterEpoch == Current.Epoch && receipt.RosterRevision == Current.Revision &&
                FriendLink.ReadReceivedLatest(ReceiveRoot)?.VersionHash == version.VersionHash,
                "Host accepted a receipt without its exact signed roster, device and verified save identity");
            Require(ConfirmedReceipt is null || ConfirmedReceipt == receipt,
                "an unchanged receive retry replaced the Host's confirmed receipt");
            ConfirmedReceipt = receipt;
            ReceiptRequests++;
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                    new SharedWorldReceiptResult(true, "CopyConfirmed"), Json))
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { link?.Dispose(); owner.Dispose(); device.Dispose(); Data.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
