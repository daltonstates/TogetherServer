using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable files, protected records and native file leases only. The fixture
// identity seam never represents a real game and the driver rejects processes.
internal static class BranchLiveSourceChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "branch-live-source-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var candidate = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootProfile = Guid.NewGuid(); var groupId = Guid.NewGuid(); var memberId = Guid.NewGuid();
        var endpoint = "https://127.0.0.1:5181";
        data.SaveSettings(new HostSettings { CompanionEndpoint = endpoint, CompanionPort = 5181,
            CompanionListeningEnabled = false, Profiles = [] });
        data.SaveProtected($"shared-world-pc-signing-{memberId:N}.protected", candidate.ExportPkcs8PrivateKey());
        using var certificate = new HostIdentity(data).Ensure(endpoint);
        var rosterDraft = new SharedWorldRoster(2, groupId, rootProfile, 1, 1, true, Public(owner),
            [new(memberId, Public(candidate), new(Receive: true, EligibleHost: true), false)], "");
        var roster = SignRoster(rosterDraft, owner);
        var initial = Encoding.UTF8.GetBytes("old retained source");
        var first = SharedWorldService.SignVersion(new(4, groupId, 1, null, rootProfile, GameKinds.Fixture,
            "BranchLiveFilesOnly", DateTimeOffset.Parse("2020-01-01T00:00:00Z"), SharedWorldCaptureKinds.PostStopBackup,
            Guid.NewGuid(), new(5182, false, "Unknown", [], []), [Fact(initial)], "", "", ""), owner);
        var vault = Path.Combine(data.RootPath, "received-shared-worlds", memberId.ToString("N"), rootProfile.ToString("N"));
        WriteCopy(vault, first, initial);
        var authority = new WorldAuthorityStore(data);
        var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(vault, roster, new(groupId, 1, 1, roster.Signature),
            memberId, candidate, endpoint, HostIdentity.Fingerprint(certificate), authority);
        var store = new SharedWorldSeparateCopyStore(data);
        var branch = store.Declare(offer, vault, candidate, true);
        var localId = SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash);
        var executable = Path.Combine(data.RootPath, "never-run.synthetic");
        File.WriteAllText(executable, "not an executable; source checks only");
        var profile = new ServerProfile { Id = localId, Kind = GameKinds.Fixture, Name = "Protected branch",
            ServerName = "Protected branch", WorldId = first.WorldId, WorldSource = "Existing",
            WorldDirectory = SharedWorldBranchLiveSource.WorldRoot(data, first, localId), ExecutablePath = executable,
            GamePort = 5182, SeparateCopySourceProfileId = rootProfile, SeparateCopyBranchHash = branch.BranchHash,
            SharedSavesEnabled = false, CrashRecovery = new() { Enabled = false }, Backups = new() { Enabled = true } };
        Directory.CreateDirectory(profile.WorldDirectory);
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), initial);
        var settings = data.LoadSettings(); settings.Profiles = [profile]; data.SaveSettings(settings);
        var setup = new TakeoverLocalSetup(executable, "Unknown", [], true, 5181, 5182);
        var state = new SeparateCopyHostState(1, rootProfile, localId, groupId, branch.BranchHash,
            first.VersionHash, profile.WorldDirectory, Ready: true, SetupHash: SharedWorldBranchLiveSource.SetupHash(profile, setup));
        void SaveState(SeparateCopyHostState value) => data.SaveProtected(SharedWorldBranchLiveSource.StateName(localId),
            JsonSerializer.SerializeToUtf8Bytes(value));
        SaveState(state);
        var context = SharedWorldBranchLiveSource.Resolve(data, profile);
        Require(context.LocalProfileId == localId && context.LocalProfileId != rootProfile && context.Root.ProfileId == rootProfile &&
            context.Root.GroupId == groupId && context.Branch?.BranchHash == branch.BranchHash && context.SourceDeviceId == memberId &&
            context.PublishingPublicKey == offer.Proposal.CandidatePublicKey && context.PublishingPublicKey != Public(candidate),
            "branch source confused the installation, world root, member identity or publishing key");
        Require(SharedWorldBranchLiveSource.IsOwnedSaveSource(data, profile, profile.WorldDirectory) &&
            !SharedWorldBranchLiveSource.IsOwnedSaveSource(data, profile, Path.Combine(profile.WorldDirectory, "unexpected")),
            "branch source allowed an arbitrary descendant or required a normal import marker");
        Reject(() => SharedWorldBranchLiveSource.Resolve(data, profile, requireStarted: true),
            "a merely prepared branch bypassed explicit manual Start evidence");
        store.MarkHostReturned(rootProfile);
        Require(SharedWorldBranchLiveSource.SameSource(context, SharedWorldBranchLiveSource.Resolve(data, profile)) &&
            !data.LoadSettings().CompanionListeningEnabled && authority.Read(rootProfile).Count == 0,
            "returning Host or absent listener preempted the branch source or granted default authority");
        SaveState(state with { SetupHash = new string('A', 64) });
        Reject(() => SharedWorldBranchLiveSource.Resolve(data, profile), "changed protected setup remained a live source");
        SaveState(state);
        var originalPath = profile.WorldDirectory;
        profile.WorldDirectory = Path.Combine(data.RootPath, "unowned-world");
        Reject(() => SharedWorldBranchLiveSource.Resolve(data, profile), "changed saved world path remained a live source");
        profile.WorldDirectory = originalPath;
        var driver = new FilesOnlyDriver();
        var games = new GameServerRegistry([driver], PortProbeMode.ObserveOnly);
        var snapshots = new ManagedLiveSnapshotStore(data, games)
        { FixtureRunIdentityForChecks = _ => true, FixtureAvailableBytesForChecks = () => 8L << 30 };
        var run = new ManagedRun { ProfileId = localId, Kind = profile.Kind, WorldId = profile.WorldId,
            WorldDirectory = profile.WorldDirectory, ExecutablePath = executable, GamePort = profile.GamePort,
            OperationId = Guid.NewGuid(), ProcessId = 424242, StartTimeUtcTicks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
            DeclaredPorts = [] };
        data.SaveRuns([run]);
        SaveState(state with { Started = true });
        ImmutableLiveSaveSnapshot Capture() => snapshots.Stage(profile, run,
            new(localId, run.OperationId, run.ProcessId!.Value, run.StartTimeUtcTicks!.Value,
                LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true));
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "first sealed branch progress");
        var captured = Capture();
        var callbacks = 0;
        var forged = captured with { SnapshotId = Guid.NewGuid() };
        Require(!store.PublishLiveCapture(branch, profile, run, forged, snapshots, () => callbacks++).Ok && callbacks == 0 &&
            store.ReadPublished(branch) is null, "an unsealed caller-created snapshot invoked acceptance or became a branch save");
        var published = store.PublishLiveCapture(branch, profile, run, captured, snapshots, () => callbacks++);
        Require(published.Ok && published.Version is { Schema: 5, CaptureKind: SharedWorldCaptureKinds.LiveSave } live &&
            live.ProfileId == rootProfile && live.GroupId == groupId && live.ParentHash == first.VersionHash && live.Number == 2 &&
            live.SigningPublicKey == context.PublishingPublicKey && callbacks >= 4 && authority.Read(rootProfile).Count == 0 &&
            !data.LoadSettings().Profiles.Single().SharedSavesEnabled,
            "a sealed branch save lost its root, parent, acceptance rechecks or separate publication boundary");
        var same = store.PublishLiveCapture(branch, profile, run, Capture(), snapshots, () => callbacks++);
        Require(same.Ok && same.Code == "RecoverySnapshotUnchanged" && same.Version!.VersionHash == published.Version!.VersionHash &&
            same.Version.CreatedUtc == published.Version.CreatedUtc,
            "unchanged sealed bytes created a version or renewed the visible save date");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "new branch progress");
        var changed = Capture(); var acceptanceChecks = 0;
        var interrupted = store.PublishLiveCapture(branch, profile, run, changed, snapshots, () =>
        {
            if (++acceptanceChecks == 4) throw new InvalidDataException("synthetic exact installation changed before pointer commit");
        });
        Require(!interrupted.Ok && acceptanceChecks == 4 && store.ReadPublished(branch)!.Version.VersionHash == published.Version!.VersionHash &&
            Directory.EnumerateDirectories(store.PublishedRoot(branch)).Any(path =>
                SharedWorldPeerTrust.ValidHash(Path.GetFileName(path)) && Path.GetFileName(path) != published.Version.VersionHash),
            "a late acceptance change advanced the pointer or erased its retained interrupted signed copy");
        Require(!store.PublishLiveCapture(branch, profile, run, changed, snapshots, () => { }).Ok,
            "an interrupted branch publication was silently promoted on retry");
        var revokedMembers = roster.Members.Select(member => member with { Revoked = true }).ToArray();
        var revoked = SignRoster(roster with { Epoch = 2, Revision = 2, Members = revokedMembers,
            OwnerLocalBaselineMembers = revokedMembers, PreviousRosterHash = SharedWorldRosterTrust.Hash(roster), Signature = "" }, owner);
        new SharedWorldRosterChainStore(data).Append(revoked, roster.OwnerPublicKey);
        Require(!SharedWorldBranchLiveSource.IsOwnedSaveSource(data, profile, profile.WorldDirectory) &&
            !store.PublishLiveCapture(branch, profile, run, captured, snapshots, () => { }).Ok &&
            driver.ProcessAttempts == 0 && !data.LoadSettings().CompanionListeningEnabled,
            "revoked branch source remained usable or source-only checks attempted a process/listener");
        return Task.CompletedTask;
    }

    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static SharedWorldRoster SignRoster(SharedWorldRoster roster, ECDsa owner) => roster with
    { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
    private static void WriteCopy(string root, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(root, version.VersionHash); Directory.CreateDirectory(Path.Combine(directory, "payload"));
        File.WriteAllBytes(Path.Combine(directory, "payload", "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(root, version);
        File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (SharedWorldBranchLiveSource.ExpectedFailure(ex)) { return; }
        throw new InvalidOperationException(message);
    }
    private sealed class FilesOnlyDriver : IGameServerDriver
    {
        internal int ProcessAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Branch live source files only";
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
