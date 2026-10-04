using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

internal static class SharedWorldLiveCaptureChecks
{
    internal static void Run()
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "shared-live-core",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            using var data = new LocalData(root);
            var backups = new WorldBackupService(data, TimeProvider.System,
                games: new GameServerRegistry(data, includeFixture: true));
            var shared = new SharedWorldService(data, backups)
            {
                LiveGameRegistryForChecks = new GameServerRegistry(data, includeFixture: true),
                FixtureLiveRunIdentityForChecks = run => run.ProcessId == 4242
            };
            var profile = new ServerProfile
            {
                Kind = GameKinds.Fixture,
                WorldId = "synthetic-live-world",
                WorldSource = "New",
                SharedSavesEnabled = true,
                GamePort = 34567,
                Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
            };
            // The profile ID determines the reviewed managed world directory.
            profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
            Directory.CreateDirectory(profile.WorldDirectory);
            var world = Path.Combine(profile.WorldDirectory, "world.dat");
            File.WriteAllText(world, "post-stop baseline");
            shared.PublishRoster(profile, []);
            var backup = backups.Create(profile, BackupKinds.Rolling);
            Require(backup.Ok && backup.Backup is not null, "synthetic post-Stop backup failed");
            var first = shared.PublishAfterStop(profile, backup.Backup!.Id);
            Require(first.Ok && first.Version is { Number: 1,
                CaptureKind: SharedWorldCaptureKinds.PostStopBackup },
                "existing post-Stop publication changed");
            Require(System.Text.Encoding.UTF8.GetString(shared.ReadChunk(first.Version!, 0, 0)) ==
                "post-stop baseline", "post-Stop chunk changed");

