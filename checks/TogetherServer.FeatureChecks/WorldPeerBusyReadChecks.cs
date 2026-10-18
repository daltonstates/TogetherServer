using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// A held semaphore models the waiting capture. Only disposable signed files
// are read: no game, process, listener, console, app, dialog or live world.
internal static class WorldPeerBusyReadChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "peer-busy-read-" + Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            Name = "Completed files only",
            SharedSavesEnabled = true,
            WorldId = "synthetic-busy-copy",
            GamePort = 2468,
            WorldDirectory = Path.Combine(data.RootPath, "never-read-live-world")
        };
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var capture = new CompletedFiles(data.RootPath);
        var shares = new SharedWorldService(data, capture);
        var roster = shares.PublishRoster(profile, []);
        new SharedWorldRosterChainStore(data).Append(roster, roster.OwnerPublicKey);
        var source = new SharedWorldPeerRoot(roster.OwnerPublicKey, profile.Id, roster.GroupId);
        var firstBytes = new byte[SharedWorldService.ChunkBytes + 17];
        RandomNumberGenerator.Fill(firstBytes);
        capture.Set(firstBytes);
        var firstPublication = shares.PublishAfterStop(profile, Guid.NewGuid());
        Require(firstPublication.Ok && firstPublication.Version is not null, "the first signed completed-file publication failed");
        var first = firstPublication.Version!;
        var latestBytes = firstBytes.ToArray(); latestBytes[3] ^= 0xff;
        capture.Set(latestBytes);
        var secondPublication = shares.PublishAfterStop(profile, Guid.NewGuid());
        Require(secondPublication.Ok && secondPublication.Version is not null, "the successor completed-file publication failed");
        var latest = secondPublication.Version!;
        var manager = new HostManager(data, new GameServerRegistry(data, true, PortProbeMode.ObserveOnly));
        var lifecycle = Gate(manager);
        await lifecycle.WaitAsync();
        try
        {
            Require((await Immediate(manager.PeerPublishedVersionAsync(source))).VersionHash == latest.VersionHash,
                "a busy lifecycle hid the previous complete immutable publication");
            Require((await Immediate(manager.PeerPublishedVersionAsync(source, first.VersionHash, first.Number))).VersionHash == first.VersionHash,
                "a busy lifecycle hid an exact verified ancestor manifest");
            Require((await Immediate(manager.PeerPublishedRosterAsync(profile.Id))).Signature == roster.Signature,
                "a busy lifecycle hid the canonical signed roster needed for discovery");
            var chunk = await Immediate(manager.ReadPeerPublishedChunkAsync(source, latest.VersionHash, 0, 0));
            var tail = await Immediate(manager.ReadPeerPublishedChunkAsync(source, latest.VersionHash, 0, SharedWorldService.ChunkBytes));
            Require(chunk.AsSpan().SequenceEqual(latestBytes.AsSpan(0, chunk.Length)) &&
                tail.AsSpan().SequenceEqual(latestBytes.AsSpan(SharedWorldService.ChunkBytes)),
                "busy peer chunks differed from their complete signed publication");
            Require(await Refuses(() => manager.PeerPublishedVersionAsync(source with { GroupId = Guid.NewGuid() })) &&
                await Refuses(() => manager.PeerPublishedVersionAsync(source with { ProfileId = Guid.NewGuid() })),
                "a busy immutable read crossed a profile or source-group boundary");
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Require(await Refuses(() => manager.PeerPublishedVersionAsync(source with { OwnerPublicKey = Public(stranger) })) &&
                await Refuses(() => manager.ReadPeerPublishedChunkAsync(source with { OwnerPublicKey = Public(stranger) }, latest.VersionHash, 0, 0)) &&
                await Refuses(() => manager.PeerPublishedVersionAsync(source, first.VersionHash, latest.Number)),
                "another signer or a mismatched exact version selected an unrelated copy");

            // HostManager's in-memory profile deliberately stays unchanged.
            // Peer reads must follow a newly loaded durable source identity.
            var changed = data.LoadSettings();
            changed.Profiles.Single().WorldDirectory = Path.Combine(data.RootPath, "different-durable-source");
            data.SaveSettings(changed);
            Require(await Refuses(() => manager.PeerPublishedVersionAsync(source)) &&
                await Refuses(() => manager.PeerPublishedRosterAsync(profile.Id)) &&
                await Refuses(() => manager.ReadPeerPublishedChunkAsync(source, latest.VersionHash, 0, 0)),
                "a stale in-memory profile bypassed a changed durable world binding");
            data.SaveSettings(new HostSettings { Profiles = [profile] });

            var publishedRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"));
            var pointer = Path.Combine(publishedRoot, "latest.json");
            var pointerBytes = File.ReadAllBytes(pointer);
            var forged = SharedWorldService.SignVersion(latest, stranger);
            File.WriteAllBytes(pointer, JsonSerializer.SerializeToUtf8Bytes(forged, Json));
            Require(await Refuses(() => manager.PeerPublishedVersionAsync(source)) &&
                await Refuses(() => manager.ReadPeerPublishedChunkAsync(source, forged.VersionHash, 0, 0)),
                "a valid foreign signature replaced the pinned world's publication");
            File.WriteAllBytes(pointer, pointerBytes);

            var ancestorPath = Path.Combine(VersionRoot(data, first), "version.json");
            var ancestorBytes = File.ReadAllBytes(ancestorPath);
            File.WriteAllBytes(ancestorPath, JsonSerializer.SerializeToUtf8Bytes(first with { Number = 900 }, Json));
            Require(await Refuses(() => manager.PeerPublishedVersionAsync(source, first.VersionHash, first.Number)),
                "a forged ancestor bypassed the signed publication chain");
            File.WriteAllBytes(ancestorPath, ancestorBytes);

            var rosterPath = Path.Combine(publishedRoot, "roster-chain", SharedWorldRosterTrust.Hash(roster) + ".json");
            var rosterBytes = File.ReadAllBytes(rosterPath);
            File.WriteAllBytes(rosterPath, JsonSerializer.SerializeToUtf8Bytes(roster with { Revision = 900 }, Json));
            Require(await Refuses(() => manager.PeerPublishedRosterAsync(profile.Id)) &&
                await Refuses(() => manager.PeerPublishedVersionAsync(source)),
                "a forged signed-roster journal unlocked busy peer reads");
            File.WriteAllBytes(rosterPath, rosterBytes);

            var payloadPath = Path.Combine(VersionRoot(data, latest), "payload", "world.dat");
            var stamp = File.GetLastWriteTimeUtc(payloadPath);
            var modified = latestBytes.ToArray(); modified[0] ^= 0xff;
            File.WriteAllBytes(payloadPath, modified); File.SetLastWriteTimeUtc(payloadPath, stamp);
            Require(await Refuses(() => manager.ReadPeerPublishedChunkAsync(source, latest.VersionHash, 0, 0)),
                "same-size, same-timestamp payload tampering bypassed the existing verified chunk inventory");
            Require(File.ReadAllBytes(pointer).SequenceEqual(pointerBytes) && !Directory.Exists(profile.WorldDirectory),
                "immutable peer reads promoted another pointer or opened the synthetic running-world path");
        }
        finally { lifecycle.Release(); }
        await MissingRootAsync(Path.Combine(root, "peer-busy-no-root-" + Guid.NewGuid().ToString("N")), source);
    }

    private static async Task MissingRootAsync(string root, SharedWorldPeerRoot source)
    {
        using var data = new LocalData(root);
        var local = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            SeparateCopySourceProfileId = source.ProfileId,
            WorldDirectory = Path.Combine(data.RootPath, "never-read-recovery-world")
        };
        data.SaveSettings(new HostSettings { Profiles = [local] });
        var manager = new HostManager(data, new GameServerRegistry(data, true, PortProbeMode.ObserveOnly));
        var lifecycle = Gate(manager); await lifecycle.WaitAsync();
        try
        {
            Require(await Refuses(() => manager.PeerPublishedVersionAsync(source)) &&
                await Refuses(() => manager.PeerPublishedRosterAsync(source.ProfileId)) &&
                await Refuses(() => manager.PeerPublishedRosterAsync(local.Id)) &&
                await Refuses(() => manager.ReadPeerPublishedChunkAsync(source, new string('A', 64), 0, 0)),
                "a new recovery app waited for a busy lifecycle or treated its local branch as a canonical source");
        }
        finally { lifecycle.Release(); }
    }

    private static SemaphoreSlim Gate(HostManager manager) => (SemaphoreSlim)(typeof(HostManager)
        .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager)
        ?? throw new InvalidOperationException("the lifecycle semaphore check seam is unavailable"));

    private static async Task<T> Immediate<T>(Task<T?> task) where T : class =>
        await task.WaitAsync(TimeSpan.FromSeconds(2)) ?? throw new InvalidOperationException("a completed immutable read unexpectedly returned no result");

    private static async Task<bool> Refuses<T>(Func<Task<T?>> action) where T : class
    {
        try { return await action().WaitAsync(TimeSpan.FromSeconds(2)) is null; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or
            CryptographicException or JsonException)
        { return true; }
    }

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string VersionRoot(LocalData data, SharedWorldVersion version) => Path.Combine(data.RootPath,
        "shared-worlds", version.ProfileId.ToString("N"), version.GroupId.ToString("N"),
        version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class CompletedFiles(string root) : ISharedWorldCaptureAdapter
    {
        private readonly string directory = Path.Combine(root, "completed-synthetic-backup");
        internal void Set(byte[] bytes)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "world.dat"), bytes);
        }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, "world.dat"));
            return new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, DateTimeOffset.UtcNow,
                [new("world.dat", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))], directory,
                new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "SyntheticFilesOnly", [], [], profile.GamePort));
        }
    }
}
