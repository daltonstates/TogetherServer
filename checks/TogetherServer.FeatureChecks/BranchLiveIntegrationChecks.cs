using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fabricated signed evidence, disposable files and protected records only.
// No app, game, process, console, listener, browser or native dialog is started.
// These checks cannot accept an installed game; root owns execution/registration.
internal static class BranchLiveIntegrationChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using var f = new Evidence(Path.Combine(root, "branch-live-integration-" + Guid.NewGuid().ToString("N")));
        ArtifactBoundaries(f);
        CurrentMembershipAndReceipts(f);
        ScopeAndDiscovery(f);
        AttemptSelection(f);
        PrivateCopies(f);
        await NominationAndInstallationRefusalAsync(f);
        BranchFreshness(f);
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static bool Refuses(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex) || ex is OperationCanceledException) { return true; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private sealed class Evidence : IDisposable
    {
        internal LocalData Data { get; }
        internal ECDsa Owner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Source { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Hosting { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Receiver { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal Guid SourceId { get; } = Guid.NewGuid();
        internal Guid ReceiverId { get; } = Guid.NewGuid();
        internal SharedWorldPeerRoot Root { get; }
        internal SharedWorldRoster Roster { get; }
        internal SharedWorldVersion Base { get; }
        internal WorldSeparateCopyBranch Branch { get; }
        internal ServerProfile Profile { get; }
        internal byte[] Bytes { get; } = Encoding.UTF8.GetBytes("private branch test bytes; never a playable world");
        internal LiveSaveTestArtifact Artifact { get; }
        internal SharedWorldReceipt Receipt { get; }
        internal LiveSaveAcceptanceRecord Record => new(Artifact.TestId, Profile.Id, Artifact.OperationId,
            Artifact.Scope, Now.AddMinutes(-1), "ArtifactReady", "LiveSaveTestArtifactReady", Artifact);
        internal LiveSaveAcceptanceRecord Accepted => Record with
        {
            State = "Accepted",
            Receipt = Receipt,
            TransferUtc = Now.AddSeconds(2),
            LoadUtc = Now.AddSeconds(3),
            ChangeUtc = Now.AddSeconds(4),
            RestartUtc = Now.AddSeconds(5)
        };

        internal Evidence(string path)
        {
            Data = new(path);
            Root = new(Public(Owner), Guid.NewGuid(), Guid.NewGuid());
            Roster = SignRoster(new(2, Root.GroupId, Root.ProfileId, 1, 1, true, Root.OwnerPublicKey,
                [new(SourceId, Public(Source), new(Receive: true, EligibleHost: true), false, Now.AddDays(1)),
                 new(ReceiverId, Public(Receiver), new(Receive: true), false, Now.AddDays(2))], ""));
            Base = SharedWorldService.SignVersion(new(4, Root.GroupId, 1, null, Root.ProfileId,
                GameKinds.Valheim, "BranchSourceFilesOnly", Now.AddYears(-20), SharedWorldCaptureKinds.PostStopBackup,
                Guid.NewGuid(), new(5182, false, "Unknown", [], []), [Fact([1, 2, 3])], "", "", ""), Owner);
            Data.SaveSettings(new() { CompanionPort = 5181, CompanionListeningEnabled = false, Profiles = [] });
            Data.SaveProtected($"shared-world-pc-signing-{SourceId:N}.protected", Source.ExportPkcs8PrivateKey());
            Data.SaveProtected(WorldAuthorityStore.HostingKeyName(Root.ProfileId), Hosting.ExportPkcs8PrivateKey());
            var vault = Path.Combine(Data.RootPath, "synthetic-source-vault");
            WriteCopy(vault, Base, [1, 2, 3]);
            Branch = new SharedWorldSeparateCopyStore(Data).Declare(Offer(Base), vault, Source, true);
            var localId = SharedWorldBranchLiveSource.LocalProfileId(Branch.BranchHash);
            Profile = new()
            {
                Id = localId,
                Kind = Base.Game,
                WorldId = Base.WorldId,
                WorldSource = "Existing",
                Name = "Files only recovery",
                ServerName = "Files only recovery",
                GamePort = 5182,
                WorldDirectory = SharedWorldBranchLiveSource.WorldRoot(Data, Base, localId),
                ExecutablePath = Path.Combine(Data.RootPath, "missing-synthetic-server.bin"),
                SharedSavesEnabled = false,
                SeparateCopySourceProfileId = Root.ProfileId,
                SeparateCopyBranchHash = Branch.BranchHash,
                CrashRecovery = new() { Enabled = false }
            };
            Directory.CreateDirectory(Profile.WorldDirectory);
            File.WriteAllBytes(Path.Combine(Profile.WorldDirectory, "world.db"), [1, 2, 3]);
            Data.SaveSettings(new() { CompanionPort = 5181, CompanionListeningEnabled = false, Profiles = [Profile] });
            var setup = new TakeoverLocalSetup(Profile.ExecutablePath, Base.PortableSetup.GameVersion,
                Base.PortableSetup.AddOns, true, 5181, Profile.GamePort);
            var state = new SeparateCopyHostState(1, Root.ProfileId, localId, Root.GroupId,
                Branch.BranchHash, Base.VersionHash, Profile.WorldDirectory, Ready: true,
                SetupHash: SharedWorldBranchLiveSource.SetupHash(Profile, setup));
            Data.SaveProtected(SharedWorldBranchLiveSource.StateName(localId), JsonSerializer.SerializeToUtf8Bytes(state));
            var scope = SharedWorldLiveSaveAdapters.BindInstallation(localId, Base.Game, Data.RootPath,
                Hash([4]), Hash([5]), false) with
            { BinaryVersion = "1.2.3.4" };
            var id = Guid.NewGuid();
            var version = SharedWorldService.SignAcceptanceTestVersion(new(5, Root.GroupId, 1, null,
                Root.ProfileId, Base.Game, Base.WorldId, Now, LiveSaveAcceptanceTrust.TestCaptureKind,
                id, Base.PortableSetup, [Fact(Bytes)], "", "", ""), Hosting);
            Artifact = SignArtifact(new(2, true, id, scope, Guid.NewGuid(), SourceId, Now, Now.AddSeconds(1),
                LiveSaveAcceptanceTrust.PayloadHash(version.Files), Bytes.LongLength, SharedWorldService.ChunkBytes,
                1, version, "", Branch));
            Receipt = SignReceipt(new(1, Root.GroupId, Root.ProfileId, version.VersionHash, ReceiverId,
                Roster.Epoch, Roster.Revision, Guid.NewGuid(), ""), Receiver);
        }

        internal WorldAuthorityOffer Offer(SharedWorldVersion version)
        {
            var proposal = new WorldAuthorityProposal(2, Root.GroupId, Root.ProfileId, 1, null,
                WorldAuthorityTrust.RosterHash(Roster), version.VersionHash, Public(Hosting),
                "https://127.0.0.1:5181", "Quorum", SourceId, Public(Source), "");
            var binding = new WorldSuccessorBinding(SourceId, Public(Source), Public(Hosting), "");
            proposal = proposal with
            {
                SuccessorBinding = binding with
                {
                    Signature = Convert.ToBase64String(
                Source.SignData(WorldAuthorityTrust.BindingBasis(proposal, binding), HashAlgorithmName.SHA256))
                }
            };
            proposal = proposal with
            {
                Signature = Convert.ToBase64String(Source.SignData(
                WorldAuthorityTrust.ProposalBasis(proposal), HashAlgorithmName.SHA256))
            };
            var receipt = SignReceipt(new(1, Root.GroupId, Root.ProfileId, version.VersionHash,
                SourceId, Roster.Epoch, Roster.Revision, Guid.NewGuid(), ""), Source);
            var pin = new string('A', 64);
            var basis = Encoding.UTF8.GetBytes("TogetherServer authority candidate TLS pin v1\n" +
                WorldAuthorityTrust.ProposalHash(proposal) + "\n" + pin);
            return new(proposal, Roster, version, receipt, [], pin,
                Convert.ToBase64String(Source.SignData(basis, HashAlgorithmName.SHA256)));
        }

        internal LiveSaveTestArtifact SignArtifact(LiveSaveTestArtifact artifact) => artifact with
        { Signature = Convert.ToBase64String(Hosting.SignData(LiveSaveAcceptanceTrust.ArtifactBasis(artifact), HashAlgorithmName.SHA256)) };
        internal SharedWorldRoster SignRoster(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(Owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
        internal SharedWorldReceipt SignReceipt(SharedWorldReceipt receipt, ECDsa key) => receipt with
        { Signature = Convert.ToBase64String(key.SignData(SharedWorldReceiptTrust.Basis(receipt), HashAlgorithmName.SHA256)) };
        internal SharedWorldVersion Live(Guid id, long number, SharedWorldVersion parent, byte[] bytes,
            DateTimeOffset? saved = null) => SharedWorldService.SignVersion(new(5, Root.GroupId, number,
                parent.VersionHash, Root.ProfileId, Base.Game, Base.WorldId, saved ?? Now,
                SharedWorldCaptureKinds.LiveSave, id, Base.PortableSetup, [Fact(bytes)], "", "", ""), Hosting);
        public void Dispose() { Receiver.Dispose(); Hosting.Dispose(); Source.Dispose(); Owner.Dispose(); Data.Dispose(); }
    }

    private static SharedWorldFile Fact(byte[] bytes) => new("world.db", bytes.LongLength, Hash(bytes));

    private static void ArtifactBoundaries(Evidence f)
    {
        Require(LiveSaveAcceptanceTrust.VerifyArtifact(f.Artifact) &&
            WorldPeerService.BranchLiveTestHasTrustedRoot(f.Artifact, f.Root, []) &&
            f.Artifact.Scope.ProfileId == f.Profile.Id && f.Profile.Id != f.Root.ProfileId &&
            f.Artifact.Version.ProfileId == f.Root.ProfileId && f.Artifact.SourceDeviceId == f.SourceId &&
            f.Artifact.Version.SigningPublicKey != Public(f.Source),
            "schema 2 confused the local installation, original world, member or separate hosting key");
        var changedVersion = SharedWorldService.SignAcceptanceTestVersion(f.Artifact.Version with { WorldId = "another-world" }, f.Hosting);
        var changedRoot = SharedWorldService.SignAcceptanceTestVersion(f.Artifact.Version with { ProfileId = Guid.NewGuid() }, f.Hosting);
        var changedGame = SharedWorldService.SignAcceptanceTestVersion(f.Artifact.Version with { Game = GameKinds.Terraria }, f.Hosting);
        var changedKey = SharedWorldService.SignAcceptanceTestVersion(f.Artifact.Version, f.Source);
        var fork = SharedWorldSeparateCopyStore.Sign(f.Branch.Offer, f.Source);
        foreach (var artifact in new[]
        {
            f.Artifact with { Schema = 1 }, f.Artifact with { Schema = 3 }, f.Artifact with { Branch = null },
            f.Artifact with { Branch = f.Branch with { BranchHash = Hash([9]) } }, f.Artifact with { Branch = fork },
            f.Artifact with { SourceDeviceId = Guid.Empty }, f.Artifact with { SourceDeviceId = f.ReceiverId },
            f.Artifact with { Scope = f.Artifact.Scope with { ProfileId = f.Root.ProfileId } },
            f.Artifact with { Scope = f.Artifact.Scope with { ProfileId = Guid.NewGuid() } },
            f.Artifact with { Scope = f.Artifact.Scope with { Synthetic = true } },
            f.Artifact with { Version = changedVersion }, f.Artifact with { Version = changedRoot },
            f.Artifact with { Version = changedGame }, f.Artifact with { Version = changedKey }
        }) Require(!LiveSaveAcceptanceTrust.VerifyArtifact(f.SignArtifact(artifact)),
            "a re-signed wrong root/local profile/branch/key/member/world/game or fixture escaped schema-2 identity checks");
        Require(!SharedWorldService.VerifySignature(f.Artifact.Version) &&
            !SharedWorldService.VerifySignature(f.Artifact.Version with { CaptureKind = SharedWorldCaptureKinds.LiveSave }) &&
            SharedWorldFreshness.SelectNewestCompleteVerified([new(f.Artifact.Version, true, true)],
                f.Root.ProfileId, f.Root.GroupId) is null,
            "a private branch test became a playable or freshness version");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fakeBase = SharedWorldService.SignVersion(f.Base with { BackupId = Guid.NewGuid() }, stranger);
        var fakeBranch = SharedWorldSeparateCopyStore.Sign(f.Offer(fakeBase), f.Source);
        var fake = f.SignArtifact(f.Artifact with
        {
            Branch = fakeBranch,
            Scope = f.Artifact.Scope with { ProfileId = SharedWorldBranchLiveSource.LocalProfileId(fakeBranch.BranchHash) }
        });
        Require(LiveSaveAcceptanceTrust.VerifyArtifact(fake) &&
            !WorldPeerService.BranchLiveTestHasTrustedRoot(fake, f.Root, []) &&
            !WorldPeerService.BranchLiveTestHasTrustedRoot(f.Artifact, f.Root with { ProfileId = Guid.NewGuid() }, []) &&
            !WorldPeerService.BranchLiveTestHasTrustedRoot(f.Artifact, f.Root with { OwnerPublicKey = Public(stranger) }, []),
            "a self-signed source base or another pinned root gained private-test authorization");
        var aliasedBase = SharedWorldService.SignVersion(f.Base with { ProfileId = Guid.NewGuid() }, f.Owner);
        var aliasedBranch = SharedWorldSeparateCopyStore.Sign(f.Offer(aliasedBase), f.Source);
        var aliasedVersion = SharedWorldService.SignAcceptanceTestVersion(f.Artifact.Version with
        { ProfileId = aliasedBase.ProfileId }, f.Hosting);
        var aliasedArtifact = f.SignArtifact(f.Artifact with
        {
            Version = aliasedVersion,
            Branch = aliasedBranch,
            Scope = f.Artifact.Scope with { ProfileId = SharedWorldBranchLiveSource.LocalProfileId(aliasedBranch.BranchHash) }
        });
        Require(!LiveSaveAcceptanceTrust.VerifyArtifact(aliasedArtifact),
            "a signed proposal for another roster profile was aliased to this test's root world");
    }

    private static void CurrentMembershipAndReceipts(Evidence f)
    {
        Require(LiveSaveAcceptanceTrust.SourceMatchesRoster(f.Artifact, f.Roster, f.Now) &&
            LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, f.Receipt, f.Roster, f.Now),
            "the signed branch proposer and a distinct exact root receipt were refused");
        var ownerReceipt = f.SignReceipt(f.Receipt with { Schema = 2, DeviceId = Guid.Empty }, f.Owner);
        Require(LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, ownerReceipt, f.Roster, f.Now) &&
            !SharedWorldReceiptTrust.Verify(ownerReceipt, f.Root.OwnerPublicKey),
            "the isolated owner test receipt failed or leaked into canonical receipt trust");
        var self = f.SignReceipt(f.Receipt with { DeviceId = f.SourceId }, f.Source);
        Require(!LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, self, f.Roster, f.Now) &&
            !LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact,
                f.SignReceipt(f.Receipt with { ProfileId = f.Profile.Id }, f.Receiver), f.Roster, f.Now),
            "a source self-receipt or local-ID receipt was mistaken for a distinct root-world transfer");
        using var changedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var source in new[]
        {
            f.Roster.Members[0] with { Revoked = true },
            f.Roster.Members[0] with { Grants = new(Receive: true) },
            f.Roster.Members[0] with { Grants = new(EligibleHost: true) },
            f.Roster.Members[0] with { PublicKey = Public(changedKey) },
            f.Roster.Members[0] with { AccessExpiresUtc = f.Now }
        })
        {
            var roster = f.SignRoster(f.Roster with { Epoch = 2, Revision = 2, Members = [source, f.Roster.Members[1]] });
            var receipt = f.SignReceipt(f.Receipt with { RosterEpoch = 2, RosterRevision = 2 }, f.Receiver);
            Require(!LiveSaveAcceptanceTrust.SourceMatchesRoster(f.Artifact, roster, f.Now) &&
                !LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, receipt, roster, f.Now),
                "revoked/expired source access, missing Receive/EligibleHost or changed proposer key retained credit");
        }
        Require(!LiveSaveAcceptanceTrust.SourceMatchesRoster(f.Artifact, f.Roster, f.Now.AddDays(1)) &&
            !LiveSaveAcceptanceTrust.SourceMatchesRoster(f.Artifact,
                f.SignRoster(f.Roster with { Members = [f.Roster.Members[1]], Epoch = 2, Revision = 2 }), f.Now),
            "the source deadline or removed source member did not invalidate acceptance");
        var transferred = f.Record with { Receipt = f.Receipt, TransferUtc = f.Now.AddSeconds(2) };
        LiveSaveTestConfirmation Confirm(string stage) => new(f.Artifact.Version.VersionHash, f.Artifact.PayloadSha256, stage, true);
        Require(Refuses(() => LiveSaveAcceptanceTrust.Confirm(transferred, Confirm("restart"), f.Now)),
            "a branch acceptance skipped ordered game reports");
        var load = LiveSaveAcceptanceTrust.Confirm(transferred, Confirm("load"), f.Now.AddSeconds(3));
        var change = LiveSaveAcceptanceTrust.Confirm(load, Confirm("change"), f.Now.AddSeconds(4));
        var accepted = LiveSaveAcceptanceTrust.Confirm(change, Confirm("restart"), f.Now.AddSeconds(5));
        Require(accepted.AllStages && LiveSaveAcceptanceTrust.AcceptedForScope(accepted, f.Artifact.Scope, f.Roster, f.Now) &&
            LiveSaveAcceptanceTrust.Stages(accepted).Where(stage => stage.Id is "load" or "change" or "restart")
                .All(stage => stage.Source == "OwnerReported") && !SharedWorldLiveSaveAdapters.IsGameAccepted(f.Base.Game),
            "branch completion granted global game acceptance or lost the owner-reported source boundary");
    }

    private static void ScopeAndDiscovery(Evidence f)
    {
        var context = SharedWorldBranchLiveSource.Resolve(f.Data, f.Profile, f.Now);
        Require(LiveSaveAcceptanceTrust.MatchesSource(f.Artifact, context, f.Profile, f.Now),
            "the protected recovery source did not match its artifact");
        foreach (var source in new[]
        {
            context with { LocalProfileId = f.Root.ProfileId },
            context with { Root = f.Root with { GroupId = Guid.NewGuid() } },
            context with { Root = f.Root with { OwnerPublicKey = Public(f.Source) } },
            context with { PublishingPublicKey = Public(f.Source) }, context with { SourceDeviceId = f.ReceiverId },
            context with { Branch = null }
        }) Require(!LiveSaveAcceptanceTrust.MatchesSource(f.Artifact, source, f.Profile, f.Now),
            "changed local/root/publishing/member/branch source retained acceptance");
        foreach (var scope in new[]
        {
            f.Artifact.Scope with { BinarySha256 = Hash([6]) },
            f.Artifact.Scope with { ConfigurationSha256 = Hash([7]) },
            f.Artifact.Scope with { InstallationId = Hash([8]) },
            f.Artifact.Scope with { ProfileId = f.Root.ProfileId }, f.Artifact.Scope with { Synthetic = true }
        }) Require(!LiveSaveAcceptanceTrust.AcceptedForScope(f.Accepted, scope, f.Roster, f.Now),
            "branch acceptance survived changed installation/binary/configuration/local-profile or fixture scope");
        foreach (var record in new[]
        {
            f.Accepted with { ProfileId = f.Root.ProfileId },
            f.Accepted with { TestId = Guid.NewGuid() },
            f.Accepted with { OperationId = Guid.NewGuid() },
            f.Accepted with { Artifact = f.SignArtifact(f.Artifact with
                { Scope = f.Artifact.Scope with { BinarySha256 = Hash([9]) } }) }
        }) Require(!LiveSaveAcceptanceTrust.AcceptedForScope(record, f.Artifact.Scope, f.Roster, f.Now),
            "a different local profile/test/run or artifact installation scope was credited to this accepted record");
        var normal = new ServerProfile { Id = f.Root.ProfileId };
        var wrong = new ServerProfile
        {
            Id = Guid.NewGuid(),
            SeparateCopySourceProfileId = f.Root.ProfileId,
            SeparateCopyBranchHash = f.Branch.BranchHash
        };
        var otherRoot = new ServerProfile { Id = Guid.NewGuid() };
        var ids = WorldPeerService.LocalLiveTestProfileIds(f.Root, [normal, f.Profile, wrong, otherRoot]);
        Require(ids.Count == 2 && ids.Contains(normal.Id) && ids.Contains(f.Profile.Id),
            "peer discovery omitted the recovery installation or admitted another local ID/root");
    }

    private static void AttemptSelection(Evidence f)
    {
        var id = Guid.NewGuid();
        var attempt = new LiveSaveAttempt
        {
            ProfileId = f.Profile.Id,
            RequestId = id,
            CaptureId = id,
            OperationId = Guid.NewGuid(),
            BranchHash = f.Branch.BranchHash,
            State = "Publishing"
        };
        var live = f.Live(id, 2, f.Base, [7, 8, 9]);
        Require(HostManager.BranchLiveAttemptMatches(f.Profile, attempt, f.Branch, live),
            "an interrupted exact branch pointer commit did not reconcile");
        foreach (var version in new[]
        {
            f.Base, f.Live(Guid.NewGuid(), 2, f.Base, [7, 8, 9]),
            SharedWorldService.SignVersion(live with { ProfileId = f.Profile.Id }, f.Hosting),
            SharedWorldService.SignVersion(live, f.Owner),
            SharedWorldService.SignVersion(live with { WorldId = "another-world" }, f.Hosting)
        }) Require(!HostManager.BranchLiveAttemptMatches(f.Profile, attempt, f.Branch, version),
            "canonical/another request/local-profile/world/key publication repaired a branch attempt");
        var other = SharedWorldSeparateCopyStore.Sign(f.Branch.Offer, f.Source);
        Require(!HostManager.BranchLiveAttemptMatches(f.Profile, attempt, other, live),
            "another branch's current save repaired or overwrote this attempt");
        attempt.State = "Unchanged"; attempt.VersionHash = f.Base.VersionHash; attempt.VersionNumber = f.Base.Number;
        Require(HostManager.BranchLiveAttemptMatches(f.Profile, attempt, f.Branch, f.Base) &&
            !HostManager.BranchLiveAttemptMatches(f.Profile, attempt, f.Branch, live),
            "unchanged reconciliation followed a canonical/newer pointer or refused the exact branch base");
        attempt.State = "Publishing";
        Require(!HostManager.BranchLiveAttemptMatches(f.Profile, attempt, f.Branch, f.Base),
            "an unrecorded unchanged comparison was inferred from metadata without sealed evidence");
        attempt.BranchHash = other.BranchHash;
        Require(!HostManager.BranchLiveAttemptMatches(f.Profile, attempt, f.Branch, live),
            "a request for an old/other branch was replayed on the current installation");
    }

    private static void PrivateCopies(Evidence f)
    {
        var store = new LiveSaveAcceptanceStore(f.Data);
        var source = Path.Combine(f.Data.RootPath, "fabricated-sealed-payload"); Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "world.db"), f.Bytes);
        var scopeChecks = 0;
        store.Stage(f.Artifact, source, () => scopeChecks++, default);
        Require(scopeChecks == 3 && store.ReadArtifact(f.Artifact, default).Branch?.BranchHash == f.Branch.BranchHash &&
            store.ReadChunk(f.Artifact, 0, default).SequenceEqual(f.Bytes) &&
            !Directory.Exists(store.Root(f.Root.ProfileId, f.Artifact.TestId)) &&
            Refuses(() => store.Stage(f.Artifact, source, () => { }, default)),
            "schema-2 private storage used the root profile, lost exact chunks or overwrote a retained test");
        var received = WorldPeerReceivedTestJournal.StorageRoot(f.Data, f.Root, f.Artifact.TestId);
        using (var names = new WorldPeerTestNamespace(f.Data.RootPath, received, create: true))
        {
            names.WriteArtifact(f.Artifact);
            using var output = names.Resume("payload/world.db"); output.Write(f.Bytes); output.Flush(true);
        }
        var journal = new WorldPeerReceivedTestJournal(f.Data, f.Root);
        Require(journal.Read(f.Artifact) is null, "a renamed test directory gained a completed marker without verification");
        using (var verified = WorldPeerVerifiedTestCopy.Open(f.Data.RootPath, received, f.Artifact, default))
        {
            var marker = new WorldPeerTestCompletion(1, f.Root, f.Artifact.TestId, f.Artifact.Version.VersionHash,
                verified.ArtifactHash, f.ReceiverId, Public(f.Receiver), f.Roster.Epoch, f.Roster.Revision, f.Receipt);
            journal.Save(f.Artifact, marker, verified);
            journal.Save(f.Artifact, marker, verified);
            Require(journal.Read(f.Artifact)?.Receipt == f.Receipt &&
                Refuses(() => File.WriteAllBytes(Path.Combine(received, "payload", "world.db"), [0])) &&
                Refuses(() => journal.Save(f.Artifact, marker with { Root = f.Root with { ProfileId = f.Profile.Id } }, verified)),
                "schema-2 completion lost stable receipts, native payload leases or root/private namespace binding");
        }
        File.WriteAllBytes(Path.Combine(received, "payload", "world.db"), [0]);
        Require(Refuses(() => WorldPeerVerifiedTestCopy.Open(f.Data.RootPath, received, f.Artifact, default)),
            "a protected completed marker supplied trust after its exact private payload changed");
    }

    private static async Task NominationAndInstallationRefusalAsync(Evidence f)
    {
        var host = new HostManager(f.Data, new GameServerRegistry(f.Data, true, PortProbeMode.ObserveOnly), TimeProvider.System);
        var branchStatus = await host.SharedWorldStatusAsync(f.Profile.Id);
        Require(branchStatus.Enabled && branchStatus.Latest?.VersionHash == f.Base.VersionHash &&
            branchStatus.Error is null && !branchStatus.CanManageSharing && branchStatus.LiveSave?.Available != true,
            "recovery status hid its own retained history behind the canonical sharing switch or bypassed live acceptance");
        var store = new LiveSaveAcceptanceStore(f.Data);
        Require(!HostManager.HasProtectedLiveAcceptanceCandidate(f.Profile, [f.Record]) &&
            (await host.SharedFreshnessTargetsAsync(default)).Count == 0,
            "a recovery profile without its own six-stage record was scheduled");
        store.Save([f.Accepted]);
        var targets = await host.SharedFreshnessTargetsAsync(default);
        Require(!f.Profile.SharedSavesEnabled && !File.Exists(f.Profile.ExecutablePath) &&
            targets is [{ ProfileId: var localId, Eligible: false, Code: "LiveSaveNotRunning" }] && localId == f.Profile.Id,
            "the scheduler required canonical sharing or hashed a missing binary while merely nominating protected acceptance");
        Require(!(await host.LiveSaveAcceptanceAsync(f.Profile.Id)).Accepted &&
            !(await host.SaveAndShareAsync(f.Profile.Id, new(Guid.NewGuid()))).Ok && f.Data.LoadRuns().Count == 0,
            "fabricated scheduler evidence accepted a missing installation or started a process");
        store.Save([f.Record]);
        Require((await host.SharedFreshnessTargetsAsync(default)).Count == 0,
            "withdrawn six-stage acceptance left recovery capture scheduling enabled");
    }

    private static void BranchFreshness(Evidence f)
    {
        var store = new SharedWorldSeparateCopyStore(f.Data);
        var shares = new SharedWorldService(f.Data, new WorldBackupService(f.Data, TimeProvider.System,
            games: new GameServerRegistry(f.Data, true, PortProbeMode.ObserveOnly)));
        var pointer = Path.Combine(f.Data.RootPath, "shared-worlds", f.Root.ProfileId.ToString("N"), "latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(f.Base, Json); File.WriteAllBytes(pointer, canonicalBytes);
        var published = f.Live(Guid.NewGuid(), 2, f.Base, [7, 8, 9], f.Now.AddYears(-10));
        FriendLink.KeepSignedManifest(store.PublishedRoot(f.Branch), f.Base);
        WriteCopy(store.PublishedRoot(f.Branch), published, [7, 8, 9]);
        var fork = SharedWorldSeparateCopyStore.Sign(f.Branch.Offer, f.Source);
        var competing = f.Live(Guid.NewGuid(), 2, f.Base, [9, 9, 9], f.Now.AddYears(100));
        FriendLink.KeepSignedManifest(store.PublishedRoot(fork), f.Base);
        WriteCopy(store.PublishedRoot(fork), competing, [9, 9, 9]);
        var orphan = f.Live(Guid.NewGuid(), 3, published, [5, 5, 5], f.Now.AddYears(101));
        WriteCopy(store.PublishedRoot(f.Branch), orphan, [5, 5, 5], current: false);
        Require(shares.NewestCompleteVerified(f.Profile, f.Now, default)?.Version.VersionHash == published.VersionHash,
            "branch freshness chose a newer clock/fork or unpublished signed directory");
        File.WriteAllBytes(Path.Combine(store.PublishedRoot(f.Branch), published.VersionHash, "payload", "world.db"), [0]);
        var selected = shares.NewestCompleteVerified(f.Profile, f.Now, default);
        Require(selected?.Version.VersionHash == f.Base.VersionHash &&
            SharedWorldFreshness.CopyAgeSeconds(selected, f.Now) > TimeSpan.FromDays(3650).TotalSeconds &&
            File.ReadAllBytes(pointer).SequenceEqual(canonicalBytes) &&
            Directory.Exists(Path.Combine(store.PublishedRoot(f.Branch), orphan.VersionHash)) &&
            store.ReadPublished(fork)?.Version.VersionHash == competing.VersionHash &&
            new WorldAuthorityStore(f.Data).Read(f.Root.ProfileId).Count == 0 &&
            !f.Data.LoadSettings().Profiles.Single().SharedSavesEnabled && !f.Data.LoadSettings().CompanionListeningEnabled,
            "fallback rejected an old verified base, changed the canonical/default pointer, erased orphan/fork evidence or enabled sharing/listening");
    }

    private static void WriteCopy(string root, SharedWorldVersion version, byte[] bytes, bool current = true)
    {
        var directory = Path.Combine(root, version.VersionHash); Directory.CreateDirectory(Path.Combine(directory, "payload"));
        File.WriteAllBytes(Path.Combine(directory, "payload", "world.db"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(root, version);
        if (current) File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
}
