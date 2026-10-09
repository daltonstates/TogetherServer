using System.Text.Json;
using TogetherServer;

internal static class LiveSaveActionChecks
{
    internal static async Task RunAsync(string root, string fixture)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static string Describe(LiveSaveActionResult result) =>
            $"Ok={result.Ok}; Code={result.Code}; Message={result.Message}; " +
            $"AttemptState={result.Attempt?.State ?? "none"}; AttemptCode={result.Attempt?.Code ?? "none"}; " +
            $"AttemptMessage={result.Attempt?.Message ?? "none"}";
        LiveSavePublicationBoundaryChecks.Run(Path.Combine(root, "live-publication-boundaries"), fixture);
        using var data = new LocalData(Path.Combine(root, "live-action"));
        var games = new GameServerRegistry(data, true, PortProbeMode.ObserveOnly);
        var profile = new ServerProfile
        {
            Name = "Synthetic live action",
            Kind = GameKinds.Fixture,
            WorldId = "synthetic",
            WorldSource = "New",
            ExecutablePath = fixture,
            GamePort = 41002,
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 },
            SharedSavesEnabled = true
        };
        profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
        Directory.CreateDirectory(profile.WorldDirectory);
        var file = Path.Combine(profile.WorldDirectory, "world.dat"); File.WriteAllText(file, "baseline");
        data.SaveSettings(new() { Profiles = [profile] });
        var backups = new WorldBackupService(data, TimeProvider.System, games: games);
        var shares = new SharedWorldService(data, backups); shares.PublishRoster(profile, []);
        var backup = backups.Create(profile, BackupKinds.Rolling);
        Require(backup.Ok && shares.PublishAfterStop(profile, backup.Backup!.Id).Ok, "fixture baseline publication failed");
        var manager = new HostManager(data, games);
        Require(!(await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()))).Ok, "production fixture action enabled");
        foreach (var game in new[] { GameKinds.Valheim, GameKinds.MinecraftJava, GameKinds.MinecraftBedrock, GameKinds.Factorio, GameKinds.Terraria })
        {
            Require(SharedWorldLiveSaveAdapters.Status(game) is { Available: false, Stages.Count: 6 }, "real game lacks a separate disabled matrix");
            var adapter = SharedWorldLiveSaveAdapters.CreateSnapshotAdapter(data, games, game);
            Require(!SharedWorldLiveSaveAdapters.IsGameAccepted(game) && adapter is not null &&
                adapter.Game == game && !adapter.LiveCaptureAccepted,
                "the typed game adapter was missing, mismatched, or enabled before its own acceptance");
        }
        manager.EnableStagingLiveFixture();
        var started = await manager.StartAsync(profile.Id);
        Require(started.Ok, $"fixture Start failed: Code={started.Code}; Message={started.Message}");
        try
        {
            File.WriteAllText(file, "running synthetic change");
            var id = Guid.NewGuid();
            var duplicate = await Task.WhenAll(manager.SaveAndShareAsync(profile.Id, new(id)), manager.SaveAndShareAsync(profile.Id, new(id)));
            Require(duplicate.All(item => item.Ok) && duplicate[0].Attempt?.VersionHash == duplicate[1].Attempt?.VersionHash,
                $"duplicate requests repeated publication or lost exact identity: first [{Describe(duplicate[0])}]; second [{Describe(duplicate[1])}]");
            var current = (await manager.SharedWorldStatusAsync(profile.Id)).Latest!;
            Require(current.CaptureKind == SharedWorldCaptureKinds.LiveSave && SharedWorldService.VerifySignature(current), "fixture current live copy was not signed");
            manager.LiveAvailableBytesForChecks = () => 0;
            Require(!(await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()))).Ok &&
                (await manager.SharedWorldStatusAsync(profile.Id)).Latest?.VersionHash == current.VersionHash, "low space changed current version");
            manager.LiveAvailableBytesForChecks = null;
            manager.LiveAfterSourceScanForChecks = () => File.WriteAllText(file, "mutation during staging");
            Require(!(await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()))).Ok &&
                (await manager.SharedWorldStatusAsync(profile.Id)).Latest?.VersionHash == current.VersionHash, "mutable copy became current");
            using var canceled = new CancellationTokenSource();
            manager.LiveAfterSourceScanForChecks = () => canceled.Cancel();
            Require((await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()), canceled.Token)).Code == "LiveSaveCanceled", "cancellation published a partial capture");
            manager.LiveAfterSourceScanForChecks = null;
            var activeRequestId = Guid.NewGuid();
            var activeCapture = manager.SaveAndShareAsync(profile.Id, new(activeRequestId));
            var staleWithdrawal = manager.WithdrawLiveSaveAsync(profile.Id, new(id));
            var unknownWithdrawal = manager.WithdrawLiveSaveAsync(profile.Id, new(Guid.NewGuid()));
            Require((await manager.WithdrawLiveSaveAsync(profile.Id, new(Guid.Empty))).Code == "LiveSaveRequestInvalid",
                "an empty withdrawal identity was accepted");
            Require((await activeCapture).Ok &&
                (await staleWithdrawal).Code == "LiveSaveWithdrawalDenied" &&
                (await unknownWithdrawal).Code == "LiveSaveWithdrawalDenied" &&
                (await manager.SharedWorldStatusAsync(profile.Id)).Latest?.Number == current.Number + 1,
                "a stale or unknown withdrawal canceled the newer request or replayed publication");
            var targetedRequestId = Guid.NewGuid();
            var targetedCapture = manager.SaveAndShareAsync(profile.Id, new(targetedRequestId));
            var targetedWithdrawal = manager.WithdrawLiveSaveAsync(profile.Id, new(targetedRequestId));
            Require((await targetedCapture).Code == "LiveSaveCanceled" && (await targetedWithdrawal).Ok &&
                (await manager.SharedWorldStatusAsync(profile.Id)).Latest?.Number == current.Number + 1,
                "the exact active withdrawal did not cancel its own capture safely");
            Task<ActionResult>? stop = null;
            // The hook runs after the exact fixture completion and source scan,
            // while this capture owns the lifecycle gate. Owner Stop cancels
            // the active request before waiting for that gate; neither a slow
            // CI scheduler nor the fixture's response delay selects the overlap.
            manager.LiveAfterSourceScanForChecks = () => stop = manager.StopAsync(profile.Id);
            LiveSaveActionResult overlapped;
            try { overlapped = await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid())); }
            finally { manager.LiveAfterSourceScanForChecks = null; }
            Require(stop is not null,
                $"concurrent owner Stop never reached the completed source scan: {Describe(overlapped)}");
            var stopped = await stop!;
            Require(overlapped.Code == "LiveSaveCanceled" && stopped.Ok,
                $"concurrent owner Stop failed to cancel bounded capture and stop exact fixture: capture [{Describe(overlapped)}]; " +
                $"Stop Code={stopped.Code}; Message={stopped.Message}");
            var restartedAfterCancellation = await manager.StartAsync(profile.Id);
            Require(restartedAfterCancellation.Ok,
                $"fixture could not restart after failed/canceled saves: Code={restartedAfterCancellation.Code}; Message={restartedAfterCancellation.Message}");
            var reachedSignedDirectory = false;
            manager.LiveAfterSignedDirectoryForChecks = () =>
            {
                reachedSignedDirectory = true;
                throw new IOException("simulated crash before latest pointer");
            };
            var failed = await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()));
            Require(reachedSignedDirectory && !failed.Ok && failed.Attempt is not null,
                $"interrupted-copy fixture did not reach its signed-directory crash point: ReachedSignedDirectory={reachedSignedDirectory}; {Describe(failed)}");
            var blockedStatus = await manager.SharedWorldStatusAsync(profile.Id);
            Require(blockedStatus.LiveSave?.Available == false,
                $"interrupted signed directory allowed another live capture: {Describe(failed)}; " +
                $"LiveSave Code={blockedStatus.LiveSave?.Code ?? "none"}; Message={blockedStatus.LiveSave?.Message ?? "none"}");
            var orphan = await manager.SharedLiveOrphanReviewAsync(profile.Id);
            Require(orphan is { Code: "Verified", VersionHash: not null },
                $"sealed interrupted copy not reviewable: Code={orphan.Code}; Message={orphan.Message}; " +
                $"HasVersionHash={orphan.VersionHash is not null}; capture [{Describe(failed)}]; " +
                $"LiveSave Code={blockedStatus.LiveSave?.Code ?? "none"}; Message={blockedStatus.LiveSave?.Message ?? "none"}");
            var quarantined = await manager.QuarantineSharedLiveOrphanAsync(profile.Id, orphan.VersionHash!);
            Require(quarantined.Ok,
                $"exact signed orphan quarantine failed: Code={quarantined.Code}; Message={quarantined.Message}");
            var withdrawnPrivateSnapshots = new ManagedLiveSnapshotStore(data, games);
            var failedRun = data.LoadRuns().Single();
            var failedPrivate = withdrawnPrivateSnapshots.Stage(profile, failedRun,
                ValheimManagedSnapshotAdapter.Completion(profile, failedRun), reservedSnapshotId: failed.Attempt!.RequestId);
            var failedWithdrawal = await manager.WithdrawLiveSaveAsync(profile.Id, new(failed.Attempt!.RequestId));
            Require(failedWithdrawal.Ok, $"failed attempt withdrawal failed: {Describe(failedWithdrawal)}");
            Require(!Directory.Exists(failedPrivate.SnapshotDirectory) && File.Exists(file),
                "explicit withdrawal retained its sealed private crash copy or deleted source bytes");
            manager.LiveAfterSignedDirectoryForChecks = null;
            var savedAfterReview = await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()));
            Require(savedAfterReview.Ok, $"new request could not save again after withdrawal/review: {Describe(savedAfterReview)}");
            byte[]? publishingJournal = null;
            manager.LiveAfterSignedDirectoryForChecks = () => publishingJournal = data.LoadProtected("live-save-attempts.protected");
            var interruptedRequestId = Guid.NewGuid();
            var completedBeforeJournalRecovery = await manager.SaveAndShareAsync(profile.Id, new(interruptedRequestId));
            manager.LiveAfterSignedDirectoryForChecks = null;
            var publishedBeforeRecovery = shares.Status(profile).Latest!;
            Require(completedBeforeJournalRecovery.Ok && publishingJournal is not null &&
                JsonSerializer.Deserialize<List<LiveSaveAttempt>>(publishingJournal)!.Single(item => item.RequestId == interruptedRequestId).State == "Publishing",
                "the interruption fixture did not retain the journal from before the current-pointer commit");
            // Simulate an app exit after latest.json commits but before the
            // protected attempt journal commits. Neither path may publish again.
            var privateSnapshots = new ManagedLiveSnapshotStore(data, games);
            var liveRun = data.LoadRuns().Single();
            var retainedPrivate = privateSnapshots.Stage(profile, liveRun,
                ValheimManagedSnapshotAdapter.Completion(profile, liveRun), reservedSnapshotId: interruptedRequestId);
            var anotherPrivate = privateSnapshots.Stage(profile, liveRun,
                ValheimManagedSnapshotAdapter.Completion(profile, liveRun), reservedSnapshotId: Guid.NewGuid());
            data.SaveProtected("live-save-attempts.protected", publishingJournal!);
            var recoveredWithdrawal = await manager.WithdrawLiveSaveAsync(profile.Id, new(interruptedRequestId));
            Require(recoveredWithdrawal is { Ok: false, Code: "LiveSaveWithdrawalDenied", Attempt.State: "Published" } &&
                JsonSerializer.Deserialize<List<LiveSaveAttempt>>(data.LoadProtected("live-save-attempts.protected")!)!
                    .Single(item => item.RequestId == interruptedRequestId).State == "Published" &&
                shares.Status(profile).Latest?.VersionHash == publishedBeforeRecovery.VersionHash,
                "withdrawal failed to persist the already-current published attempt or created another version");
            Require(!Directory.Exists(retainedPrivate.SnapshotDirectory) && Directory.Exists(anotherPrivate.SnapshotDirectory),
                "publication recovery leaked its private seal or deleted another request's sealed copy");
            // Also cover a crash after the Published journal was durable but
            // before its private-copy cleanup, independent of pointer recovery.
            retainedPrivate = privateSnapshots.Stage(profile, liveRun,
                ValheimManagedSnapshotAdapter.Completion(profile, liveRun), reservedSnapshotId: interruptedRequestId);
            _ = await manager.SharedWorldStatusAsync(profile.Id);
            Require(!Directory.Exists(retainedPrivate.SnapshotDirectory) && privateSnapshots.Verify(profile, liveRun, anotherPrivate),
                "a durable Published attempt did not clean only its own redundant private copy");
            privateSnapshots.Discard(anotherPrivate);
            data.SaveProtected("live-save-attempts.protected", publishingJournal!);
            var restarted = new HostManager(data, games);
            restarted.EnableStagingLiveFixture();
            var recoveredStatus = await restarted.SharedWorldStatusAsync(profile.Id);
            Require(recoveredStatus.LiveSave is { Available: true, LastAttempt.State: "Published" } &&
                JsonSerializer.Deserialize<List<LiveSaveAttempt>>(data.LoadProtected("live-save-attempts.protected")!)!
                    .Single(item => item.RequestId == interruptedRequestId).State == "Published" &&
                recoveredStatus.Latest?.VersionHash == publishedBeforeRecovery.VersionHash,
                "status after restart did not durably reconcile the current capture without replay");
            Require((await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()))).Ok &&
                shares.Status(profile).Latest?.Number == publishedBeforeRecovery.Number + 1,
                "reconciled publication left later fixed requests permanently blocked");
            File.WriteAllText(Path.Combine(data.RootPath, "bedrock-live-save-pending-resume.json"), "invalid pending hold");
            Require(!(await manager.RecoverPendingBedrockResumeAsync()).Ok &&
                File.Exists(Path.Combine(data.RootPath, "bedrock-live-save-pending-resume.json")), "invalid resume marker was erased or dispatched");
        }
        finally { _ = await manager.StopAsync(profile.Id); }
    }
}
