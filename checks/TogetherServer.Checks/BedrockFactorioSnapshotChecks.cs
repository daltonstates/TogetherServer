using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TogetherServer;

// Synthetic evidence only. These checks never opt a game into owner/Friend
// capture and do not establish native console grammar or real-world loadability.
internal static class BedrockFactorioSnapshotChecks
{
    internal static Task RunAsync(string root, string fixture)
    {
        var bedrock = new ServerProfile { Kind = GameKinds.MinecraftBedrock, WorldId = "world" };
        var files = BedrockHeldSnapshotGrammar.ParseFiles(bedrock,
            "worlds/world/db/synthetic.dat:4, worlds/world/level.dat:0");
        Require(files.Count == 2 && files[0] == new ManagedLiveSnapshotFile("db/synthetic.dat", 4),
            "Bedrock query did not retain reviewed relative files and exact lengths");
        foreach (var query in new[]
        {
            "worlds/world/../outside:4", "worlds/other/db/synthetic.dat:4", "/worlds/world/db/synthetic.dat:4",
            "worlds/world/db\\synthetic.dat:4", "worlds/world/C:/outside:4", "worlds/world/db//synthetic.dat:4",
            "worlds/world/db/synthetic.dat:4, worlds/world/DB/SYNTHETIC.DAT:4",
            "worlds/world/db/synthetic.dat:-1", "worlds/world/db/synthetic.dat:+4",
            "worlds/world/db/synthetic.dat:4.0", "worlds/world/db/synthetic.dat: 4",
            "worlds/world/db/synthetic.dat:2147483649", "worlds/world/db/synthetic.dat:4, ",
            "worlds/world/db/synthetic.dat:4,worlds/world/level.dat:0",
            "worlds/world/db/trailing.:4", "worlds/world/db/trailing :4",
            "worlds/world/db/synthetic.dat:4\nworlds/world/other:4", new string('x', 65 * 1024),
            string.Join(", ", Enumerable.Range(0, 5).Select(index => $"worlds/world/file{index}:2147483648"))
        }) Reject(() => BedrockHeldSnapshotGrammar.ParseFiles(bedrock, query), "unsafe Bedrock query accepted");

        var now = DateTimeOffset.UtcNow;
        var nativeReady = "[2026-10-04 12:00:00.000 INFO] " + BedrockHeldSnapshotGrammar.QueryReady;
        Require(BedrockHeldSnapshotGrammar.TrustedBody(Frame(nativeReady, now), now.AddSeconds(-1)) ==
            BedrockHeldSnapshotGrammar.QueryReady, "fresh captured stdout was not recognized");
        foreach (var line in new[]
        {
            nativeReady, Frame(nativeReady, now.AddSeconds(-2)), Frame(nativeReady, now.AddMinutes(1)),
            Frame(nativeReady, now, "Stderr"), Frame(nativeReady, now, truncated: true),
            Frame("[Server thread/INFO]: <Alice> " + nativeReady, now), Frame(nativeReady + "\rforged", now)
        }) Require(BedrockHeldSnapshotGrammar.TrustedBody(line, now.AddSeconds(-1)) is null,
            "untrusted/stale Bedrock output became completion evidence");

        var marker = new BedrockPendingResume(Guid.NewGuid(), Guid.NewGuid(), 7101, now.UtcTicks,
            Path.Combine(root, "synthetic-bedrock.exe"), Path.Combine(root, "synthetic-bedrock"),
            Guid.NewGuid(), true, now.AddSeconds(-1));
        var acknowledged = BedrockHeldSnapshotGrammar.ResumeEvidence(
            Frame("[2026-10-04 12:00:00.001 INFO] " + BedrockHeldSnapshotGrammar.ResumeAcknowledged, now), marker)!;
        Require(BedrockLiveSaveCandidate.MatchesOwnedResume(marker, acknowledged, now),
            "fresh exact-attempt resume acknowledgement rejected");
        foreach (var wrong in new[]
        {
            acknowledged with { ProfileId = Guid.NewGuid() }, acknowledged with { OperationId = Guid.NewGuid() },
            acknowledged with { ProcessId = acknowledged.ProcessId + 1 },
            acknowledged with { StartTimeUtcTicks = acknowledged.StartTimeUtcTicks + 1 },
            acknowledged with { AttemptNonce = Guid.NewGuid() },
            acknowledged with { ResumeRequestedUtc = acknowledged.ResumeRequestedUtc.AddTicks(-1) },
            acknowledged with { AcknowledgedUtc = acknowledged.ResumeRequestedUtc.AddTicks(-1) },
            acknowledged with { AcknowledgedUtc = now.AddMinutes(1) }
        }) Require(!BedrockLiveSaveCandidate.MatchesOwnedResume(marker, wrong, now),
            "a stale/different resume acknowledgement matched the durable hold");
        Require(!BedrockLiveSaveCandidate.MatchesOwnedResume(marker with { ResumeDispatched = false }, acknowledged, now) &&
            !BedrockLiveSaveCandidate.MatchesOwnedResume(marker with { AttemptNonce = Guid.Empty }, acknowledged, now) &&
            BedrockHeldSnapshotGrammar.ResumeEvidence(Frame("[2026-10-04 12:00:00.001 INFO] " +
                BedrockHeldSnapshotGrammar.ResumeAcknowledged, now.AddSeconds(-2)), marker) is null,
            "a pre-dispatch or invalid hold matched resume evidence");
        var markerRoot = Path.Combine(root, "synthetic-resume-reopen");
        using (var markerData = new LocalData(markerRoot))
            File.WriteAllText(Path.Combine(markerRoot, "bedrock-live-save-pending-resume.json"),
                JsonSerializer.Serialize(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using (var reopened = new LocalData(markerRoot))
            Require(new BedrockLiveSaveCandidate(reopened).ReadPending() == marker,
                "durable exact-attempt resume marker did not survive reopen");

        var factorio = new ServerProfile
        {
            Kind = GameKinds.Factorio,
            WorldId = "world",
            WorldDirectory = Path.Combine(root, "reviewed-factorio")
        };
        string Start(string target) => "   1.000 Info AppManager.cpp:394: Saving game as " + target;
        const string finish = "   1.001 Info AppManagerStates.cpp:1802: Saving finished";
        var grammar = new FactorioSaveCompletionGrammar(factorio);
        Require(!grammar.Observe("RCON replied Save requested") && !grammar.Observe(Start(grammar.Target)) &&
            grammar.Observe(finish), "Factorio required target/start/completion sequence was not recognized");
        Reject(() => grammar.Observe(finish), "duplicate Factorio completion accepted");
        Reject(() => new FactorioSaveCompletionGrammar(factorio).Observe(finish), "reply/completion alone accepted");
        foreach (var target in new[]
        {
            Path.Combine(factorio.WorldDirectory, "different.zip"), "world.zip",
            Path.Combine(factorio.WorldDirectory, "..", "reviewed-factorio", "world.zip"),
            FactorioSaveCompletionGrammar.ReviewedTarget(factorio) + " (non-blocking)", "C:\\outside\\world.zip"
        }) Reject(() => new FactorioSaveCompletionGrammar(factorio).Observe(Start(target)), "wrong/ambiguous Factorio target accepted");
        var repeated = new FactorioSaveCompletionGrammar(factorio);
        repeated.Observe(Start(repeated.Target));
        Reject(() => repeated.Observe(Start(repeated.Target)), "overlapping Factorio saves accepted");
        Reject(() => new FactorioSaveCompletionGrammar(factorio).Observe(
            "   1.000 Info AppManager.cpp:394: Auto saving map as " + grammar.Target), "autosave accepted as requested save");

        CheckSealedFixtureCopies(root, fixture);
        using var acceptanceData = new LocalData(Path.Combine(root, "snapshot-acceptance"));
        var games = new GameServerRegistry(acceptanceData, true, PortProbeMode.ObserveOnly);
        Require(!new BedrockManagedSnapshotAdapter(acceptanceData, games).LiveCaptureAccepted &&
            !new FactorioManagedSnapshotAdapter(acceptanceData, games).LiveCaptureAccepted &&
            !SharedWorldLiveSaveAdapters.Status(GameKinds.MinecraftBedrock).Available &&
            !SharedWorldLiveSaveAdapters.Status(GameKinds.Factorio).Available,
            "synthetic evidence enabled a real-game action");
        return Task.CompletedTask;
    }

    private static void CheckSealedFixtureCopies(string root, string fixture)
    {
        using var data = new LocalData(Path.Combine(root, "synthetic-held-snapshot"));
        var profile = new ServerProfile
        {
            Name = "Synthetic held snapshot",
            Kind = GameKinds.Fixture,
            WorldId = "world",
            ExecutablePath = fixture,
            GamePort = 48071,
            Backups = new() { MinimumFreeSpaceMb = 0 }
        };
        profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
        Directory.CreateDirectory(Path.Combine(profile.WorldDirectory, "db"));
        var source = Path.Combine(profile.WorldDirectory, "db", "synthetic.dat");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "unqueried.dat"), "not selected");
        var run = new ManagedRun
        {
            ProfileId = profile.Id,
            OperationId = Guid.NewGuid(),
            Kind = profile.Kind,
            WorldId = profile.WorldId,
            WorldDirectory = profile.WorldDirectory,
            ExecutablePath = fixture,
            GamePort = profile.GamePort,
            DeclaredPorts = [new("TCP", profile.GamePort, "Fixture")],
            ProcessId = 7102,
            StartTimeUtcTicks = DateTimeOffset.UtcNow.AddSeconds(-1).UtcTicks,
            WasReady = true
        };
        data.SaveSettings(new() { Profiles = [profile] });
        data.SaveRuns([run]);
        var games = new GameServerRegistry(data, true, PortProbeMode.ObserveOnly);
        var store = new ManagedLiveSnapshotStore(data, games) { FixtureRunIdentityForChecks = _ => true };
        LiveSaveCompletionEvidence Completion(LiveSaveEvidence kind) => new(profile.Id, run.OperationId,
            run.ProcessId!.Value, run.StartTimeUtcTicks!.Value, kind, DateTimeOffset.UtcNow, true);
        var query = new ManagedLiveSnapshotFile[] { new("db/synthetic.dat", 4) };
        var snapshot = store.Stage(profile, run, Completion(LiveSaveEvidence.FrozenSnapshotQuery), query);
        try
        {
            Require(snapshot.Files.Count == 1 && snapshot.Files[0].Path == "db/synthetic.dat" &&
                File.ReadAllBytes(Path.Combine(snapshot.SnapshotDirectory, "db", "synthetic.dat")).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "held copy included unqueried files or lost the requested bytes");
            // Resume may mutate the source; a verified sealed copy must own the
            // original bytes independently of the running game's later writes.
            File.WriteAllBytes(source, [4, 3, 2, 1]);
            Require(store.Verify(profile, run, snapshot), "post-resume source writes changed the sealed snapshot");
            Require(!store.Verify(profile, run, snapshot with { OperationId = Guid.NewGuid() }),
                "snapshot was accepted for a different operation");
            var payload = Path.Combine(snapshot.SnapshotDirectory, "db", "synthetic.dat");
            File.SetAttributes(payload, FileAttributes.Normal);
            File.WriteAllBytes(payload, [9, 9, 9, 9]);
            Require(!store.Verify(profile, run, snapshot), "tampered sealed bytes remained verified");
        }
        finally { store.Discard(snapshot); }

        Reject(() => store.Stage(profile, run, Completion(LiveSaveEvidence.FrozenSnapshotQuery),
            [new("db/synthetic.dat", 5)]), "a mismatched query size was copied");
        using (var writer = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Reject(() => store.Stage(profile, run, Completion(LiveSaveEvidence.FrozenSnapshotQuery), query),
                "an existing game writer was shared by a held snapshot");
        store.FixtureAvailableBytesForChecks = () => 0;
        Reject(() => store.Stage(profile, run, Completion(LiveSaveEvidence.FrozenSnapshotQuery), query),
            "low-space held copy was returned");
        store.FixtureAvailableBytesForChecks = null;
        using (var canceled = new CancellationTokenSource())
        {
            store.AfterPayloadCopyForChecks = canceled.Cancel;
            Reject(() => store.Stage(profile, run, Completion(LiveSaveEvidence.FrozenSnapshotQuery), query, canceled.Token),
                "cancellation after payload copy returned a snapshot");
            store.AfterPayloadCopyForChecks = null;
        }
        store.AfterSourceLeaseForChecks = () => Reject(() => File.WriteAllText(source, "writer"),
            "the selected source could be modified while all leases were held");
        var leased = store.Stage(profile, run, Completion(LiveSaveEvidence.FrozenSnapshotQuery), query);
        store.AfterSourceLeaseForChecks = null;
        store.Discard(leased);

        var archivePath = Path.Combine(profile.WorldDirectory, "world.zip");
        WriteZip(archivePath, "world/level.dat", "synthetic closed save");
        using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var inspected = FactorioClosedArchiveCandidate.InspectArchiveContents(archive);
            var closed = store.Stage(profile, run, Completion(LiveSaveEvidence.ClosedSaveArchive),
                [new("world.zip", inspected.ArchiveLength)]);
            try { Require(store.Verify(profile, run, closed), "closed archive was not sealed/verified"); }
            finally { store.Discard(closed); }
            Reject(() => File.WriteAllText(archivePath, "incomplete writer"), "archive lease allowed a writer");
        }
        using (var canceled = new CancellationTokenSource())
        using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            canceled.Cancel();
            Reject(() => FactorioClosedArchiveCandidate.InspectArchiveContents(archive, canceled.Token),
                "archive inspection ignored cancellation");
        }
        var invalid = Path.Combine(profile.WorldDirectory, "invalid.zip");
        File.WriteAllBytes(invalid, [0x50, 0x4b, 0x03, 0x04]);
        RejectArchive(invalid, "a partial ZIP became a closed archive");
        WriteZip(invalid, "../outside.dat", "synthetic unsafe entry");
        RejectArchive(invalid, "a traversal archive was accepted");
        WriteZip(invalid, "world/level.dat", "unique-crc-payload");
        var bytes = File.ReadAllBytes(invalid);
        var at = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("unique-crc-payload"));
        Require(at >= 0, "synthetic CRC payload was compressed unexpectedly");
        bytes[at] ^= 0x01;
        File.WriteAllBytes(invalid, bytes);
        RejectArchive(invalid, "archive content with a wrong CRC was accepted");
    }

    // Include this helper source in the existing MinecraftChecks runner, which
    // already serves the MinecraftConsoleCapture child command. No acceptance
    // switch is changed: only an explicitly copied disposable fixture is used.
    internal static async Task RunBedrockProcessAsync(string root, string minecraftFixture)
    {
        RequireSyntheticFixture(minecraftFixture, "TogetherServer.MinecraftFixture");
        var testRoot = Path.Combine(root, "bedrock-sealed-snapshot-process");
        using var data = new LocalData(Path.Combine(testRoot, "host-data"));
        var server = Path.Combine(data.MinecraftInstallRoot, "bedrock-synthetic");
        Directory.CreateDirectory(server);
        var executable = CopyFixture(minecraftFixture, server, "bedrock_server.exe");
        var port = FreePorts(bedrock: true).Game;
        File.WriteAllText(Path.Combine(server, "server.properties"),
            $"level-name=world\nserver-port={port}\nserver-portv6={port + 1}\nenable-lan-visibility=false\n");
        var source = Path.Combine(server, "worlds", "world", "db", "synthetic.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var profile = new ServerProfile
        {
            Kind = GameKinds.MinecraftBedrock,
            Name = "Synthetic Bedrock sealed snapshot",
            WorldId = "world",
            WorldDirectory = server,
            ExecutablePath = executable,
            GamePort = port,
            Minecraft = new(),
            Backups = new() { Enabled = false, MinimumFreeSpaceMb = 0 }
        };
        var games = new GameServerRegistry(data, false, PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games);
        Require((await manager.UpdateSettingsAsync(new() { Profiles = [profile] })).Ok, "Bedrock fixture profile rejected");
        Require((await manager.StartAsync(profile.Id)).Ok, "Bedrock fixture failed to start");
        try
        {
            await Ready(manager, profile.Id);
            var run = data.LoadRuns().Single();
            File.WriteAllText(Path.Combine(server, "synthetic-save-operation-id.txt"), run.OperationId.ToString("D"));
            var adapter = new BedrockManagedSnapshotAdapter(data, games);
            var store = new ManagedLiveSnapshotStore(data, games);
            using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var mutateOnResume = Path.Combine(server, "synthetic-mutate-on-resume.marker");
            File.WriteAllText(mutateOnResume, "change the disposable source when fixed resume arrives");
            var snapshot = await adapter.CaptureAsync(profile, run, captureTimeout.Token);
            try
            {
                Require(snapshot.Completion.Kind == LiveSaveEvidence.FrozenSnapshotQuery &&
                    snapshot.Files.Count == 1 && snapshot.Files[0].Path == "db/synthetic.dat" &&
                    store.Verify(profile, run, snapshot) && !new BedrockLiveSaveCandidate(data).HasPendingResume,
                    "held query/copy/resume did not produce exact sealed synthetic evidence");
                Require(File.ReadAllBytes(source).SequenceEqual(new byte[] { 4, 3, 2, 1 }) &&
                    store.Verify(profile, run, snapshot) &&
                    File.ReadAllBytes(Path.Combine(snapshot.SnapshotDirectory, "db", "synthetic.dat")).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                    "copy did not finish before resume released the held source to game writes");
            }
            finally { File.Delete(mutateOnResume); store.Discard(snapshot); }
            var commands = File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt"));
            Require(commands.TakeLast(3).SequenceEqual(new[] { "save hold", "save query", "save resume" }),
                "Bedrock fixed commands were not hold/query/copy/resume ordered");

            var queryMode = Path.Combine(server, "synthetic-snapshot-query-mode.txt");
            foreach (var mode in new[] { "traversal", "ambiguous", "size-mismatch" })
            {
                File.WriteAllText(queryMode, mode);
                await RejectAsync(() => adapter.CaptureAsync(profile, run, captureTimeout.Token),
                    "invalid held query returned a snapshot");
                Require(!new BedrockLiveSaveCandidate(data).HasPendingResume, "parser/copy failure did not confirm resume");
            }
            File.WriteAllText(queryMode, "missing");
            var beforeCanceled = File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt")).Length;
            using (var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
                await RejectAsync(() => adapter.CaptureAsync(profile, run, canceled.Token), "canceled held query returned a snapshot");
            Require(!new BedrockLiveSaveCandidate(data).HasPendingResume &&
                File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt")).Length >= beforeCanceled + 3,
                "capture cancellation skipped the held query or fresh resume acknowledgement");
            File.WriteAllText(queryMode, "normal");
            using (var writer = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                await RejectAsync(() => adapter.CaptureAsync(profile, run, captureTimeout.Token), "held source with a writer was copied");
            Require(!new BedrockLiveSaveCandidate(data).HasPendingResume, "source-lease failure skipped resume");

            var candidate = new BedrockLiveSaveCandidate(data);
            candidate.Hold(run);
            var legacy = data.LoadRuns().Single(); legacy.LogPath = "";
            Require(!await adapter.ResumeAsync(profile, legacy, captureTimeout.Token) &&
                candidate.ReadPending() is { ResumeDispatched: true },
                "legacy/no-owned-log recovery claimed completion or erased the durable marker");
            Require(await adapter.ResumeAsync(profile, run, captureTimeout.Token) && !candidate.HasPendingResume,
                "fresh owned-log retry did not confirm exact-run resume");

            candidate.Hold(run);
            var refusedConsole = data.LoadRuns().Single();
            refusedConsole.ConsoleCaptureProcessId = Environment.ProcessId;
            var beforeRefused = File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt")).Length;
            data.SaveRuns([refusedConsole]);
            try
            {
                var refused = await manager.RecoverPendingBedrockResumeAsync(profile.Id);
                Require(!refused.Ok && refused.Code == "BedrockResumeReviewRequired" &&
                    candidate.ReadPending() is { ResumeDispatched: false } &&
                    File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt")).Length == beforeRefused,
                    "refused console dispatch was reported as sent or erased the held attempt");
            }
            finally { data.SaveRuns([run]); }
            Require(await adapter.ResumeAsync(profile, run, captureTimeout.Token), "exact-run retry after refused console dispatch failed");

            candidate.Hold(run);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Require(!await adapter.ResumeAsync(profile, run, canceled.Token) &&
                    candidate.ReadPending() is { ResumeDispatched: true },
                    "canceled owner recovery skipped resume or claimed an acknowledgement");
            }
            Require(await adapter.ResumeAsync(profile, run, captureTimeout.Token), "fresh retry after canceled owner recovery failed");

            BedrockPendingResume? owned = null;
            candidate.Hold(run, Guid.NewGuid(), value => owned = value);
            var resumeCount = File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt")).Count(value => value == "save resume");
            var markerPath = Path.Combine(data.RootPath, "bedrock-live-save-pending-resume.json");
            File.WriteAllText(markerPath, "synthetic damaged marker");
            Reject(() => candidate.ResumeOwnedAttempt(run, owned!), "damaged hold marker was silently accepted");
            await Wait(() => File.ReadAllLines(Path.Combine(server, "synthetic-console-lines.txt")).Count(value => value == "save resume") > resumeCount);
            Require(candidate.HasPendingResume, "damaged marker was removed without acknowledgement");
            File.WriteAllText(markerPath, JsonSerializer.Serialize(owned, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Require(await adapter.ResumeAsync(profile, run, captureTimeout.Token), "synthetic reviewed-marker retry did not resume");

            File.WriteAllText(Path.Combine(server, "synthetic-resume-withhold.marker"), "synthetic withheld acknowledgement");
            await RejectAsync(() => adapter.CaptureAsync(profile, run, CancellationToken.None),
                "unacknowledged resume returned a publishable snapshot");
            Require(candidate.ReadPending() is { ResumeDispatched: true, ResumeRequestedUtc: not null },
                "missing resume acknowledgement erased its durable attempt");
            File.Delete(Path.Combine(server, "synthetic-resume-withhold.marker"));
            using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Require(await adapter.ResumeAsync(profile, run, retryTimeout.Token) && !candidate.HasPendingResume &&
                !adapter.LiveCaptureAccepted, "exact-run resume retry failed or synthetic evidence accepted a real game");
            RequireExactFixtureAlive(run);
        }
        finally { Require((await manager.StopAsync(profile.Id)).Ok, "Bedrock fixture failed graceful cleanup"); }
    }

    internal static async Task RunFactorioProcessAsync(string root, string factorioFixture)
    {
        RequireSyntheticFixture(factorioFixture, "TogetherServer.FactorioFixture");
        var testRoot = Path.Combine(root, "factorio-sealed-snapshot-process");
        Directory.CreateDirectory(testRoot);
        var previousRoot = Environment.GetEnvironmentVariable("TOGETHERSERVER_FACTORIO_FIXTURE_ROOT");
        Environment.SetEnvironmentVariable("TOGETHERSERVER_FACTORIO_FIXTURE_ROOT", testRoot);
        try
        {
            using var data = new LocalData(Path.Combine(testRoot, "host-data"));
            var ports = FreePorts();
            var source = Path.Combine(testRoot, "original", "world.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            WriteZip(source, "world/level.dat", "synthetic original save");
            var profile = new ServerProfile
            {
                Kind = GameKinds.Factorio,
                Name = "Synthetic Factorio sealed snapshot",
                ExecutablePath = factorioFixture,
                GamePort = ports.Game,
                Factorio = new() { RconPort = ports.Rcon },
                Backups = new() { Enabled = false, MinimumFreeSpaceMb = 0 }
            };
            var imported = FactorioSetup.ImportCopy(data, profile.Id, source);
            Require(imported.Ok, "synthetic Factorio source was not copied");
            profile.WorldId = imported.WorldId!; profile.WorldDirectory = imported.WorldDirectory!;
            var games = new GameServerRegistry(data, false, PortProbeMode.ObserveOnly);
            var manager = new HostManager(data, games);
            Require((await manager.UpdateSettingsAsync(new() { Profiles = [profile] })).Ok, "Factorio fixture profile rejected");
            Require((await manager.StartAsync(profile.Id)).Ok, "Factorio fixture failed to start");
            try
            {
                await Ready(manager, profile.Id);
                var run = data.LoadRuns().Single();
                var adapter = new FactorioManagedSnapshotAdapter(data, games);
                var store = new ManagedLiveSnapshotStore(data, games);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var snapshot = await adapter.CaptureAsync(profile, run, timeout.Token);
                try
                {
                    Require(snapshot.Completion.Kind == LiveSaveEvidence.ClosedSaveArchive &&
                        snapshot.Files.Count == 1 && snapshot.Files[0].Path == "world.zip" &&
                        store.Verify(profile, run, snapshot), "Factorio did not bind/seal the exact completed reviewed archive");
                    Require(File.ReadAllText(Path.Combine(profile.WorldDirectory, "synthetic-server-save-received.marker")) ==
                        "authenticated /server-save", "Factorio sent an unreviewed target argument or command");
                    using var zip = new FileStream(Path.Combine(snapshot.SnapshotDirectory, "world.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    Require(FactorioClosedArchiveCandidate.InspectArchiveContents(zip).EntryCount == 1,
                        "Factorio sealed archive was not complete and CRC verified");
                }
                finally { store.Discard(snapshot); }
                var modePath = Path.Combine(profile.WorldDirectory, "synthetic-snapshot-mode.txt");
                foreach (var mode in new[] { "different-target", "partial-archive" })
                {
                    File.WriteAllText(modePath, mode);
                    await RejectAsync(() => adapter.CaptureAsync(profile, run, timeout.Token),
                        "mismatched completion target or partial archive returned a snapshot");
                }
                File.WriteAllText(modePath, "reply-only");
                using (var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    await RejectAsync(() => adapter.CaptureAsync(profile, run, canceled.Token),
                        "RCON acknowledgement without fresh completion returned a snapshot");
                File.WriteAllText(modePath, "normal");
                var recovered = await adapter.CaptureAsync(profile, run, timeout.Token);
                store.Discard(recovered);
                RequireExactFixtureAlive(run);
                Require(!adapter.LiveCaptureAccepted, "synthetic Factorio archive evidence accepted a real game");
            }
            finally { Require((await manager.StopAsync(profile.Id)).Ok, "Factorio fixture failed graceful cleanup"); }
        }
        finally { Environment.SetEnvironmentVariable("TOGETHERSERVER_FACTORIO_FIXTURE_ROOT", previousRoot); }
    }

    private static void RequireSyntheticFixture(string fixture, string project)
    {
        Require(File.Exists(fixture) && Path.GetFullPath(fixture).Contains(
            Path.DirectorySeparatorChar + project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Only the reviewed disposable fixture build may be passed to a snapshot process check");
    }

    private static string CopyFixture(string fixture, string directory, string name)
    {
        var source = Path.GetDirectoryName(fixture)!;
        var assembly = Path.GetFileNameWithoutExtension(fixture);
        foreach (var file in Directory.GetFiles(source, assembly + ".*"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        var target = Path.Combine(directory, name);
        File.Copy(fixture, target, overwrite: true);
        foreach (var suffix in new[] { ".runtimeconfig.json", ".deps.json" })
            File.Copy(Path.Combine(source, assembly + suffix), Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + suffix), overwrite: true);
        return target;
    }

    private static (int Game, int Rcon) FreePorts(bool bedrock = false)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var game = Random.Shared.Next(35000, 49000); var rcon = Random.Shared.Next(50000, 59000);
            GamePort[] ports = bedrock ? [new("UDP", game, "Bedrock", "IPv4"), new("UDP", game + 1, "Bedrock IPv6", "IPv6")] :
                [new("UDP", game, "Factorio"), new("TCP", rcon, "Factorio local RCON")];
            if (GameServerRegistry.PortsAvailable(ports, PortProbeMode.ObserveOnly)) return (game, rcon);
        }
        throw new Exception("No free disposable snapshot fixture ports found");
    }

    private static async Task Ready(HostManager manager, Guid profileId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await manager.RefreshObservationsAsync();
            if ((await manager.SnapshotAsync()).Runs.SingleOrDefault(run => run.ProfileId == profileId)?.State == "Ready") return;
            await Task.Delay(100);
        }
        throw new Exception("Disposable snapshot fixture did not become ready");
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++) { if (condition()) return; await Task.Delay(100); }
        throw new Exception("Disposable snapshot fixture did not observe the expected fixed command");
    }

    private static void RequireExactFixtureAlive(ManagedRun run)
    {
        using var process = Process.GetProcessById(run.ProcessId!.Value);
        Require(!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
            Path.GetFullPath(process.MainModule!.FileName).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase),
            "Snapshot capture stopped or replaced its exact disposable fixture run");
    }

    private static async Task RejectAsync(Func<Task<ImmutableLiveSaveSnapshot>> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ManagedLiveSnapshotStore.ExpectedFailure(ex) || ex is OperationCanceledException) { return; }
        throw new Exception(message);
    }

    private static void RejectArchive(string path, string message)
    {
        using var archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Reject(() => FactorioClosedArchiveCandidate.InspectArchiveContents(archive), message);
    }

    private static void WriteZip(string path, string entry, string content)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(entry, CompressionLevel.NoCompression).Open());
        writer.Write(content);
    }

    private static string Frame(string body, DateTimeOffset utc, string stream = "Stdout", bool truncated = false) =>
        Encoding.UTF8.GetString(MinecraftCapturedLogFrame.Encode(new(utc, stream, body, truncated))).TrimEnd('\n');

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ManagedLiveSnapshotStore.ExpectedFailure(ex) || ex is OperationCanceledException) { return; }
        throw new Exception(message);
    }
}
