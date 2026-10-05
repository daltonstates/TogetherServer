using TogetherServer;

internal static class LiveSaveActionChecks
{
    internal static async Task RunAsync(string root, string fixture)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
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
            Require(SharedWorldLiveSaveAdapters.Status(game) is { Available: false, Stages.Count: 6 }, "real game lacks a separate disabled matrix");
        manager.EnableStagingLiveFixture();
        Require((await manager.StartAsync(profile.Id)).Ok, "fixture Start failed");
        try
        {
            File.WriteAllText(file, "running synthetic change");
            var id = Guid.NewGuid();
            var duplicate = await Task.WhenAll(manager.SaveAndShareAsync(profile.Id, new(id)), manager.SaveAndShareAsync(profile.Id, new(id)));
            Require(duplicate.All(item => item.Ok) && duplicate[0].Attempt?.VersionHash == duplicate[1].Attempt?.VersionHash,
                "duplicate requests repeated publication or lost exact identity");
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
            var overlapped = manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()));
            await Task.Delay(30);
            var stop = manager.StopAsync(profile.Id);
            Require((await overlapped).Code == "LiveSaveCanceled" && (await stop).Ok, "concurrent owner Stop failed to cancel bounded capture and stop exact fixture");
            Require((await manager.StartAsync(profile.Id)).Ok, "fixture could not restart after failed/canceled saves");
            manager.LiveAfterSignedDirectoryForChecks = () => throw new IOException("simulated crash before latest pointer");
            var failed = await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()));
            Require(!failed.Ok && (await manager.SharedWorldStatusAsync(profile.Id)).LiveSave?.Available == false,
                "interrupted signed directory allowed another live capture");
            var orphan = await manager.SharedLiveOrphanReviewAsync(profile.Id);
            Require(orphan is { Code: "Verified", VersionHash: not null }, "sealed interrupted copy not reviewable");
            Require((await manager.QuarantineSharedLiveOrphanAsync(profile.Id, orphan.VersionHash!)).Ok, "exact signed orphan quarantine failed");
            Require((await manager.WithdrawLiveSaveAsync(profile.Id, new(failed.Attempt!.RequestId))).Ok, "failed attempt withdrawal failed");
            manager.LiveAfterSignedDirectoryForChecks = null;
            Require((await manager.SaveAndShareAsync(profile.Id, new(Guid.NewGuid()))).Ok, "new request could not save again after withdrawal/review");
            File.WriteAllText(Path.Combine(data.RootPath, "bedrock-live-save-pending-resume.json"), "invalid pending hold");
            Require(!(await manager.RecoverPendingBedrockResumeAsync()).Ok &&
                File.Exists(Path.Combine(data.RootPath, "bedrock-live-save-pending-resume.json")), "invalid resume marker was erased or dispatched");
        }
        finally { _ = await manager.StopAsync(profile.Id); }
    }
}