            var run = new ManagedRun
            {
                ProfileId = profile.Id, OperationId = Guid.NewGuid(), Kind = profile.Kind,
                WorldId = profile.WorldId, WorldDirectory = profile.WorldDirectory,
                ProcessId = 4242,
                StartTimeUtcTicks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
                WasReady = true
            };
            var completion = new LiveSaveCompletionEvidence(profile.Id, run.OperationId,
                run.ProcessId!.Value, run.StartTimeUtcTicks!.Value,
                LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true);
            ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                "an unrecorded managed run staged a save");
            data.SaveRuns([run]);
            shared.FixtureLiveRunIdentityForChecks = _ => false;
            ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                "an exited or mismatched process staged a live save");
            shared.FixtureLiveRunIdentityForChecks = current => current.ProcessId == 4242;
            ExpectInvalid(() => shared.StageLiveCapture(profile, run,
                completion with { Complete = false }), "incomplete exact-run evidence staged a save");
            ExpectInvalid(() => shared.StageLiveCapture(profile, run,
                completion with { Kind = LiveSaveEvidence.FrozenSnapshotQuery }),
                "a frozen query without its typed file list staged a live tree");
            ExpectInvalid(() => shared.StageLiveCapture(profile, run,
                completion with { Kind = LiveSaveEvidence.ClosedSaveArchive }),
                "a closed archive without its typed archive staged a live tree");
            ExpectInvalid(() => shared.StageLiveCapture(profile, run,
                completion with { OperationId = Guid.NewGuid() }),
                "another run's completion staged a save");
            profile.GamePort = 0;
            ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                "an invalid reviewed setup staged a save");
            profile.GamePort = 34567;
            shared.AfterLiveSourceScanForChecks = () => File.WriteAllText(world, "changed during scan");
            ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                "source drift during capture was accepted");
            shared.AfterLiveSourceScanForChecks = null;
            var excess = Path.Combine(profile.WorldDirectory, "excess");
            Directory.CreateDirectory(excess);
            for (var index = 0; index < SharedWorldService.MaximumFiles; index++)
                File.WriteAllText(Path.Combine(excess, $"{index:D4}.dat"), "x");
            ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                "more than 512 live save files were accepted");
            foreach (var file in Directory.EnumerateFiles(excess)) File.Delete(file);
            Directory.Delete(excess);
            var link = Path.Combine(profile.WorldDirectory, "linked.dat");
            var linkCreated = false;
            try
            {
                File.CreateSymbolicLink(link, world);
                linkCreated = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
                PlatformNotSupportedException)
            {
                Console.WriteLine("SKIP symbolic-link rejection fixture: this Windows user cannot create a link");
            }
            if (linkCreated)
            {
                try
                {
                    ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                        "a linked live save file was accepted");
                }
                finally { File.Delete(link); }
            }

            File.WriteAllText(world, "tamper target");
            var tamperedId = shared.StageLiveCapture(profile, run, completion);
            var tamperedPath = Path.Combine(root, "shared-live-captures", profile.Id.ToString("N"),
                tamperedId.ToString("N"), "payload", "world.dat");
            File.WriteAllText(tamperedPath, "changed after capture");
            shared.FixtureLiveCaptureAcceptedForChecks = true;
            Require(!shared.PublishLiveCapture(profile, tamperedId,
                new(tamperedId, run.OperationId, true)).Ok &&
                shared.Status(profile).Latest?.VersionHash == first.Version!.VersionHash,
                "tampered staged payload advanced the pointer");
            var incompleteId = Guid.NewGuid();
            var incompleteRoot = Path.Combine(root, "shared-live-captures",
                profile.Id.ToString("N"), incompleteId.ToString("N"));
            Directory.CreateDirectory(incompleteRoot);
            File.WriteAllText(Path.Combine(incompleteRoot, "world.dat"), "interrupted");
            Require(!shared.PublishLiveCapture(profile, incompleteId,
                new(incompleteId, run.OperationId, true)).Ok &&
                shared.Status(profile).Latest?.VersionHash == first.Version!.VersionHash,
                "incomplete capture advanced the pointer");
            var setupTamperId = shared.StageLiveCapture(profile, run, completion);
            var setupTamperPath = Path.Combine(root, "shared-live-captures",
                profile.Id.ToString("N"), setupTamperId.ToString("N"), "setup.protected");
            File.WriteAllBytes(setupTamperPath, [1, 2, 3]);
            Require(!shared.PublishLiveCapture(profile, setupTamperId,
                new(setupTamperId, run.OperationId, true)).Ok,
                "tampered setup checkpoint published a live save");
            var duplicateId = shared.StageLiveCapture(profile, run, completion);
            var duplicateManifestPath = Path.Combine(root, "shared-live-captures",
                profile.Id.ToString("N"), duplicateId.ToString("N"), "complete.json");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var original = JsonSerializer.Deserialize<LiveSaveCaptureManifest>(
                File.ReadAllBytes(duplicateManifestPath), json)!;
            var duplicate = original with { Files = [original.Files[0], original.Files[0]], Signature = "" };
            using (var signerForDuplicate = ECDsa.Create())
            {
                signerForDuplicate.ImportPkcs8PrivateKey(
                    data.LoadProtected("shared-world-signing-key.protected")!, out _);
                duplicate = duplicate with { Signature = Convert.ToBase64String(
                    signerForDuplicate.SignData(JsonSerializer.SerializeToUtf8Bytes(duplicate, json),
                        HashAlgorithmName.SHA256)) };
            }
            File.WriteAllBytes(duplicateManifestPath, JsonSerializer.SerializeToUtf8Bytes(duplicate, json));
            Require(!shared.PublishLiveCapture(profile, duplicateId,
                new(duplicateId, run.OperationId, true)).Ok,
                "duplicate signed staged paths published a live save");

            var bytes = new byte[SharedWorldService.ChunkBytes + 19];
            RandomNumberGenerator.Fill(bytes);
            File.WriteAllBytes(world, bytes);
            var acceptedId = shared.StageLiveCapture(profile, run, completion);
            var approval = new LiveSavePublicationApproval(acceptedId, run.OperationId, true);
            shared.FixtureLiveCaptureAcceptedForChecks = false;
            Require(!shared.PublishLiveCapture(profile, acceptedId, approval).Ok,
                "an unaccepted game published a live save");
            shared.FixtureLiveCaptureAcceptedForChecks = true;
            shared.FixtureLiveRunIdentityForChecks = _ => false;
            Require(!shared.PublishLiveCapture(profile, acceptedId, approval).Ok,
                "an exited or mismatched process published a live save");
            shared.FixtureLiveRunIdentityForChecks = current => current.ProcessId == 4242;
            Require(!shared.PublishLiveCapture(profile, acceptedId,
                approval with { Approved = false }).Ok &&
                !shared.PublishLiveCapture(profile, acceptedId,
                    approval with { OperationId = Guid.NewGuid() }).Ok,
                "missing or cross-run approval published a live save");
            var originalDirectory = profile.WorldDirectory;
            profile.WorldDirectory = Path.Combine(data.ManagedWorldsRoot, "other-profile");
            Require(!shared.PublishLiveCapture(profile, acceptedId, approval).Ok,
                "a changed reviewed source published the staged capture");
            profile.WorldDirectory = originalDirectory;
            var live = shared.PublishLiveCapture(profile, acceptedId, approval);
            Require(live.Ok && live.Version is { Schema: 5, Number: 2,
                CaptureKind: SharedWorldCaptureKinds.LiveSave } &&
                SharedWorldService.VerifySignature(live.Version!),
                "accepted synthetic capture did not publish a signed live version");
            Require(!OldSchemaFourReaderAccepts(live.Version!) &&
                !SharedWorldService.VerifySignature(live.Version! with { Schema = 4 }),
                "an older reader accepted the new live capture version");
            using (var downgradeSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                ExpectInvalid(() => SharedWorldService.SignVersion(live.Version! with
                {
                    Schema = 4, SigningPublicKey = "", VersionHash = "", Signature = ""
                }, downgradeSigner), "schema 4 accepted a newly signed live capture");
            Require(!JsonSerializer.Serialize(live.Version).Contains("SourceDirectory",
                StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(live.Version).Contains("shared-live-captures",
                StringComparison.Ordinal),
                "a local source path entered the portable version");
            Require(!Directory.Exists(Path.Combine(root, "shared-live-captures",
                profile.Id.ToString("N"), acceptedId.ToString("N"))),
                "the redundant private capture was retained after verified publication");
            Require(shared.ReadChunk(live.Version!, 0, 0).AsSpan().SequenceEqual(
                    bytes.AsSpan(0, SharedWorldService.ChunkBytes)) &&
                shared.ReadChunk(live.Version!, 0, SharedWorldService.ChunkBytes).AsSpan().SequenceEqual(
                    bytes.AsSpan(SharedWorldService.ChunkBytes)),
                "live version did not use bounded resumable chunk reads");

            File.WriteAllText(world, "another post-stop save");
            var secondBackup = backups.Create(profile, BackupKinds.Rolling);
            Require(secondBackup.Ok && secondBackup.Backup is not null, "second backup failed");
            var versionGroupRoot = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                live.Version!.GroupId.ToString("N"));
            var orphanRoot = Path.Combine(versionGroupRoot, "3");
            Directory.CreateDirectory(Path.Combine(orphanRoot, "payload"));
            using (var publishingKey = ECDsa.Create())
            {
                publishingKey.ImportPkcs8PrivateKey(
                    data.LoadProtected("shared-world-signing-key.protected")!, out _);
                var orphan = SharedWorldService.SignVersion(live.Version with
                {
                    Number = 3, ParentHash = live.Version.VersionHash,
                    BackupId = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow,
                    SigningPublicKey = "", VersionHash = "", Signature = ""
                }, publishingKey);
                File.Copy(Path.Combine(versionGroupRoot, "2", "payload", "world.dat"),
                    Path.Combine(orphanRoot, "payload", "world.dat"));
                File.WriteAllBytes(Path.Combine(orphanRoot, "version.json"),
                    JsonSerializer.SerializeToUtf8Bytes(orphan, json));
            }
            Require(!shared.PublishAfterStop(profile, secondBackup.Backup!.Id).Ok &&
                shared.Status(profile).Latest?.VersionHash == live.Version.VersionHash,
                "a crash orphan live version bypassed approval during post-Stop reconciliation");
            File.Delete(Path.Combine(orphanRoot, "payload", "world.dat"));
            Directory.Delete(Path.Combine(orphanRoot, "payload"));
            File.Delete(Path.Combine(orphanRoot, "version.json"));
            Directory.Delete(orphanRoot);
            var postStop = shared.PublishAfterStop(profile, secondBackup.Backup!.Id);
            Require(postStop.Ok && postStop.Version is { Number: 3,
                CaptureKind: SharedWorldCaptureKinds.PostStopBackup } &&
                postStop.Version!.ParentHash == live.Version!.VersionHash,
                "post-Stop publishing did not continue after a live version");
            Require(shared.ReadChunk(live.Version!, 0, 0).AsSpan().SequenceEqual(
                    bytes.AsSpan(0, SharedWorldService.ChunkBytes)),
                "post-Stop retention removed an approved live transfer payload");

            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var legacy = SharedWorldService.SignVersion(postStop.Version! with
            {
                Schema = 1, Number = 1, ParentHash = null,
                PortableSetup = new SharedWorldPortableSetup(profile.GamePort, false),
                SigningPublicKey = "", VersionHash = "", Signature = ""
            }, signer);
            Require(SharedWorldService.VerifySignature(legacy),
                "legacy post-Stop signature was rejected");
            Console.WriteLine("PASS Shared Worlds live core: exact-run/incomplete/tamper/approval denial, signed synthetic publication, chunk transfer, post-Stop continuity, legacy format");
        }
        finally
        {
            var parent = Path.GetFullPath(Path.Combine("local-data", "shared-live-core")) +
                Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Synthetic live check root escaped its workspace.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // Mirrors the previous released reader's schema/capture-kind gate. That
    // reader must reject the new signed version before it can use its head.
    private static bool OldSchemaFourReaderAccepts(SharedWorldVersion version) =>
        (version.Schema is 1 or 2 or 3 or 4) &&
        version.CaptureKind == SharedWorldCaptureKinds.PostStopBackup;

    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }
}
