using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure projections, synthetic signed files/protected state and fake asynchronous
// capture delegates only. Never launches a game/app/fixture, listener, browser or process.
internal static class SharedWorldFreshnessChecks
{
    internal static async Task RunAsync(string root)
    {
        Selection();
        CaptureEquality();
        PublishedCopyVerification(Path.Combine(root, "freshness-published-" + Guid.NewGuid().ToString("N")));
        UnchangedSealedCapture(Path.Combine(root, "freshness-unchanged-" + Guid.NewGuid().ToString("N")));
        AcceptanceScope();
        await SchedulingAsync();
        await CancellationAsync();
        await BackoffAsync();
        await HostHooksAsync(Path.Combine(root, "freshness-host-" + Guid.NewGuid().ToString("N")));
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new Exception(message); }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Ancient = new(1971, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SharedWorldVersion Sign(ECDsa key, Guid profileId, Guid groupId, long number,
        DateTimeOffset saved, string? parent = null, IReadOnlyList<SharedWorldFile>? files = null,
        string game = GameKinds.Valheim, SharedWorldPortableSetup? setup = null) =>
        SharedWorldService.SignVersion(new(setup is null ? 1 : 4, groupId, number, parent, profileId,
            game, "synthetic-freshness", saved, SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
            setup ?? new(2456, false), files ?? [new("world.dat", 3, Hash([1, 2, 3]))], "", "", ""), key);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Selection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Guid.NewGuid(); var group = Guid.NewGuid();
        var old = Sign(key, profile, group, 1, Ancient);
        var newer = Sign(key, profile, group, 2, Ancient.AddYears(40), old.VersionHash);
        var partial = Sign(key, profile, group, 3, Ancient.AddYears(50), newer.VersionHash);
        var unverified = Sign(key, profile, group, 4, Ancient.AddYears(51), partial.VersionHash);
        SharedWorldFreshnessCopy[] copies =
        [
            new SharedWorldFreshnessCopy(old, true, true), new(newer, true, true),
            new(partial, false, true), new(unverified, true, false),
            new(unverified with { Number = 999 }, true, true),
            new(Sign(key, Guid.NewGuid(), group, 1000, Ancient), true, true),
            new(Sign(key, profile, Guid.NewGuid(), 1000, Ancient), true, true)
        ];
        Require(SharedWorldFreshness.SelectNewestCompleteVerified(copies, profile, group)?.Version.VersionHash == newer.VersionHash,
            "partial, unsigned, unverified or another scope's newer copy displaced the newest safe copy");
        var ancient = SharedWorldFreshness.SelectNewestCompleteVerified([copies[0]], profile, group);
        Require(ancient?.Version.VersionHash == old.VersionHash &&
            SharedWorldFreshness.CopyAgeSeconds(ancient, Ancient.AddYears(200)) > TimeSpan.FromDays(36500).TotalSeconds,
            "an ancient valid copy was rejected by a recovery age cutoff");
        Require(SharedWorldFreshness.SelectNewestCompleteVerified([copies[2], copies[3]], profile, group) is null,
            "no verified copy was replaced with an incomplete fallback");
        var branch = Sign(key, profile, group, 2, newer.CreatedUtc.AddMinutes(1), old.VersionHash);
        var concurrent = new[] { copies[1], new SharedWorldFreshnessCopy(branch, true, true), copies[0] };
        Require(SharedWorldFreshness.SelectNewestCompleteVerified(concurrent, profile, group) is null &&
            SharedWorldFreshness.Select(concurrent, profile, group).CompetingHeads.Count == 2 &&
            SharedWorldFreshness.SelectNewestCompleteVerified(concurrent, profile, group, newer.VersionHash)?.Version.VersionHash == newer.VersionHash &&
            concurrent.Length == 3 && concurrent[0].Version == newer && concurrent[2].Version == old,
            "a divergent history was selected by a timestamp or branch-local number");
        var distantBranch = Sign(key, profile, group, 9000, Ancient.AddYears(100), branch.VersionHash);
        Require(SharedWorldFreshness.SelectNewestCompleteVerified([copies[0], copies[1], new(distantBranch, true, true)], profile, group) is null &&
            SharedWorldFreshness.SelectNewestCompleteVerified(concurrent, profile, group, new string('F', 64)) is null,
            "a larger unproven branch number or unknown selected head gained default authority");
        var grandchild = Sign(key, profile, group, 3, Ancient.AddYears(-1), newer.VersionHash);
        Require(SharedWorldFreshness.SelectNewestCompleteVerified([copies[0], copies[1], new(grandchild, true, true)], profile, group)?.Version.VersionHash == grandchild.VersionHash &&
            SharedWorldFreshness.SelectNewestCompleteVerified([copies[0], copies[1], new(grandchild, false, false)], profile, group, grandchild.VersionHash)?.Version.VersionHash == newer.VersionHash &&
            SharedWorldFreshness.SelectNewestCompleteVerified([copies[0], new(grandchild, true, true)], profile, group) is null,
            "proven ancestry depended on clocks, ignored missing links or failed verified-predecessor fallback");
        Require(SharedWorldFreshness.CopyAgeSeconds(copies[0] with { Version = old with { CreatedUtc = default } }, Ancient) is null &&
            SharedWorldFreshness.CopyAgeSeconds(copies[1], Ancient) is null,
            "unknown or future save dates invented a fresh age");
    }

