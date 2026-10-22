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
        await RecoveredFloorReceivesSaveAsync(root);
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
        public int PageFailures { get; set; }
        public int CurrentRequests { get; private set; }
        public int PageRequests { get; private set; }
        public int RootFallbacks { get; private set; }
        public int ManifestRequests { get; private set; }
        public int ChunkRequests { get; private set; }
        public int ReceiptRequests { get; private set; }
        public SharedWorldVersion? PublishedVersion { get; private set; }
        public byte[] PublishedPayload { get; private set; } = [];
        public SharedWorldReceipt? ConfirmedReceipt { get; private set; }
        public string? LastAnchor { get; private set; }
        public long LastOffset { get; private set; } = -1;
        public string CacheName => $"shared-world-roster-{deviceId:N}-{ProfileId:N}.protected";
        public string ReceiveRoot => Path.Combine(Data.RootPath, "received-shared-worlds",
            deviceId.ToString("N"), ProfileId.ToString("N"));
        public FriendConfiguration SavedConfiguration => JsonSerializer.Deserialize<FriendConfiguration>(
            Data.LoadProtected("friend.protected")!, Json)!;
        public ReceivedSharedWorldStatus ReceiveStatus =>
            (link ?? throw new InvalidOperationException("Check the synthetic connection first.")).SharedWorldStatus(ProfileId);

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

        public void PrepareFirstSave()
        {
            PublishedPayload = Enumerable.Range(0, SharedWorldService.ChunkBytes + 31)
                .Select(index => (byte)(index % 251)).ToArray();
            PublishedVersion = SharedWorldService.SignVersion(new(1, Current.GroupId, 1, null,
                ProfileId, GameKinds.Valheim, "synthetic-roster-repair", DateTimeOffset.UtcNow,
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false),
                [new("synthetic.db", PublishedPayload.LongLength,
                    Convert.ToHexString(SHA256.HashData(PublishedPayload)))], "", "", ""), owner);
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

        public Task<ReceivedSharedWorldResult> PullAsync() =>
            (link ?? throw new InvalidOperationException("Connect the synthetic PC first."))
                .PullSharedWorldAsync(ProfileId);

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
            else if (path.EndsWith("/authority", StringComparison.Ordinal)) body = Array.Empty<WorldAuthorityRecord>();
            else if (path.EndsWith("/revisions/current", StringComparison.Ordinal))
            {
                CurrentRequests++;
                body = Current;
            }
            else if (path.EndsWith("/revisions", StringComparison.Ordinal))
            {
                PageRequests++;
                Require(long.TryParse(request.RequestUri.Query.AsSpan("?offset=".Length), out var offset), "invalid offset");
                LastOffset = offset;
                LastAnchor = request.Headers.TryGetValues(SharedWorldRosterProtocol.AfterHeader, out var anchors)
                    ? anchors.Single() : null;
                if (PageFailures > 0) { PageFailures--; throw new HttpRequestException("synthetic lost roster response"); }
                if (RemoteHistory is not null && LastAnchor is not null)
                {
                    var position = Array.FindIndex(RemoteHistory.ToArray(), item => SharedWorldRosterTrust.Hash(item) == LastAnchor);
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
                    Content = new ByteArrayContent(PublishedPayload.AsSpan((int)offset,
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
