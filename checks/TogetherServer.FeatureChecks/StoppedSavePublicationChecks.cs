using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated files and identities only. The fake driver refuses all process
// operations, and the archived-process probe is replaced before every action.
internal static class StoppedSavePublicationChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        await RecaptureAsync(root);
        await IntactBackupAsync(root);
        await UnknownPublishedStateAsync(root);
        await InterruptedPublicationAsync(root);
        foreach (var change in new[] { "extra", "missing", "equal-size", "setup", "consent", "key", "revoked", "grant", "expiry", "fork", "archive", "missing-archive", "ambiguous", "process", "newer-rolling", "marker", "provenance", "setup-checkpoint", "selection", "reservation", "overlap" })
            await RefusalAsync(root, change);
        foreach (var change in new[] { "extra-source", "stage-edit", "stage-missing", "stage-extra", "setup", "consent", "revoked", "expiry", "archive", "selection", "cancel" })
            await CopyRaceAsync(root, change);
    }

    private static async Task RecaptureAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, corrupt: true);
        await f.RequireUnpublishedAsync();
        Require(await f.PendingAsync(), "metadata did not expose the exact unpublished stopped backup");
        var old = f.PreservedBackups();
        var world = Tree(f.Profile.WorldDirectory);
        var keys = f.Keys();
        var result = await f.RetryAsync();
        Require(result.Ok && result.Code == "StoppedProgressShared", "the exact stopped-world recapture was refused: " + result.Code);
        var catalog = f.Data.LoadBackupCatalog();
        var fresh = catalog.Records.Last();
        Require(catalog.Records.Count == 3 && fresh.Id != f.Rolling.Id && fresh.BackupKind == BackupKinds.Rolling &&
            fresh.RecapturedFromBackupId == f.Rolling.Id && fresh.StoppedOperationId == f.Archive.OperationId,
            "the fresh completion did not directly preserve its original stop/backup binding");
        var proof = f.Backups.ReadStoppedSaveProof(f.Profile);
        Require(proof.Backup.Id == fresh.Id && proof.OriginalBackup.Id == f.Rolling.Id &&
            proof.Manifest.RecapturedFromBackupId == fresh.RecapturedFromBackupId &&
            proof.Manifest.StoppedOperationId == fresh.StoppedOperationId,
            "the catalog and completion provenance were not verified together");
        var head = f.Store.ReadPublishedManifestHead(f.Branch);
        Require(head is not null && head.BackupId == fresh.Id && head.ParentHash == f.Base.VersionHash &&
            head.Number == f.Base.Number + 1 && head.CaptureKind == SharedWorldCaptureKinds.PostStopBackup &&
            SharedWorldSeparateCopyStore.VerifySnapshotVersion(f.Branch, head),
            "the stopped save was not signed as the exact next child of this branch");
        Require((await f.Manager.VerifiedLocalContinuationAsync(f.SourceId, f.Profile.Id, default))?.VersionHash == head.VersionHash &&
            !await f.PendingAsync(), "the repaired inventory did not become the verified local continuation");
        Require(f.PreservedBackups() == old && Tree(f.Profile.WorldDirectory) == world && f.Keys() == keys,
            "recovery changed an earlier backup, failure, world byte or local signing key");
        var repeated = await f.RetryAsync();
        Require(repeated.Ok && repeated.Code == "StoppedProgressUnchanged" && f.Data.LoadBackupCatalog().Records.Count == 3 &&
            f.Store.ReadPublishedManifestHead(f.Branch)?.VersionHash == head.VersionHash,
            "repeating the explicit action created a second backup or child");
        f.NoProcess();
    }

    private static async Task IntactBackupAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, corrupt: false);
        var old = f.PreservedBackups();
        var result = await f.RetryAsync();
        Require(result.Ok && f.Data.LoadBackupCatalog().Records.Count == 2 &&
            f.Store.ReadPublishedManifestHead(f.Branch)?.BackupId == f.Rolling.Id && f.PreservedBackups() == old,
            "a valid completed stopped backup was unnecessarily recaptured or altered");
        f.NoProcess();
    }

    private static async Task InterruptedPublicationAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, corrupt: true);
        var old = f.PreservedBackups();
        // Fail the first lifecycle recheck after the new catalog append. No
        // production interruption hook or real process is needed.
        f.Manager.StoppedSaveProcessAbsentForChecks = _ => f.Data.LoadBackupCatalog().Records.Count == 2;
        Require(!(await f.RetryAsync()).Ok && f.Data.LoadBackupCatalog().Records.Count == 3 &&
            f.Store.ReadPublishedManifestHead(f.Branch) is null,
            "an interrupted post-recapture publication advanced a signed pointer");
        var fresh = f.Data.LoadBackupCatalog().Records.Last();
        f.Manager.StoppedSaveProcessAbsentForChecks = _ => true;
        Require((await f.RetryAsync()).Ok && f.Data.LoadBackupCatalog().Records.Count == 3 &&
            f.Store.ReadPublishedManifestHead(f.Branch)?.BackupId == fresh.Id && f.PreservedBackups() == old,
            "retry did not reuse the fresh completion against its original archive-backed proof");
        f.NoProcess();
    }

    private static async Task UnknownPublishedStateAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, corrupt: true);
        var published = f.Store.PublishedRoot(f.Branch);
        Directory.CreateDirectory(published);
        File.WriteAllText(Path.Combine(published, "latest.json"), "{}");
        var retained = Tree(published);
        var world = Tree(f.Profile.WorldDirectory);
        var before = f.PreservedBackups();
        var refused = false;
        try { await f.PendingAsync(); }
        catch (InvalidDataException) { refused = true; }
        Require(refused && Tree(published) == retained && Tree(f.Profile.WorldDirectory) == world &&
            f.PreservedBackups() == before,
            "unreadable publication metadata was projected as prepared or rewritten during status");
        f.NoProcess();
    }

    private static async Task RefusalAsync(string root, string change)
    {
        using var f = await Fixture.CreateAsync(root, corrupt: true);
        switch (change)
        {
            case "extra": File.WriteAllText(Path.Combine(f.Profile.WorldDirectory, "new.dat"), "new"); break;
            case "missing": File.Delete(Path.Combine(f.Profile.WorldDirectory, "stopped-0.dat")); break;
            case "equal-size": File.WriteAllText(Path.Combine(f.Profile.WorldDirectory, "stopped-0.dat"), "changed-0"); break;
            case "setup": f.ChangeSetup(); break;
            case "consent": new WorldPeerReceiveConsentStore(f.Data).Set(f.Root, f.DeviceId, false); break;
            case "key": f.Data.DeleteProtected(WorldAuthorityStore.HostingKeyName(f.SourceId)); break;
            case "revoked": case "grant": case "expiry": case "fork": f.ChangeRoster(change); break;
            case "archive": f.Data.SaveRunArchive([f.Archive with { CompletedBackupId = Guid.NewGuid() }]); break;
            case "missing-archive": f.Data.SaveRunArchive([]); break;
            case "ambiguous": f.Data.SaveRunArchive([f.Archive, f.Archive with { OperationId = Guid.NewGuid() }]); break;
            case "process": f.Manager.StoppedSaveProcessAbsentForChecks = _ => false; break;
            case "newer-rolling":
                f.Clock.Now = f.Clock.Now.AddSeconds(1);
                Require(f.Backups.Create(f.Profile, BackupKinds.Rolling).Ok, "the later synthetic backup could not be created");
                break;
            case "marker":
                var marker = Path.Combine(f.BackupRoot(f.Rolling), "complete.json");
                File.WriteAllText(marker, File.ReadAllText(marker).Insert(1, "\"backupId\":\"" + f.Rolling.Id + "\","));
                break;
            case "provenance":
                var catalog = f.Data.LoadBackupCatalog();
                var latest = catalog.Records.Single(item => item.Id == f.Rolling.Id);
                latest.RecapturedFromBackupId = latest.Id;
                latest.StoppedOperationId = f.Archive.OperationId;
                f.Data.SaveBackupCatalog(catalog);
                break;
            case "setup-checkpoint": File.AppendAllText(Path.Combine(f.BackupRoot(f.Rolling), "setup.protected"), "changed"); break;
            case "selection": f.SelectionCurrent = false; break;
            case "reservation": Field<HashSet<Guid>>(f.Manager, "worldCopyReservations").Add(f.Profile.Id); break;
            case "overlap":
                var settings = Field<HostSettings>(f.Manager, "settings");
                settings.Profiles.Add(new()
                {
                    Id = Guid.NewGuid(),
                    Kind = GameKinds.Fixture,
                    WorldDirectory = Path.Combine(f.Profile.WorldDirectory, "other"),
                    WorldId = "Other"
                });
                f.Data.SaveSettings(settings);
                break;
        }
        var count = f.Data.LoadBackupCatalog().Records.Count;
        var world = Tree(f.Profile.WorldDirectory);
        var old = f.PreservedBackups();
        var result = await f.RetryAsync();
        Require(!result.Ok && result.Code is "StoppedProgressProofRequired" or "StoppedProgressBusy" or "StoppedProgressSharingChanged" &&
            f.Data.LoadBackupCatalog().Records.Count == count && f.Store.ReadPublishedManifestHead(f.Branch) is null &&
            f.PreservedBackups() == old && Tree(f.Profile.WorldDirectory) == world,
            "an unproved " + change + " source advanced publication, fell back, or changed retained bytes");
        f.NoProcess();
    }

    private static async Task CopyRaceAsync(string root, string change)
    {
        using var f = await Fixture.CreateAsync(root, corrupt: true);
        using var canceled = new CancellationTokenSource();
        var old = f.PreservedBackups();
        f.Backups.BeforeCompletionVerificationForChecks = stage =>
        {
            switch (change)
            {
                case "extra-source": File.WriteAllText(Path.Combine(f.Profile.WorldDirectory, "arrived.dat"), "late"); break;
                case "stage-edit": File.WriteAllText(Path.Combine(stage, "payload", "stopped-0.dat"), "changed-0"); break;
                case "stage-missing": File.Delete(Path.Combine(stage, "payload", "stopped-0.dat")); break;
                case "stage-extra": File.WriteAllText(Path.Combine(stage, "payload", "arrived.dat"), "late"); break;
                case "setup": f.ChangeSetup(); break;
                case "consent": new WorldPeerReceiveConsentStore(f.Data).Set(f.Root, f.DeviceId, false); break;
                case "revoked": case "expiry": f.ChangeRoster(change); break;
                case "archive": f.Data.SaveRunArchive([f.Archive with { CompletedBackupId = Guid.NewGuid() }]); break;
                case "selection": f.SelectionCurrent = false; break;
                case "cancel": canceled.Cancel(); break;
            }
        };
        try
        {
            var result = await f.RetryAsync(canceled.Token);
            Require(change != "cancel" && !result.Ok, "a changed copied source or cancellation was accepted");
        }
        catch (OperationCanceledException) { Require(change == "cancel", "an uncanceled copy threw cancellation"); }
        Require(f.Data.LoadBackupCatalog().Records.Count == 2 && f.Store.ReadPublishedManifestHead(f.Branch) is null &&
            f.PreservedBackups() == old && !Directory.EnumerateDirectories(Path.Combine(f.Data.BackupsRoot, f.Profile.Id.ToString("N")))
                .Any(path => path.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)),
            "a copy/setup race completed a backup, signed a pointer, or changed the old proof");
        f.NoProcess();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly Guid SourceId = Guid.NewGuid();
        internal readonly Guid DeviceId = Guid.NewGuid();
        private readonly Guid groupId = Guid.NewGuid();
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly FriendService friends;
        internal readonly Clock Clock = new();
        internal readonly LocalHostingChecks.FilesOnlyDriver Driver = new();
        internal readonly HostManager Manager;
        private readonly FriendLocalHostingService service;
        internal readonly SharedWorldVersion Base;
        internal readonly SharedWorldPeerRoot Root;
        internal ServerProfile Profile = null!;
        internal WorldSeparateCopyBranch Branch = null!;
        internal WorldBackupRecord Rolling = null!;
        internal ManagedRunArchive Archive = null!;
        internal readonly WorldBackupService Backups;
        internal bool SelectionCurrent = true;
        internal SharedWorldSeparateCopyStore Store => new(Data);

        private Fixture(string root)
        {
            Data = new(Path.Combine(root, "stopped-save-" + Guid.NewGuid().ToString("N")));
            Data.SaveSettings(new HostSettings { CompanionPort = 53310, Profiles = [] });
            Require(Data.ApprovedGamePorts.Save(new(GameKinds.Fixture, 53312, null)).Ok,
                "the generated stopped world's local port approval failed");
            var draft = new SharedWorldRoster(3, groupId, SourceId, 1, 1, true, Public(owner),
                [new(DeviceId, Public(member), new(Receive: true, EligibleHost: true), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Public(owner));
            var roster = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            Data.SaveProtected($"shared-world-pc-signing-{DeviceId:N}.protected", member.ExportPkcs8PrivateKey());
            Data.SaveProtected($"shared-world-roster-{DeviceId:N}-{SourceId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            Root = new(Public(owner), SourceId, groupId);
            var files = Enumerable.Range(0, 5).Select(i => (Path: $"base-{i}.dat", Bytes: Encoding.UTF8.GetBytes($"original{i}")))
                .Append((Path: "server.cfg", Bytes: Enumerable.Repeat((byte)'c', 43).ToArray()))
                .Append((Path: "world.dat", Bytes: Encoding.UTF8.GetBytes("unchanged-world")))
                .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            var vault = Path.Combine(Data.RootPath, "received-shared-worlds", DeviceId.ToString("N"), SourceId.ToString("N"));
            SharedWorldVersion? prior = null;
            for (var number = 1; number <= 3; number++)
            {
                prior = SharedWorldService.SignVersion(new(4, groupId, number, prior?.VersionHash, SourceId, GameKinds.Fixture,
                    "GeneratedStoppedWorld", DateTimeOffset.UtcNow.AddMinutes(-13 + number), SharedWorldCaptureKinds.PostStopBackup,
                    Guid.NewGuid(), new(53310, false, "Unknown", [], []), files.Select(item =>
                        new SharedWorldFile(item.Path, item.Bytes.LongLength, Convert.ToHexString(SHA256.HashData(item.Bytes)))).ToArray(), "", "", ""), owner);
                FriendLink.KeepSignedManifest(vault, prior);
            }
            Base = prior!;
            var directory = Path.Combine(vault, Base.VersionHash);
            Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
            foreach (var file in files) File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, file.Path), file.Bytes);
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(Base, Json));
            FriendLink.KeepSignedManifest(vault, Base);
            File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(Base, Json));
            Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DeviceId = DeviceId,
                DisplayName = "Generated stopped-save check",
                Endpoint = "https://192.0.2.10:5131",
                Fingerprint = new string('A', 64),
                Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                ApprovedSharedWorldGroups = new() { [SourceId] = groupId },
                SharedWorldSigningKeys = new() { [SourceId] = Public(owner) },
                SharedRosterFloors = new() { [SourceId] = new(groupId, 1, 1, roster.Signature) },
                LastSharedHostManifests = new() { [SourceId] = Base }
            }, Json));
            var executable = Path.Combine(Data.RootPath, "never-run.synthetic");
            File.WriteAllText(executable, "not a program");
            Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly), Clock);
            Manager.StoppedSaveProcessAbsentForChecks = _ => true;
            Backups = Field<WorldBackupService>(Manager, "backups");
            friends = new(Data, (_, _) => throw new InvalidOperationException("No network is allowed in stopped-save checks."));
            service = new(Data, friends, Manager, _ => [executable]);
        }

        internal static async Task<Fixture> CreateAsync(string root, bool corrupt)
        {
            var f = new Fixture(root);
            try
            {
                var prepared = await f.service.PrepareAsync(f.SourceId, new(true, true));
                Require(prepared.Ok && prepared.LocalProfileId is not null && prepared.BranchHash is not null,
                    "the generated stopped world did not prepare: " + prepared.Code);
                f.Profile = Field<HostSettings>(f.Manager, "settings").Profiles.Single(item => item.Id == prepared.LocalProfileId);
                f.Profile.Backups.MinimumFreeSpaceMb = 0;
                f.Profile.Backups.RetentionCount = 20;
                f.Data.SaveSettings(Field<HostSettings>(f.Manager, "settings"));
                f.Branch = f.Store.Read(f.SourceId).Single();
                var state = SharedWorldBranchLiveSource.ReadState(f.Data, f.Profile.Id)!;
                f.Data.SaveProtected(SharedWorldBranchLiveSource.StateName(f.Profile.Id), JsonSerializer.SerializeToUtf8Bytes(state with { Started = true }));
                // Equal count, different inventory: five old base paths become
                // five stopped-session paths before either completed backup.
                for (var i = 0; i < 5; i++) File.Move(Path.Combine(f.Profile.WorldDirectory, $"base-{i}.dat"), Path.Combine(f.Profile.WorldDirectory, $"stopped-{i}.dat"));
                var rolling = f.Backups.Create(f.Profile, BackupKinds.Rolling);
                Require(rolling.Ok && rolling.Backup is not null, "the generated rolling backup did not complete");
                f.Rolling = rolling.Backup;
                Require(f.Backups.Create(f.Profile, BackupKinds.Manual).Ok, "the healthy independent manual backup did not complete");
                var ended = f.Rolling.CreatedUtc.AddSeconds(-1);
                var started = ended.AddMinutes(-1);
                f.Archive = new(f.Profile.Id, Guid.NewGuid(), f.Profile.Kind, f.Profile.WorldId, 12345, started.UtcTicks,
                    true, "Synthetic graceful Stop", f.Rolling.CreatedUtc.AddSeconds(1), false, 1, started, ended, 60,
                    ServerSessionEndReason.GracefulStop, ServerSessionOutcome.GracefulStop, 0, 0, ServerSessionBackupResult.Completed);
                f.Data.SaveRunArchive([f.Archive]);
                f.Clock.Now = f.Archive.ArchivedUtc.AddSeconds(1);
                var catalog = f.Data.LoadBackupCatalog();
                catalog.Failures.Add(new() { ProfileId = f.Profile.Id, FailedUtc = f.Clock.Now, Code = "RetainedSyntheticFailure", Message = "Keep this earlier record." });
                f.Data.SaveBackupCatalog(catalog);
                // Explicit recovery must preserve both records even though a
                // normal rolling capture would now enforce tighter retention.
                f.Profile.Backups.RetentionCount = 1;
                f.Data.SaveSettings(Field<HostSettings>(f.Manager, "settings"));
                if (corrupt) File.AppendAllText(Path.Combine(f.BackupRoot(f.Rolling), "payload", "server.cfg"), "bad!");
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        internal string BackupRoot(WorldBackupRecord backup) => Path.Combine(Data.BackupsRoot, Profile.Id.ToString("N"), backup.Id.ToString("N") + ".backup");
        internal Task<ActionResult> RetryAsync(CancellationToken token = default) => Manager.RetryStoppedSeparateSavePublicationAsync(SourceId, Profile.Id, Branch.BranchHash, token,
            () => { if (!SelectionCurrent) throw new InvalidDataException("The generated selection changed."); });
        internal Task<bool> PendingAsync() => Manager.StoppedSeparateSavePublicationPendingAsync(SourceId, Profile.Id, Branch.BranchHash, default);
        internal async Task RequireUnpublishedAsync()
        {
            try { await Manager.VerifiedLocalContinuationAsync(SourceId, Profile.Id, default); }
            catch (InvalidDataException error)
            {
                Require(error.Message == "This PC has progress that has not been shared safely. Keep this save and review it before switching hosts.",
                    "the inventory regression failed a different continuation predicate");
                return;
            }
            throw new InvalidOperationException("a changed inventory was accepted before stopped publication");
        }
        internal void ChangeSetup() { Profile.Crossplay = !Profile.Crossplay; Data.SaveSettings(Field<HostSettings>(Manager, "settings")); }
        internal void ChangeRoster(string change)
        {
            var current = Branch.Offer.Roster;
            var chain = new SharedWorldRosterChainStore(Data);
            chain.Append(current, Public(owner));
            var original = current.Members.Single();
            var changed = change switch
            {
                "revoked" => original with { Revoked = true },
                "grant" => original with { Grants = original.Grants with { EligibleHost = false } },
                "expiry" => original with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
                _ => original with { Grants = original.Grants with { Reshare = true } }
            };
            SharedWorldRoster Next(SharedWorldRosterMember entry)
            {
                var draft = current with
                {
                    Epoch = current.Epoch + 1,
                    Revision = current.Revision + 1,
                    PreviousRosterHash = SharedWorldRosterTrust.Hash(current),
                    Members = [entry],
                    Signature = ""
                };
                return draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            }
            chain.Append(Next(changed), Public(owner));
            if (change == "fork") chain.Append(Next(original with { Grants = original.Grants with { RecoveryVoter = true } }), Public(owner));
        }
        internal string PreservedBackups()
        {
            var catalog = Data.LoadBackupCatalog();
            var original = catalog.Records.Where(item => item.Id == Rolling.Id || item.BackupKind == BackupKinds.Manual).ToArray();
            return JsonSerializer.Serialize(original, Json) + JsonSerializer.Serialize(catalog.Failures, Json) +
                string.Join('|', original.Select(item => Tree(BackupRoot(item))));
        }
        internal string Keys() => Convert.ToHexString(SHA256.HashData(Data.LoadProtected(WorldAuthorityStore.HostingKeyName(SourceId))!)) +
            Convert.ToHexString(SHA256.HashData(Data.LoadProtected($"shared-world-pc-signing-{DeviceId:N}.protected")!));
        internal void NoProcess() => Require(Driver.ProcessAttempts == 0 && Data.LoadRuns().Count == 0, "stopped-save recovery attempted a process operation");
        public void Dispose() { friends.Dispose(); Data.Dispose(); owner.Dispose(); member.Dispose(); }
    }

    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Tree(string root) => string.Join('|', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ':' + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