    private static void CaptureEquality()
    {
        SharedWorldFile[] files = [new("a.dat", 3, Hash([1, 2, 3])), new("b.dat", 0, Hash([]))];
        var setup = new SharedWorldPortableSetup(2456, false, "1.2.3", []);
        Require(SharedWorldFreshness.SameCapture(files, setup, files.Reverse().ToArray(), setup),
            "unchanged complete payload depended on inventory order");
        Require(!SharedWorldFreshness.SameCapture(files, setup, [files[0]], setup) &&
            !SharedWorldFreshness.SameCapture(files, setup, [files[0], files[1] with { Sha256 = Hash([9]) }], setup) &&
            !SharedWorldFreshness.SameCapture(files, setup, files, setup with { GameVersion = "1.2.4" }) &&
            !SharedWorldFreshness.SameCapture(files, setup, files, setup with { GamePort = 2458 }) &&
            !SharedWorldFreshness.SameCapture([files[0], files[0]], setup, [files[0], files[0]], setup) &&
            !SharedWorldFreshness.SameCapture([], setup, [], setup),
            "changed setup/content or an incomplete/duplicate inventory was deduplicated");
    }

    private sealed class SyntheticStore : IDisposable
    {
        internal LocalData Data { get; }
        internal ServerProfile Profile { get; }
        internal SharedWorldService Shares { get; }
        internal SharedWorldRoster Roster { get; }
        internal ECDsa Key { get; }
        internal SharedWorldPortableSetup Setup { get; }
        internal bool ExactRun = true;

        internal SyntheticStore(string root)
        {
            Data = new(root);
            Profile = new() { Kind = GameKinds.Fixture, SharedSavesEnabled = true, WorldId = "synthetic-freshness", WorldSource = "New" };
            Profile.WorldDirectory = Data.NewWorldDirectory(Profile.Id);
            Directory.CreateDirectory(Profile.WorldDirectory);
            var games = new GameServerRegistry(Data, true, PortProbeMode.ObserveOnly);
            Shares = new(Data, new WorldBackupService(Data, TimeProvider.System, games: games))
            {
                FixtureLiveCaptureAcceptedForChecks = true,
                LiveGameRegistryForChecks = games,
                FixtureLiveRunIdentityForChecks = _ => ExactRun
            };
            Roster = Shares.PublishRoster(Profile, []);
            Key = ECDsa.Create();
            Key.ImportPkcs8PrivateKey(Data.LoadProtected("shared-world-signing-key.protected")!, out _);
            Setup = SharedWorldPortableSetupReader.Capture(ServerSetupSnapshots.Read(Profile, ServerSetupSnapshots.Capture(Profile, Data)));
        }

        internal SharedWorldVersion Version(long number, DateTimeOffset saved, string? parent, byte[] content) =>
            Sign(Key, Profile.Id, Roster.GroupId, number, saved, parent,
                [new("world.dat", content.Length, Hash(content))], Profile.Kind, Setup);

        internal string VersionRoot(SharedWorldVersion version) => Path.Combine(Data.RootPath,
            "shared-worlds", Profile.Id.ToString("N"), version.GroupId.ToString("N"),
            version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));

