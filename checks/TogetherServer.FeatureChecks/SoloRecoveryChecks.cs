using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fresh synthetic files, protected state and a driver that refuses every
// process operation. No app, listener, fixture process, browser or game runs.
internal static class SoloRecoveryChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "solo-recovery-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var candidate = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextCandidate = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profileId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var nextDevice = Guid.NewGuid();
        var roster = SignRoster(new(2, groupId, profileId, 1, 1, true, Public(owner),
            [new(deviceId, Public(candidate), new(Receive: true, EligibleHost: true), false),
             new(nextDevice, Public(nextCandidate), new(Receive: true, EligibleHost: true), false)], ""), owner);
        var floor = new SharedRosterFloor(groupId, 1, 1, roster.Signature);
        var ports = FreeLocalPorts(2);
        var endpoint = $"https://127.0.0.1:{ports[0]}";
        data.SaveSettings(new HostSettings
        {
            CompanionEndpoint = endpoint,
            CompanionPort = ports[0],
            CompanionListeningEnabled = false,
            Profiles = []
        });
        data.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", candidate.ExportPkcs8PrivateKey());
        using var certificate = new HostIdentity(data).Ensure(endpoint);
        var pin = HostIdentity.Fingerprint(certificate);
        var vault = Path.Combine(data.RootPath, "received-shared-worlds", deviceId.ToString("N"), profileId.ToString("N"));
        byte[] initialBytes = Encoding.UTF8.GetBytes("old but complete owner snapshot");
        var first = SharedWorldService.SignVersion(new(4, groupId, 1, null, profileId,
            GameKinds.Fixture, "SyntheticRecovery", DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
            SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
            new(ports[1], false, "Unknown", [], []), [FileFact(initialBytes)], "", "", ""), owner);
        WriteCopy(vault, first, initialBytes);
        var authority = new WorldAuthorityStore(data);
        var loss = new SharedWorldHostLoss();
        Require(!loss.MayPropose, "a newly created Host-loss observer was already confirmed");
        Reject(() => SharedWorldElection.PrepareOffer(loss, vault, roster, floor, deviceId,
            candidate, endpoint, pin, authority), "strict default recovery lost its Host-loss gate");
        var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(vault, roster, floor,
            deviceId, candidate, endpoint, pin, authority);
        Require(SharedWorldElection.VerifyOffer(offer) && offer.Version.VersionHash == first.VersionHash,
            "solo recovery rejected a complete old snapshot or a literal loopback identity");
        var store = new SharedWorldSeparateCopyStore(data);
        Reject(() => store.Declare(offer, vault, candidate, false), "recovery did not require the split warning");
        Reject(() => store.Declare(offer, vault, stranger, true), "another PC declared the recovery branch");
        var branch = store.Declare(offer, vault, candidate, true);
        Require(SharedWorldSeparateCopyStore.Verify(branch) && authority.Read(profileId).Count == 0 &&
            store.Declare(offer, vault, candidate, true).BranchHash == branch.BranchHash,
            "manual recovery changed default authority or lost idempotency");
        Require(!SharedWorldSeparateCopyStore.Verify(branch with { WarningAccepted = false }) &&
            !SharedWorldSeparateCopyStore.Verify(branch with { BranchHash = new string('A', 64) }),
            "tampered recovery identity or warning verified");
        store.MarkHostReturned(profileId);
        Require(store.HostReturned(profileId, branch.BranchHash) &&
            store.Read(profileId).Single().BranchHash == branch.BranchHash,
            "a returning Host erased the signed recovery history");

        var executable = Path.Combine(data.RootPath, "never-run.synthetic");
        File.WriteAllText(executable, "synthetic installed-file boundary; never executable");
        var driver = new FilesOnlyDriver();
        var games = new GameServerRegistry([driver], PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games);
        var setup = new TakeoverLocalSetup(executable, "Unknown", [], true, ports[0], ports[1]);
        var restored = await manager.RestoreSeparateCopyAsync(profileId,
            new(branch.BranchHash, setup, "Solo recovery", "Solo recovery"), freeBytes: _ => 8L << 30);
        Require(restored.Ok && restored.ProfileId is not null,
            $"solo restore without a listener/second PC failed: {restored.Code}: {restored.Message}");
        var localId = restored.ProfileId!.Value;
        var finished = await manager.FinishSeparateCopyAsync(profileId, new(branch.BranchHash, setup));
        Require(finished.Ok, $"solo local checks required a listener/second PC: {finished.Code}: {finished.Message}");
        var status = await manager.SeparateCopyHostStatusAsync(profileId, branch.BranchHash);
        Require(status.ReadyForManualStart && !status.ReviewRequired && !status.ControlRouteVerified &&
            (await manager.StartAsync(localId)).Code == "SeparateCopyManualStartRequired",
            "returned Host or absent route blocked solo readiness, or normal Start bypassed the manual fence");
        var retainedFile = Path.Combine(store.RetainedRoot(branch), first.VersionHash,
            SharedWorldService.PayloadDirectory, "world.dat");
        File.WriteAllText(retainedFile, "tampered retained branch source");
        Require(!(await manager.SeparateCopyHostStatusAsync(profileId, branch.BranchHash)).ReadyForManualStart,
            "a damaged retained source remained Start-ready");
        File.WriteAllBytes(retainedFile, initialBytes);
        Require((await manager.SeparateCopyHostStatusAsync(profileId, branch.BranchHash)).ReadyForManualStart,
            "restored exact source failed local readiness");

        var profile = data.LoadSettings().Profiles.Single(item => item.Id == localId);
        var backups = new WorldBackupService(data, TimeProvider.System, _ => 8L << 30, games);
        byte[] changeOne = Encoding.UTF8.GetBytes("recovery progress one");
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), changeOne);
        var backupOne = backups.Create(profile, BackupKinds.Rolling);
        Require(backupOne.Ok && backupOne.Backup is not null, "synthetic recovery checkpoint failed");
        var publishedOne = store.PublishAfterStop(branch, profile, backupOne.Backup!.Id, backups);
        Require(publishedOne.Ok && publishedOne.Version is { } child &&
            child.Number == first.Number + 1 && child.ParentHash == first.VersionHash &&
            child.SigningPublicKey == branch.Offer.Proposal.CandidatePublicKey,
            $"recovery checkpoint did not produce a signed separate descendant: {publishedOne.Code}");
        byte[] changeTwo = Encoding.UTF8.GetBytes("recovery progress two");
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), changeTwo);
        var backupTwo = backups.Create(profile, BackupKinds.Rolling);
        Require(backupTwo.Ok && backupTwo.Backup is not null, "second synthetic recovery checkpoint failed");
        var publishedTwo = store.PublishAfterStop(branch, profile, backupTwo.Backup!.Id, backups);
        Require(publishedTwo.Ok && publishedTwo.Version!.ParentHash == publishedOne.Version!.VersionHash &&
            publishedTwo.Version.Number == publishedOne.Version.Number + 1 &&
            store.PublishAfterStop(branch, profile, backupTwo.Backup.Id, backups).Version?.VersionHash == publishedTwo.Version.VersionHash &&
            store.ReadLatestSnapshot(branch).Version.VersionHash == publishedTwo.Version.VersionHash &&
            authority.Read(profileId).Count == 0,
            "latest recovery selection used a foreign branch, lost idempotency, or changed default authority");
        Require(store.ReadPublishedChunk(branch, publishedOne.Version.Number, publishedOne.Version.VersionHash, 0, 0)
                .SequenceEqual(changeOne) &&
            store.ReadPublishedChunk(branch, publishedTwo.Version.Number, publishedTwo.Version.VersionHash, 0, 0)
                .SequenceEqual(changeTwo), "a descendant overwrote the prior recovery snapshot");
        Reject(() => store.ReadPublishedChunk(branch, publishedTwo.Version.Number,
            publishedTwo.Version.VersionHash, 0, 1), "a nonaligned recovery chunk was served");
        Reject(() => store.ReadPublishedManifest(branch, publishedTwo.Version.Number, first.VersionHash),
            "a foreign snapshot hash was accepted with a different branch version number");
        var publishedFile = Path.Combine(store.PublishedRoot(branch), publishedTwo.Version.VersionHash,
            SharedWorldService.PayloadDirectory, "world.dat");
        File.WriteAllText(publishedFile, "tampered recovery publication");
        Reject(() => store.ReadLatestSnapshot(branch), "a tampered branch snapshot was advertised");
        File.WriteAllBytes(publishedFile, changeTwo);

        using var nextData = new LocalData(Path.Combine(root, "solo-descendant-" + Guid.NewGuid().ToString("N")));
        nextData.SaveProtected($"shared-world-pc-signing-{nextDevice:N}.protected", nextCandidate.ExportPkcs8PrivateKey());
        var receivedBranch = SharedWorldBranchVault.Root(nextData, branch);
        WriteCopy(receivedBranch, publishedOne.Version, changeOne);
        WriteCopy(receivedBranch, publishedTwo.Version, changeTwo);
        File.WriteAllBytes(Path.Combine(receivedBranch, "branch.json"), JsonSerializer.SerializeToUtf8Bytes(branch, Json));
        var selected = SharedWorldBranchVault.ReadBranch(nextData, roster, branch.BranchHash);
        var nextOffer = SharedWorldSeparateCopyStore.PrepareSoloOffer(receivedBranch, roster, floor,
            nextDevice, nextCandidate, endpoint, pin, new WorldAuthorityStore(nextData), selected);
        var nextBranch = new SharedWorldSeparateCopyStore(nextData).Declare(nextOffer, receivedBranch,
            nextCandidate, true, selected);
        Require(nextBranch.Schema == 2 && nextBranch.SourceBranch?.BranchHash == branch.BranchHash &&
            nextBranch.Offer.Version.VersionHash == publishedTwo.Version.VersionHash &&
            SharedWorldSeparateCopyStore.Verify(nextBranch),
            "another eligible PC could not manually recover the newest verified branch descendant");
        Require(!SharedWorldSeparateCopyStore.Verify(nextBranch with { SourceBranch = null }),
            "a child recovery lost its bound source branch without invalidating its signature");
        using (var nextHostingKey = ECDsa.Create())
        {
            var hostingBytes = nextData.LoadProtected(WorldAuthorityStore.HostingKeyName(profileId))!;
            nextHostingKey.ImportPkcs8PrivateKey(hostingBytes, out _);
            var final = SharedWorldService.SignVersion(publishedTwo.Version with
            {
                Number = publishedTwo.Version.Number + 1,
                ParentHash = publishedTwo.Version.VersionHash,
                BackupId = Guid.NewGuid(),
                CreatedUtc = first.CreatedUtc.AddYears(-1),
                Files = [FileFact(Encoding.UTF8.GetBytes("C continued the world"))]
            }, nextHostingKey);
            var rootIdentity = new SharedWorldPeerRoot(Public(owner), profileId, groupId);
            var manifests = new[] { first, publishedOne.Version, publishedTwo.Version, final }
                .ToDictionary(item => item.VersionHash);
            var chosenLatest = await WorldPeerLatestSelection.SelectAsync(rootIdentity,
                [new(first), new(publishedTwo.Version, branch), new(final, nextBranch)],
                [first.VersionHash, publishedTwo.Version.VersionHash, final.VersionHash],
                (_, _) => Task.CompletedTask,
                (hash, number, _) => Task.FromResult(manifests.TryGetValue(hash, out var value) && value.Number == number ? value : null), default);
            Require(chosenLatest == 2,
                "A -> B -> C continuation did not select C for the returning owner, or a wall clock displaced signed descendants");
            var concurrentOwner = SharedWorldService.SignVersion(first with
            {
                Number = first.Number + 1,
                ParentHash = first.VersionHash,
                BackupId = Guid.NewGuid(),
                Files = [FileFact(Encoding.UTF8.GetBytes("A concurrently changed the original"))]
            }, owner);
            manifests.Add(concurrentOwner.VersionHash, concurrentOwner);
            try
            {
                await WorldPeerLatestSelection.SelectAsync(rootIdentity,
                    [new(concurrentOwner), new(final, nextBranch)], [], (_, _) => Task.CompletedTask,
                    (hash, number, _) => Task.FromResult(manifests.TryGetValue(hash, out var value) && value.Number == number ? value : null), default);
                throw new InvalidOperationException("returning owner priority silently discarded C's divergent continuation");
            }
            catch (InvalidDataException) { }
        }
        var missingAncestor = Path.Combine(receivedBranch, "signed-history");
        var ancestorFile = Directory.EnumerateFiles(missingAncestor, "*.json", SearchOption.AllDirectories)
            .Single(path => JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json)!.VersionHash == publishedOne.Version.VersionHash);
        File.WriteAllText(ancestorFile, "damaged ancestry");
        Reject(() => SharedWorldSeparateCopyStore.PrepareSoloOffer(receivedBranch, roster, floor,
            nextDevice, nextCandidate, endpoint, pin, new WorldAuthorityStore(nextData), selected),
            "recovery from a branch skipped damaged signed ancestry");
        var noHostRoster = SignRoster(roster with
        {
            Members = [roster.Members[0] with { Grants = new(Receive: true) }, roster.Members[1]],
            Signature = ""
        }, owner);
        Reject(() => SharedWorldSeparateCopyStore.PrepareSoloOffer(vault, noHostRoster,
            floor with { Signature = noHostRoster.Signature }, deviceId, candidate, endpoint, pin, authority),
            "Receive alone allowed recovery hosting");

        // Two genuine owner-signed decisions remain unresolved. A separate
        // manual branch may still use the explicitly selected proven history.
        var leftAuthority = PlannedRecord(roster, first, deviceId, candidate, owner, endpoint);
        var rightAuthority = PlannedRecord(roster, first, nextDevice, nextCandidate, owner, endpoint);
        authority.Append(leftAuthority);
        authority.Append(rightAuthority);
        Require(WorldAuthorityTrust.EffectiveHeads(authority.Read(profileId)).Length == 2,
            "the synthetic authority fixture did not retain competing heads");
        var chosenBytes = Encoding.UTF8.GetBytes("selected left authority progress");
        var chosen = SharedWorldService.SignVersion(first with
        {
            Number = first.Number + 1,
            ParentHash = first.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [FileFact(chosenBytes)]
        }, candidate);
        WriteCopy(vault, chosen, chosenBytes);
        var selectedOffer = SharedWorldSeparateCopyStore.PrepareSoloOffer(vault, roster, floor,
            deviceId, candidate, endpoint, pin, authority);
        var selectedBranch = store.Declare(selectedOffer, vault, candidate, true);
        Require(selectedOffer.Proposal.ParentAuthorityHash == leftAuthority.RecordHash &&
            selectedBranch.Offer.Version.VersionHash == chosen.VersionHash &&
            WorldAuthorityTrust.EffectiveHeads(authority.Read(profileId)).Length == 2,
            "manual recovery required unique authority or resolved competing histories implicitly");
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), initialBytes);
        Require((await manager.SeparateCopyHostStatusAsync(profileId, branch.BranchHash)).ReadyForManualStart,
            "a later competing authority preempted an existing separate recovery branch");
        Require(driver.ProcessAttempts == 0 && !data.LoadSettings().CompanionListeningEnabled,
            "source-only recovery checks attempted a process or enabled a listener");
    }

    private static SharedWorldFile FileFact(byte[] bytes) =>
        new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));

    private static void WriteCopy(string root, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(root, version.VersionHash);
        var payload = Path.Combine(directory, SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(payload);
        File.WriteAllBytes(Path.Combine(payload, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(root, version);
        File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static SharedWorldRoster SignRoster(SharedWorldRoster draft, ECDsa owner) => draft with
    {
        Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };

    private static WorldAuthorityRecord PlannedRecord(SharedWorldRoster roster, SharedWorldVersion version,
        Guid deviceId, ECDsa candidate, ECDsa owner, string endpoint)
    {
        var proposalDraft = new WorldAuthorityProposal(1, roster.GroupId, roster.ProfileId, 1, null,
            WorldAuthorityTrust.RosterHash(roster), version.VersionHash, Public(candidate), endpoint,
            "Planned", Guid.Empty, Public(owner), "");
        var proposal = proposalDraft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
                WorldAuthorityTrust.ProposalBasis(proposalDraft), HashAlgorithmName.SHA256))
        };
        var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, roster.ProfileId,
            version.VersionHash, deviceId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with
        {
            Signature = Convert.ToBase64String(candidate.SignData(
                SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
        };
        var draft = new WorldAuthorityRecord(1, proposal, roster, version, [],
            Convert.ToBase64String(owner.SignData(WorldAuthorityTrust.OwnerBasis(proposal), HashAlgorithmName.SHA256)),
            "", receipt);
        return draft with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(draft)) };
    }

    private static int[] FreeLocalPorts(int count)
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var used = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners())
            .Select(item => item.Port).ToHashSet();
        return Enumerable.Range(50000, 10000).Where(port => !used.Contains(port)).Take(count).ToArray();
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException or JsonException)
        { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class FilesOnlyDriver : IGameServerDriver
    {
        internal int ProcessAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Synthetic recovery files only";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        private InvalidOperationException NoProcess() { ProcessAttempts++; return new("No process test is permitted."); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
    }
}
