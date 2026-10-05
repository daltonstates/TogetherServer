using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TogetherServer;

internal static class GameConsoleSnapshotChecks
{
    internal static void Run(string root, Action<string, string> createJunction)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Snapshot checks require Windows file identity and sharing.");
        CheckSnapshots(Path.Combine(root, "console-snapshot-store"), createJunction);
        SnapshotReparseChecks.Run(Path.Combine(root, "console-snapshot-reparse"), data => Fixture(data));
        CheckLogCursor(Path.Combine(root, "console-snapshot-cursors"), createJunction);
        CheckGameLayouts(Path.Combine(root, "console-snapshot-layouts"));
        CheckGrammars(Path.Combine(root, "console-snapshot-grammars"));
        CheckTerrariaRouting(Path.Combine(root, "console-snapshot-terraria"));
    }

    // Invoked only by the separate, explicitly isolated Windows console journey.
    internal static async Task RunTerrariaProcessAsync(string root, string fixture)
    {
        var trial = Path.Combine(root, "typed-terraria-process");
        Directory.CreateDirectory(trial);
        var priorFixtureRoot = Environment.GetEnvironmentVariable("TOGETHERSERVER_TERRARIA_FIXTURE_ROOT");
        Environment.SetEnvironmentVariable("TOGETHERSERVER_TERRARIA_FIXTURE_ROOT", trial);
        using var data = new LocalData(Path.Combine(trial, "data"));
        var source = Path.Combine(trial, "original.wld");
        File.WriteAllText(source, "disposable original Terraria fixture world");
        var originalHash = SHA256.HashData(File.ReadAllBytes(source));
        var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(item => item.Port).ToHashSet();
        var port = Enumerable.Range(49152, 16384).First(candidate => !occupied.Contains(candidate));
        var profile = new ServerProfile
        {
            Kind = GameKinds.Terraria,
            Name = "Typed captured fixture",
            ExecutablePath = Path.GetFullPath(fixture),
            GamePort = port
        };
        var imported = TerrariaSetup.ImportCopy(data, profile.Id, source);
        Require(imported.Ok, "typed Terraria fixture world import failed");
        profile.WorldId = imported.WorldId!;
        profile.WorldDirectory = imported.WorldDirectory!;
        var games = new GameServerRegistry(data, false, PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games);
        ManagedRun? run = null;
        ImmutableLiveSaveSnapshot? snapshot = null;
        var store = new ManagedLiveSnapshotStore(data, games);
        try
        {
            Require((await manager.UpdateSettingsAsync(new() { Profiles = [profile] })).Ok, "typed Terraria fixture profile rejected");
            var started = await manager.StartAsync(profile.Id);
            Require(started.Ok, "captured Terraria fixture did not start: " + started.Code);
            profile = data.LoadSettings().Profiles.Single();
            run = data.LoadRuns().Single();
            var driver = new TerrariaServerDriver(data);
            for (var attempt = 0; attempt < 50 && !driver.Health(run).Ok; attempt++) await Task.Delay(100);
            Require(driver.Health(run).Ok && run.ConsoleCaptureProcessId is > 0 &&
                WindowsConsoleProcess.CaptureIdentityMatches(run) && run.LogPath == data.RunLogPath(run.OperationId),
                "Terraria fixture was not routed through its exact recorded console capture");
            Require(!driver.Health(run).PlayerCountTrusted && !run.WasReady,
                "captured Terraria fixture invented readiness/player authority");
            var world = Path.Combine(profile.WorldDirectory, profile.WorldId + ".wld");
            File.WriteAllText(world, "recognizable disposable change before fixed save");
            var adapter = new TerrariaManagedSnapshotAdapter(data, games);
            Require(!adapter.LiveCaptureAccepted, "fixture capture enabled real Terraria acceptance");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            snapshot = await adapter.CaptureAsync(profile, run, timeout.Token);
            Require(store.Verify(profile, run, snapshot) && snapshot.Completion.Kind == LiveSaveEvidence.RunScopedCompletion &&
                snapshot.Files.Count == 1 && snapshot.Files[0].Path == profile.WorldId + ".wld" &&
                File.ReadAllText(Path.Combine(snapshot.SnapshotDirectory, profile.WorldId + ".wld")) == "recognizable disposable change before fixed save",
                "captured Terraria save did not produce exact sealed fixture bytes");
            var commands = Path.Combine(profile.WorldDirectory, "synthetic-console-lines.txt");
            Require(File.ReadAllLines(commands).SequenceEqual(["save"]), "typed adapter sent something besides fixed save");
            File.WriteAllText(world, "later mutable game save");
            Require(store.Verify(profile, run, snapshot) &&
                File.ReadAllText(Path.Combine(snapshot.SnapshotDirectory, profile.WorldId + ".wld")) == "recognizable disposable change before fixed save",
                "later fixture save mutated sealed snapshot");
            Require(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(originalHash), "typed capture touched the original world");
            var stopped = await manager.StopAsync(profile.Id);
            Require(stopped.Ok && File.ReadAllLines(commands).SequenceEqual(["save", "exit"]),
                "captured Terraria exact Stop did not use existing fixed exit");
            Require(!store.Verify(profile, run, snapshot), "retired Terraria run retained capture authority");
            if (run.ConsoleCaptureProcessId is { } captureId)
            {
                try { using var capture = Process.GetProcessById(captureId); Require(capture.WaitForExit(5000), "owned capture did not drain and exit"); }
                catch (ArgumentException) { }
            }
        }
        finally
        {
            try { if (snapshot is not null) store.Discard(snapshot); }
            finally
            {
                try
                {
                    // Only identities returned by this isolated launch are eligible.
                    if (run is not null && run.WorldDirectory == profile.WorldDirectory &&
                        profile.WorldDirectory == data.NewWorldDirectory(profile.Id))
                    {
                        CleanupExactFixture(run.ProcessId, run.StartTimeUtcTicks, run.ExecutablePath, Path.GetFullPath(fixture));
                        if (Environment.ProcessPath is { } host)
                            CleanupExactFixture(run.ConsoleCaptureProcessId, run.ConsoleCaptureStartTimeUtcTicks,
                                run.ConsoleCaptureExecutablePath, Path.GetFullPath(host));
                    }
                }
                finally { Environment.SetEnvironmentVariable("TOGETHERSERVER_TERRARIA_FIXTURE_ROOT", priorFixtureRoot); }
            }
        }
    }

    private static void CleanupExactFixture(int? pid, long? ticks, string recordedExecutable, string expectedExecutable)
    {
        if (pid is not > 0 || ticks is not > 0 || pid == Environment.ProcessId ||
            !Path.GetFullPath(recordedExecutable).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks &&
                Path.GetFullPath(process.MainModule!.FileName).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase))
            { process.Kill(); process.WaitForExit(5000); }
        }
        catch (ArgumentException) { }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ManagedLiveSnapshotStore.ExpectedFailure(ex)) { return; }
        throw new Exception(message);
    }

    private static (ServerProfile Profile, ManagedRun Run, GameServerRegistry Games) Fixture(LocalData data,
        string kind = GameKinds.Fixture)
    {
        using var process = Process.GetCurrentProcess();
        var executable = process.MainModule!.FileName;
        var profile = new ServerProfile
        {
            Kind = kind,
            Name = "Disposable console snapshot",
            WorldId = "synthetic",
            WorldSource = "New",
            SharedSavesEnabled = true,
            ExecutablePath = executable,
            GamePort = kind == GameKinds.Terraria ? 7777 : 2456
        };
        profile.WorldDirectory = kind == GameKinds.MinecraftJava
            ? Path.Combine(data.MinecraftInstallRoot, "java-" + profile.Id.ToString("N"))
            : data.NewWorldDirectory(profile.Id);
        Directory.CreateDirectory(profile.WorldDirectory);
        var run = new ManagedRun
        {
            ProfileId = profile.Id,
            OperationId = Guid.NewGuid(),
            Kind = kind,
            WorldId = profile.WorldId,
            WorldDirectory = profile.WorldDirectory,
            ExecutablePath = executable,
            ProcessId = process.Id,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            GamePort = profile.GamePort,
            WasReady = kind != GameKinds.Terraria,
            DeclaredPorts = [new("TCP", profile.GamePort, "Synthetic")]
        };
        run.LogPath = data.NewRunLogPath(run.OperationId);
        File.WriteAllText(run.LogPath, "");
        data.SaveSettings(new() { Profiles = [profile] });
        data.SaveRuns([run]);
        if (kind == GameKinds.Valheim) data.RecordNewWorld(profile);
        var games = new GameServerRegistry([new SnapshotFixtureDriver(kind)], PortProbeMode.ObserveOnly);
        return (profile, run, games);
    }
    private static LiveSaveCompletionEvidence Completion(ServerProfile profile, ManagedRun run) =>
        new(profile.Id, run.OperationId, run.ProcessId!.Value, run.StartTimeUtcTicks!.Value,
            LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true);

    private static void CheckSnapshots(string root, Action<string, string> createJunction)
    {
        using var data = new LocalData(root);
        var (profile, run, games) = Fixture(data);
        var file = Path.Combine(profile.WorldDirectory, "copy.bin");
        File.WriteAllText(file, "closed original bytes");
        Directory.CreateDirectory(Path.Combine(profile.WorldDirectory, "nested"));
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "nested", "save.bin"), "closed nested save bytes");
        var store = new ManagedLiveSnapshotStore(data, games);
        var evidence = Completion(profile, run);
        Reject(() => store.Stage(profile, run, evidence with { Complete = false }), "incomplete save evidence accepted");
        Reject(() => store.Stage(profile, run, evidence with { OperationId = Guid.NewGuid() }), "cross-run save evidence accepted");
        Reject(() => store.Stage(profile, run, evidence with { CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-10) }), "stale save evidence accepted");
        Reject(() => store.Stage(profile, run, evidence, [new("../copy.bin", 1)]), "query traversal accepted");
        Reject(() => store.Stage(profile, run, evidence, [new("copy.bin", 1)]), "wrong query byte length accepted");
        store.FixtureAvailableBytesForChecks = () => 0;
        Reject(() => store.Stage(profile, run, evidence), "low-space snapshot accepted");
        store.FixtureAvailableBytesForChecks = () => long.MaxValue;
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Reject(() => store.Stage(profile, run, evidence), "an open writable source was copied as immutable");
        store.BeforeSourceLeaseForChecks = () => File.AppendAllText(file, " changed before lease");
        Reject(() => store.Stage(profile, run, evidence), "mutation between inventory and lease accepted");
        store.BeforeSourceLeaseForChecks = null;
        var deniedWrite = false;
        var deniedRename = false;
        var deniedRootRename = false;
        var deniedDestinationRename = false;
        var deniedDestinationParentRename = false;
        var deniedCommitAncestorRename = false;
        var deniedDestinationMetadataWrite = false;
        store.AfterSourceLeaseForChecks = () =>
        {
            try { using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { deniedWrite = true; }
            try { File.Move(file, file + ".moved"); }
            catch (IOException) { deniedRename = true; }
            try { Directory.Move(profile.WorldDirectory, profile.WorldDirectory + ".moved"); }
            catch (IOException) { deniedRootRename = true; }
        };
        store.BeforeDestinationWriteForChecks = () =>
        {
            var pendingRoot = Directory.EnumerateDirectories(Path.Combine(root, "managed-live-snapshots", profile.Id.ToString("N")), "*.partial").Single();
            var destination = Path.Combine(pendingRoot, "payload");
            using (var writableDirectory = OpenWritableDirectory(destination, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
                deniedDestinationMetadataWrite = writableDirectory.IsInvalid && Marshal.GetLastWin32Error() == 32;
            try { Directory.Move(destination, destination + ".plain"); }
            catch (IOException) { deniedDestinationRename = true; }
            try { Directory.Move(Path.Combine(destination, "nested"), Path.Combine(destination, "nested.plain")); }
            catch (IOException) { deniedDestinationParentRename = true; }
        };
        store.BeforeSnapshotCommitForChecks = () =>
        {
            var profileRoot = Path.Combine(root, "managed-live-snapshots", profile.Id.ToString("N"));
            try { Directory.Move(profileRoot, profileRoot + ".plain"); }
            catch (IOException) { deniedCommitAncestorRename = true; }
        };
        var snapshot = store.Stage(profile, run, Completion(profile, run));
        Require(deniedWrite && deniedRename && deniedRootRename && deniedDestinationRename && deniedDestinationParentRename && deniedCommitAncestorRename && deniedDestinationMetadataWrite &&
            store.Verify(profile, run, snapshot), "snapshot did not hold source/destination paths and bytes against mutation at the write boundary");
        store.AfterSourceLeaseForChecks = null;
        store.BeforeDestinationWriteForChecks = null;
        store.BeforeSnapshotCommitForChecks = null;
        var captured = File.ReadAllText(Path.Combine(snapshot.SnapshotDirectory, "copy.bin"));
        File.WriteAllText(file, "game may keep saving after immutable capture");
        Require(File.ReadAllText(Path.Combine(snapshot.SnapshotDirectory, "copy.bin")) == captured &&
            store.Verify(profile, run, snapshot), "game's next save mutated the private captured bytes");
        Require(!store.Verify(profile, run, snapshot with { Completion = evidence with { Complete = false } }) &&
            !store.Verify(profile, run, snapshot with { SnapshotDirectory = profile.WorldDirectory }) &&
            !store.Verify(profile, run, snapshot with { Files = [snapshot.Files[0] with { Sha256 = new string('0', 64) }] }),
            "a forged record bypassed the protected snapshot seal");
        Require(snapshot.Files is not SharedWorldFile[], "mutable file array was exposed by the snapshot record");

        run.StartTimeUtcTicks++;
        data.SaveRuns([run]);
        Require(!store.Verify(profile, run, snapshot), "native process start-time mismatch accepted");
        Reject(() => store.Stage(profile, run, Completion(profile, run)), "record-matched evidence bypassed native start-time identity");
        run.StartTimeUtcTicks--;
        data.SaveRuns([run]);
        run.StopRequestedUtc = DateTimeOffset.UtcNow;
        data.SaveRuns([run]);
        Require(!store.Verify(profile, run, snapshot), "Stop intent retained live-save authority");
        run.StopRequestedUtc = null;
        data.SaveRuns([run]);

        var snapshotRoot = Path.GetDirectoryName(snapshot.SnapshotDirectory)!;
        var plain = snapshotRoot + ".plain";
        Directory.Move(snapshotRoot, plain);
        createJunction(snapshotRoot, profile.WorldDirectory);
        try
        {
            Require(!store.Verify(profile, run, snapshot), "substituted sealed root verified");
            Reject(() => store.Discard(snapshot), "discard followed a substituted snapshot root");
            Require(File.ReadAllText(file) == "game may keep saving after immutable capture", "discard touched source bytes");
        }
        finally { Directory.Delete(snapshotRoot); Directory.Move(plain, snapshotRoot); }
        var privateFile = Path.Combine(snapshot.SnapshotDirectory, "copy.bin");
        File.SetAttributes(privateFile, FileAttributes.Normal);
        File.WriteAllText(privateFile, "tampered sealed bytes");
        Require(!store.Verify(profile, run, snapshot), "tampered sealed bytes verified");
        store.Discard(snapshot);
        Require(!Directory.Exists(snapshotRoot) && File.Exists(file), "discard did not remove only its exact private snapshot");

        using (var cancelled = new CancellationTokenSource())
        {
            store.AfterSourceLeaseForChecks = cancelled.Cancel;
            try { store.Stage(profile, run, Completion(profile, run), cancellationToken: cancelled.Token); throw new Exception("cancelled capture accepted"); }
            catch (OperationCanceledException) { }
            store.AfterSourceLeaseForChecks = null;
            File.AppendAllText(file, " writable after cancellation");
        }
        store.AfterPayloadCopyForChecks = () => throw new IOException("synthetic copy interruption");
        Reject(() => store.Stage(profile, run, Completion(profile, run)), "copy interruption accepted");
        store.AfterPayloadCopyForChecks = null;
        Require(!Directory.EnumerateDirectories(Path.Combine(root, "managed-live-snapshots", profile.Id.ToString("N"))).Any(),
            "failed capture left reusable partial payloads");

        // Simulate a crash intent using this check's disposable protected store.
        var abandonedId = Guid.NewGuid();
        var abandonedRoot = Path.Combine(root, "managed-live-snapshots", profile.Id.ToString("N"), abandonedId.ToString("N"));
        var abandoned = snapshot with { SnapshotId = abandonedId, SnapshotDirectory = Path.Combine(abandonedRoot, "payload"), Files = [] };
        Directory.CreateDirectory(Path.Combine(abandonedRoot + ".partial", "payload"));
        File.WriteAllText(Path.Combine(abandonedRoot + ".partial", "payload", "copy.bin"), "unfinished");
        data.SaveProtected($"managed-live-snapshot-{profile.Id:N}-{abandonedId:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Complete = false, Snapshot = abandoned }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Require(!store.Verify(profile, run, abandoned), "interrupted intent promoted to a sealed snapshot");
        Require(store.DiscardInterrupted(profile.Id) == 1 && !Directory.Exists(abandonedRoot + ".partial"),
            "incomplete protected intent was not safely discardable");
        var finished = store.Stage(profile, run, Completion(profile, run));
        Require(store.DiscardInterrupted(profile.Id) == 0 && store.Verify(profile, run, finished),
            "interrupted cleanup swept a completed sealed snapshot");
        store.Discard(finished);

        var reserved = Guid.NewGuid();
        var ownedRequest = store.Stage(profile, run, Completion(profile, run), reservedSnapshotId: reserved);
        Require(ownedRequest.SnapshotId == reserved, "durable capture request identity was not sealed before return");
        var otherRequest = store.Stage(profile, run, Completion(profile, run));
        Reject(() => store.Stage(profile, run, Completion(profile, run), reservedSnapshotId: reserved), "reserved request silently reused/promoted existing private bytes");
        Reject(() => store.DiscardForAttempt(profile.Id, Guid.NewGuid(), reserved), "wrong operation discarded an exact request's snapshot");
        store.DiscardForAttempt(profile.Id, run.OperationId, reserved);
        Require(!Directory.Exists(ownedRequest.SnapshotDirectory) && store.Verify(profile, run, otherRequest),
            "withdrawn/published cleanup deleted another capture from the same run");
        store.Discard(otherRequest);
        var bounded = Enumerable.Range(0, 8).Select(_ => store.Stage(profile, run, Completion(profile, run))).ToArray();
        Reject(() => store.Stage(profile, run, Completion(profile, run)), "private snapshot bound was exceeded");
        Require(store.DiscardInterrupted(profile.Id) == 0 && bounded.All(item => store.Verify(profile, run, item)),
            "generic recovery deleted complete private snapshots");
        foreach (var item in bounded) store.DiscardForAttempt(profile.Id, run.OperationId, item.SnapshotId);

        var linked = Path.Combine(profile.WorldDirectory, "linked");
        var outside = Path.Combine(root, "disposable-link-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "untouched target");
        createJunction(linked, outside);
        try
        {
            Reject(() => store.Stage(profile, run, Completion(profile, run)), "nested source junction was accepted");
            Require(File.ReadAllText(Path.Combine(outside, "keep.bin")) == "untouched target", "source rejection changed junction target");
        }
        finally { Directory.Delete(linked); }
    }

    private static void CheckLogCursor(string root, Action<string, string> createJunction)
    {
        using var data = new LocalData(root);
        var (profile, run, games) = Fixture(data);
        File.WriteAllText(run.LogPath, "old completion\nold partial");
        using (var cursor = ManagedSaveLogCursor.Open(data, games, profile, run))
        {
            File.AppendAllText(run.LogPath, " must be discarded\nfresh partial");
            Require(cursor.ReadNewLines(profile, run).Count == 0, "stale/partial log line became fresh evidence");
            File.AppendAllText(run.LogPath, " completed\n");
            Require(cursor.ReadNewLines(profile, run).SequenceEqual(["fresh partial completed"]), "fresh complete line was not preserved");
            var renamed = run.LogPath + ".old";
            File.Move(run.LogPath, renamed);
            File.WriteAllText(run.LogPath, "replacement completion\n");
            Reject(() => cursor.ReadNewLines(profile, run), "replaced owned log retained cursor authority");
            File.Delete(renamed);
        }
        File.WriteAllText(run.LogPath, "retained anchor\n");
        using (var cursor = ManagedSaveLogCursor.Open(data, games, profile, run))
        {
            File.WriteAllText(run.LogPath, "rewritten prefix\nnew completion\n");
            Reject(() => cursor.ReadNewLines(profile, run), "rewritten anchor retained save authority");
        }
        File.WriteAllText(run.LogPath, "long retained prefix\n");
        using (var cursor = ManagedSaveLogCursor.Open(data, games, profile, run))
        {
            File.WriteAllText(run.LogPath, "short\n");
            Reject(() => cursor.ReadNewLines(profile, run), "truncated save log retained cursor authority");
        }
        File.WriteAllText(run.LogPath, "");
        using (var cursor = ManagedSaveLogCursor.Open(data, games, profile, run))
        {
            File.AppendAllText(run.LogPath, new string('x', MinecraftConsoleCapture.MaximumFrameBytes + 1) + "\n");
            Reject(() => cursor.ReadNewLines(profile, run), "oversized save line accepted");
        }
        File.WriteAllText(run.LogPath, "");
        using (var cursor = ManagedSaveLogCursor.Open(data, games, profile, run))
        {
            var different = JsonSerializer.Deserialize<ManagedRun>(JsonSerializer.Serialize(run))!;
            different.OperationId = Guid.NewGuid();
            Reject(() => cursor.ReadNewLines(profile, different), "cross-run log cursor accepted");
        }
        var preservedLogs = data.LogsRoot + ".plain";
        var target = Path.Combine(root, "disposable-logs-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, Path.GetFileName(run.LogPath)), "fake completion\n");
        Directory.Move(data.LogsRoot, preservedLogs);
        createJunction(data.LogsRoot, target);
        try { Reject(() => ManagedSaveLogCursor.Open(data, games, profile, run), "linked operation log root accepted"); }
        finally { Directory.Delete(data.LogsRoot); Directory.Move(preservedLogs, data.LogsRoot); }
    }

    private static void CheckGameLayouts(string root)
    {
        foreach (var game in new[] { GameKinds.Valheim, GameKinds.MinecraftJava, GameKinds.Terraria })
        {
            using var data = new LocalData(Path.Combine(root, game));
            var (profile, run, games) = Fixture(data, game);
            var source = new SnapshotFixtureDriver(game).ManagedSaveDirectory(profile)!;
            Directory.CreateDirectory(source);
            string save;
            if (game == GameKinds.Valheim)
            {
                Directory.CreateDirectory(Path.Combine(source, "worlds_local"));
                save = Path.Combine(source, "worlds_local", "synthetic.db");
                File.WriteAllText(Path.Combine(source, "worlds_local", "synthetic.fwl"), "closed metadata");
                File.WriteAllText(Path.Combine(source, "worlds_local", "other.db"), "other world must stay private");
            }
            else save = Path.Combine(source, game == GameKinds.MinecraftJava ? "level.dat" : "synthetic.wld");
            File.WriteAllText(save, "closed synthetic " + game + " world");
            if (game == GameKinds.MinecraftJava) File.WriteAllText(Path.Combine(source, "session.lock"), "session marker");
            File.WriteAllText(Path.Combine(profile.WorldDirectory, "serverconfig.txt"), "private owner configuration");
            var store = new ManagedLiveSnapshotStore(data, games);
            if (game == GameKinds.Valheim)
            {
                const string complete = "10/04/2026 12:34:56: World save (5/5) done. Total time [123ms]";
                File.WriteAllText(run.LogPath, complete + "\n");
                var observer = new ValheimAutosaveObservationCandidate(data);
                using var cursor = observer.Begin(run);
                Require(!observer.Observe(run, cursor), "Valheim historical completion became a fresh save observation");
                File.AppendAllText(run.LogPath, "player says " + complete + "\n" + complete[..^3]);
                Require(!observer.Observe(run, cursor), "Valheim chat-like/partial output completed a save");
                File.AppendAllText(run.LogPath, "ms]\n");
                Require(observer.Observe(run, cursor), "actual Valheim observer missed fresh complete fixture grammar");
            }
            using (var writer = new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Reject(() => store.Stage(profile, run, Completion(profile, run)), game + " writable save accepted");
            var snapshot = store.Stage(profile, run, Completion(profile, run));
            Require(store.Verify(profile, run, snapshot) && snapshot.Files.Count == (game == GameKinds.Valheim ? 2 : 1) &&
                snapshot.Files.All(file => !file.Path.Contains("other", StringComparison.Ordinal) && file.Path != "session.lock" && file.Path != "serverconfig.txt"),
                game + " snapshot included another world/configuration or lost reviewed bytes");
            if (game == GameKinds.Terraria)
            {
                var config = Path.Combine(profile.WorldDirectory, "serverconfig.txt");
                File.WriteAllText(config, "changed reviewed setup");
                Require(!store.Verify(profile, run, snapshot), "changed Terraria setup retained sealed capture authority");
                File.WriteAllText(config, "private owner configuration");
            }
            Require(game != GameKinds.Terraria || !run.WasReady, "Terraria fixture invented readiness authority");
            File.AppendAllText(save, " next save");
            Require(store.Verify(profile, run, snapshot), game + " next save changed its sealed copy");
            store.Discard(snapshot);
        }
    }

    private static string Frame(string message, DateTimeOffset captured, string stream = "Stdout", bool truncated = false) =>
        Encoding.ASCII.GetString(MinecraftCapturedLogFrame.Encode(new(captured, stream, message, truncated))).TrimEnd('\n');

    private static void CheckGrammars(string root)
    {
        var requested = DateTimeOffset.UtcNow;
        foreach (var game in new[] { GameKinds.MinecraftJava, GameKinds.Terraria })
        {
            var begin = game == GameKinds.MinecraftJava ? "[12:34:56] [Server thread/INFO]: Saving the game (this may take a moment!)" : "Saving world data: 100%";
            var complete = game == GameKinds.MinecraftJava ? "[12:34:57] [Server thread/INFO]: Saved the game" : "World saved.";
            var grammar = new ManagedConsoleSaveGrammar(game, requested);
            Require(!grammar.Observe(Frame(complete, requested), requested), game + " completion without a fresh start accepted");
            Require(!grammar.Observe(Frame(begin, requested.AddSeconds(-1)), requested), game + " stale start accepted");
            Require(!grammar.Observe(Frame(begin, requested, "Stderr"), requested), game + " stderr start accepted");
            Require(!grammar.Observe(Frame(begin, requested, truncated: true), requested), game + " truncated start accepted");
            Require(!grammar.Observe(Frame("player says: " + begin, requested), requested), game + " chat-like start accepted");
            Require(!grammar.Observe(Frame(begin, requested), requested), game + " start claimed completion");
            Require(!grammar.Observe(Frame(complete + " extra", requested), requested), game + " partial completion grammar accepted");
            Require(grammar.Observe(Frame(complete, requested.AddMilliseconds(1)), requested.AddMilliseconds(1)), game + " fresh exact sequence was missed");
            Reject(() => grammar.Observe(Frame(complete, requested), requested), game + " completion evidence reused");
            var warning = new ManagedConsoleSaveGrammar(game, requested);
            Reject(() => warning.Observe(Frame("dropped output", requested, "Capture"), requested), game + " output gap remained authoritative");
            var ambiguous = new ManagedConsoleSaveGrammar(game, requested);
            ambiguous.Observe(Frame(begin, requested), requested);
            Reject(() => ambiguous.Observe(Frame(begin, requested.AddMilliseconds(1)), requested), game + " overlapping saves were accepted");
        }
        using var data = new LocalData(root);
        var games = new GameServerRegistry(data);
        Require(!new ValheimManagedSnapshotAdapter(data, games).LiveCaptureAccepted &&
            !new JavaManagedSnapshotAdapter(data, games).LiveCaptureAccepted &&
            !new TerrariaManagedSnapshotAdapter(data, games).LiveCaptureAccepted,
            "synthetic grammar checks enabled real-game acceptance");
    }

    private static void CheckTerrariaRouting(string root)
    {
        using var data = new LocalData(root);
        var (profile, run, _) = Fixture(data, GameKinds.Terraria);
        var driver = new TerrariaServerDriver(data);
        run.LogPath = "";
        driver.PrepareStart(profile, run);
        Require(run.LogPath == data.RunLogPath(run.OperationId), "Terraria did not assign its exact app-owned operation log");
        var world = Path.Combine(profile.WorldDirectory, profile.WorldId + ".wld");
        var allowed = new List<string> { "-world", world, "-port", "7777", "-noupnp" };
        Require(MinecraftConsoleCapture.ValidTerrariaArguments(profile.WorldDirectory, allowed), "fixed Terraria captured launch rejected");
        Require(MinecraftConsoleCapture.ValidTerrariaArguments(profile.WorldDirectory,
            [.. allowed, "-config", Path.Combine(profile.WorldDirectory, "serverconfig.txt")]), "reviewed fixed Terraria config rejected");
        Require(!MinecraftConsoleCapture.ValidTerrariaArguments(profile.WorldDirectory, [.. allowed, "-other", "raw argument"]) &&
            !MinecraftConsoleCapture.ValidTerrariaArguments(profile.WorldDirectory, ["-world", Path.Combine(root, "outside.wld"), "-port", "7777", "-noupnp"]) &&
            !MinecraftConsoleCapture.ValidTerrariaArguments(profile.WorldDirectory, ["-world", world, "-port", "0", "-noupnp"]) &&
            !MinecraftConsoleCapture.ValidTerrariaArguments(profile.WorldDirectory, [.. allowed, "-config", Path.Combine(root, "outside.txt")]),
            "Terraria capture accepted raw or mismatched arguments");
        var observation = TerrariaServerDriver.LocalTcpObservation(true);
        Require(!observation.PlayerCountTrusted && observation.OnlinePlayers is null && observation.State != "Ready",
            "owned log capture promoted Terraria listener to readiness/occupancy authority");
        run.ConsoleCaptureProcessId = run.ProcessId;
        run.ConsoleCaptureStartTimeUtcTicks = run.StartTimeUtcTicks;
        run.ConsoleCaptureExecutablePath = run.ExecutablePath;
        data.SaveRuns([run]);
        Reject(() => new ExactManagedTerrariaLiveSaveCommandPort(data).RequestTerrariaSave(run.OperationId),
            "game PID was accepted as its own independent console capture");
    }

    private sealed class SnapshotFixtureDriver(string kind) : IGameServerDriver
    {
        public string Kind => kind;
        public string DisplayName => "Synthetic closed-save driver";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => kind == GameKinds.MinecraftJava
            ? Path.Combine(profile.WorldDirectory, profile.WorldId) : profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [new("TCP", profile.GamePort, "Synthetic")];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw new NotSupportedException();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw new NotSupportedException();
        public GameHealthResult Health(ManagedRun run) => throw new NotSupportedException();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw new NotSupportedException();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle OpenWritableDirectory(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