        internal void Write(SharedWorldVersion version, byte[]? content, bool current)
        {
            var directory = VersionRoot(version);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            if (content is not null)
            {
                Directory.CreateDirectory(Path.Combine(directory, "payload"));
                File.WriteAllBytes(Path.Combine(directory, "payload", "world.dat"), content);
            }
            if (current) File.WriteAllBytes(Path.Combine(Data.RootPath, "shared-worlds",
                Profile.Id.ToString("N"), "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        }

        public void Dispose() { Key.Dispose(); Data.Dispose(); }
    }

    private static void PublishedCopyVerification(string root)
    {
        using var store = new SyntheticStore(root);
        var old = store.Version(1, Ancient, null, [1, 2, 3]);
        store.Write(old, [1, 2, 3], true);
        Require(store.Shares.NewestCompleteVerified(store.Profile, DateTimeOffset.UtcNow, default)?.Version.VersionHash == old.VersionHash,
            "actual signed ancient payload could not be selected");
        var latest = store.Version(2, Ancient.AddYears(40), old.VersionHash, [4, 5, 6]);
        store.Write(latest, [4, 5, 6], true);
        var partial = store.Version(3, Ancient.AddYears(50), latest.VersionHash, [7, 8, 9]);
        store.Write(partial, [7], true);
        var pointer = Path.Combine(store.Data.RootPath, "shared-worlds", store.Profile.Id.ToString("N"), "latest.json");
        var before = File.ReadAllBytes(pointer);
        Require(store.Shares.NewestCompleteVerified(store.Profile, DateTimeOffset.UtcNow, default)?.Version.VersionHash == latest.VersionHash &&
            before.SequenceEqual(File.ReadAllBytes(pointer)) && Directory.Exists(store.VersionRoot(partial)),
            "a partial newer payload was selected, promoted or destroyed");
        File.WriteAllBytes(Path.Combine(store.VersionRoot(latest), "payload", "world.dat"), [0, 0, 0]);
        Require(store.Shares.NewestCompleteVerified(store.Profile, DateTimeOffset.UtcNow, default)?.Version.VersionHash == old.VersionHash,
            "same-length changed bytes escaped verification or no-age fallback");
        var orphan = store.Version(4, DateTimeOffset.UtcNow, partial.VersionHash, [9, 9, 9]);
        store.Write(orphan, [9, 9, 9], false);
        Require(store.Shares.NewestCompleteVerified(store.Profile, DateTimeOffset.UtcNow, default)?.Version.VersionHash == old.VersionHash,
            "a newer complete but unpublished directory gained current-copy authority");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var refused = false;
        try { store.Shares.NewestCompleteVerified(store.Profile, DateTimeOffset.UtcNow, canceled.Token); }
        catch (OperationCanceledException) { refused = true; }
        Require(refused, "published-copy hashing ignored cancellation");
    }

    private static void UnchangedSealedCapture(string root)
    {
        using var store = new SyntheticStore(root);
        var original = store.Version(1, Ancient, null, [1, 2, 3]);
        store.Write(original, [1, 2, 3], true);
        var source = Path.Combine(store.Profile.WorldDirectory, "world.dat");
        File.WriteAllBytes(source, [1, 2, 3]);
        var run = new ManagedRun
        {
            ProfileId = store.Profile.Id,
            Kind = store.Profile.Kind,
            WorldId = store.Profile.WorldId,
            WorldDirectory = store.Profile.WorldDirectory,
            OperationId = Guid.NewGuid(),
            ProcessId = 424242,
            StartTimeUtcTicks = DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
            WasReady = true
        };
        store.Data.SaveRuns([run]);
        LiveSaveCompletionEvidence Completion() => new(store.Profile.Id, run.OperationId, run.ProcessId!.Value,
            run.StartTimeUtcTicks!.Value, LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true);
        var capture = store.Shares.StageLiveCapture(store.Profile, run, Completion());
        Require(store.Shares.ReuseUnchangedLiveCapture(store.Profile, capture, default)?.VersionHash == original.VersionHash &&
            store.Shares.Status(store.Profile).Latest?.Number == 1,
            "an unchanged completed private capture created a duplicate signed publication");
        store.Shares.DiscardLiveCapture(store.Profile.Id, capture);
        File.WriteAllBytes(source, [4, 5, 6]);
        var changed = store.Shares.StageLiveCapture(store.Profile, run, Completion());
        Require(store.Shares.ReuseUnchangedLiveCapture(store.Profile, changed, default) is null &&
            store.Shares.Status(store.Profile).Latest?.VersionHash == original.VersionHash,
            "changed capture bytes were silently deduplicated");
        store.Shares.DiscardLiveCapture(store.Profile.Id, changed);
        File.WriteAllBytes(source, [1, 2, 3]);
        var lateRunChange = store.Shares.StageLiveCapture(store.Profile, run, Completion());
        store.ExactRun = false;
        var denied = false;
        try { store.Shares.ReuseUnchangedLiveCapture(store.Profile, lateRunChange, default); }
        catch (InvalidDataException) { denied = true; }
        Require(denied && File.ReadAllBytes(source).SequenceEqual(new byte[] { 1, 2, 3 }),
            "unchanged-copy success ignored an exact-run change or damaged source bytes");
    }

    private static void AcceptanceScope()
    {
        var profile = Guid.NewGuid(); var binary = Hash([1]); var config = Hash([2]);
        var scope = SharedWorldLiveSaveAdapters.BindInstallation(profile, GameKinds.Valheim,
            "synthetic installation identity", binary, config, false);
        Require(SharedWorldLiveSaveAdapters.SameRealInstallation(scope, scope), "an identical acceptance scope did not match");
        foreach (var other in new[]
        {
            scope with { ProfileId = Guid.NewGuid() }, scope with { Game = GameKinds.MinecraftJava },
            scope with { BinarySha256 = Hash([3]) }, scope with { ConfigurationSha256 = Hash([4]) },
            scope with { InstallationId = Hash([5]) }, scope with { ContractVersion = 2 }, scope with { Synthetic = true },
            scope with { BinaryVersion = "2.0.0.0" }
        }) Require(!SharedWorldLiveSaveAdapters.SameRealInstallation(scope, other), "acceptance escaped its binary/configuration/install scope");
        foreach (var game in new[] { GameKinds.Valheim, GameKinds.MinecraftJava, GameKinds.MinecraftBedrock, GameKinds.Factorio, GameKinds.Terraria })
        {
            var checklist = SharedWorldLiveSaveAdapters.InstallationChecklist(scope with { Game = game }, game);
            Require(!checklist.Accepted && !SharedWorldLiveSaveAdapters.IsGameAccepted(game) &&
                !SharedWorldLiveSaveAdapters.Status(game).Available && checklist.Stages.Count == 6 &&
                checklist.Stages.Select(item => item.Id).SequenceEqual(new[] { "completion", "snapshot", "transfer", "load", "change", "restart" }) &&
                checklist.Stages.All(item => item.State == "Unverified"), "a checklist or fixture credited real-game acceptance");
        }
        Require(SharedWorldLiveSaveAdapters.InstallationChecklist(scope with { Synthetic = true }, scope.Game).Code == "SyntheticEvidenceOnly" &&
            !SharedWorldLiveSaveAdapters.SameRealInstallation(scope with { Synthetic = true }, scope with { Synthetic = true }),
            "synthetic staging evidence accepted a real installation");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private static async Task SchedulingAsync()
    {
        var clock = new ManualClock(); var profile = Guid.NewGuid(); var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LiveSaveActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new SharedWorldFreshnessScheduler(clock,
            _ => Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, true, "Ready")]),
            async (_, token) => { Interlocked.Increment(ref calls); started.TrySetResult(); return await release.Task.WaitAsync(token); });
        var first = scheduler.Schedule(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(Enumerable.Range(0, 20).All(_ => ReferenceEquals(first, scheduler.Schedule(default))) &&
            calls == 1 && scheduler.State(profile).InProgress, "supervisor scheduling created overlapping capture flights");
        release.SetResult(new(true, "LiveSaveUnchanged", "Synthetic unchanged safe capture."));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 20; index++) await scheduler.Schedule(default);
        Require(calls == 1 && !scheduler.State(profile).InProgress && scheduler.State(profile).ConsecutiveFailures == 0,
            "a successful/unchanged capture became a tight forced-save loop");
        clock.Advance(SharedWorldFreshnessScheduler.CaptureInterval);
        await scheduler.Schedule(default);
        Require(calls == 2, "a due enabled shared world was not refreshed");

        var blocked = new SharedWorldFreshnessScheduler(clock,
            _ => Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, false, "GameAcceptanceRequired")]),
            (_, _) => { throw new Exception("An unaccepted real-game capture ran."); });
        await blocked.Schedule(default);
        Require(blocked.State(profile).Code == "GameAcceptanceRequired" && !blocked.State(profile).InProgress,
            "a blocked real adapter was advertised as scheduled live capture");

        var other = Guid.NewGuid(); var order = new List<Guid>();
        var fair = new SharedWorldFreshnessScheduler(clock,
            _ => Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, true, "Ready"), new(other, true, "Ready")]),
            (id, _) => { order.Add(id); return Task.FromResult(new LiveSaveActionResult(true, "LiveSavePublished", "Synthetic capture.")); });
        await fair.Schedule(default); await fair.Schedule(default); await fair.Schedule(default);
        Require(order.Count == 2 && order.Distinct().Count() == 2, "single-flight scheduling starved another due enabled world");

        DateTimeOffset? manualCaptureUtc = null;
        var automaticCalls = 0;
        var afterManual = new SharedWorldFreshnessScheduler(clock,
            _ => Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, true, "Ready", manualCaptureUtc)]),
            (_, _) => { automaticCalls++; return Task.FromResult(new LiveSaveActionResult(true, "LiveSavePublished", "Synthetic capture.")); });
        await afterManual.Schedule(default);
        clock.Advance(TimeSpan.FromSeconds(59)); manualCaptureUtc = clock.GetUtcNow();
        await afterManual.Schedule(default);
        clock.Advance(TimeSpan.FromSeconds(1)); await afterManual.Schedule(default);
        Require(automaticCalls == 1 && afterManual.State(profile).NextAttemptUtc == manualCaptureUtc + SharedWorldFreshnessScheduler.CaptureInterval,
            "automatic capture ran immediately after an explicit owner capture");
    }

    private static async Task CancellationAsync()
    {
        var clock = new ManualClock(); var profile = Guid.NewGuid(); var reads = 0; var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new SharedWorldFreshnessScheduler(clock,
            _ => { Interlocked.Increment(ref reads); return Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, true, "Ready")]); },
            async (_, token) =>
            {
                Interlocked.Increment(ref calls); started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new(true, "Unreachable", "Canceled synthetic capture.");
            });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await scheduler.Schedule(canceled.Token);
        Require(reads == 0 && calls == 0, "pre-canceled scheduling dispatched capture work");
        using var active = new CancellationTokenSource();
        var flight = scheduler.Schedule(active.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); active.Cancel();
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
        Require(scheduler.State(profile) is { InProgress: false, Code: "SharedFreshnessCanceled", LastFailureUtc: null } && calls == 1,
            "canceled scheduling retained a capture flight or invented a save failure");
        await scheduler.Schedule(default);
        Require(calls == 1, "cancellation retried immediately in a forced-save loop");
    }

    private static async Task BackoffAsync()
    {
        var clock = new ManualClock(); var profile = Guid.NewGuid(); var calls = 0;
        var scheduler = new SharedWorldFreshnessScheduler(clock,
            _ => Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, true, "Ready")]),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return calls == 4 ? Task.FromResult(new LiveSaveActionResult(true, "LiveSavePublished", "Synthetic success.")) :
                    Task.FromResult(new LiveSaveActionResult(false, "LiveSaveFailed", "Synthetic failure."));
            });
        for (var failure = 1; failure <= 3; failure++)
        {
            await scheduler.Schedule(default);
            var state = scheduler.State(profile); var delay = SharedWorldFreshnessScheduler.FailureDelay(failure);
            Require(state.ConsecutiveFailures == failure && state.LastFailureCode == "LiveSaveFailed" &&
                state.LastFailureUtc == clock.GetUtcNow() && state.NextAttemptUtc == clock.GetUtcNow() + delay,
                "failure backoff, status or last-failure projection was lost");
            for (var retry = 0; retry < 10; retry++) await scheduler.Schedule(default);
            Require(calls == failure, "a failed capture retried before its backoff deadline");
            clock.Advance(delay - TimeSpan.FromTicks(1)); await scheduler.Schedule(default);
            Require(calls == failure, "capture retried just before its exact backoff boundary");
            clock.Advance(TimeSpan.FromTicks(1));
        }
        var retainedFailure = scheduler.State(profile).LastFailureUtc;
        await scheduler.Schedule(default);
        Require(calls == 4 && scheduler.State(profile).ConsecutiveFailures == 0 &&
            scheduler.State(profile).LastFailureUtc == retainedFailure && scheduler.State(profile).LastSafeCaptureUtc == clock.GetUtcNow() &&
            SharedWorldFreshnessScheduler.FailureDelay(int.MaxValue) == SharedWorldFreshnessScheduler.MaximumBackoff,
            "successful retry erased historical failure or failed to reset/cap backoff");

        var reads = 0;
        var broken = new SharedWorldFreshnessScheduler(clock,
            _ => { Interlocked.Increment(ref reads); throw new IOException("Synthetic private path must not leak."); },
            (_, _) => throw new Exception("No target exists."));
        await broken.Schedule(default); await broken.Schedule(default);
        Require(reads == 1 && broken.State(profile).Code == "SharedFreshnessReviewRequired",
            "target-read failure faulted the supervisor or created a tight retry loop");

        var seeded = new SharedWorldFreshnessScheduler(clock,
            _ => Task.FromResult<IReadOnlyList<SharedFreshnessTarget>>([new(profile, true, "Ready", clock.GetUtcNow())]),
            (_, _) => throw new Exception("Restart replayed a just-completed save."));
        await seeded.Schedule(default);
        Require(seeded.State(profile).NextAttemptUtc == clock.GetUtcNow() + SharedWorldFreshnessScheduler.CaptureInterval,
            "restart ignored the canonical journal's recent capture interval");
    }

    private static async Task HostHooksAsync(string root)
    {
        using var data = new LocalData(root);
        var profile = new ServerProfile { Kind = GameKinds.Valheim, SharedSavesEnabled = true, WorldId = "synthetic-hooks" };
        profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
        Directory.CreateDirectory(profile.WorldDirectory);
        data.SaveSettings(new() { Profiles = [profile] });
        var host = new HostManager(data, new GameServerRegistry(data, true, PortProbeMode.ObserveOnly), new ManualClock());
        await host.ScheduleSharedFreshness(default);
        var status = await host.SharedFreshnessStatusAsync(profile.Id);
        Require(status is { Enabled: true, Code: "GameAcceptanceRequired", CaptureInProgress: false, NewestCompleteVerified: null } &&
            status.LiveSave is { Available: false, Stages.Count: 6 } && data.LoadRuns().Count == 0,
            "automatic scheduling started a game or bypassed real-game acceptance");
        Require((await host.LiveSaveAcceptanceChecklistAsync(profile.Id)) is { Accepted: false, Scope: null, Stages.Count: 6 },
            "a missing installed binary produced bound live-save acceptance");
        Require((await host.SharedFreshnessStatusAsync(Guid.NewGuid())).Code == "UnknownProfile",
            "freshness accepted an unknown saved profile");

        // Actual binary/configuration binding uses only disposable bytes. This
        // never claims that those bytes are an executable or launches anything.
        profile.Kind = GameKinds.Fixture;
        profile.ExecutablePath = Path.Combine(data.RootPath, "synthetic-binary.bin");
        File.WriteAllBytes(profile.ExecutablePath, [1, 2, 3]);
        data.SaveSettings(new() { Profiles = [profile] });
        var scopedHost = new HostManager(data, new GameServerRegistry(data, true, PortProbeMode.ObserveOnly), new ManualClock());
        var originalScope = await scopedHost.LiveSaveAcceptanceChecklistAsync(profile.Id);
        Require(originalScope is { Accepted: false, Code: "SyntheticEvidenceOnly", Scope.Synthetic: true },
            "synthetic installed-byte checklist was unbound or granted real acceptance");
        File.WriteAllBytes(profile.ExecutablePath, [4, 5, 6]);
        var changedBinary = await scopedHost.LiveSaveAcceptanceChecklistAsync(profile.Id);
        Require(changedBinary.Scope is not null && originalScope.Scope?.BinarySha256 != changedBinary.Scope.BinarySha256,
            "changed installed bytes retained the old binary acceptance scope");
        profile.GamePort += 2;
        data.SaveSettings(new() { Profiles = [profile] });
        var changedHost = new HostManager(data, new GameServerRegistry(data, true, PortProbeMode.ObserveOnly), new ManualClock());
        var changedSetup = await changedHost.LiveSaveAcceptanceChecklistAsync(profile.Id);
        Require(changedSetup.Scope is not null && changedBinary.Scope.BinarySha256 == changedSetup.Scope.BinarySha256 &&
            changedBinary.Scope.ConfigurationSha256 != changedSetup.Scope.ConfigurationSha256 && !changedSetup.Accepted,
            "changed reviewed setup retained acceptance or changed the unrelated binary fingerprint");
    }
}
