using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated identities, disposable completed backups and fake HTTPS only.
// This exercises the publication reached after Stop, never Stop/process control,
// a listener, a real world, an installed game, or an actual network receipt.
internal static class WorldPeerCompletedSaveDistributionChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        ReceiptBoundaries();
        await TerminalHeadBoundariesAsync(parent);
        var folder = Path.Combine(parent, "completed-save-distribution-" + Guid.NewGuid().ToString("N"));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var sourceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var observerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var lateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var a = new LocalData(Path.Combine(folder, "returning-owner"));
        using var b = new LocalData(Path.Combine(folder, "successor"));
        using var c = new LocalData(Path.Combine(folder, "online-observer"));
        using var d = new LocalData(Path.Combine(folder, "offline-then-expired"));
        var original = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            WorldId = "CompletedFilesOnly",
            SharedSavesEnabled = true,
            WorldDirectory = Path.Combine(a.RootPath, "original-synthetic-world"),
            GamePort = 2456
        };
        Directory.CreateDirectory(original.WorldDirectory);
        File.WriteAllText(Path.Combine(original.WorldDirectory, "world.dat"), "original remains unchanged");
        Configure(a, "https://192.0.2.160:5131", [original]);
        Configure(b, "https://192.0.2.161:5131", []);
        Configure(c, "https://192.0.2.162:5131", []);
        Configure(d, "https://192.0.2.163:5131", []);
        a.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
        var aRoute = Route(a); var bRoute = Route(b); var cRoute = Route(c); var dRoute = Route(d);
        var clock = new FixedClock(DateTimeOffset.UtcNow.AddHours(-2));
        var pairing = new PairingService(a, clock);
        var invite = pairing.IssueServer(original.Id, false, false, aRoute.Address, aRoute.CertificateSha256, false);
        var bCredential = pairing.Activate(new(original.Id, invite.Code, ServerScope: true))!;
        var cCredential = pairing.Activate(new(original.Id, invite.Code, ServerScope: true))!;
        var dCredential = pairing.Activate(new(original.Id, invite.Code, ServerScope: true))!;
        Bind(pairing, bCredential.DeviceId, sourceKey);
        Bind(pairing, cCredential.DeviceId, observerKey);
        Bind(pairing, dCredential.DeviceId, lateKey);
        var completed = new CompletedFiles(a.RootPath);
        var publisher = new SharedWorldService(a, completed);
        var roster = publisher.PublishRoster(original, pairing.SharedRosterMembers(original.Id));
        pairing.ConfirmSharedRosterPublished(original.Id, roster);
        var root = new SharedWorldPeerRoot(roster.OwnerPublicKey, original.Id, roster.GroupId);
        var initialBytes = Encoding.UTF8.GetBytes("synthetic original completed copy");
        completed.Set(initialBytes);
        var firstResult = publisher.PublishAfterStop(original, Guid.NewGuid());
        Require(firstResult is { Ok: true, Version: not null }, "synthetic original publication failed");
        var first = firstResult.Version!;
        Initialize(a, root, roster, Guid.Empty, owner, bRoute, null);
        Initialize(b, root, roster, bCredential.DeviceId, sourceKey, aRoute, bCredential);
        Initialize(c, root, roster, cCredential.DeviceId, observerKey, aRoute, cCredential);
        Initialize(d, root, roster, dCredential.DeviceId, lateKey, aRoute, dCredential);
        var bVault = Vault(b, bCredential.DeviceId, original.Id);
        WriteCopy(bVault, first, initialBytes);
        var copies = new SharedWorldSeparateCopyStore(b);
        var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(bVault, roster,
            new(root.GroupId, roster.Epoch, roster.Revision, roster.Signature), bCredential.DeviceId, sourceKey,
            bRoute.Address, bRoute.CertificateSha256, new WorldAuthorityStore(b));
        var branch = copies.Declare(offer, bVault, sourceKey, true);
        var localId = SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash);
        var local = new ServerProfile
        {
            Id = localId,
            Kind = GameKinds.Fixture,
            WorldId = first.WorldId,
            GamePort = first.PortableSetup.GamePort,
            WorldDirectory = SharedWorldBranchLiveSource.WorldRoot(b, first, localId),
            SeparateCopySourceProfileId = root.ProfileId,
            SeparateCopyBranchHash = branch.BranchHash,
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        Directory.CreateDirectory(local.WorldDirectory);
        var localSettings = b.LoadSettings(); localSettings.Profiles = [local]; b.SaveSettings(localSettings);
        var finalBytes = new byte[SharedWorldService.ChunkBytes * 2 + 19];
        for (var index = 0; index < finalBytes.Length; index++) finalBytes[index] = (byte)(index % 239);
        File.WriteAllBytes(Path.Combine(local.WorldDirectory, "world.dat"), finalBytes);
        var backups = new WorldBackupService(b, TimeProvider.System, games: new GameServerRegistry(b, true, PortProbeMode.ObserveOnly));
        var backup = backups.Create(local, BackupKinds.Rolling);
        Require(backup is { Ok: true, Backup: not null }, "synthetic final rolling backup failed");
        var publication = copies.PublishAfterStop(branch, local, backup.Backup!.Id, backups);
        Require(publication is { Ok: true, Version: not null } && publication.Version.BackupId == backup.Backup.Id &&
            publication.Version.ParentHash == first.VersionHash, "the completed successor backup was not published with its exact identity");
        var final = publication.Version!;

        // The previous advertisement was Hosting with an older head. No managed
        // run exists in this file-only test; the next projection must close that
        // session at the final completed backup, and keep it immutable afterward.
        var runId = Guid.NewGuid(); var uncertainId = Guid.NewGuid();
        var bStore = Store(b, root, roster);
        bStore.Publish(bCredential.DeviceId, sourceKey, [bRoute],
            [new(runId, branch.BranchHash, 1, "Hosting", first.VersionHash, first.VersionHash, DateTimeOffset.UtcNow.AddMinutes(-5)),
             new(uncertainId, branch.BranchHash, 1, "Interrupted", first.VersionHash, first.VersionHash, DateTimeOffset.UtcNow.AddMinutes(-4))],
            [new(branch.BranchHash, first.VersionHash, first.VersionHash, first.ParentHash, first.Number)], [first.VersionHash]);
        foreach (var (data, id, key, route) in new[]
        {
            (c, cCredential.DeviceId, observerKey, cRoute), (d, dCredential.DeviceId, lateKey, dRoute)
        })
        {
            var contact = Store(data, root, roster).Publish(id, key, [route], [], [], []);
            Require(bStore.Import(contact).Changed, "synthetic recipient contact was not retained");
        }
        HttpClient Offline(string _, IEnumerable<string> __) => throw new HttpRequestException("Synthetic route is offline.");
        using var aFriends = new FriendService(a, Offline);
        using var bFriends = new FriendService(b, Offline);
        using var cFriends = new FriendService(c, Offline);
        using var dFriends = new FriendService(d, Offline);
        using var aService = new WorldPeerService(a, new HostManager(a), aFriends, pairing, Offline);
        using var cService = new WorldPeerService(c, new HostManager(c), cFriends, new PairingService(c), Offline);
        using var dService = new WorldPeerService(d, new HostManager(d), dFriends, new PairingService(d), Offline);
        using var aHandler = new Handler(aService, root, aRoute) { DropFirstCompletedSeed = true };
        using var cHandler = new Handler(cService, root, cRoute);
        using var dHandler = new Handler(dService, root, dRoute);
        var lateOnline = false;
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            var route = endpoint.TrimEnd('/') == aRoute.Address ? aRoute : endpoint.TrimEnd('/') == cRoute.Address ? cRoute : dRoute;
            Require(endpoint.TrimEnd('/') == route.Address && pins.SequenceEqual([route.CertificateSha256]),
                "distribution changed a recipient origin or TLS pin");
            if (route == dRoute && !lateOnline) return Offline(endpoint, pins);
            var handler = route == aRoute ? aHandler : route == cRoute ? cHandler : dHandler;
            return new(handler, disposeHandler: false) { BaseAddress = new Uri(route.Address + "/") };
        }
        using var source = new WorldPeerService(b, new HostManager(b), bFriends, new PairingService(b), Client);
        for (var pass = 0; pass < 8; pass++) await source.SyncNowAsync(default);
        var ownerVault = SharedWorldBranchVault.Root(a, branch);
        var observerVault = SharedWorldBranchVault.Root(c, branch);
        VerifyCopy(ownerVault, final, finalBytes);
        VerifyCopy(observerVault, final, finalBytes);
        var ownerReceipt = Receipts(b, root).Single(item => item.DeviceId == Guid.Empty && item.VersionHash == final.VersionHash);
        var observerReceipt = Receipts(b, root).Single(item => item.DeviceId == cCredential.DeviceId && item.VersionHash == final.VersionHash);
        var ownerSignatureValid = SharedWorldReceiptTrust.VerifyPeerOwner(ownerReceipt, root);
        var canonicalOwnerRejected = !SharedWorldReceiptTrust.Verify(ownerReceipt, root.OwnerPublicKey);
        var observerSignatureValid = SharedWorldReceiptTrust.Verify(observerReceipt, Public(observerKey));
        var sameReceiptRecovered = aHandler.DroppedReceiptId == ownerReceipt.ReceiptId;
        var noSeedRetransmission = aHandler.SeedChunksAfterDroppedCompletion == 0;
        Require(ownerSignatureValid && canonicalOwnerRejected && observerSignatureValid && sameReceiptRecovered && noSeedRetransmission,
            $"Synthetic receipt retry checks: ownerSignatureValid={ownerSignatureValid}, canonicalOwnerRejected={canonicalOwnerRejected}, " +
            $"observerSignatureValid={observerSignatureValid}, sameReceiptRecovered={sameReceiptRecovered}, noSeedRetransmission={noSeedRetransmission}.");
        Require(!Directory.Exists(SharedWorldBranchVault.Root(d, branch)) &&
            !Receipts(b, root).Any(item => item.DeviceId == dCredential.DeviceId),
            "an offline recipient gained a completed pointer or receipt");
        var terminal = bStore.Project().Sessions.Single(item => item.Session.SessionId == runId).Session;
        Require(terminal.State == "Stopped" && terminal.Revision == 2 && terminal.EndedUtc is not null &&
            terminal.HeadVersionHash == final.VersionHash && terminal.BaseVersionHash == first.VersionHash,
            "the stopped successor session still named its pre-Stop save instead of its exact final backup");
        Require(bStore.Project().Sessions.Single(item => item.Session.SessionId == uncertainId).Session is
        { State: "Stopped", EndedUtc: not null } uncertain && uncertain.HeadVersionHash == final.VersionHash,
            "an uncertain managed session was mistaken for an already closed record after completed Stop publication");
        Require(File.ReadAllText(Path.Combine(original.WorldDirectory, "world.dat")) == "original remains unchanged" &&
            publisher.Status(original).Latest?.VersionHash == first.VersionHash,
            "returning-owner receipt or storage changed the original world/publication");

        // Signed current revocation and a deadline already reached at real UTC
        // deny payloads even when the old signed contacts remain cached/online.
        Require(pairing.Revoke(cCredential.DeviceId).Ok &&
            pairing.SetAccessExpiry(dCredential.DeviceId, new(AccessExpiresUtc: clock.GetUtcNow().AddHours(1))).Ok,
            "synthetic revoke/expiry setup failed");
        var current = publisher.PublishRoster(original, pairing.SharedRosterMembers(original.Id));
        pairing.ConfirmSharedRosterPublished(original.Id, current);
        var deadline = current.Members.Single(item => item.DeviceId == dCredential.DeviceId).AccessExpiresUtc!.Value;
        Require(SharedWorldPeerTrust.AccessFromRoster(root, current, dCredential.DeviceId, Public(lateKey), new FixedClock(deadline)) is null &&
            SharedWorldPeerTrust.AccessFromRoster(root, current, dCredential.DeviceId, Public(lateKey), new FixedClock(deadline.AddTicks(-1)))?.Receive == true,
            "a peer's exact access deadline did not fail closed at UTC equality");
        Initialize(b, root, current, bCredential.DeviceId, sourceKey, aRoute, bCredential);
        Initialize(c, root, current, cCredential.DeviceId, observerKey, aRoute, cCredential);
        Initialize(d, root, current, dCredential.DeviceId, lateKey, aRoute, dCredential);
        lateOnline = true;
        var changedBytes = finalBytes.ToArray(); changedBytes[0] ^= 0x7F;
        File.WriteAllBytes(Path.Combine(local.WorldDirectory, "world.dat"), changedBytes);
        var changedBackup = backups.Create(local, BackupKinds.Rolling);
        Require(changedBackup is { Ok: true, Backup: not null }, "synthetic next backup failed");
        var changedResult = copies.PublishAfterStop(branch, local, changedBackup.Backup!.Id, backups);
        Require(changedResult is { Ok: true, Version: not null }, "synthetic next completed publication failed");
        var changed = changedResult.Version!;
        var receivedOwnLane = FriendLink.ReadReceivedLatest(SharedWorldBranchVault.Root(b, branch));
        var duplicateOwnLanePresent = receivedOwnLane?.VersionHash == final.VersionHash &&
            branch.Offer.Proposal.ProposerDeviceId == bCredential.DeviceId && branch.CandidatePublicKey == Public(sourceKey);
        var currentHeader = SharedWorldPeerTrust.Sign(new(1, root, bCredential.DeviceId, Public(sourceKey), 1, null,
            [], [], [new(branch.BranchHash, first.VersionHash, changed.VersionHash, changed.ParentHash, changed.Number)],
            [], DateTimeOffset.UtcNow, "", ""), sourceKey);
        var duplicateHeaderRejected = false;
        try
        {
            _ = SharedWorldPeerTrust.Sign(currentHeader with
            {
                Branches = [.. currentHeader.Branches, new(branch.BranchHash, first.VersionHash,
                    final.VersionHash, final.ParentHash, final.Number)],
                AnnouncementHash = "",
                Signature = ""
            }, sourceKey);
        }
        catch (InvalidDataException) { duplicateHeaderRejected = true; }
        Require(duplicateOwnLanePresent && SharedWorldPeerTrust.VerifySignature(currentHeader) && duplicateHeaderRejected,
            $"Synthetic previous-projection diagnosis: receivedOwnLanePresent={duplicateOwnLanePresent}, " +
            $"duplicateSignedHeaderRejected={duplicateHeaderRejected}.");
        var revokedRequests = cHandler.SeedRequests; var expiredRequests = dHandler.SeedRequests;
        var publishedFile = Path.Combine(copies.PublishedRoot(branch), changed.VersionHash, SharedWorldService.PayloadDirectory, "world.dat");
        File.Delete(publishedFile); // Disposable signed metadata alone cannot deliver bytes.
        for (var pass = 0; pass < 4; pass++) await source.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(ownerVault)?.VersionHash == final.VersionHash &&
            !Receipts(b, root).Any(item => item.VersionHash == changed.VersionHash),
            "a published manifest with missing payload advanced a received pointer or receipt");
        File.WriteAllBytes(publishedFile, changedBytes);
        var withdrawn = false;
        aHandler.BeforeExchange = async request =>
        {
            if (withdrawn || request.Request.Operation != WorldPeerOperations.Seed ||
                request.Request.VersionHash != changed.VersionHash ||
                request.Request.ChunkOffset != SharedWorldService.ChunkBytes * 2L) return;
            withdrawn = true;
            Require((await aService.SetReceivingAsync(root.GroupKey, false, default)).Ok,
                "the original owner could not withdraw receiving consent");
        };
        for (var pass = 0; pass < 6; pass++) await source.SyncNowAsync(default);
        var ownerHeadKept = FriendLink.ReadReceivedLatest(ownerVault)?.VersionHash == final.VersionHash;
        var observerHeadKept = FriendLink.ReadReceivedLatest(observerVault)?.VersionHash == final.VersionHash;
        var changedReceiptAbsent = !Receipts(b, root).Any(item => item.VersionHash == changed.VersionHash);
        var revokedPeerNotSeeded = cHandler.SeedRequests == revokedRequests;
        var expiredPeerNotSeeded = dHandler.SeedRequests == expiredRequests;
        var revokedAccessDenied = SharedWorldPeerTrust.AccessFromRoster(root, current, cCredential.DeviceId, Public(observerKey)) is null;
        var expiredAccessDenied = SharedWorldPeerTrust.AccessFromRoster(root, current, dCredential.DeviceId, Public(lateKey)) is null;
        Require(withdrawn && ownerHeadKept && observerHeadKept && changedReceiptAbsent && revokedPeerNotSeeded &&
            expiredPeerNotSeeded && revokedAccessDenied && expiredAccessDenied,
            $"Synthetic current-access checks: consentWithdrawn={withdrawn}, ownerHeadKept={ownerHeadKept}, " +
            $"observerHeadKept={observerHeadKept}, changedReceiptAbsent={changedReceiptAbsent}, revokedPeerNotSeeded={revokedPeerNotSeeded}, " +
            $"expiredPeerNotSeeded={expiredPeerNotSeeded}, revokedAccessDenied={revokedAccessDenied}, expiredAccessDenied={expiredAccessDenied}.");
        var announcements = bStore.ReadPage().Announcements;
        var projected = announcements.Where(item => item.DeviceId == bCredential.DeviceId).MaxBy(item => item.Revision)!;
        Require(projected.Branches.Count(item => item.BranchHash == branch.BranchHash) == 1 &&
            projected.Branches.Any(item => item.BranchHash == branch.BranchHash && item.HeadVersionHash == changed.VersionHash) &&
            announcements.Any(item => item.DeviceId == bCredential.DeviceId &&
                item.Branches.Any(head => head.HeadVersionHash == final.VersionHash)) &&
            FriendLink.ReadReceivedLatest(SharedWorldBranchVault.Root(b, branch))?.VersionHash == final.VersionHash,
            "deduplicating the current advertisement changed received own-lane copies or older signed journal claims");
        Require(bStore.Project().Sessions.Single(item => item.Session.SessionId == runId).Session == terminal,
            "a later backup rewrote an already closed session's final save");
    }

    private static async Task TerminalHeadBoundariesAsync(string parent)
    {
        foreach (var scenario in new[] { "MissingPointer", "SameNumberFork", "LargerNumberFork", "NullHead" })
        {
            using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var data = new LocalData(Path.Combine(parent, "terminal-head-" + scenario + "-" + Guid.NewGuid().ToString("N")));
            data.SaveSettings(new HostSettings { Profiles = [] });
            var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
            var memberId = Guid.NewGuid();
            var draft = new SharedWorldRoster(3, root.GroupId, root.ProfileId, 1, 1, true, root.OwnerPublicKey,
                [new(memberId, Public(member), new(Receive: true, EligibleHost: true, Reshare: true), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: root.OwnerPublicKey);
            var roster = draft with
            {
                Signature = Convert.ToBase64String(owner.SignData(
                    SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
            var route = new SharedWorldPeerRoute("https://192.0.2.170:5131", new string('A', 64));
            Initialize(data, root, roster, memberId, member, route, null);
            var firstBytes = Encoding.UTF8.GetBytes("synthetic retained branch base");
            var first = SharedWorldService.SignVersion(new(4, root.GroupId, 1, null, root.ProfileId,
                GameKinds.Fixture, "TerminalHeadFilesOnly", DateTimeOffset.UtcNow.AddDays(-5),
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false, "Unknown", [], []),
                [Fact(firstBytes)], "", "", ""), owner);
            var received = Vault(data, memberId, root.ProfileId);
            WriteCopy(received, first, firstBytes);
            var copies = new SharedWorldSeparateCopyStore(data);
            var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(received, roster,
                new(root.GroupId, roster.Epoch, roster.Revision, roster.Signature), memberId, member,
                route.Address, route.CertificateSha256, new WorldAuthorityStore(data));
            var branch = copies.Declare(offer, received, member, true);
            using var hosting = ECDsa.Create();
            hosting.ImportPkcs8PrivateKey(data.LoadProtected(WorldAuthorityStore.HostingKeyName(root.ProfileId))!, out _);
            var priorBytes = Encoding.UTF8.GetBytes("synthetic last announced session head");
            var prior = SharedWorldService.SignVersion(first with
            {
                Number = 2,
                ParentHash = first.VersionHash,
                BackupId = Guid.NewGuid(),
                Files = [Fact(priorBytes)]
            }, hosting);
            var published = copies.PublishedRoot(branch);
            WriteCopy(published, first, firstBytes);
            WriteCopy(published, prior, priorBytes);
            var runId = Guid.NewGuid();
            var store = Store(data, root, roster);
            store.Publish(memberId, member, [],
                [new(runId, branch.BranchHash, 1, "Hosting", first.VersionHash,
                    scenario == "NullHead" ? null : prior.VersionHash, DateTimeOffset.UtcNow.AddMinutes(-5))],
                [new(branch.BranchHash, first.VersionHash, prior.VersionHash, prior.ParentHash, prior.Number)], []);
            if (scenario == "MissingPointer") File.Delete(Path.Combine(published, "latest.json"));
            else if (scenario is "SameNumberFork" or "LargerNumberFork")
            {
                var forkBytes = Encoding.UTF8.GetBytes("synthetic signed competing progress");
                var fork = SharedWorldService.SignVersion(prior with
                {
                    BackupId = Guid.NewGuid(),
                    Files = [Fact(forkBytes)],
                    CreatedUtc = DateTimeOffset.UtcNow.AddDays(1)
                }, hosting);
                WriteCopy(published, fork, forkBytes);
                if (scenario == "LargerNumberFork")
                {
                    var later = SharedWorldService.SignVersion(fork with
                    {
                        Number = 3,
                        ParentHash = fork.VersionHash,
                        BackupId = Guid.NewGuid()
                    }, hosting);
                    WriteCopy(published, later, forkBytes);
                }
            }
            HttpClient Offline(string _, IEnumerable<string> __) => throw new HttpRequestException("Synthetic route is offline.");
            using var friends = new FriendService(data, Offline);
            using var service = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data), Offline);
            await service.SyncNowAsync(default);
            var terminal = store.Project().Sessions.Single(item => item.Session.SessionId == runId).Session;
            Require(terminal.State == "Stopped" && terminal.Revision == 2 && terminal.EndedUtc is not null &&
                terminal.HeadVersionHash == prior.VersionHash && terminal.BaseVersionHash == first.VersionHash,
                "terminal session head regressed to a retained base or advanced onto an unproved signed fork: " + scenario);
            Require(data.LoadRuns().Count == 0 && data.LoadSettings().Profiles.Count == 0 &&
                !data.LoadSettings().CompanionListeningEnabled,
                "terminal metadata checks created a run/profile or enabled incoming connections");
        }
    }

    private static void ReceiptBoundaries()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var draft = new SharedWorldReceipt(SharedWorldReceiptTrust.PeerOwnerSchema, root.GroupId, root.ProfileId,
            new string('A', 64), Guid.Empty, 1, 1, Guid.NewGuid(), "");
        var receipt = draft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldReceiptTrust.PeerOwnerBasis(draft, root), HashAlgorithmName.SHA256))
        };
        Require(SharedWorldReceiptTrust.VerifyPeerOwner(receipt, root) &&
            !SharedWorldReceiptTrust.Verify(receipt, root.OwnerPublicKey) &&
            !WorldPeerOwnerTestReceiptTrust.Verify(receipt, root) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt with { Schema = 1 }, root) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt with { Schema = 2 }, root) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt with { DeviceId = Guid.NewGuid() }, root) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt with { VersionHash = new string('B', 64) }, root) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt, root with { OwnerPublicKey = Public(stranger) }) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt, root with { ProfileId = Guid.NewGuid() }) &&
            !SharedWorldReceiptTrust.VerifyPeerOwner(receipt, root with { GroupId = Guid.NewGuid() }),
            "production owner receipts crossed a key/world/version/schema/member boundary");
        var privateDraft = draft with { Schema = 2 };
        var privateReceipt = privateDraft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldReceiptTrust.Basis(privateDraft), HashAlgorithmName.SHA256))
        };
        Require(!SharedWorldReceiptTrust.VerifyPeerOwner(privateReceipt with { Schema = SharedWorldReceiptTrust.PeerOwnerSchema }, root),
            "changing a private-test receipt's schema promoted it into a production receipt");
    }

    private static void Configure(LocalData data, string endpoint, List<ServerProfile> profiles) => data.SaveSettings(new()
    { CompanionEndpoint = endpoint, CompanionBindAddress = "0.0.0.0", CompanionListeningEnabled = true, Profiles = profiles });
    private static SharedWorldPeerRoute Route(LocalData data)
    {
        var endpoint = data.LoadSettings().CompanionEndpoint;
        using var certificate = new HostIdentity(data).Ensure(endpoint);
        return new(endpoint, HostIdentity.Fingerprint(certificate));
    }
    private static void Bind(PairingService pairing, Guid id, ECDsa key)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Require(pairing.BindSharedWorldKey(id, new(nonce, Public(key), Convert.ToBase64String(key.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(id, nonce, Public(key)), HashAlgorithmName.SHA256)))).Ok,
            "synthetic recipient key enrollment failed");
    }
    private static void Initialize(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster,
        Guid id, ECDsa key, SharedWorldPeerRoute bootstrap, PairingCredential? credential)
    {
        if (id != Guid.Empty) data.SaveProtected($"shared-world-pc-signing-{id:N}.protected", key.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(data).Append(roster, root.OwnerPublicKey);
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = root, Roster = roster, DeviceId = id,
                Name = "Synthetic completed-copy distribution", ReceiveConsented = true, SharingEnabled = true, Bootstrap = bootstrap } }
        }, Json));
        if (credential is null) return;
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DeviceId = id,
            Endpoint = bootstrap.Address,
            Fingerprint = bootstrap.CertificateSha256,
            Credential = credential.Credential,
            CredentialExpiresUtc = credential.ExpiresUtc,
            ConsentedSharedWorldProfiles = [root.ProfileId],
            ApprovedSharedWorldGroups = new() { [root.ProfileId] = root.GroupId },
            SharedWorldSigningKeys = new() { [root.ProfileId] = root.OwnerPublicKey },
            SharedRosterFloors = new() { [root.ProfileId] = new(root.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
        }, Json));
        data.SaveProtected($"shared-world-roster-{id:N}-{root.ProfileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
    }
    private static SharedWorldPeerStore Store(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster) =>
        new(data, root, new SharedWorldPeerTrust(root, (id, key) => SharedWorldPeerTrust.AccessFromRoster(root, roster, id, key)));
    private static SharedWorldReceipt[] Receipts(LocalData data, SharedWorldPeerRoot root) =>
        data.LoadProtected($"world-peer-receipts-{root.GroupKey}.protected") is { } bytes
            ? JsonSerializer.Deserialize<SharedWorldReceipt[]>(bytes, Json)! : [];
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Vault(LocalData data, Guid id, Guid profile) => Path.Combine(data.RootPath,
        "received-shared-worlds", id.ToString("N"), profile.ToString("N"));
    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    private static void WriteCopy(string vault, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(vault, version.VersionHash); Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private static void VerifyCopy(string vault, SharedWorldVersion version, byte[] bytes) => Require(
        FriendLink.ReadReceivedLatest(vault)?.VersionHash == version.VersionHash &&
        File.ReadAllBytes(Path.Combine(vault, version.VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(bytes),
        "a recipient did not retain the final completed backup with matching bytes/hash");
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class CompletedFiles(string root) : ISharedWorldCaptureAdapter
    {
        private readonly string path = Path.Combine(root, "completed-synthetic-backup");
        internal void Set(byte[] bytes) { Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, "world.dat"), bytes); }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId) => new(1,
            SharedWorldCaptureKinds.PostStopBackup, backupId, DateTimeOffset.UtcNow,
            [Fact(File.ReadAllBytes(Path.Combine(path, "world.dat")))], path,
            new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "SyntheticFilesOnly", [], [], profile.GamePort));
    }
    private sealed class Handler(WorldPeerService service, SharedWorldPeerRoot root, SharedWorldPeerRoute route) : HttpMessageHandler
    {
        internal bool DropFirstCompletedSeed { get; set; }
        internal Guid? DroppedReceiptId { get; private set; }
        private string? droppedVersionHash;
        internal int SeedChunksAfterDroppedCompletion { get; private set; }
        internal int SeedRequests { get; private set; }
        internal Func<WorldPeerExchangeRequest, Task>? BeforeExchange { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
            {
                var hello = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024);
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(service.PrepareChallenge(root.GroupKey, hello,
                    route.CertificateSha256), options: SharedWorldPeerTrust.Json)
                };
            }
            var exchange = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
            if (exchange.Request.Operation == WorldPeerOperations.Seed)
            {
                SeedRequests++;
                // Other catalog entries may legitimately be seeded afterward.
                // Only another chunk for this exact completed save is a retry.
                if (exchange.Request.VersionHash == droppedVersionHash && exchange.Request.FileIndex is not null)
                    SeedChunksAfterDroppedCompletion++;
            }
            if (BeforeExchange is not null) await BeforeExchange(exchange);
            var result = await service.ExchangeAsync(root.GroupKey, exchange.Request, exchange.Payload, token, route.CertificateSha256);
            if (DropFirstCompletedSeed && exchange.Request.Operation == WorldPeerOperations.Seed &&
                WorldPeerProtocol.DecodeEnvelope<WorldPeerSeedResponse>(result, WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Seed)) is
                { Code: "SeedComplete", Receipt: { } receipt })
            {
                DropFirstCompletedSeed = false; DroppedReceiptId = receipt.ReceiptId;
                droppedVersionHash = receipt.VersionHash;
                throw new HttpRequestException("Synthetic completed-seed response was lost.");
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(result) };
        }
    }
}
