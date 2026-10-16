using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic signatures, protected disposable state, fixed JSON and directory
// handles only. No game/app/process/fixture/listener/browser is launched. These
// records exercise evidence gates; they cannot certify a real installation.
internal static class LiveSaveAcceptanceChecks
{
    internal static async Task RunAsync(string root)
    {
        using var fixture = new Evidence();
        SignatureAndScope(fixture);
        RunBinding();
        Receipts(fixture);
        Confirmations(fixture);
        FixedLocalRequests();
        await BodyLimitsAsync();
        ProtectedCopies(Path.Combine(root, "live-acceptance-copies-" + Guid.NewGuid().ToString("N")), fixture);
        await HostRefusalsAsync(Path.Combine(root, "live-acceptance-host-" + Guid.NewGuid().ToString("N")));
        await StopBeforeCaptureRegistrationAsync(root);
    }

    private static async Task StopBeforeCaptureRegistrationAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "live-stop-preflight-" + Guid.NewGuid().ToString("N")));
        var host = new HostManager(data);
        var profile = Guid.NewGuid();
        var beforePreflight = host.CaptureStopGeneration(profile);
        await host.StopAsync(profile); // No profile/run: no process or driver can be invoked.
        using var delayedRegistration = new CancellationTokenSource();
        host.RegisterLiveCaptureCancellation(profile, Guid.NewGuid(), delayedRegistration, beforePreflight);
        Require(delayedRegistration.IsCancellationRequested,
            "a local Stop during preflight was lost before the capture installed its cancellation source");
        var current = host.CaptureStopGeneration(profile);
        await host.StopAsync(Guid.NewGuid());
        using var unrelated = new CancellationTokenSource();
        host.RegisterLiveCaptureCancellation(profile, Guid.NewGuid(), unrelated, current);
        Require(!unrelated.IsCancellationRequested, "another world's Stop canceled this capture");
        await host.StopAsync(profile);
        Require(unrelated.IsCancellationRequested, "local Stop no longer canceled an already registered capture");
        using var remote = new CancellationTokenSource();
        host.RegisterLiveCaptureCancellation(profile, Guid.NewGuid(), remote, host.CaptureStopGeneration(profile));
        await host.StopAsync(profile, _ => false);
        Require(!remote.IsCancellationRequested, "remote Stop bypassed its occupancy guard by canceling a capture");
        Require(HostManager.LiveCaptureTimeout(GameKinds.Valheim) > TimeSpan.FromMinutes(30) &&
            HostManager.LiveCaptureTimeout(GameKinds.MinecraftJava) >= TimeSpan.FromMinutes(3),
            "an ordinary installed game's capture budget expired before its reviewed save candidate");
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new Exception(message); }
    private static bool Refuses(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex) || ex is OperationCanceledException) { return true; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Evidence : IDisposable
    {
        internal ECDsa Owner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa Receiver { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal Guid Device { get; } = Guid.NewGuid();
        internal byte[] Bytes { get; } = Enumerable.Range(0, SharedWorldService.ChunkBytes + 13).Select(index => (byte)(index % 251)).ToArray();
        internal LiveSaveTestArtifact Artifact { get; }
        internal SharedWorldRoster Roster { get; }
        internal SharedWorldReceipt Receipt { get; }
        internal LiveSaveAcceptanceRecord Record => new(Artifact.TestId, Artifact.Scope.ProfileId,
            Artifact.OperationId, Artifact.Scope, Now.AddMinutes(-1), "ArtifactReady", "LiveSaveTestArtifactReady", Artifact);
        internal Evidence()
        {
            var profile = Guid.NewGuid(); var group = Guid.NewGuid(); var test = Guid.NewGuid();
            // Deliberately fabricated in-memory cryptographic evidence. No Host
            // scope binder, actual binary or process is bypassed or enabled.
            var scope = SharedWorldLiveSaveAdapters.BindInstallation(profile, GameKinds.Valheim,
                "synthetic disposable scope", Hash([1]), Hash([2]), false) with { BinaryVersion = "1.2.3.4" };
            SharedWorldFile[] files = [new("world.db", Bytes.Length, Hash(Bytes)), new("empty.dat", 0, Hash([]))];
            var version = SharedWorldService.SignAcceptanceTestVersion(new(5, group, 1, null,
                profile, GameKinds.Valheim, "synthetic-private-acceptance", Now,
                LiveSaveAcceptanceTrust.TestCaptureKind, test, new(2456, false, "synthetic-1", [], []), files, "", "", ""), Owner);
            Artifact = SignArtifact(new(1, true, test, scope, Guid.NewGuid(), Guid.Empty, Now,
                Now.AddSeconds(1), LiveSaveAcceptanceTrust.PayloadHash(files), files.Sum(file => file.Length),
                SharedWorldService.ChunkBytes, 2, version, ""));
            Roster = SignRoster(new(2, group, profile, 1, 1, true, Public(Owner),
                [new(Device, Public(Receiver), new(Receive: true), false, Now.AddDays(1))], ""));
            Receipt = SignReceipt(new(1, group, profile, version.VersionHash, Device, 1, 1, Guid.NewGuid(), ""));
        }
        internal LiveSaveTestArtifact SignArtifact(LiveSaveTestArtifact artifact) => artifact with
        { Signature = Convert.ToBase64String(Owner.SignData(LiveSaveAcceptanceTrust.ArtifactBasis(artifact), HashAlgorithmName.SHA256)) };
        internal SharedWorldRoster SignRoster(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(Owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
        internal SharedWorldReceipt SignReceipt(SharedWorldReceipt receipt, ECDsa? key = null) => receipt with
        { Signature = Convert.ToBase64String((key ?? Receiver).SignData(SharedWorldReceiptTrust.Basis(receipt), HashAlgorithmName.SHA256)) };
        internal static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        public void Dispose() { Owner.Dispose(); Receiver.Dispose(); }
    }

    private static void SignatureAndScope(Evidence f)
    {
        var a = f.Artifact;
        var legacyBasis = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Domain = "TogetherServer private live-save acceptance v1",
            Artifact = new
            {
                a.Schema, a.TestOnly, a.TestId, a.Scope, a.OperationId, a.SourceDeviceId,
                a.CompletionUtc, a.SnapshotUtc, a.PayloadSha256, a.PayloadBytes,
                a.ChunkBytes, a.ChunkCount, a.Version, Signature = ""
            }
        }, LiveSaveAcceptanceTrust.Json);
        Require(LiveSaveAcceptanceTrust.ArtifactBasis(a).SequenceEqual(legacyBasis) &&
            !JsonSerializer.Serialize(a, LiveSaveAcceptanceTrust.Json).Contains("\"branch\"", StringComparison.Ordinal),
            "schema-1 signing bytes changed or a null branch field entered the legacy envelope");
        Require(LiveSaveAcceptanceTrust.VerifyArtifact(f.Artifact) &&
            SharedWorldService.VerifyAcceptanceTestVersion(f.Artifact.Version) &&
            !SharedWorldService.VerifySignature(f.Artifact.Version),
            "a private candidate failed its test verifier or was accepted as a production save");
        foreach (var artifact in new[]
        {
            f.Artifact with { TestOnly = false }, f.Artifact with { TestId = Guid.NewGuid() },
            f.Artifact with { OperationId = Guid.NewGuid() }, f.Artifact with { SourceDeviceId = Guid.NewGuid() },
            f.Artifact with { PayloadSha256 = Hash([8]) }, f.Artifact with { PayloadBytes = 1 },
            f.Artifact with { ChunkCount = 3 }, f.Artifact with { ChunkBytes = 1 },
            f.Artifact with { Scope = f.Artifact.Scope with { BinarySha256 = Hash([4]) } },
            f.Artifact with { Scope = f.Artifact.Scope with { Synthetic = true } },
            f.Artifact with { Version = f.Artifact.Version with { CaptureKind = SharedWorldCaptureKinds.LiveSave } }
        }) Require(!LiveSaveAcceptanceTrust.VerifyArtifact(artifact), "a changed test descriptor escaped signature/scope binding");
        var promoted = f.Artifact.Version with { CaptureKind = SharedWorldCaptureKinds.LiveSave };
        Require(!SharedWorldService.VerifySignature(promoted), "stripping the test kind promoted a test into production authority");
        Require(SharedWorldFreshness.SelectNewestCompleteVerified([new(f.Artifact.Version, true, true)],
            f.Artifact.Scope.ProfileId, f.Artifact.Version.GroupId) is null,
            "a private test displaced a production recovery/freshness save");
        var first = LiveSaveAcceptanceTrust.Chunk(f.Artifact, 0);
        var last = LiveSaveAcceptanceTrust.Chunk(f.Artifact, 1);
        Require(first.Offset == 0 && first.Length == SharedWorldService.ChunkBytes &&
            last.Offset == SharedWorldService.ChunkBytes && last.Length == 13 &&
            Refuses(() => LiveSaveAcceptanceTrust.Chunk(f.Artifact, -1)) && Refuses(() => LiveSaveAcceptanceTrust.Chunk(f.Artifact, 2)),
            "test chunks permitted arbitrary offsets, crossed files or included an empty-file pseudo-chunk");
        foreach (var scope in new[]
        {
            f.Artifact.Scope with { ProfileId = Guid.NewGuid() }, f.Artifact.Scope with { Game = GameKinds.MinecraftJava },
            f.Artifact.Scope with { InstallationId = Hash([6]) }, f.Artifact.Scope with { BinarySha256 = Hash([7]) },
            f.Artifact.Scope with { BinaryVersion = "1.2.3.5" }, f.Artifact.Scope with { ConfigurationSha256 = Hash([9]) },
            f.Artifact.Scope with { Synthetic = true }
        }) Require(!SharedWorldLiveSaveAdapters.SameRealInstallation(f.Artifact.Scope, scope), "accepted scope escaped an installed/profile/configuration change");
    }

    private static void Receipts(Evidence f)
    {
        Require(LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, f.Receipt, f.Roster, Now), "a distinct authorized normal signed receipt was refused");
        foreach (var receipt in new[]
        {
            f.Receipt with { Signature = "AA==" }, f.SignReceipt(f.Receipt with { GroupId = Guid.NewGuid() }),
            f.SignReceipt(f.Receipt with { ProfileId = Guid.NewGuid() }), f.SignReceipt(f.Receipt with { VersionHash = Hash([3]) }),
            f.SignReceipt(f.Receipt with { DeviceId = Guid.NewGuid() }), f.SignReceipt(f.Receipt with { RosterEpoch = 2 }),
            f.SignReceipt(f.Receipt with { RosterRevision = 2 }), f.SignReceipt(f.Receipt with { ReceiptId = Guid.Empty })
        }) Require(!LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, receipt, f.Roster, Now), "a forged, wrong-test, stale or unassigned receipt granted transfer");
        foreach (var member in new[]
        {
            f.Roster.Members[0] with { Revoked = true }, f.Roster.Members[0] with { Grants = new() },
            f.Roster.Members[0] with { AccessExpiresUtc = Now }, f.Roster.Members[0] with { AccessExpiresUtc = Now.AddTicks(-1) }
        }) Require(!LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, f.Receipt,
            f.SignRoster(f.Roster with { Members = [member] }), Now), "revocation, Receive withdrawal or exact owner expiry allowed transfer credit");
        Require(!LiveSaveAcceptanceTrust.ReceiptMatches(f.SignArtifact(f.Artifact with { SourceDeviceId = f.Device }), f.Receipt, f.Roster, Now),
            "a source device self-receipt passed the separate-device gate");
        var sameKey = f.SignRoster(f.Roster with { Members = [f.Roster.Members[0] with { PublicKey = Evidence.Public(f.Owner) }] });
        Require(!LiveSaveAcceptanceTrust.ReceiptMatches(f.Artifact, f.SignReceipt(f.Receipt, f.Owner), sameKey, Now),
            "a different device ID with the source's key passed as a separate PC");
    }

    private static void RunBinding()
    {
        var profile = new ServerProfile { Kind = GameKinds.MinecraftJava, WorldId = "synthetic-run", GamePort = 25565,
            WorldDirectory = @"C:\synthetic-live-test\world", ExecutablePath = @"C:\synthetic-live-test\java.exe",
            Minecraft = new() { ServerJarPath = @"C:\synthetic-live-test\server.jar" } };
        GamePort[] ports = [new("TCP", 25565, "Game")];
        ManagedRun Run() => new() { ProfileId = profile.Id, Kind = profile.Kind, OperationId = Guid.NewGuid(),
            WorldId = profile.WorldId, WorldDirectory = profile.WorldDirectory, ExecutablePath = profile.ExecutablePath,
            ServerArtifactPath = profile.Minecraft!.ServerJarPath, ProcessId = 424242, StartTimeUtcTicks = 123456,
            GamePort = profile.GamePort, DeclaredPorts = ports.ToList() };
        Require(LiveSaveAcceptanceTrust.RunMatchesProfile(profile, Run(), profile.ExecutablePath, ports),
            "the same synthetic exact run/profile/artifact/ports did not bind");
        var changedArtifact = Run(); changedArtifact.ServerArtifactPath = @"C:\synthetic-live-test\different.jar";
        var changedRun = Run(); changedRun.WorldId = "another-world";
        var stopping = Run(); stopping.StopRequestedUtc = Now;
        var changedPorts = Run(); changedPorts.DeclaredPorts = [new("TCP", 25566, "Game")];
        var wrongProcess = Run(); wrongProcess.ProcessId = null;
        Require(new[] { changedArtifact, changedRun, stopping, changedPorts, wrongProcess }.All(run =>
            !LiveSaveAcceptanceTrust.RunMatchesProfile(profile, run, profile.ExecutablePath, ports)),
            "a changed Java artifact, world, port, stopping run or absent exact process identity retained candidate authority");
    }

    private static void Confirmations(Evidence f)
    {
        LiveSaveTestConfirmation Confirm(string stage) => new(f.Artifact.Version.VersionHash, f.Artifact.PayloadSha256, stage, true);
        Require(Refuses(() => LiveSaveAcceptanceTrust.Confirm(f.Record, Confirm("load"), Now)), "an owner report supplied missing transfer evidence");
        var transferred = f.Record with { Receipt = f.Receipt, TransferUtc = Now.AddSeconds(2) };
        foreach (var confirmation in new[]
        {
            Confirm("completion"), Confirm("snapshot"), Confirm("transfer"), Confirm("change"), Confirm("restart"),
            Confirm("load") with { Confirmed = false }, Confirm("load") with { VersionHash = Hash([9]) },
            Confirm("load") with { PayloadSha256 = Hash([8]) }
        }) Require(Refuses(() => LiveSaveAcceptanceTrust.Confirm(transferred, confirmation, Now.AddSeconds(3))),
            "an owner report forged an observed stage, skipped order or confirmed a different payload");
        var loaded = LiveSaveAcceptanceTrust.Confirm(transferred, Confirm("load"), Now.AddSeconds(3));
        var changed = LiveSaveAcceptanceTrust.Confirm(loaded, Confirm("change"), Now.AddSeconds(4));
        var accepted = LiveSaveAcceptanceTrust.Confirm(changed, Confirm("restart"), Now.AddSeconds(5));
        Require(!loaded.AllStages && !changed.AllStages && accepted.AllStages && accepted.State == "Accepted" &&
            accepted.Receipt == f.Receipt && accepted.Artifact == f.Artifact &&
            LiveSaveAcceptanceTrust.Stages(accepted).All(stage => stage.State == "Passed") &&
            LiveSaveAcceptanceTrust.Stages(accepted).Where(stage => stage.Id is "load" or "change" or "restart").All(stage => stage.Source == "OwnerReported") &&
            !SharedWorldLiveSaveAdapters.IsGameAccepted(GameKinds.Valheim),
            "exact test stages failed to complete, lost their sources or globally enabled a game");
        Require(LiveSaveAcceptanceTrust.AcceptedForScope(accepted, accepted.Scope, f.Roster, Now) &&
            !LiveSaveAcceptanceTrust.AcceptedForScope(accepted, accepted.Scope with { BinarySha256 = Hash([10]) }, f.Roster, Now) &&
            !LiveSaveAcceptanceTrust.AcceptedForScope(accepted, accepted.Scope with { Synthetic = true }, f.Roster, Now) &&
            !LiveSaveAcceptanceTrust.AcceptedForScope(accepted, accepted.Scope, f.Roster, Now.AddDays(1)) &&
            !LiveSaveAcceptanceTrust.AcceptedForScope(accepted, accepted.Scope, f.SignRoster(f.Roster with { Revision = 2 }), Now),
            "protected acceptance survived a changed binary, fixture scope, owner expiry or current roster revision");
        Require(LiveSaveAcceptanceTrust.Confirm(accepted, Confirm("restart"), Now.AddHours(1)) == accepted,
            "repeated owner confirmation silently replaced historical evidence");
        Require(Refuses(() => LiveSaveAcceptanceTrust.Confirm(accepted with { State = "Interrupted" }, Confirm("load"), Now)),
            "an interrupted private copy could be accepted by owner reports");
    }

    private static void FixedLocalRequests()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        Require(LiveSaveAcceptanceRoutes.FixedLocalRequest(context, true), "a fixed local read was rejected");
        foreach (var header in new[] { "", "true", " 1", "1 ", "1,1", "01" })
        {
            context.Request.Headers["X-TogetherServer-Local"] = header;
            Require(!LiveSaveAcceptanceRoutes.FixedLocalRequest(context, false), "an inexact local header was accepted");
        }
        context.Request.Headers["X-TogetherServer-Local"] = new Microsoft.Extensions.Primitives.StringValues(["1", "1"]);
        Require(!LiveSaveAcceptanceRoutes.FixedLocalRequest(context, false), "duplicate local headers were accepted");
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.QueryString = new QueryString("?path=C:/private");
        Require(!LiveSaveAcceptanceRoutes.FixedLocalRequest(context, false), "a private test route accepted a path/query input");
        context.Request.QueryString = default; context.Request.ContentLength = 1;
        Require(!LiveSaveAcceptanceRoutes.FixedLocalRequest(context, true), "a read accepted a body");
        foreach (var json in new[]
        {
            "{}", "null", "[]", "{\"requestId\":null}",
            $"{{\"requestId\":\"{Guid.NewGuid()}\",\"command\":\"save\"}}",
            $"{{\"requestId\":\"{Guid.NewGuid()}\",\"requestId\":\"{Guid.NewGuid()}\"}}",
            $"{{\"requestId\":\"{Guid.NewGuid()}\",\"RequestId\":\"{Guid.NewGuid()}\"}}"
        }) Require(Refuses(() => LiveSaveAcceptanceRoutes.Decode<LiveSaveRequest>(Encoding.UTF8.GetBytes(json))),
            "a missing, duplicate, unknown, case-aliased or malformed fixed field was accepted");
        var id = Guid.NewGuid();
        Require(LiveSaveAcceptanceRoutes.Decode<LiveSaveRequest>(Encoding.UTF8.GetBytes($"{{\"requestId\":\"{id}\"}}")).RequestId == id,
            "the fixed request ID failed strict decoding");
        Require(Refuses(() => LiveSaveAcceptanceRoutes.Decode<LiveSaveTestConfirmation>(Encoding.UTF8.GetBytes(
            "{\"versionHash\":\"x\",\"payloadSha256\":\"y\",\"stage\":\"load\",\"confirmed\":true,\"completion\":true}"))),
            "unknown owner confirmation fields were accepted");
    }

    private static void ProtectedCopies(string root, Evidence f)
    {
        using var data = new LocalData(root);
        var store = new LiveSaveAcceptanceStore(data);
        var source = Path.Combine(root, "synthetic-sealed-source"); Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "world.db"), f.Bytes); File.WriteAllBytes(Path.Combine(source, "empty.dat"), []);
        var before = File.ReadAllBytes(Path.Combine(source, "world.db")); var sealChecks = 0;
        store.Stage(f.Artifact, source, () => sealChecks++, default);
        store.Save([f.Record]);
        Require(sealChecks == 3 && store.Read().Single().Artifact?.Version.VersionHash == f.Artifact.Version.VersionHash &&
            store.ReadArtifact(f.Artifact, default).PayloadSha256 == f.Artifact.PayloadSha256 &&
            store.ReadChunk(f.Artifact, 0, default).SequenceEqual(f.Bytes[..SharedWorldService.ChunkBytes]) &&
            store.ReadChunk(f.Artifact, 1, default).SequenceEqual(f.Bytes[SharedWorldService.ChunkBytes..]) &&
            File.ReadAllBytes(Path.Combine(source, "world.db")).SequenceEqual(before) &&
            !Directory.Exists(Path.Combine(root, "shared-worlds")),
            "private staging changed its source/current pointer, lost its seal or returned wrong exact chunks");
        var retained = store.Root(f.Artifact.Scope.ProfileId, f.Artifact.TestId);
        Require(Refuses(() => store.Stage(f.Artifact, source, () => { }, default)), "a duplicate test overwrote retained private bytes");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            Require(Refuses(() => store.ReadArtifact(f.Artifact, canceled.Token)), "private verification ignored cancellation");
        }
        var changed = Path.Combine(retained, "payload", "world.db");
        File.SetAttributes(changed, FileAttributes.Normal); File.WriteAllBytes(changed, new byte[f.Bytes.Length]);
        Require(Refuses(() => store.ReadArtifact(f.Artifact, default)) && Refuses(() => store.ReadChunk(f.Artifact, 0, default)),
            "same-length private payload tampering escaped descriptor/chunk verification");
        var nextId = Guid.NewGuid();
        var nextVersion = SharedWorldService.SignAcceptanceTestVersion(f.Artifact.Version with { BackupId = nextId }, f.Owner);
        var interrupted = f.SignArtifact(f.Artifact with { TestId = nextId, Version = nextVersion }); var checks = 0;
        Require(Refuses(() => store.Stage(interrupted, source, () => { if (++checks == 2) throw new OperationCanceledException(); }, default)) &&
            Directory.Exists(store.Root(interrupted.Scope.ProfileId, nextId) + ".partial") &&
            !Directory.Exists(store.Root(interrupted.Scope.ProfileId, nextId)) &&
            File.ReadAllBytes(Path.Combine(source, "world.db")).SequenceEqual(before),
            "interrupted copy was promoted/deleted or damaged its synthetic source");
        store.Save([f.Record with { State = "Capturing", Artifact = null }]);
        Require(!store.Read().Single().AllStages && store.Read().Single().Pending,
            "an interrupted protected request was automatically granted completion");
        Require(Refuses(() => store.Save(Enumerable.Repeat(f.Record, LiveSaveAcceptanceStore.MaximumTests + 1).ToArray())),
            "the bounded acceptance journal accepted unlimited records");
    }

    private static async Task BodyLimitsAsync()
    {
        using var declared = new MemoryStream(new byte[1025]);
        Require(await SharedWorldReceiptTrust.ReadBoundedAsync(declared, 1025, 1024, default) is null && declared.Position == 0,
            "an oversized declared body was read before refusal");
        using var streamed = new MemoryStream(new byte[1025]);
        Require(await SharedWorldReceiptTrust.ReadBoundedAsync(streamed, null, 1024, default) is null && streamed.Position <= 1025,
            "an unknown-length body escaped the streaming cap");
        using var missing = new MemoryStream();
        Require(await SharedWorldReceiptTrust.ReadBoundedAsync(missing, 0, 1024, default) is null,
            "a missing private-test body was accepted");
        using var exact = new MemoryStream(new byte[1024]);
        Require((await SharedWorldReceiptTrust.ReadBoundedAsync(exact, 1024, 1024, default))?.Length == 1024,
            "an exact bounded body was truncated");
    }

    private static async Task HostRefusalsAsync(string root)
    {
        using var data = new LocalData(root);
        var profile = new ServerProfile { Kind = GameKinds.Fixture, SharedSavesEnabled = true, WorldId = "synthetic-only" };
        profile.WorldDirectory = data.NewWorldDirectory(profile.Id); Directory.CreateDirectory(profile.WorldDirectory);
        data.SaveSettings(new() { Profiles = [profile] });
        var host = new HostManager(data, new GameServerRegistry(data, true, PortProbeMode.ObserveOnly), TimeProvider.System);
        Require(!(await host.RequestLiveSaveAcceptanceTestAsync(profile.Id, new(Guid.NewGuid()))).Ok &&
            !(await host.RequestLiveSaveAcceptanceTestAsync(profile.Id, new(Guid.Empty))).Ok &&
            !(await host.LiveSaveAcceptanceAsync(profile.Id)).Accepted && data.LoadRuns().Count == 0,
            "a fixture/checklist/request supplied real-game credit or started a process");
        Require(await host.LiveSaveTestArtifactAsync(profile.Id, Guid.NewGuid()) is null &&
            await host.LiveSaveTestChunkAsync(profile.Id, Guid.NewGuid(), 0) is null &&
            !(await host.ReviewLiveSaveAcceptanceTestAsync(profile.Id, Guid.NewGuid())).Ok &&
            !(await host.ConfirmLiveSaveAcceptanceAsync(profile.Id, Guid.NewGuid(), new(Hash([1]), Hash([2]), "completion", true))).Ok,
            "an unknown test gained descriptor/chunk/owner-confirmation authority");
    }
}
