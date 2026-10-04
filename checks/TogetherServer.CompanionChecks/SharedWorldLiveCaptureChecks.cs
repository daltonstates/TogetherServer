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
            profile.SharedSavesEnabled = false;
            Require(shared.ReviewLiveOrphan(profile) is { Code: "None", VersionHash: null },
                "sharing off without storage showed an interrupted live copy");
            profile.SharedSavesEnabled = true;
            using var receiverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var receiverId = Guid.NewGuid();
            shared.PublishRoster(profile, [new SharedWorldRosterMember(receiverId,
                Convert.ToBase64String(receiverKey.ExportSubjectPublicKeyInfo()),
                new SharedWorldGrants(Receive: true), false)]);
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
            foreach (var realGame in new[] { GameKinds.Valheim, GameKinds.MinecraftJava,
                         GameKinds.MinecraftBedrock, GameKinds.Factorio, GameKinds.Terraria })
            {
                profile.Kind = realGame;
                ExpectInvalid(() => shared.StageLiveCapture(profile, run, completion),
                    $"{realGame} staged mutable bytes using completion evidence alone");
            }
            profile.Kind = GameKinds.Fixture;
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
            var roster = shared.ReadRoster(profile)!;
            var unsignedReceipt = new SharedWorldReceipt(1, live.Version!.GroupId, profile.Id,
                live.Version.VersionHash, receiverId, roster.Epoch, roster.Revision,
                Guid.NewGuid(), "");
            var receipt = unsignedReceipt with { Signature = Convert.ToBase64String(
                receiverKey.SignData(SharedWorldReceiptTrust.Basis(unsignedReceipt),
                    HashAlgorithmName.SHA256)) };
            Require(shared.ConfirmReceipt(profile, receiverId, receipt).Ok &&
                shared.Status(profile).ConfirmedCopies == 1,
                "the exact signed live version receipt was not confirmed");

            File.WriteAllText(world, "another post-stop save");
            var secondBackup = backups.Create(profile, BackupKinds.Rolling);
            Require(secondBackup.Ok && secondBackup.Backup is not null, "second backup failed");
            var versionGroupRoot = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                live.Version!.GroupId.ToString("N"));
            var orphanRoot = Path.Combine(versionGroupRoot, "3");
            Directory.CreateDirectory(Path.Combine(orphanRoot, "payload"));
            SharedWorldVersion orphan;
            using (var publishingKey = ECDsa.Create())
            {
                publishingKey.ImportPkcs8PrivateKey(
                    data.LoadProtected("shared-world-signing-key.protected")!, out _);
                orphan = SharedWorldService.SignVersion(live.Version with
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
            var pointerPath = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                "latest.json");
            var pointerBytes = File.ReadAllBytes(pointerPath);
            var orphanManifestBytes = File.ReadAllBytes(Path.Combine(orphanRoot, "version.json"));
            var orphanPayloadBytes = File.ReadAllBytes(Path.Combine(orphanRoot, "payload", "world.dat"));
            // A new service instance must see the same orphan and recover it
            // without relying on the publisher's in-memory state.
            var resumed = new SharedWorldService(data, backups)
            {
                LiveGameRegistryForChecks = new GameServerRegistry(data, includeFixture: true),
                FixtureLiveRunIdentityForChecks = current => current.ProcessId == 4242,
                FixtureLiveCaptureAcceptedForChecks = true
            };
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "Verified", VersionHash: var hash } &&
                hash == orphan.VersionHash,
                "the owner review did not identify the exact signed next live copy");
            var originalProfileId = profile.Id;
            profile.Id = Guid.NewGuid();
            Require(resumed.ReviewLiveOrphan(profile) is { VersionHash: null } &&
                !resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok,
                "another exact profile offered or moved the orphan");
            profile.Id = originalProfileId;
            var dirtyRosterPath = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                "roster-dirty");
            File.WriteAllText(dirtyRosterPath, "unresolved authority");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                !resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok,
                "unresolved sharing authority offered or moved the orphan");
            File.Delete(dirtyRosterPath);
            var sourcePath = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"), "source.json");
            var sourceBytes = File.ReadAllBytes(sourcePath);
            File.WriteAllText(sourcePath, "changed authority binding");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                !resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok,
                "changed source binding offered or moved an orphan");
            File.WriteAllBytes(sourcePath, sourceBytes);
            var rosterPath = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                $"{live.Version.GroupId:N}.roster.json");
            var rosterBytes = File.ReadAllBytes(rosterPath);
            File.WriteAllText(rosterPath, "changed signed roster");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                !resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok,
                "changed roster offered or moved an orphan");
            File.WriteAllBytes(rosterPath, rosterBytes);
            File.WriteAllText(pointerPath, "changed published pointer");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                !resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok,
                "changed published pointer offered or moved an orphan");
            File.WriteAllBytes(pointerPath, pointerBytes);
            var ancestorPath = Path.Combine(versionGroupRoot, "1", "version.json");
            var ancestorBytes = File.ReadAllBytes(ancestorPath);
            File.WriteAllText(ancestorPath, "changed signed ancestor");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                !resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok,
                "broken signed lineage offered or moved the orphan");
            File.WriteAllBytes(ancestorPath, ancestorBytes);
            File.WriteAllText(world, "an unrelated later live save");
            var unrelatedCapture = shared.StageLiveCapture(profile, run,
                completion with { CompletedUtc = DateTimeOffset.UtcNow });
            Require(!resumed.PublishAfterStop(profile, secondBackup.Backup!.Id).Ok &&
                resumed.Status(profile).Latest?.VersionHash == live.Version.VersionHash,
                "a crash orphan live version bypassed approval during post-Stop reconciliation");
            Require(!resumed.PublishLiveCapture(profile, unrelatedCapture,
                    new(unrelatedCapture, run.OperationId, true)).Ok &&
                resumed.Status(profile).Latest?.VersionHash == live.Version.VersionHash,
                "an unrelated live capture bypassed the crash orphan");
            Require(!resumed.QuarantineVerifiedLiveOrphan(profile, new string('0', 64)).Ok &&
                Directory.Exists(orphanRoot), "quarantine accepted the wrong orphan identity");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "Verified", VersionHash: var retryHash } &&
                retryHash == orphan.VersionHash,
                "a denied wrong-hash request hid the verified copy");
            var orphanPayloadPath = Path.Combine(orphanRoot, "payload", "world.dat");
            File.WriteAllText(orphanPayloadPath, "tampered orphan payload");
            Require(!resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok &&
                !resumed.PublishAfterStop(profile, secondBackup.Backup.Id).Ok &&
                resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                Directory.Exists(orphanRoot),
                "a tampered live orphan was quarantined or promoted");
            File.WriteAllBytes(orphanPayloadPath, orphanPayloadBytes);
            var orphanManifestPath = Path.Combine(orphanRoot, "version.json");
            File.WriteAllText(orphanManifestPath, "malformed manifest");
            Require(!resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok &&
                !resumed.PublishAfterStop(profile, secondBackup.Backup.Id).Ok &&
                resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                Directory.Exists(orphanRoot),
                "a malformed live orphan was quarantined or promoted");
            File.WriteAllBytes(orphanManifestPath, orphanManifestBytes);
            var extraOrphanPath = Path.Combine(orphanRoot, "payload", "unsigned.dat");
            File.WriteAllText(extraOrphanPath, "not in the signed file list");
            Require(!resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok &&
                resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                Directory.Exists(orphanRoot),
                "an orphan with an extra payload file entered quarantine");
            File.Delete(extraOrphanPath);
            var publishedPayloadPath = Path.Combine(versionGroupRoot, "2", "payload", "world.dat");
            File.WriteAllText(publishedPayloadPath, "damaged prior head");
            Require(!resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok &&
                resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                Directory.Exists(orphanRoot),
                "recovery abandoned an orphan when the published head payload was damaged");
            File.WriteAllBytes(publishedPayloadPath, bytes);
            var quarantineBase = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                ".quarantined-live");
            for (var index = 0; index < 8; index++)
                Directory.CreateDirectory(Path.Combine(quarantineBase, $"fixture-{index}"));
            Require(!resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash).Ok &&
                resumed.ReviewLiveOrphan(profile) is { Code: "ReviewRequired", VersionHash: null } &&
                Directory.Exists(orphanRoot), "quarantine exceeded its eight-version bound");
            for (var index = 0; index < 8; index++)
                Directory.Delete(Path.Combine(quarantineBase, $"fixture-{index}"));
            Require(File.ReadAllBytes(pointerPath).AsSpan().SequenceEqual(pointerBytes),
                "denied recovery changed the signed latest pointer");
            profile.SharedSavesEnabled = false;
            Require(resumed.HasPotentialLiveOrphan(profile),
                "turning sharing off hid an unpublished live copy");
            profile.SharedSavesEnabled = true;
            var quarantined = resumed.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash);
            var quarantineRoot = Path.Combine(quarantineBase,
                $"{orphan.GroupId:N}-{orphan.Number}-{orphan.VersionHash}");
            Require(quarantined.Ok && !Directory.Exists(orphanRoot) &&
                File.ReadAllBytes(Path.Combine(quarantineRoot, "version.json")).AsSpan()
                    .SequenceEqual(orphanManifestBytes) &&
                File.ReadAllBytes(Path.Combine(quarantineRoot, "payload", "world.dat")).AsSpan()
                    .SequenceEqual(orphanPayloadBytes) &&
                File.ReadAllBytes(pointerPath).AsSpan().SequenceEqual(pointerBytes),
                "verified recovery lost signed evidence, payload bytes, or the latest pointer");
            Require(resumed.ReviewLiveOrphan(profile) is { Code: "None", VersionHash: null },
                "the owner review still offered a moved live copy");
            profile.SharedSavesEnabled = false;
            Require(!resumed.HasPotentialLiveOrphan(profile),
                "a disabled world with retained published history and no orphan was flagged");
            profile.SharedSavesEnabled = true;
            var postStop = resumed.PublishAfterStop(profile, secondBackup.Backup!.Id);
            Require(postStop.Ok && postStop.Version is { Number: 3,
                CaptureKind: SharedWorldCaptureKinds.PostStopBackup } &&
                postStop.Version!.ParentHash == live.Version!.VersionHash,
                "post-Stop publishing did not continue after a live version");
            Require(shared.ReadChunk(live.Version!, 0, 0).AsSpan().SequenceEqual(
                    bytes.AsSpan(0, SharedWorldService.ChunkBytes)),
                "post-Stop retention removed an approved live transfer payload");
            Require(shared.Status(profile).ConfirmedCopies == 0,
                "a receipt for an older live version was counted for the new head");

            // A separate signed source group has only one verified local copy.
            // Long-history cleanup must never delete its payload.
            var soleGroup = Guid.NewGuid();
            var soleRoot = Path.Combine(root, "shared-worlds", profile.Id.ToString("N"),
                soleGroup.ToString("N"), "1");
            var solePayload = Path.Combine(soleRoot, "payload", "world.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(solePayload)!);
            var soleBytes = System.Text.Encoding.UTF8.GetBytes("sole verified branch");
            File.WriteAllBytes(solePayload, soleBytes);
            using (var publishingKey = ECDsa.Create())
            {
                publishingKey.ImportPkcs8PrivateKey(
                    data.LoadProtected("shared-world-signing-key.protected")!, out _);
                var soleDraft = postStop.Version! with
                {
                    GroupId = soleGroup, Number = 1, ParentHash = null,
                    BackupId = Guid.NewGuid(),
                    Files = [new SharedWorldFile("world.dat", soleBytes.Length,
                        Convert.ToHexString(SHA256.HashData(soleBytes)))],
                    SigningPublicKey = "", VersionHash = "", Signature = ""
                };
                var sole = SharedWorldService.SignVersion(soleDraft, publishingKey);
                File.WriteAllBytes(Path.Combine(soleRoot, "version.json"),
                    JsonSerializer.SerializeToUtf8Bytes(sole, json));
            }

            // These fixture hashes stand in for effective competing authority
            // heads; production gets them from signed authority history.
            shared.RetentionProtectedHeadsForChecks = _ =>
                [live.Version!.VersionHash, postStop.Version!.VersionHash];
            var longHistory = new List<SharedWorldVersion>();
            for (var sequence = 4; sequence <= 7; sequence++)
            {
                Thread.Sleep(2);
                File.WriteAllText(world, $"live save {sequence}");
                var evidence = completion with { CompletedUtc = DateTimeOffset.UtcNow };
                var capture = shared.StageLiveCapture(profile, run, evidence);
                var published = shared.PublishLiveCapture(profile, capture,
                    new(capture, run.OperationId, true));
                Require(published.Ok && published.Version?.Number == sequence,
                    $"long-history live version {sequence} failed");
                longHistory.Add(published.Version!);
            }
            var mainPayload = Path.Combine(versionGroupRoot, "1", "payload", "world.dat");
            var protectedLivePayload = Path.Combine(versionGroupRoot, "2", "payload", "world.dat");
            var protectedPostStopPayload = Path.Combine(versionGroupRoot, "3", "payload", "world.dat");
            Require(!File.Exists(mainPayload) && File.Exists(protectedLivePayload) &&
                File.Exists(protectedPostStopPayload) && File.Exists(solePayload),
                "retention did not keep protected heads and the only verified group copy");
            Require(File.Exists(Path.Combine(versionGroupRoot, "1", "version.json")),
                "retention deleted a signed historical manifest");

            // Simulate low space after staging. A previously pruned, verified
            // non-head payload is restored as surplus; pre-copy cleanup must
            // reclaim it before the reserve check, regardless of file time.
            File.WriteAllText(world, "live save 8");
            var lastEvidence = completion with { CompletedUtc = DateTimeOffset.UtcNow };
            var lastCapture = shared.StageLiveCapture(profile, run, lastEvidence);
            var surplusLivePayload = Path.Combine(versionGroupRoot, "4", "payload", "world.dat");
            File.WriteAllText(surplusLivePayload, "live save 4");
            File.SetLastWriteTimeUtc(surplusLivePayload, DateTime.UtcNow.AddYears(1));
            const long reserveBytes = 1024L * 1024 * 1024;
            var lastBytes = new FileInfo(world).Length;
            shared.FixtureLiveAvailableBytesForChecks = () => File.Exists(surplusLivePayload)
                ? reserveBytes + lastBytes - 1 : long.MaxValue;
            var last = shared.PublishLiveCapture(profile, lastCapture,
                new(lastCapture, run.OperationId, true));
            shared.FixtureLiveAvailableBytesForChecks = null;
            Require(last.Ok && last.Version?.Number == 8 && !File.Exists(surplusLivePayload) &&
                File.Exists(protectedLivePayload) && File.Exists(protectedPostStopPayload) &&
                File.Exists(solePayload) &&
                longHistory.Skip(2).All(version => File.Exists(Path.Combine(versionGroupRoot,
                    version.Number.ToString(), "payload", "world.dat"))),
                "low-space reclamation touched newest copies, protected heads, or the only branch copy");
            Require(File.Exists(Path.Combine(versionGroupRoot, "4", "version.json")) &&
                shared.Status(profile).Latest?.VersionHash == last.Version!.VersionHash,
                "low-space reclamation changed signed history or the current pointer");

            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var legacy = SharedWorldService.SignVersion(postStop.Version! with
            {
                Schema = 1, Number = 1, ParentHash = null,
                PortableSetup = new SharedWorldPortableSetup(profile.GamePort, false),
                SigningPublicKey = "", VersionHash = "", Signature = ""
            }, signer);
            Require(SharedWorldService.VerifySignature(legacy),
                "legacy post-Stop signature was rejected");
            CheckSuccessorHeadWithoutPointer(root, json);
            Console.WriteLine("PASS Shared Worlds live core: real-game staging denied, exact-run/tamper/approval denial, signed fixture publication, verified bounded orphan quarantine after restart, post-Stop continuity, chunk/receipt/retention and legacy format");
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

    private static void CheckSuccessorHeadWithoutPointer(string root, JsonSerializerOptions json)
    {
        using var ownerData = new LocalData(Path.Combine(root, "successor-owner"));
        var ownerGames = new GameServerRegistry(ownerData, includeFixture: true);
        var ownerBackups = new WorldBackupService(ownerData, TimeProvider.System, games: ownerGames);
        var ownerShared = new SharedWorldService(ownerData, ownerBackups);
        var ownerProfile = new ServerProfile
        {
            Kind = GameKinds.Fixture, WorldId = "successor-anchor-world", WorldSource = "New",
            SharedSavesEnabled = true, GamePort = 34568,
            Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        ownerProfile.WorldDirectory = ownerData.NewWorldDirectory(ownerProfile.Id);
        Directory.CreateDirectory(ownerProfile.WorldDirectory);
        File.WriteAllText(Path.Combine(ownerProfile.WorldDirectory, "world.dat"), "signed head");
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid();
        var devicePublicKey = Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo());
        var roster = ownerShared.PublishRoster(ownerProfile,
            [new SharedWorldRosterMember(deviceId, devicePublicKey,
                new SharedWorldGrants(Receive: true, EligibleHost: true), false)]);
        var backup = ownerBackups.Create(ownerProfile, BackupKinds.Rolling);
        Require(backup is { Ok: true, Backup: not null }, "successor head backup failed");
        var published = ownerShared.PublishAfterStop(ownerProfile, backup.Backup!.Id);
        Require(published is { Ok: true, Version: not null }, "successor head publication failed");
        var head = published.Version!;
        var receiptDraft = new SharedWorldReceipt(1, head.GroupId, ownerProfile.Id,
            head.VersionHash, deviceId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with { Signature = Convert.ToBase64String(
            deviceKey.SignData(SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256)) };
        var record = ownerShared.SignPlannedHandoff(roster, head, receipt, deviceId,
            "https://127.0.0.1:5132", 1, null);

        using var successorData = new LocalData(Path.Combine(root, "successor-pc"));
        var successorProfile = new ServerProfile
        {
            Id = ownerProfile.Id, Kind = ownerProfile.Kind, WorldId = ownerProfile.WorldId,
            WorldSource = "New", SharedSavesEnabled = true, GamePort = ownerProfile.GamePort,
            Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        successorProfile.WorldDirectory = successorData.NewWorldDirectory(successorProfile.Id);
        Directory.CreateDirectory(successorProfile.WorldDirectory);
        successorData.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected",
            deviceKey.ExportPkcs8PrivateKey());
        var authority = new WorldAuthorityStore(successorData);
        authority.AppendReceived(record, successorProfile.Id, head.GroupId, roster.OwnerPublicKey);
        authority.BindLocalSuccessor(successorProfile.Id, record.RecordHash, deviceId);
        var successorGames = new GameServerRegistry(successorData, includeFixture: true);
        var successorBackups = new WorldBackupService(successorData, TimeProvider.System,
            games: successorGames);
        var successorShared = new SharedWorldService(successorData, successorBackups);
        successorShared.AdoptSuccessor(successorProfile, record);
        var sharedRoot = Path.Combine(successorData.RootPath, "shared-worlds",
            successorProfile.Id.ToString("N"));
        var latestPath = Path.Combine(sharedRoot, "latest.json");
        var predecessorRoot = Path.Combine(sharedRoot, head.GroupId.ToString("N"), "1");
        Require(!File.Exists(latestPath) && !Directory.Exists(predecessorRoot) &&
            authority.LocalAuthorizedHead(successorProfile.Id)?.RecordHash == record.RecordHash,
            "successor fixture unexpectedly had a local pointer or lacked signed authority");

        var bytes = System.Text.Encoding.UTF8.GetBytes("unpublished successor file copy");
        using var successorKey = ECDsa.Create();
        successorKey.ImportPkcs8PrivateKey(deviceKey.ExportPkcs8PrivateKey(), out _);
        var candidate = SharedWorldService.SignVersion(head with
        {
            Schema = 5, Number = head.Number + 1, ParentHash = head.VersionHash,
            CreatedUtc = head.CreatedUtc.AddSeconds(1),
            CaptureKind = SharedWorldCaptureKinds.LiveSave, BackupId = Guid.NewGuid(),
            Files = [new SharedWorldFile("world.dat", bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)))],
            SigningPublicKey = "", VersionHash = "", Signature = ""
        }, successorKey);
        var orphanRoot = Path.Combine(sharedRoot, head.GroupId.ToString("N"), "2");
        var payloadPath = Path.Combine(orphanRoot, "payload", "world.dat");
        var manifestPath = Path.Combine(orphanRoot, "version.json");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
        File.WriteAllBytes(payloadPath, bytes);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(candidate, json);
        File.WriteAllBytes(manifestPath, manifestBytes);
        Require(successorShared.ReviewLiveOrphan(successorProfile) is
                { Code: "Verified", VersionHash: var hash } && hash == candidate.VersionHash,
            "signed successor head without latest.json did not anchor the next live copy");
        var wrongParent = SharedWorldService.SignVersion(candidate with
        { ParentHash = new string('A', 64) }, successorKey);
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(wrongParent, json));
        Require(successorShared.ReviewLiveOrphan(successorProfile) is
                { Code: "ReviewRequired", VersionHash: null },
            "successor review offered a copy with the wrong signed parent");
        var wrongNumber = SharedWorldService.SignVersion(candidate with { Number = 3 },
            successorKey);
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(wrongNumber, json));
        Require(successorShared.ReviewLiveOrphan(successorProfile) is
                { Code: "ReviewRequired", VersionHash: null },
            "successor review offered a copy with the wrong signed number");
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongSigner = SharedWorldService.SignVersion(candidate, wrongKey);
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(wrongSigner, json));
        Require(successorShared.ReviewLiveOrphan(successorProfile) is
                { Code: "ReviewRequired", VersionHash: null },
            "successor review offered a copy signed by another key");
        File.WriteAllBytes(manifestPath, manifestBytes);
        File.WriteAllText(payloadPath, "changed payload");
        Require(successorShared.ReviewLiveOrphan(successorProfile) is
                { Code: "ReviewRequired", VersionHash: null },
            "successor review offered a copy with changed payload bytes");
        File.WriteAllBytes(payloadPath, bytes);
        Require(!successorShared.QuarantineVerifiedLiveOrphan(successorProfile,
                new string('0', 64)).Ok && Directory.Exists(orphanRoot),
            "successor recovery accepted the wrong exact hash");
        Require(successorShared.QuarantineVerifiedLiveOrphan(successorProfile,
                candidate.VersionHash).Ok && !Directory.Exists(orphanRoot) &&
            !File.Exists(latestPath) &&
            File.ReadAllBytes(Path.Combine(sharedRoot, ".quarantined-live",
                $"{head.GroupId:N}-2-{candidate.VersionHash}", "version.json"))
                .AsSpan().SequenceEqual(manifestBytes) &&
            File.ReadAllBytes(Path.Combine(sharedRoot, ".quarantined-live",
                $"{head.GroupId:N}-2-{candidate.VersionHash}", "payload", "world.dat"))
                .AsSpan().SequenceEqual(bytes),
            "successor recovery lost the signed copy or created a latest pointer");
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
