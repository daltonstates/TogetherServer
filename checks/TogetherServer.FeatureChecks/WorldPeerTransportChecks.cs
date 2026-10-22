using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fake HttpMessageHandler dispatches fixed requests straight to the service.
// No socket/listener, app, browser, process, dialog or real world is involved.
internal static class WorldPeerTransportChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string rootPath)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var returningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var prefix = Path.Combine(rootPath, "peer-transport-" + Guid.NewGuid().ToString("N"));
        using var ownerData = new LocalData(Path.Combine(prefix, "owner"));
        using var firstData = new LocalData(Path.Combine(prefix, "first-peer"));
        using var returningData = new LocalData(Path.Combine(prefix, "offline-returning-peer"));
        var profileId = Guid.NewGuid();
        ownerData.SaveSettings(new HostSettings
        {
            CompanionEndpoint = "https://192.0.2.50:5131",
            CompanionListeningEnabled = true,
            Profiles = [new() { Id = profileId, SharedSavesEnabled = false,
                WorldDirectory = Path.Combine(ownerData.RootPath, "synthetic-world") }]
        });
        firstData.SaveSettings(new HostSettings { CompanionEndpoint = "https://192.0.2.51:5131", CompanionBindAddress = "0.0.0.0", CompanionListeningEnabled = true });
        returningData.SaveSettings(new HostSettings { CompanionEndpoint = "https://192.0.2.52:5131" });
        ownerData.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
        using var ownerCertificate = new HostIdentity(ownerData).Ensure("https://192.0.2.50:5131");
        using var firstCertificate = new HostIdentity(firstData).Ensure("https://192.0.2.51:5131");
        var ownerRoute = new SharedWorldPeerRoute("https://192.0.2.50:5131", HostIdentity.Fingerprint(ownerCertificate));
        var firstRoute = new SharedWorldPeerRoute("https://192.0.2.51:5131", HostIdentity.Fingerprint(firstCertificate));
        var pairing = new PairingService(ownerData);
        var invite = pairing.IssueServer(profileId, false, false, ownerRoute.Address, ownerRoute.CertificateSha256, refresh: false);
        var first = pairing.Activate(new(profileId, invite.Code, ServerScope: true)) ??
            throw new InvalidOperationException("first synthetic peer pairing failed");
        var returning = pairing.Activate(new(profileId, invite.Code, ServerScope: true)) ??
            throw new InvalidOperationException("returning synthetic peer pairing failed");
        Bind(pairing, first.DeviceId, firstKey);
        var source = new SharedWorldPeerRoot(Public(owner), profileId, Guid.NewGuid());
        var draft = new SharedWorldRoster(3, source.GroupId, profileId, 1, 1, true,
            source.OwnerPublicKey, pairing.SharedRosterMembers(profileId).OrderBy(member => member.DeviceId).ToArray(), "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: source.OwnerPublicKey);
        var roster = draft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        new SharedWorldRosterChainStore(ownerData).Append(roster, source.OwnerPublicKey);
        pairing.ConfirmSharedRosterPublished(profileId, roster);
        firstData.SaveProtected($"shared-world-pc-signing-{first.DeviceId:N}.protected", firstKey.ExportPkcs8PrivateKey());
        returningData.SaveProtected($"shared-world-pc-signing-{returning.DeviceId:N}.protected", returningKey.ExportPkcs8PrivateKey());
        SaveMembership(ownerData, source, roster, Guid.Empty, true, null);
        SaveMembership(firstData, source, roster, first.DeviceId, true, ownerRoute);
        var branch = new SharedWorldPeerBranch(Hash("synthetic branch"), Hash("synthetic saved version"),
            Hash("synthetic saved version"), null, 1);
        var ownerStore = new SharedWorldPeerStore(ownerData, source,
            new SharedWorldPeerTrust(source, (id, key) => SharedWorldPeerTrust.AccessFromRoster(source, roster, id, key)));
        _ = ownerStore.Publish(Guid.Empty, owner, [ownerRoute], [], [branch]);
        using var ownerFriends = new FriendService(ownerData);
        using var firstFriends = new FriendService(firstData);
        using var returningFriends = new FriendService(returningData);
        var ownerManager = new HostManager(ownerData);
        var firstManager = new HostManager(firstData);
        var returningManager = new HostManager(returningData);
        using var ownerService = new WorldPeerService(ownerData, ownerManager, ownerFriends, pairing,
            (_, _) => throw new InvalidOperationException("the original owner must not open another peer route"));
        using var ownerHandler = new FakePeer(ownerService, source, ownerRoute);
        using var firstService = new WorldPeerService(firstData, firstManager, firstFriends,
            new PairingService(firstData), (endpoint, pins) => Client(ownerHandler, ownerRoute, endpoint, pins));
        await firstService.SyncNowAsync(CancellationToken.None);
        var firstView = firstService.Views().Single();
        Require(firstView.State.Root == source && firstView.State.Branches.Any(value => value.Branch == branch) &&
            !firstView.CanReceive && ownerHandler.Operations.All(value => value == WorldPeerOperations.Metadata),
            "a metadata-only peer did not learn signed world state without pulling payloads");
        Require(ownerHandler.RequestCount >= 2 && firstView.State.Replication.LastSuccessfulSyncUtc is not null,
            "the real fixed signed challenge/exchange path was not exercised through fake HTTP");

        // This peer goes offline before the owner enrolls the returning PC.
        // The returning PC received the newer signed chain from the owner; the
        // old peer has never seen its device ID/key or the second roster.
        var intervening = new List<SharedWorldRoster>();
        var previousRoster = roster;
        for (var revision = 2; revision <= 10; revision++)
        {
            var nextDraft = roster with
            {
                Epoch = revision,
                Revision = revision,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(previousRoster),
                OwnerLocalBaselineMembers = roster.Members,
                Signature = ""
            };
            var next = nextDraft with
            {
                Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldRosterTrust.Basis(nextDraft), HashAlgorithmName.SHA256))
            };
            new SharedWorldRosterChainStore(ownerData).Append(next, source.OwnerPublicKey);
            intervening.Add(next);
            previousRoster = next;
        }
        Bind(pairing, returning.DeviceId, returningKey);
        var newerDraft = roster with
        {
            Epoch = 11,
            Revision = 11,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(previousRoster),
            Members = pairing.SharedRosterMembers(profileId).OrderBy(member => member.DeviceId).ToArray(),
            OwnerLocalBaselineMembers = pairing.SharedRosterMembers(profileId).OrderBy(member => member.DeviceId).ToArray(),
            Signature = ""
        };
        var newer = newerDraft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(newerDraft), HashAlgorithmName.SHA256))
        };
        new SharedWorldRosterChainStore(ownerData).Append(newer, source.OwnerPublicKey);
        pairing.ConfirmSharedRosterPublished(profileId, newer);
        SaveMembership(ownerData, source, newer, Guid.Empty, true, null);
        SaveMembership(returningData, source, newer, returning.DeviceId, false, firstRoute);
        var returningChain = new SharedWorldRosterChainStore(returningData);
        returningChain.Append(roster, source.OwnerPublicKey);
        foreach (var item in intervening) returningChain.Append(item, source.OwnerPublicKey);
        returningChain.Append(newer, source.OwnerPublicKey);
        Invalid(() => firstService.Challenge(source.GroupKey, returning.DeviceId, Public(returningKey), firstRoute.CertificateSha256),
            "the old peer already authorized a PC absent from its signed roster");
        var forged = newer with { Members = roster.Members };
        Invalid(() => firstService.PrepareChallenge(source.GroupKey, WorldPeerService.SignChallengeRequest(source,
            returning.DeviceId, returningKey, firstRoute.CertificateSha256, 11, [forged]), firstRoute.CertificateSha256),
            "a changed roster enrolled an unknown requester before signature verification");
        Require(new SharedWorldRosterChainStore(firstData).Heads(profileId).Single().Revision == 1,
            "rejected unsigned membership changed the old peer's protected roster floor");

        // The original owner route is deliberately not available to this PC.
        // Its trusted peer forwards the original signed owner announcement and
        // complete owner-rooted roster chain without possessing the owner key.
        using var firstHandler = new FakePeer(firstService, source, firstRoute);
        using var returningService = new WorldPeerService(returningData, returningManager, returningFriends,
            new PairingService(returningData), (endpoint, pins) => Client(firstHandler, firstRoute, endpoint, pins));
        // The bootstrap first proves its responder key without receiving the
        // requester's roster. Later bounded calls resume the two-way proof.
        for (var attempt = 0; attempt < 3; attempt++) await returningService.SyncNowAsync(CancellationToken.None);
        var returned = returningService.Views().Single();
        Require(returned.State.GroupKey == source.GroupKey && returned.State.Branches.Any(value => value.Branch == branch) &&
            returned.State.Peers.Any(value => value.PublicKey == Public(owner)) &&
            !returned.CanReceive && firstHandler.Operations.All(value => value == WorldPeerOperations.Metadata),
            "an offline-returning metadata-only PC could not catch up through an approved peer without the original owner");
        Require(new SharedWorldRosterChainStore(firstData).Heads(profileId).Single().Signature == newer.Signature,
            "the unknown requester did not advance the old peer through the verified owner-rooted enrollment chain");
        Require(firstHandler.PendingRosterAcks > 0,
            "a requester absent from the old roster received a nonce before the bounded enrollment chain reached its member record");
        var retained = returned.State.AnnouncementCount;
        firstHandler.Mode = Fault.WrongRoot;
        await returningService.SyncNowAsync(CancellationToken.None);
        Require(returningService.Views().Single().State.Branches.Any(value => value.Branch == branch) &&
            returningService.Views().Single().State.GroupKey == source.GroupKey &&
            returningService.Views().Single().State.AnnouncementCount >= retained,
            "a wrong-root HTTP response replaced the stable world or erased its history");
        firstHandler.Mode = Fault.Oversized;
        await returningService.SyncNowAsync(CancellationToken.None);
        Require(returningService.Views().Single().Warning is not null &&
            returningService.Views().Single().State.Branches.Any(value => value.Branch == branch),
            "an oversized peer response was accepted or discarded an existing branch");
        firstHandler.Mode = Fault.Redirect;
        await returningService.SyncNowAsync(CancellationToken.None);
        Require(firstHandler.Requests.All(uri => uri.Host == "192.0.2.51" && uri.Query.Length == 0 &&
            uri.AbsolutePath.StartsWith($"/api/companion/world-peers/{source.GroupKey}/", StringComparison.Ordinal)),
            "a peer selected an arbitrary proxy target or query path");
        var replay = firstService.PrepareChallenge(source.GroupKey, WorldPeerService.SignChallengeRequest(source,
            returning.DeviceId, returningKey, firstRoute.CertificateSha256, 11, [roster]), firstRoute.CertificateSha256);
        Require(replay.Challenge is not null && replay.KnownRosterRevision == 11 &&
            new SharedWorldRosterChainStore(firstData).Heads(profileId).Single().Signature == newer.Signature,
            "an old signed roster rolled back the new enrollment floor");
        var alternateDraft = newerDraft with { Members = roster.Members, OwnerLocalBaselineMembers = roster.Members };
        var alternate = alternateDraft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(alternateDraft), HashAlgorithmName.SHA256))
        };
        Invalid(() => firstService.PrepareChallenge(source.GroupKey, WorldPeerService.SignChallengeRequest(source,
            returning.DeviceId, returningKey, firstRoute.CertificateSha256, 11, [alternate]), firstRoute.CertificateSha256),
            "a competing owner-signed roster silently selected one membership history");
        var competing = new SharedWorldRosterChainStore(firstData).Heads(profileId);
        Require(competing.Count == 2 && competing.Any(value => value.Signature == newer.Signature) &&
            competing.Any(value => value.Signature == alternate.Signature),
            "a roster conflict deleted one of the signed histories");
        Invalid(() => firstService.Challenge(source.GroupKey, returning.DeviceId, Public(returningKey), firstRoute.CertificateSha256),
            "an unresolved roster fork retained old metadata access");

        await CheckRosterBootstrapAsync(Path.Combine(prefix, "new-host"), BootstrapCase.NewResponder);
        await CheckRosterBootstrapAsync(Path.Combine(prefix, "intermediate-expiry"), BootstrapCase.InactiveResponder);
        await CheckRosterBootstrapAsync(Path.Combine(prefix, "requester-extension"), BootstrapCase.ExpiredRequester);
        await CheckRosterBootstrapAsync(Path.Combine(prefix, "outsider-contact"), BootstrapCase.OutsiderContact);
        await CheckRosterBootstrapAsync(Path.Combine(prefix, "legacy-number-gaps"), BootstrapCase.LegacyNumberGaps);
    }

    private enum BootstrapCase { NewResponder, InactiveResponder, ExpiredRequester, OutsiderContact, LegacyNumberGaps }

    private static async Task CheckRosterBootstrapAsync(string rootPath, BootstrapCase scenario)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var requesterKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var responderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var outsiderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var requesterData = new LocalData(Path.Combine(rootPath, "requester"));
        using var responderData = new LocalData(Path.Combine(rootPath, "responder"));
        var requesterId = Guid.NewGuid();
        var responderId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var expired = DateTimeOffset.UtcNow.AddHours(-1);
        var history = new List<SharedWorldRoster>();
        for (var revision = 1; revision <= 19; revision++)
        {
            var requester = new SharedWorldRosterMember(requesterId, Public(requesterKey), new(), false,
                scenario == BootstrapCase.ExpiredRequester && revision < 19 ? expired : null);
            var responder = new SharedWorldRosterMember(responderId, Public(responderKey), new(), false,
                scenario == BootstrapCase.InactiveResponder && revision is > 1 and < 19 ? expired : null);
            IReadOnlyList<SharedWorldRosterMember> members = scenario == BootstrapCase.InactiveResponder && revision < 19
                ? [responder] : (scenario is BootstrapCase.NewResponder or BootstrapCase.OutsiderContact) && revision < 19
                    ? [requester] : [requester, responder];
            var number = scenario == BootstrapCase.LegacyNumberGaps && revision > 1 ? revision + 1 : revision;
            var schema = scenario == BootstrapCase.LegacyNumberGaps && revision <= 2 ? 2 : 3;
            var draft = new SharedWorldRoster(schema, root.GroupId, root.ProfileId, number, number, true,
                root.OwnerPublicKey, members, "", revision == 1 ? null : SharedWorldRosterTrust.Hash(history[^1]),
                Guid.Empty, root.OwnerPublicKey, members);
            history.Add(draft with
            {
                Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            });
        }
        var latest = history[^1];
        var responderInitiallyOld = scenario == BootstrapCase.InactiveResponder;
        var requestRoster = responderInitiallyOld ? latest : history[0];
        var responseRoster = responderInitiallyOld ? history[0] : latest;
        var servingId = scenario == BootstrapCase.OutsiderContact ? outsiderId : responderId;
        var servingKey = scenario == BootstrapCase.OutsiderContact ? outsiderKey : responderKey;
        requesterData.SaveSettings(new HostSettings { CompanionEndpoint = "https://192.0.2.71:5131" });
        responderData.SaveSettings(new HostSettings { CompanionEndpoint = "https://192.0.2.72:5131", CompanionBindAddress = "0.0.0.0", CompanionListeningEnabled = true });
        requesterData.SaveProtected($"shared-world-pc-signing-{requesterId:N}.protected", requesterKey.ExportPkcs8PrivateKey());
        responderData.SaveProtected($"shared-world-pc-signing-{servingId:N}.protected", servingKey.ExportPkcs8PrivateKey());
        var requestChain = new SharedWorldRosterChainStore(requesterData);
        var responseChain = new SharedWorldRosterChainStore(responderData);
        foreach (var item in history.Where(item => item.Revision <= requestRoster.Revision)) requestChain.Append(item, root.OwnerPublicKey);
        foreach (var item in history.Where(item => item.Revision <= responseRoster.Revision)) responseChain.Append(item, root.OwnerPublicKey);
        SaveMembership(requesterData, root, requestRoster, requesterId, false, null);
        SaveMembership(responderData, root, responseRoster, servingId, true, null);
        using var certificate = new HostIdentity(responderData).Ensure("https://192.0.2.72:5131");
        var route = new SharedWorldPeerRoute("https://192.0.2.72:5131", HostIdentity.Fingerprint(certificate));
        using var requesterFriends = new FriendService(requesterData);
        using var responderFriends = new FriendService(responderData);
        using var responderService = new WorldPeerService(responderData, new HostManager(responderData), responderFriends,
            new PairingService(responderData), (_, _) => throw new InvalidOperationException("the original owner must stay offline"));
        if (scenario != BootstrapCase.OutsiderContact) await responderService.SyncNowAsync(CancellationToken.None);
        using var handler = new FakePeer(responderService, root, route);
        using var requesterService = new WorldPeerService(requesterData, new HostManager(requesterData), requesterFriends,
            new PairingService(requesterData), (endpoint, pins) => Client(handler, route, endpoint, pins));
        var contact = scenario == BootstrapCase.OutsiderContact
            ? ContactCode(root, outsiderId, outsiderKey, route) : responderService.CreateContactCode(root.GroupKey);
        Require(requesterService.ImportContactCode(root.GroupKey, contact).Ok,
            "a signed exact-world contact could not be retained until its new enrollment is proved");
        if (scenario == BootstrapCase.OutsiderContact)
            handler.RewriteChallenge = reply =>
            {
                var page = reply.RosterRevisions!.ToArray();
                page[0] = page[0] with { Members = page[0].Members.Append(new(outsiderId, Public(outsiderKey), new(), false)).ToArray() };
                return SignReply(reply with { RosterRevisions = page, Signature = "" }, outsiderKey);
            };
        await requesterService.SyncNowAsync(CancellationToken.None);
        Require(handler.Operations.Count == 0 && handler.RequestCount <= 2 && handler.PendingRosterAcks > 0,
            "a partial roster page authorized an exchange or exceeded the bounded per-call handshake budget");
        if (scenario == BootstrapCase.InactiveResponder)
        {
            Require(responseChain.Heads(root.ProfileId).Single().Revision == 16 && responderService.HasEnabledGroups,
                "an inactive intermediate responder lost its retained-key acknowledgement or explicitly enabled proof listener");
            Invalid(() => responderService.Challenge(root.GroupKey, Guid.Empty, root.OwnerPublicKey, route.CertificateSha256),
                "an inactive responder issued a world-operation challenge using its retained key");
            var before = responderService.Views().Single().State.AnnouncementCount;
            await responderService.SyncNowAsync(CancellationToken.None);
            Require(responderService.Views().Single().State.AnnouncementCount == before,
                "an inactive responder published an advertisement while only roster catchup was permitted");
        }
        else Require(requestChain.Heads(root.ProfileId).Single().Revision ==
                (scenario == BootstrapCase.OutsiderContact ? 1 : scenario == BootstrapCase.LegacyNumberGaps ? 18 : 17),
            "bounded valid proof progress was lost, or a forged owner roster advanced the protected floor");
        handler.RewriteChallenge = null;
        for (var attempt = 0; attempt < 3; attempt++) await requesterService.SyncNowAsync(CancellationToken.None);
        Require(requestChain.Heads(root.ProfileId).Single().Signature == latest.Signature &&
            responseChain.Heads(root.ProfileId).Single().Signature == latest.Signature,
            "two-way bootstrap failed to resume through the exact owner-rooted history without the original owner");
        Require(handler.Challenges.All(value => (value.RosterRevisions?.Count ?? 0) <= 8) &&
            handler.Replies.All(value => (value.RosterRevisions?.Count ?? 0) <= 8) &&
            handler.LargestChallengeReply <= WorldPeerService.MaximumRosterHandshakeBytes,
            "a bootstrap proof exceeded its revision or wire budget");
        if (scenario == BootstrapCase.OutsiderContact)
        {
            Require(handler.Operations.Count == 0 && handler.Challenges.All(value => value.RosterRevisions is null or { Count: 0 }) &&
                !requesterService.Views().Single().State.Peers.Any(value => value.PublicKey == Public(outsiderKey) && value.Authorized),
                "a self-signed outsider contact obtained roster details, world data, or a membership grant");
            return;
        }
        Require(handler.Operations.Count > 0 && handler.Operations.All(value => value == WorldPeerOperations.Metadata) &&
            requesterService.Views().Single().State.Peers.Any(value => value.PublicKey == Public(responderKey) && value.Authorized),
            "a returning metadata-only PC did not authenticate its new or regranted Host after full roster catchup");
        if (scenario == BootstrapCase.LegacyNumberGaps)
        {
            SharedWorldRoster SignRoster(SharedWorldRoster draft) => draft with
            { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            // This returning PC accepted B2 between the responder's retained A1
            // and C3 legacy snapshots. Its old grants are not current authority.
            var intermediate = SignRoster(history[0] with
            {
                Epoch = 2,
                Revision = 2,
                Members = history[0].Members.Select(member => member.DeviceId == requesterId ? member with
                { Grants = new(Receive: true, EligibleHost: true, Reshare: true) } : member).ToArray()
            });
            var before = responseChain.Count(root.ProfileId);
            var catchUp = responderService.PrepareChallenge(root.GroupKey,
                WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey,
                    route.CertificateSha256, 2, [intermediate]), route.CertificateSha256);
            Require(catchUp.Challenge is null && catchUp.RosterRevisions?.FirstOrDefault()?.Signature == history[1].Signature &&
                responseChain.Count(root.ProfileId) == before && responseChain.Heads(root.ProfileId).Single().Signature == latest.Signature,
                "a superseded legacy proof replaced current membership or blocked bounded peer catchup");
            var active = responderService.PrepareChallenge(root.GroupKey,
                WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey,
                    route.CertificateSha256, latest.Revision, [intermediate]), route.CertificateSha256);
            Require(active.Challenge is not null, "current metadata membership was not used after ignoring redundant legacy proof");
            var chunk = WorldPeerProtocol.Sign(active.Challenge, WorldPeerOperations.Chunk, requesterKey,
                versionHash: new string('A', 64), fileIndex: 0, chunkOffset: 0);
            var payloadDenied = false;
            try { _ = await responderService.ExchangeAsync(root.GroupKey, chunk, [], CancellationToken.None, route.CertificateSha256); }
            catch (InvalidDataException error) { payloadDenied = error.Message == "Save access is not enabled for this peer."; }
            Require(payloadDenied && responseChain.Heads(root.ProfileId).Single().Members.Single(member =>
                member.DeviceId == requesterId).Grants == new SharedWorldGrants(),
                "a redundant legacy proof restored withdrawn save access");
            foreach (var fault in new[] { "invalid-signature", "wrong-group", "wrong-profile", "schema3", "same-epoch", "same-revision", "sibling" })
            {
                var candidate = SignRoster(fault switch
                {
                    "wrong-group" => intermediate with { GroupId = Guid.NewGuid() },
                    "wrong-profile" => intermediate with { ProfileId = Guid.NewGuid() },
                    "schema3" => intermediate with
                    {
                        Schema = 3,
                        SignerDeviceId = Guid.Empty,
                        SignerPublicKey = root.OwnerPublicKey,
                        PreviousRosterHash = SharedWorldRosterTrust.Hash(history[0])
                    },
                    "same-epoch" => intermediate with { Epoch = 1 },
                    "same-revision" => intermediate with { Revision = 1 },
                    "sibling" => history[0] with { OwnerOverride = !history[0].OwnerOverride },
                    _ => intermediate
                });
                if (fault == "invalid-signature") candidate = candidate with { Signature = new string('A', 88) };
                Invalid(() => responderService.PrepareChallenge(root.GroupKey,
                    WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey,
                        route.CertificateSha256, 2, [candidate]), route.CertificateSha256),
                    "an unverified, forked or exact-parent proof was ignored: " + fault);
                Require(responseChain.Count(root.ProfileId) == before &&
                    responseChain.Heads(root.ProfileId).Single().Signature == latest.Signature,
                    "rejected redundant metadata changed the protected journal");
            }
            var removedMembers = latest.Members.Select(member => member.DeviceId == requesterId ? member with { Revoked = true } : member).ToArray();
            var revokedRoster = SignRoster(latest with
            {
                Epoch = latest.Epoch + 1,
                Revision = latest.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(latest),
                Members = removedMembers,
                OwnerLocalBaselineMembers = removedMembers
            });
            responseChain.Append(revokedRoster, root.OwnerPublicKey);
            SaveMembership(responderData, root, revokedRoster, responderId, true, null);
            var revokedChallenge = responderService.PrepareChallenge(root.GroupKey,
                WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey,
                    route.CertificateSha256, 2, [intermediate]), route.CertificateSha256);
            Require(revokedChallenge.Challenge is null && (revokedChallenge.RosterRevisions is null or { Count: 0 }) &&
                responseChain.Heads(root.ProfileId).Single().Signature == revokedRoster.Signature,
                "a superseded legacy proof restored a currently revoked PC");
            return;
        }
        if (scenario == BootstrapCase.NewResponder)
            Require(handler.Challenges.Take(2).All(value => value.RosterRevisions is null or { Count: 0 }),
                "an unproved new contact obtained the requester's roster before its enrollment was known");

        var valid = WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey, route.CertificateSha256, 1);
        var proof = responderService.PrepareChallenge(root.GroupKey, valid, route.CertificateSha256);
        var replay = responderService.PrepareChallenge(root.GroupKey, valid, route.CertificateSha256);
        Require(proof.RosterRevisions is { Count: > 0 } && (replay.RosterRevisions is null or { Count: 0 }) && replay.Challenge is null,
            "a replayed request proof released another roster page or operation challenge");
        var unproved = WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey, route.CertificateSha256, 1)
            with
        { Signature = null };
        var denied = responderService.PrepareChallenge(root.GroupKey, unproved, route.CertificateSha256);
        var outsider = responderService.PrepareChallenge(root.GroupKey,
            WorldPeerService.SignChallengeRequest(root, outsiderId, outsiderKey, route.CertificateSha256, 1), route.CertificateSha256);
        Require((denied.RosterRevisions is null or { Count: 0 }) && denied.Challenge is null &&
            (outsider.RosterRevisions is null or { Count: 0 }) && outsider.Challenge is null,
            "an unproved key or unknown requester received owner-roster details or a world challenge");
        Invalid(() => responderService.PrepareChallenge(root.GroupKey,
            WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey, Hash("another TLS target"), 1), route.CertificateSha256),
            "a request signed for another TLS peer released roster details");
        Invalid(() => responderService.PrepareChallenge(root.GroupKey,
            WorldPeerService.SignChallengeRequest(root with { GroupId = Guid.NewGuid() }, requesterId, requesterKey,
                route.CertificateSha256, 1), route.CertificateSha256),
            "a request signed for another world released roster details");
        var revokedMembers = latest.Members.Select(member => member.DeviceId == requesterId ? member with { Revoked = true } : member).ToArray();
        var revokedDraft = latest with
        {
            Epoch = 20,
            Revision = 20,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(latest),
            Members = revokedMembers,
            OwnerLocalBaselineMembers = revokedMembers,
            Signature = ""
        };
        var revoked = revokedDraft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(revokedDraft), HashAlgorithmName.SHA256))
        };
        responseChain.Append(revoked, root.OwnerPublicKey);
        SaveMembership(responderData, root, revoked, responderId, true, null);
        var removed = responderService.PrepareChallenge(root.GroupKey,
            WorldPeerService.SignChallengeRequest(root, requesterId, requesterKey, route.CertificateSha256, 19), route.CertificateSha256);
        Require((removed.RosterRevisions is null or { Count: 0 }) && removed.Challenge is null,
            "an explicitly revoked requester received fresh roster details or a world-operation challenge");
    }

    private static string ContactCode(SharedWorldPeerRoot root, Guid id, ECDsa key, SharedWorldPeerRoute route)
    {
        var card = new WorldPeerContactCard(1, root, id, Public(key), 1, [route], "");
        var basis = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer world peer contact v1",
            card.Schema,
            card.Root,
            card.DeviceId,
            card.PublicKey,
            card.Revision,
            card.Routes
        }, Json);
        card = card with { Signature = Convert.ToBase64String(key.SignData(basis, HashAlgorithmName.SHA256)) };
        return "TSW1-" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(card, Json));
    }

    // An outsider can sign its own envelope but cannot change owner-signed
    // membership. This builds that attack independently of the service signer.
    private static WorldPeerChallengeResponse SignReply(WorldPeerChallengeResponse value, ECDsa key)
    {
        var basis = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer peer roster catchup acknowledgement v1",
            value.Schema,
            value.Root,
            value.DeviceId,
            value.PublicKey,
            value.ResponderDeviceId,
            value.ResponderPublicKey,
            value.CertificateSha256,
            value.RequestSha256,
            value.KnownRosterRevision,
            value.Code,
            value.Challenge,
            value.RosterRevisions
        }, Json);
        return value with { Signature = Convert.ToBase64String(key.SignData(basis, HashAlgorithmName.SHA256)) };
    }

    private static HttpClient Client(FakePeer peer, SharedWorldPeerRoute expected, string endpoint, IEnumerable<string> pins)
    {
        if (expected.Address == "https://192.0.2.51:5131" && endpoint.TrimEnd('/') == "https://192.0.2.50:5131")
            throw new HttpRequestException("The synthetic original owner is offline.");
        Require(endpoint.TrimEnd('/') == expected.Address && pins.SequenceEqual([expected.CertificateSha256]),
            "peer sync escaped the known exact pinned origin");
        return new(peer, disposeHandler: false) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
    }

    private static void SaveMembership(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster,
        Guid deviceId, bool sharing, SharedWorldPeerRoute? bootstrap) =>
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = root, Roster = roster, DeviceId = deviceId, Name = "Synthetic shared world",
                ReceiveConsented = false, SharingEnabled = sharing, Bootstrap = bootstrap } }
        }, Json));

    private static void Bind(PairingService pairing, Guid deviceId, ECDsa key)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var request = new SharedWorldEnrollmentRequest(nonce, Public(key), Convert.ToBase64String(key.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(deviceId, nonce, Public(key)), HashAlgorithmName.SHA256)));
        Require(pairing.BindSharedWorldKey(deviceId, request).Ok, "the synthetic peer signing key did not enroll");
    }

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Invalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
    private enum Fault { None, WrongRoot, Oversized, Redirect }

    private sealed class FakePeer(WorldPeerService service, SharedWorldPeerRoot root, SharedWorldPeerRoute route) : HttpMessageHandler
    {
        internal Fault Mode { get; set; }
        internal int RequestCount { get; private set; }
        internal int PendingRosterAcks { get; private set; }
        internal List<Uri> Requests { get; } = [];
        internal List<string> Operations { get; } = [];
        internal List<WorldPeerChallengeRequest> Challenges { get; } = [];
        internal List<WorldPeerChallengeResponse> Replies { get; } = [];
        internal int LargestChallengeReply { get; private set; }
        internal Func<WorldPeerChallengeResponse, WorldPeerChallengeResponse>? RewriteChallenge { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Requests.Add(request.RequestUri!);
            Require(request.Method == HttpMethod.Post && request.RequestUri!.GetLeftPart(UriPartial.Authority) == route.Address &&
                request.Headers.Authorization is null &&
                request.Headers.GetValues(CompanionProtocol.HeaderName).Single() == CompanionProtocol.Current.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                request.Headers.GetValues("X-TogetherServer-Capability").Single() == CompanionProtocol.SharedWorldPeersCapability,
                "a fake transport request bypassed fixed POST, pin scope, protocol or capability, or shared a Host bearer token");
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (request.RequestUri.AbsolutePath == $"/api/companion/world-peers/{root.GroupKey}/challenge")
            {
                var input = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(body, 2 * 1024 * 1024);
                Challenges.Add(input);
                var reply = service.PrepareChallenge(root.GroupKey, input, route.CertificateSha256);
                if (RewriteChallenge is not null) reply = RewriteChallenge(reply);
                Replies.Add(reply);
                LargestChallengeReply = Math.Max(LargestChallengeReply,
                    JsonSerializer.SerializeToUtf8Bytes(reply, SharedWorldPeerTrust.Json).Length);
                if (reply.Code == "PeerRosterCatchUpRequired") PendingRosterAcks++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(reply, options: SharedWorldPeerTrust.Json) };
            }
            Require(request.RequestUri.AbsolutePath == $"/api/companion/world-peers/{root.GroupKey}/exchange",
                "an unknown peer transport path was requested");
            var envelope = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(body, 2 * 1024 * 1024);
            Operations.Add(envelope.Request.Operation);
            if (Mode == Fault.Oversized) return new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Metadata) + 1]) };
            if (Mode == Fault.Redirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://203.0.113.99/arbitrary");
                return redirect;
            }
            var bytes = await service.ExchangeAsync(root.GroupKey, envelope.Request, envelope.Payload,
                cancellationToken, route.CertificateSha256);
            if (Mode == Fault.WrongRoot && envelope.Request.Operation == WorldPeerOperations.Metadata)
            {
                var response = WorldPeerProtocol.DecodeEnvelope<WorldPeerMetadataResponse>(bytes, 4 * 1024 * 1024);
                bytes = JsonSerializer.SerializeToUtf8Bytes(response with
                { Page = response.Page with { Root = root with { GroupId = Guid.NewGuid() } } }, SharedWorldPeerTrust.Json);
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
