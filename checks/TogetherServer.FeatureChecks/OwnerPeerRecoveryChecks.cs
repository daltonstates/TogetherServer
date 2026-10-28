using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable signed files and a driver that refuses every process operation.
// No listener, app, browser, game, native dialog or real world is involved.
internal static class OwnerPeerRecoveryChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "owner-peer-recovery-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profileId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var roster = SignRoster(new(3, Guid.NewGuid(), profileId, 1, 1, true, Public(owner),
            [new(memberId, Public(member), new(Receive: true, EligibleHost: true), false)], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: Public(owner)), owner);
        var chain = new SharedWorldRosterChainStore(data);
        chain.Append(roster, roster.OwnerPublicKey);
        data.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
        data.SaveProtected($"shared-world-pc-signing-{memberId:N}.protected", member.ExportPkcs8PrivateKey());
        var ports = FreePorts();
        var endpoint = $"https://127.0.0.1:{ports[0]}";
        data.SaveSettings(new HostSettings { CompanionEndpoint = endpoint, CompanionPort = ports[0], Profiles = [] });
        using var certificate = new HostIdentity(data).Ensure(endpoint);
        var pin = HostIdentity.Fingerprint(certificate);
        var bytes = Encoding.UTF8.GetBytes("old complete owner source");
        var first = SharedWorldService.SignVersion(new(4, roster.GroupId, 1, null, profileId,
            GameKinds.Fixture, "OwnerRecovery", DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
            SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(ports[1], false, "Unknown", [], []),
            [Fact(bytes)], "", "", ""), owner);
        var ownerVault = Path.Combine(data.RootPath, "received-shared-worlds", Guid.Empty.ToString("N"), profileId.ToString("N"));
        WriteCopy(ownerVault, first, bytes);
        var authority = new WorldAuthorityStore(data);
        var store = new SharedWorldSeparateCopyStore(data);
        var ownerOffer = SharedWorldSeparateCopyStore.PrepareOwnerOffer(ownerVault, roster,
            owner, endpoint, pin, authority);
        Require(ownerOffer.Proposal is { Schema: 4, Kind: "OwnerSeparate", ProposerDeviceId: var id } && id == Guid.Empty &&
            SharedWorldSeparateCopyStore.VerifySeparateOffer(ownerOffer) &&
            !SharedWorldElection.VerifyOffer(ownerOffer) &&
            !WorldAuthorityTrust.VerifyProposal(ownerOffer.Proposal, roster) &&
            !SharedWorldReceiptTrust.Verify(ownerOffer.CandidateReceipt, Public(owner)),
            "owner separate proof entered canonical election, proposal or receipt authorization");
        Reject(() => store.Declare(ownerOffer, ownerVault, owner, false), "owner recovery skipped the split warning");
        Reject(() => store.Declare(ownerOffer, ownerVault, stranger, true), "a foreign key declared an owner recovery");
        var ownerBranch = store.Declare(ownerOffer, ownerVault, owner, true);
        Require(ownerBranch.Schema == 3 && ownerBranch.SourceBranch is null &&
            SharedWorldSeparateCopyStore.Verify(ownerBranch) && SharedWorldSeparateCopyStore.HasLocalBranchKeys(data, ownerBranch) &&
            authority.Read(profileId).Count == 0,
            "an old owner save could not become a retained separate branch without changing canonical authority");
        Require(!SharedWorldSeparateCopyStore.Verify(ownerBranch with { Schema = 1 }) &&
            !SharedWorldSeparateCopyStore.VerifySeparateOffer(ownerOffer with
            { Proposal = ownerOffer.Proposal with { Kind = "Quorum" } }) &&
            !SharedWorldSeparateCopyStore.VerifySeparateOffer(ownerOffer with
            { CandidateReceipt = ownerOffer.CandidateReceipt with { DeviceId = memberId } }),
            "owner proof accepted an old envelope, canonical kind or substituted member identity");

        // A friend made independent progress. The returning original owner
        // selects this exact signed branch rather than replacing its base.
        var memberVault = Path.Combine(data.RootPath, "received-shared-worlds", memberId.ToString("N"), profileId.ToString("N"));
        WriteCopy(memberVault, first, bytes);
        var floor = new SharedRosterFloor(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
        var memberOffer = SharedWorldSeparateCopyStore.PrepareSoloOffer(memberVault, roster, floor,
            memberId, member, endpoint, pin, authority);
        var memberBranch = store.Declare(memberOffer, memberVault, member, true);
        using var hosting = ECDsa.Create();
        hosting.ImportPkcs8PrivateKey(data.LoadProtected(WorldAuthorityStore.HostingKeyName(profileId))!, out _);
        var progressBytes = Encoding.UTF8.GetBytes("friend branch progress retained for owner recovery");
        var progress = SharedWorldService.SignVersion(first with
        {
            Number = 2,
            ParentHash = first.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [Fact(progressBytes)]
        }, hosting);
        var branchVault = SharedWorldBranchVault.Root(data, memberBranch);
        WriteCopy(branchVault, first, bytes);
        WriteCopy(branchVault, progress, progressBytes);
        File.WriteAllBytes(Path.Combine(branchVault, "branch.json"), JsonSerializer.SerializeToUtf8Bytes(memberBranch, Json));
        var recoveredOffer = SharedWorldSeparateCopyStore.PrepareOwnerOffer(branchVault, roster,
            owner, endpoint, pin, authority, memberBranch);
        var recovered = store.Declare(recoveredOffer, branchVault, owner, true, memberBranch);
        CompanionContinuationChecks.Run(data, roster, recovered, ownerBranch);
        Require(recovered.Schema == 3 && recovered.SourceBranch?.BranchHash == memberBranch.BranchHash &&
            recovered.Offer.Version.VersionHash == progress.VersionHash && SharedWorldSeparateCopyStore.Verify(recovered) &&
            FriendLink.ReadReceivedLatest(store.RetainedRoot(recovered))?.VersionHash == progress.VersionHash &&
            FriendLink.ReadReceivedLatest(branchVault)?.VersionHash == progress.VersionHash &&
            !SharedWorldSeparateCopyStore.Verify(recovered with { SourceBranch = null }),
            "owner return did not preserve and bind the complete selected friend branch");
        var nextMemberOffer = SharedWorldSeparateCopyStore.PrepareSoloOffer(store.RetainedRoot(recovered),
            roster, floor, memberId, member, endpoint, pin, authority, recovered);
        var nextMemberBranch = SharedWorldSeparateCopyStore.Sign(nextMemberOffer, member, recovered);
        Require(nextMemberBranch.Schema == 2 && nextMemberBranch.SourceBranch?.Schema == 3 &&
            SharedWorldSeparateCopyStore.Verify(nextMemberBranch),
            "a member could not verify descendants of an owner-only recovery envelope");

        var executable = Path.Combine(data.RootPath, "never-run.synthetic");
        File.WriteAllText(executable, "synthetic installed file; never executed");
        var driver = new FilesOnlyDriver();
        var games = new GameServerRegistry([driver], PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games);
        var setup = new TakeoverLocalSetup(executable, "Unknown", [], true, ports[0], ports[1]);
        var restored = await manager.RestoreSeparateCopyAsync(profileId,
            new(recovered.BranchHash, setup, "Owner return", "Owner return"), freeBytes: _ => 8L << 30);
        Require(restored.Ok && restored.ProfileId is not null, "the owner branch failed the existing fresh-world restore");
        Require((await manager.FinishSeparateCopyAsync(profileId, new(recovered.BranchHash, setup))).Ok &&
            (await manager.SeparateCopyHostStatusAsync(profileId, recovered.BranchHash)).ReadyForManualStart &&
            (await manager.StartAsync(restored.ProfileId!.Value)).Code == "SeparateCopyManualStartRequired",
            "the owner branch failed manual readiness or bypassed explicit separate Start");
        var profile = data.LoadSettings().Profiles.Single(item => item.Id == restored.ProfileId);
        var backups = new WorldBackupService(data, TimeProvider.System, _ => 8L << 30, games);
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), Encoding.UTF8.GetBytes("owner resumed progress"));
        var backup = backups.Create(profile, BackupKinds.Rolling);
        Require(backup.Ok && backup.Backup is not null, "the synthetic owner checkpoint was not saved");
        var published = store.PublishAfterStop(recovered, profile, backup.Backup!.Id, backups);
        Require(published.Ok && published.Version!.ParentHash == progress.VersionHash &&
            published.Version.SigningPublicKey == recovered.Offer.Proposal.CandidatePublicKey &&
            authority.Read(profileId).Count == 0, "owner progress did not remain a separate signed branch");

        var memberRestored = await manager.RestoreSeparateCopyAsync(profileId,
            new(memberBranch.BranchHash, setup, "Member branch", "Member branch"), freeBytes: _ => 8L << 30);
        Require(memberRestored.Ok && memberRestored.ProfileId is not null &&
            (await manager.FinishSeparateCopyAsync(profileId, new(memberBranch.BranchHash, setup))).Ok,
            "the member branch was not ready before the current-roster denial test");
        var memberProfile = data.LoadSettings().Profiles.Single(item => item.Id == memberRestored.ProfileId);
        var memberBackup = backups.Create(memberProfile, BackupKinds.Rolling);
        Require(memberBackup.Ok && memberBackup.Backup is not null, "the member checkpoint fixture failed");

        var revoked = NextRoster(roster, roster.Members.Select(item => item with { Revoked = true }).ToArray(), owner);
        chain.Append(revoked, roster.OwnerPublicKey);
        Require(SharedWorldSeparateCopyStore.Verify(memberBranch) && store.Read(profileId).Count == 3 &&
            !SharedWorldSeparateCopyStore.CandidateCurrentlyAuthorized(data, memberOffer) &&
            SharedWorldSeparateCopyStore.CandidateCurrentlyAuthorized(data, recoveredOffer),
            "new member revocation erased history, remained authorized, or revoked the root owner");
        Require(!(await manager.SeparateCopyHostStatusAsync(profileId, memberBranch.BranchHash)).ReadyForManualStart &&
            !(await manager.StartSeparateCopyAsync(memberRestored.ProfileId!.Value, memberBranch.BranchHash)).Ok &&
            !store.PublishAfterStop(memberBranch, memberProfile, memberBackup.Backup!.Id, backups).Ok,
            "newly learned revocation allowed a future Start or save publication");
        Reject(() => store.ReadPublishedChunk(memberBranch, first.Number, first.VersionHash, 0, 0),
            "a newly revoked branch host could still serve save chunks");
        var expired = NextRoster(revoked,
            roster.Members.Select(item => item with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }).ToArray(), owner);
        chain.Append(expired, roster.OwnerPublicKey);
        Require(!SharedWorldSeparateCopyStore.CandidateCurrentlyAuthorized(data, memberOffer) &&
            SharedWorldSeparateCopyStore.Verify(memberBranch) && driver.ProcessAttempts == 0 &&
            !data.LoadSettings().CompanionListeningEnabled,
            "expired access remained usable, erased history, or source-only checks launched a process/listener");
    }

    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)));
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static SharedWorldRoster SignRoster(SharedWorldRoster roster, ECDsa key) => roster with
    { Signature = Convert.ToBase64String(key.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
    private static SharedWorldRoster NextRoster(SharedWorldRoster previous,
        IReadOnlyList<SharedWorldRosterMember> members, ECDsa owner) => SignRoster(previous with
        {
            Epoch = previous.Epoch + 1,
            Revision = previous.Revision + 1,
            Members = members,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(previous),
            OwnerLocalBaselineMembers = members,
            Signature = ""
        }, owner);
    private static void WriteCopy(string root, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(root, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(root, version);
        File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private static int[] FreePorts()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var used = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners()).Select(item => item.Port).ToHashSet();
        return Enumerable.Range(50000, 10000).Where(port => !used.Contains(port)).Take(2).ToArray();
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException)
        { return; }
        throw new InvalidOperationException(message);
    }
    private sealed class FilesOnlyDriver : IGameServerDriver
    {
        internal int ProcessAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Owner recovery synthetic files only";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        private InvalidOperationException NoProcess() { ProcessAttempts++; return new("Process operations are forbidden in this check."); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
    }
}
