using TogetherServer;

internal static class LiveSavePublicationBoundaryChecks
{
    internal static void Run(string root, string fixture)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        using var data = new LocalData(root);
        var games = new GameServerRegistry(data, true, PortProbeMode.ObserveOnly);
        var backups = new WorldBackupService(data, TimeProvider.System, games: games);
        var exactNativeRun = true;
        var shares = new SharedWorldService(data, backups)
        {
            LiveGameRegistryForChecks = games,
            FixtureLiveCaptureAcceptedForChecks = true,
            FixtureLiveRunIdentityForChecks = _ => exactNativeRun
        };
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            WorldId = "publication-boundary",
            WorldSource = "New",
            ExecutablePath = fixture,
            GamePort = 41004,
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 },
            SharedSavesEnabled = true
        };
        profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
        Directory.CreateDirectory(profile.WorldDirectory);
        var source = Path.Combine(profile.WorldDirectory, "world.dat");
        File.WriteAllText(source, "baseline bytes");
        data.SaveSettings(new() { Profiles = [profile] });
        shares.PublishRoster(profile, []);
        var backup = backups.Create(profile, BackupKinds.Rolling);
        Require(backup.Ok, "publication boundary baseline backup failed");
        var baseline = shares.PublishAfterStop(profile, backup.Backup!.Id);
        Require(baseline.Ok && baseline.Version is not null, "publication boundary baseline failed");
        var initialVersion = baseline.Version!;
        var run = new ManagedRun
        {
            ProfileId = profile.Id,
            OperationId = Guid.NewGuid(),
            Kind = profile.Kind,
            WorldId = profile.WorldId,
            WorldDirectory = profile.WorldDirectory,
            ExecutablePath = fixture,
            ProcessId = 4242,
            StartTimeUtcTicks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
            WasReady = true
        };
        data.SaveRuns([run]);
        File.WriteAllText(source, "new immutable synthetic bytes");
        using var boundaryCancellation = new CancellationTokenSource();
        foreach (var boundaryChange in new Action[]
        {
            () => exactNativeRun = false,
            () => { run.StopRequestedUtc = DateTimeOffset.UtcNow; data.SaveRuns([run]); },
            () => { run.WasReady = false; data.SaveRuns([run]); },
            () => shares.FixtureLiveRunIdentityForChecks = _ => { boundaryCancellation.Cancel(); return true; }
        })
        {
            var completion = new LiveSaveCompletionEvidence(profile.Id, run.OperationId,
                run.ProcessId!.Value, run.StartTimeUtcTicks!.Value, LiveSaveEvidence.RunScopedCompletion,
                initialVersion.CreatedUtc.AddSeconds(1), true);
            var capture = shares.StageLiveCapture(profile, run, completion);
            shares.AfterLiveSignedDirectoryForChecks = boundaryChange;
            var publicationOk = false;
            try
            {
                publicationOk = shares.PublishLiveCapture(profile, capture, new(capture, run.OperationId, true), boundaryCancellation.Token).Ok;
            }
            catch (OperationCanceledException) when (boundaryCancellation.IsCancellationRequested) { }
            Require(!publicationOk && shares.Status(profile).Latest?.VersionHash == initialVersion.VersionHash &&
                File.ReadAllText(source) == "new immutable synthetic bytes",
                "a run change at the final pointer boundary published or damaged source bytes");
            shares.AfterLiveSignedDirectoryForChecks = null;
            exactNativeRun = true;
            shares.FixtureLiveRunIdentityForChecks = _ => exactNativeRun;
            run.StopRequestedUtc = null;
            run.WasReady = true;
            data.SaveRuns([run]);
            var orphan = shares.ReviewLiveOrphan(profile);
            Require(orphan is { Code: "Verified", VersionHash: not null } &&
                !shares.PublishLiveCapture(profile, capture, new(capture, run.OperationId, true)).Ok &&
                shares.Status(profile).Latest?.VersionHash == initialVersion.VersionHash,
                "the interrupted signed copy was lost or promoted automatically on retry");
            Require(shares.QuarantineVerifiedLiveOrphan(profile, orphan.VersionHash!).Ok,
                "the exact interrupted signed copy could not be preserved for review");
            shares.DiscardLiveCapture(profile.Id, capture);
        }
    }
}
