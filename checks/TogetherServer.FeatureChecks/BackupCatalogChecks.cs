using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Register in the independent feature harness only. This operates on fresh
// synthetic files/protected state and never runs a driver, process, listener,
// console, desktop, app, browser, dialog, installer or real backup/world.
internal static class BackupCatalogChecks
{
    internal static void Run(string root)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        Require(HostManager.OwnerGameBackupOutcome("OwnerConfirmed", "Unobserved", "Unobserved") == "Incomplete" &&
                HostManager.OwnerGameBackupOutcome("OwnerConfirmed", "OwnerConfirmed", "OwnerConfirmed") == "Passed" &&
                HostManager.OwnerGameBackupOutcome("OwnerConfirmed", "OwnerConfirmed", "OwnerFailed") == "Failed" &&
                HostManager.OwnerGameBackupOutcome("Ready", "Passed", "ProcessExited") == "Incomplete",
            "owner game rehearsal inferred load/change/restart from partial or process/hash evidence");
        var checkRoot = Path.Combine(Path.GetFullPath(root), "backup-catalog-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(checkRoot, "synthetic-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "synthetic.bin"), "disposable catalog bytes");
        var clock = new CatalogClock();
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Fixture,
            Name = "Synthetic catalog checks",
            WorldId = "SyntheticWorld",
            WorldDirectory = source,
            Backups = new() { RetentionCount = 3, MinimumFreeSpaceMb = 16 }
        };
        var dataRoot = Path.Combine(checkRoot, "synthetic-app-data");
        Guid firstId;
        Guid secondId;
        string firstIdentity;
        using (var data = new LocalData(dataRoot))
        {
            data.SaveSettings(new HostSettings { Profiles = [profile] });
            var service = new WorldBackupService(data, clock, _ => 8L * 1024 * 1024 * 1024);
            var first = service.Create(profile, BackupKinds.Manual);
            Require(first.Ok && first.Backup is not null, "first synthetic catalog backup did not complete");
            firstId = first.Backup!.Id;
            clock.Advance();
            var second = service.Create(profile, BackupKinds.Manual);
            Require(second.Ok && second.Backup is not null, "second synthetic catalog backup did not complete");
            secondId = second.Backup!.Id;
            var before = service.Catalog(profile);
            Require(before.Ok && before.Backups.Count == 2 && before.EvidenceAvailable &&
                    before.Backups.All(item => item.MetadataAvailable && item.Evidence.Count == 0),
                "catalog inspection manufactured verification results or lost valid metadata");
            Require(before.RetainedSizeBytes == first.Backup.SizeBytes + second.Backup.SizeBytes &&
                    before.AvailableSpaceBytes == 8L * 1024 * 1024 * 1024 &&
                    before.Retention is { RetentionCount: 3, MinimumFreeSpaceBytes: 16L * 1024 * 1024 },
                "capacity or saved retention policy was not projected exactly");
            var summaries = before.Backups.ToArray();
            Require(summaries[0].PayloadSha256 == summaries[1].PayloadSha256 &&
                    service.BackupEvidenceIdentity(profile.Id, firstId) != service.BackupEvidenceIdentity(profile.Id, secondId),
                "identical payload summaries were confused with exact completed backup identity");
            firstIdentity = service.BackupEvidenceIdentity(profile.Id, firstId)!;
            Require(service.BackupIdForEvidenceIdentity(profile.Id, firstIdentity) == firstId &&
                    service.BackupIdForEvidenceIdentity(Guid.NewGuid(), firstIdentity) is null,
                "a rehearsal completion identity resolved another server");
            var directory = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"), firstId.ToString("N") + ".backup");
            var markerPath = Path.Combine(directory, "complete.json");
            var payloadPath = Path.Combine(directory, "payload", "synthetic.bin");
            var originalMarker = File.ReadAllBytes(markerPath);
            var originalPayload = File.ReadAllBytes(payloadPath);
            var verified = service.Verify(profile, firstId);
            Require(verified.Ok && service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    BackupEvidenceKinds.Integrity, "Passed", verified.CheckedUtc, verified.Code),
                "a measured exact-backup integrity result was not saved");
            var onlyIntegrity = service.Catalog(profile).Backups.Single(item => item.BackupId == firstId).Evidence;
            Require(onlyIntegrity.Count == 1 && onlyIntegrity[0].Kind == BackupEvidenceKinds.Integrity &&
                    service.Catalog(profile).Backups.Single(item => item.BackupId == secondId).Evidence.Count == 0,
                "integrity was credited to another stage or another backup");
            Require(!service.RecordBackupEvidence(profile.Id, firstId, service.BackupEvidenceIdentity(profile.Id, secondId),
                    BackupEvidenceKinds.Vault, "Passed", clock.GetUtcNow(), "VaultCopyVerified") &&
                    !service.RecordBackupEvidence(Guid.NewGuid(), firstId, firstIdentity,
                    BackupEvidenceKinds.Vault, "Passed", clock.GetUtcNow(), "VaultCopyVerified") &&
                    !service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    "GameReady", "Passed", clock.GetUtcNow(), "Forged") &&
                    !service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    BackupEvidenceKinds.Vault, "Passed", clock.GetUtcNow(), "BackupVerified") &&
                    !service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    BackupEvidenceKinds.Integrity, "Passed", clock.GetUtcNow().AddMinutes(1), "BackupVerified"),
                "wrong identity, stage or future dated result was accepted");
            clock.Advance();
            var vaultRoot = Path.Combine(checkRoot, "synthetic-vault");
            Directory.CreateDirectory(vaultRoot);
            var vault = service.CopyToVault(profile, firstId, vaultRoot);
            Require(vault.Ok && service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    BackupEvidenceKinds.Vault, "Passed", vault.CompletedUtc, vault.Code),
                "a measured synthetic vault copy was not recorded independently");
            clock.Advance();
            // Hash-only scratch rehearsal is the existing reviewed file operation.
            var rehearsal = service.RehearseRestore(profile, firstId);
            Require(rehearsal.Ok && service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    BackupEvidenceKinds.HashRehearsal, "Passed", rehearsal.CompletedUtc, rehearsal.Code),
                "synthetic hash rehearsal outcome was not retained separately");
            Require(File.ReadAllBytes(markerPath).SequenceEqual(originalMarker) &&
                    SHA256.HashData(File.ReadAllBytes(payloadPath)).SequenceEqual(SHA256.HashData(originalPayload)),
                "catalog/evidence/rehearsal changed the immutable synthetic source backup");
            File.WriteAllText(payloadPath, "synthetic damage");
            clock.Advance();
            var damaged = service.Verify(profile, firstId);
            Require(!damaged.Ok && service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                    BackupEvidenceKinds.Integrity, "Failed", damaged.CheckedUtc, damaged.Code),
                "failed measured integrity retry was not recorded");
            var failed = service.Catalog(profile).Backups.Single(item => item.BackupId == firstId).Evidence;
            Require(failed.Single(item => item.Kind == BackupEvidenceKinds.Integrity).Outcome == "Failed" &&
                    failed.Single(item => item.Kind == BackupEvidenceKinds.HashRehearsal).Outcome == "Passed" &&
                    failed.Single(item => item.Kind == BackupEvidenceKinds.Vault).Outcome == "Passed" &&
                    failed.All(item => item.Kind != BackupEvidenceKinds.OwnerGameRehearsal),
                "failed retry kept a prior integrity pass, changed another measured stage or inferred game acceptance");
            Require(service.RecordBackupEvidence(profile.Id, firstId, firstIdentity, BackupEvidenceKinds.Integrity,
                    "Passed", verified.CheckedUtc, verified.Code) &&
                    service.Catalog(profile).Backups.Single(item => item.BackupId == firstId).Evidence
                        .Single(item => item.Kind == BackupEvidenceKinds.Integrity).Outcome == "Failed",
                "an older async result replaced a newer failed measurement");
            File.WriteAllBytes(payloadPath, originalPayload);
            File.WriteAllText(markerPath, "{}");
            var unavailable = service.Catalog(profile).Backups.Single(item => item.BackupId == firstId);
            Require(!unavailable.MetadataAvailable && unavailable.PayloadSha256 is null && unavailable.Evidence.Count == 0 &&
                    !service.RecordBackupEvidence(profile.Id, firstId, firstIdentity,
                        BackupEvidenceKinds.Integrity, "Passed", clock.GetUtcNow(), "BackupVerified"),
                "changed completion metadata retained prior evidence credit");
            File.WriteAllBytes(markerPath, originalMarker);
            Require(service.UpdateBookmark(profile.Id, firstId, new("Synthetic checkpoint", true)).Ok &&
                    service.Catalog(profile).Backups.Single(item => item.BackupId == firstId).Evidence.Count == 3,
                "renaming/pinning changed exact completion evidence identity");
        }
        using (var reopened = new LocalData(dataRoot))
        {
            var service = new WorldBackupService(reopened, clock, _ => throw new IOException("synthetic unavailable space"));
            var current = service.Catalog(profile);
            Require(current.AvailableSpaceBytes is null && current.EvidenceAvailable &&
                    current.Backups.Single(item => item.BackupId == firstId).Evidence.Single(item => item.Kind == BackupEvidenceKinds.Integrity).Outcome == "Failed" &&
                    current.Backups.Single(item => item.BackupId == secondId).Evidence.Count == 0,
                "dated evidence did not survive reopening or space failure became zero");
            var excessive = Enumerable.Range(0, WorldBackupService.MaximumBackupEvidenceResults + 1).Select(index =>
                new SavedBackupEvidence(profile.Id, Guid.NewGuid(), firstIdentity, BackupEvidenceKinds.Integrity,
                    "Passed", clock.GetUtcNow(), "BackupVerified")).ToArray();
            reopened.SaveProtected("backup-evidence-v1.protected", JsonSerializer.SerializeToUtf8Bytes(new BackupEvidenceState(1, excessive)));
            var bytes = reopened.LoadProtected("backup-evidence-v1.protected")!;
            Require(!service.Catalog(profile).EvidenceAvailable && service.Catalog(profile).Backups.All(item => item.Evidence.Count == 0) &&
                    !service.RecordBackupEvidence(profile.Id, firstId, firstIdentity, BackupEvidenceKinds.Integrity,
                        "Passed", clock.GetUtcNow(), "BackupVerified") &&
                    reopened.LoadProtected("backup-evidence-v1.protected")!.SequenceEqual(bytes),
                "unbounded protected evidence became credit or was silently overwritten");
        }
        LeasedMeasurementRegression(checkRoot);
    }

    private static void LeasedMeasurementRegression(string root)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        var checkRoot = Path.Combine(root, "leased-evidence-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(checkRoot, "synthetic-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "synthetic.bin"), "original synthetic evidence bytes");
        using var data = new LocalData(Path.Combine(checkRoot, "synthetic-app-data"));
        var clock = new CatalogClock();
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Fixture,
            Name = "Source-only evidence lease checks",
            WorldId = "SyntheticEvidence",
            WorldDirectory = source,
            ExecutablePath = Path.Combine(checkRoot, "never-launched.exe"),
            Backups = new() { RetentionCount = 3, MinimumFreeSpaceMb = 0 }
        };
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var registry = new GameServerRegistry([new EvidenceOnlyDriver()], PortProbeMode.ObserveOnly);
        var service = new WorldBackupService(data, clock, _ => 8L * 1024 * 1024 * 1024, registry);
        var created = service.Create(profile, BackupKinds.Manual);
        Require(created.Ok && created.Backup is not null, "synthetic lease regression backup did not complete");
        var backup = created.Backup!;
        var directory = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"), backup.Id.ToString("N") + ".backup");
        var markerPath = Path.Combine(directory, "complete.json");
        var payloadDirectory = Path.Combine(directory, "payload");
        var payloadPath = Path.Combine(payloadDirectory, "synthetic.bin");
        var markerA = File.ReadAllBytes(markerPath);
        var payloadA = File.ReadAllBytes(payloadPath);
        var identityA = service.BackupEvidenceIdentity(profile.Id, backup.Id);
        var payloadB = payloadA.Select(value => (byte)(value ^ 0x5a)).ToArray();
        var manifestB = JsonSerializer.Deserialize<BackupManifest>(markerA, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        manifestB.Files = [new("synthetic.bin", payloadB.LongLength, Convert.ToHexString(SHA256.HashData(payloadB)))];
        var markerB = JsonSerializer.SerializeToUtf8Bytes(manifestB, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(identityA is not null && !SHA256.HashData(payloadA).SequenceEqual(SHA256.HashData(payloadB)),
            "ABA regression did not create distinct same-sized completions");
        var manager = new HostManager(data, registry, clock);
        var vaultRoot = Path.Combine(checkRoot, "synthetic-vault");
        Directory.CreateDirectory(vaultRoot);

        foreach (var kind in new[] { BackupEvidenceKinds.Integrity, BackupEvidenceKinds.Vault, BackupEvidenceKinds.HashRehearsal })
        {
            clock.Advance();
            var replacement = Path.Combine(directory, "aba-marker-" + Guid.NewGuid().ToString("N") + ".new");
            File.WriteAllBytes(replacement, markerB);
            var movedDirectory = directory + ".aba-move";
            var movedPayload = payloadDirectory + ".aba-move";
            var beforeMeasurementBlocked = false;
            var duringRecordingBlocked = false;
            var replacementBlocked = false;
            var ancestorMoveBlocked = false;
            var payloadMoveBlocked = false;
            var reads = 0;
            // The first clock read is inside the actual measured operation, after
            // capture. The second is inside evidence recording, before its reread.
            // Without a retained lease this deterministically measures B then
            // restores A, making the old before/after identity checks both pass.
            clock.BeforeUtcRead = () =>
            {
                var read = Interlocked.Increment(ref reads);
                if (read == 1)
                {
                    beforeMeasurementBlocked = WriteBlocked(markerPath, markerB);
                    replacementBlocked = ReplaceBlocked(replacement, markerPath);
                    ancestorMoveBlocked = MoveBlocked(directory, movedDirectory, checkRoot);
                    payloadMoveBlocked = MoveBlocked(payloadDirectory, movedPayload, checkRoot);
                    if (!beforeMeasurementBlocked || !replacementBlocked) File.WriteAllBytes(payloadPath, payloadB);
                }
                else if (read == 2)
                {
                    duringRecordingBlocked = WriteBlocked(markerPath, markerA);
                    if (!duringRecordingBlocked) File.WriteAllBytes(payloadPath, payloadA);
                }
            };
            try
            {
                if (kind == BackupEvidenceKinds.Integrity)
                {
                    var measured = manager.VerifyBackupWithEvidenceAsync(profile.Id, backup.Id).GetAwaiter().GetResult();
                    Require(measured.Ok && measured.Code == "BackupVerified", "leased verification changed its measured result");
                }
                else if (kind == BackupEvidenceKinds.Vault)
                {
                    var measured = manager.CopyBackupToVaultWithEvidenceAsync(profile.Id, backup.Id, vaultRoot).GetAwaiter().GetResult();
                    Require(measured.Ok && measured.Code == "VaultCopyVerified" && measured.FileCount == backup.FileCount &&
                        measured.SizeBytes == backup.SizeBytes, "leased vault copy changed its measured result");
                }
                else
                {
                    var measured = manager.RehearseRestoreWithEvidenceAsync(profile.Id, backup.Id).GetAwaiter().GetResult();
                    Require(measured.Ok && measured.Code == "RestoreRehearsalCompleted" && measured.FileCount == backup.FileCount &&
                        measured.SizeBytes == backup.SizeBytes, "leased scratch rehearsal changed its measured result");
                }
            }
            finally { clock.BeforeUtcRead = null; }
            Require(reads >= 2 && beforeMeasurementBlocked && duringRecordingBlocked && replacementBlocked &&
                    ancestorMoveBlocked && payloadMoveBlocked,
                "completion/ancestor leases did not cover both the measured operation and evidence recording");
            Require(File.ReadAllBytes(markerPath).SequenceEqual(markerA) && File.ReadAllBytes(payloadPath).SequenceEqual(payloadA) &&
                    service.BackupEvidenceIdentity(profile.Id, backup.Id) == identityA,
                "an ABA attempt changed the immutable completion or measured payload");
            var evidence = manager.BackupCatalogAsync(profile.Id).GetAwaiter().GetResult().Backups.Single(item => item.BackupId == backup.Id).Evidence;
            Require(evidence.Single(item => item.Kind == kind).Outcome == "Passed", "a leased measurement lost its dated outcome");
            File.Delete(replacement);
            // This succeeds only after the wrapper disposed its retained lease.
            File.WriteAllBytes(markerPath, markerA);
        }

        var historyPath = Path.Combine(data.RootPath, "backup-evidence-v1.protected");
        var historyBytes = File.ReadAllBytes(historyPath);
        using (var heldHistory = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            clock.Advance();
            var verified = manager.VerifyBackupWithEvidenceAsync(profile.Id, backup.Id).GetAwaiter().GetResult();
            Require(verified.Ok && verified.Code == "BackupVerified" && verified.Message.Contains("dated result could not be saved", StringComparison.Ordinal),
                "optional evidence persistence changed a successful measured verification");
            clock.Advance();
            var copied = manager.CopyBackupToVaultWithEvidenceAsync(profile.Id, backup.Id, vaultRoot).GetAwaiter().GetResult();
            Require(copied.Ok && copied.Code == "VaultCopyVerified" && copied.FileCount == backup.FileCount && copied.SizeBytes == backup.SizeBytes &&
                    copied.Message.Contains("dated result could not be saved", StringComparison.Ordinal),
                "optional evidence persistence changed a successful measured vault result");
            clock.Advance();
            var rehearsed = manager.RehearseRestoreWithEvidenceAsync(profile.Id, backup.Id).GetAwaiter().GetResult();
            Require(rehearsed.Ok && rehearsed.Code == "RestoreRehearsalCompleted" && rehearsed.FileCount == backup.FileCount && rehearsed.SizeBytes == backup.SizeBytes &&
                    rehearsed.Message.Contains("dated result could not be saved", StringComparison.Ordinal),
                "optional evidence persistence changed a successful measured scratch rehearsal");
            File.WriteAllBytes(payloadPath, payloadB);
            clock.Advance();
            var failed = manager.VerifyBackupWithEvidenceAsync(profile.Id, backup.Id).GetAwaiter().GetResult();
            Require(!failed.Ok && failed.Code == "BackupIntegrityFailed" && failed.Message.Contains("dated result could not be saved", StringComparison.Ordinal),
                "optional evidence persistence hid a measured integrity failure");
            File.WriteAllBytes(payloadPath, payloadA);
        }
        Require(File.ReadAllBytes(historyPath).SequenceEqual(historyBytes) && File.ReadAllBytes(markerPath).SequenceEqual(markerA) &&
                File.ReadAllBytes(payloadPath).SequenceEqual(payloadA),
            "failed optional evidence writes changed prior history or the original completed backup");
    }

    private static bool WriteBlocked(string path, byte[] bytes)
    {
        try { File.WriteAllBytes(path, bytes); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
    private static bool ReplaceBlocked(string replacement, string destination)
    {
        try { File.Move(replacement, destination, true); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
    private static bool MoveBlocked(string source, string destination, string testRoot)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination) ||
            !AppInstance.ContainsPath(testRoot, source) || !AppInstance.ContainsPath(testRoot, destination))
            throw new InvalidOperationException("Synthetic directory moves must remain within this regression's private root.");
        try { Directory.Move(source, destination); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        Directory.Move(destination, source);
        return false;
    }

    private sealed class EvidenceOnlyDriver : IGameServerDriver
    {
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Source-only backup evidence adapter";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
        public GameHealthResult Health(ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
    }

    private sealed class CatalogClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        internal Action? BeforeUtcRead { get; set; }
        public override DateTimeOffset GetUtcNow() { BeforeUtcRead?.Invoke(); return now; }
        internal void Advance() => now = now.AddSeconds(1);
    }
}
