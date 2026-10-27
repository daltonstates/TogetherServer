using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerHostingDiscoveryChecks
{
    private static async Task OriginalPublicationStartChecksAsync(string parent)
    {
        await StaleBackgroundAdvertisementScheduleAsync(parent);
        await OriginalAdvertisementScheduleAsync(parent);
        await LocalAdvertisementIdentityChecksAsync(parent);
        using (var fixture = new OriginalPublicationFixture(parent, "two-new-publications"))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var version = attempt == 0 ? fixture.Current : fixture.PublishSameBytes();
                fixture.Driver.AllowPreparation = true;
                fixture.Driver.Validate = _ => RequireHeldOriginal(fixture.PublicationFile(version));
                fixture.Driver.Prepare = _ => RequireHeldOriginal(fixture.PublicationFile(version));
                fixture.Driver.AtStart = _ => RequireHeldOriginal(fixture.PublicationFile(version));
                var result = await fixture.Owner.StartAsync(fixture.Profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
                Require(result.Code == "LaunchFailed" && fixture.Driver.ValidationAttempts == attempt + 1 &&
                    fixture.Driver.PrepareAttempts == attempt + 1 && fixture.Driver.StartAttempts == attempt + 1 &&
                    version.Files.Count == 315 && fixture.Peers.Views().Single(item => item.GroupKey == fixture.Root.GroupKey) is
                    { SelfDeviceId: var id, SharingEnabled: true, ReceiveConsented: true, CanHost: true } && id == Guid.Empty,
                    $"original publication {version.Number} did not reach the final refusing driver with armed outbound sharing: {result.Code}");
                fixture.RequireNoCopyOrTransfer();
                using var released = new FileStream(fixture.PublicationFile(version), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Require(released.CanWrite, "original Start leaked its held publication after the refusing driver returned");
            }
        }
        foreach (var fault in new[] { "newer", "fork", "permission", "consent", "payload" })
        {
            using var fixture = new OriginalPublicationFixture(parent, "refuse-" + fault, fileCount: 3);
            if (fault is "newer" or "fork")
            {
                var basis = fault == "fork" ? fixture.Base.Target.Version : fixture.Current;
                var claim = SharedWorldService.SignVersion(basis with
                {
                    Number = basis.Number + 1,
                    ParentHash = basis.VersionHash,
                    BackupId = Guid.NewGuid(),
                    Files = [Fact(Encoding.UTF8.GetBytes("synthetic unreceived " + fault))]
                }, fixture.Base.Owner);
                fixture.Base.Target.Announce(claim);
            }
            else if (fault == "permission") fixture.CorruptRoster();
            else if (fault == "consent")
                Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, Guid.Empty, false),
                    "synthetic original consent withdrawal failed");
            else File.WriteAllText(fixture.PublicationFile(fixture.Current), "synthetic changed publication");
            var result = await fixture.Owner.StartAsync(fixture.Profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
            Require(!result.Ok && fixture.Driver.ValidationAttempts == 0 && fixture.Driver.StartAttempts == 0,
                $"original {fault} bypassed latest signed permission/save checks: {result.Code}");
            fixture.RequireNoCopyOrTransfer();
        }
        foreach (var fault in new[] { "material", "settings", "consent", "permission", "pending-permission" })
        {
            using var fixture = new OriginalPublicationFixture(parent, "final-" + fault, fileCount: 3);
            fixture.Driver.AllowPreparation = true;
            fixture.Driver.Prepare = _ =>
            {
                RequireHeldOriginal(fixture.PublicationFile(fixture.Current));
                if (fault == "material")
                {
                    var store = Store(fixture.Data, fixture.Root, fixture.Base.Target.Roster);
                    var current = store.ReadPage().Announcements.Where(item => item.DeviceId == Guid.Empty)
                        .OrderByDescending(item => item.Revision).First();
                    store.Publish(Guid.Empty, fixture.Base.Owner, current.Routes, current.Sessions, current.Branches,
                        [.. current.AvailableVersionHashes, new string('A', 64)]);
                }
                else if (fault == "settings")
                {
                    var settings = fixture.Data.LoadSettings();
                    settings.Profiles.Single(item => item.Id == fixture.Profile.Id).Crossplay = true;
                    fixture.Data.SaveSettings(settings);
                }
                else if (fault == "consent")
                    Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, Guid.Empty, false),
                        "synthetic final original consent withdrawal failed");
                else if (fault == "pending-permission")
                    File.WriteAllText(Path.Combine(fixture.Data.RootPath, "shared-worlds", fixture.Profile.Id.ToString("N"), "roster-dirty"),
                        "Synthetic unsigned permission change");
                else fixture.CorruptRoster();
            };
            var result = await fixture.Owner.StartAsync(fixture.Profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
            Require(result.Code == "LaunchFailed" && fixture.Driver.ValidationAttempts == 1 &&
                fixture.Driver.PrepareAttempts == 1 && fixture.Driver.StartAttempts == 0,
                $"original {fault} changed during common preparation without final refusal: {result.Code}");
            fixture.RequireNoCopyOrTransfer();
            using var released = new FileStream(fixture.PublicationFile(fixture.Current), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(released.CanWrite, "final original-source refusal leaked its publication lease");
        }
        using (var fixture = new OriginalPublicationFixture(parent, "cancel-verification", fileCount: 3))
        using (var canceled = new CancellationTokenSource())
        {
            Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
                "synthetic cancellation fixture could not register its original world");
            var sawVerification = false;
            try
            {
                await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, canceled.Token, update =>
                {
                    if (update.Phase != "Verifying") return;
                    sawVerification = true;
                    canceled.Cancel();
                });
                throw new InvalidOperationException("Canceled original verification returned a usable source.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Require(sawVerification && fixture.Driver.ValidationAttempts == 0, "original cancellation did not exercise payload verification");
            fixture.RequireNoCopyOrTransfer();
            using var released = new FileStream(fixture.PublicationFile(fixture.Current), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(released.CanWrite, "canceled original verification leaked its held payload");
        }
    }

    private static async Task StaleBackgroundAdvertisementScheduleAsync(string parent)
    {
        using var fixture = new OriginalPublicationFixture(parent, "stale-background-advertisement", fileCount: 3);
        Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
            "the stale background schedule lacked its original signed membership");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(fixture.Peers, [fixture.Root.GroupKey])!;
        using var release = new ManualResetEventSlim();
        var barrierReleased = false;
        var built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = typeof(WorldPeerService).GetField("localAdvertisementBuiltForChecks", flags)!;
        barrier.SetValue(fixture.Peers, (Action)(() =>
        {
            built.TrySetResult(true);
            barrierReleased = release.Wait(TimeSpan.FromSeconds(5));
        }));
        var background = Task.Run(async () => await (Task)typeof(WorldPeerService).GetMethod("SyncScheduledGroupAsync", flags)!
            .Invoke(fixture.Peers, [group, CancellationToken.None, CancellationToken.None, false])!);
        try
        {
            await built.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var preparation = fixture.Peers.BeginHostingPreparation(fixture.Root);
            var before = fixture.Peers.LatestObservationHash(fixture.Root);
            release.Set();
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            Require(barrierReleased && before is not null && fixture.Peers.LatestObservationHash(fixture.Root) == before,
                "a background advertisement begun before preparation published stale material after preparation started");
            var builds = 0;
            barrier.SetValue(fixture.Peers, (Action)(() => builds++));
            await (Task)typeof(WorldPeerService).GetMethod("SyncScheduledGroupAsync", flags)!
                .Invoke(fixture.Peers, [group, CancellationToken.None, CancellationToken.None, false])!;
            Require(builds == 0 && fixture.Peers.LatestObservationHash(fixture.Root) == before && fixture.Base.Target.Handler.Challenges == 0,
                "a new background publisher did not yield before snapshot/network work during preparation");
            fixture.RequireNoCopyOrTransfer();
        }
        finally { release.Set(); barrier.SetValue(fixture.Peers, null); await background; }
    }

    private static async Task OriginalAdvertisementScheduleAsync(string parent)
    {
        using var fixture = new OriginalPublicationFixture(parent, "local-advertisement-schedule", fileCount: 3);
        Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
            "the local advertisement schedule lacked its original signed membership");
        var armed = await fixture.Peers.AnnounceLocalHostingAsync(fixture.Root, default);
        Require(armed.Ok || armed.Code == "RecipientConnectionRequired", "the local advertisement schedule could not arm its existing world");
        var published = false;
        var changed = false;
        var version = fixture.Current.VersionHash;
        var result = await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, default, update =>
        {
            if (published || update.Phase != "Verifying") return;
            published = true;
            var before = fixture.Peers.LatestObservationHash(fixture.Root);
            // Exercise the real background metadata builder at the exact
            // verification boundary. The publication and setup stay unchanged.
            var advertisement = LocalAdvertisementForChecksAsync(fixture.Peers, fixture.Root).GetAwaiter().GetResult();
            Store(fixture.Data, fixture.Root, fixture.Base.Target.Roster).Publish(Guid.Empty, fixture.Base.Owner,
                advertisement.Routes, advertisement.Sessions, advertisement.Branches, advertisement.AvailableVersionHashes);
            changed = before != fixture.Peers.LatestObservationHash(fixture.Root);
        });
        using var source = result.Source;
        Require(published && !changed && result.Ok && source?.Descriptor.Version.VersionHash == version,
            $"same-publication local advertisement invalidated original Start: {result.Code}; published={published}; materialChanged={changed}");
        source.RequireCurrent(fixture.Data, fixture.Root);
        fixture.RequireNoCopyOrTransfer();
    }

    private static async Task<WorldPeerAdvertisement> LocalAdvertisementForChecksAsync(WorldPeerService service, SharedWorldPeerRoot root)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(service, [root.GroupKey])!;
        var method = typeof(WorldPeerService).GetMethod("BuildAdvertisementAsync", flags)!;
        object?[] arguments = method.GetParameters().Length == 2 ? [group, CancellationToken.None] : [group, CancellationToken.None, true];
        return await (Task<WorldPeerAdvertisement>)method.Invoke(service, arguments)!;
    }

    private static void RequireHeldOriginal(string path)
    {
        var blocked = false;
        try { using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
        catch (IOException) { blocked = true; }
        Require(blocked, "the common driver boundary did not hold the original immutable publication against writes");
    }

    private sealed class OriginalPublicationFixture : IDisposable
    {
        internal Fixture Base { get; }
        internal LocalData Data => Base.Data;
        internal ServerProfile Profile { get; }
        internal SharedWorldPeerRoot Root => Base.Target.Root;
        internal SharedWorldVersion Current { get; private set; }
        internal OriginalStartDriver Driver { get; } = new();
        internal WorldPeerService Peers { get; }
        internal SimpleOwnerHostingService Owner { get; }
        private readonly OriginalFilesCapture capture;
        private readonly SharedWorldService publisher;
        private readonly FriendService friends;

        internal OriginalPublicationFixture(string parent, string name, int fileCount = 315)
        {
            Base = new(parent, "original-" + name);
            var settings = Data.LoadSettings();
            Profile = settings.Profiles.Single(item => item.Id == Base.Target.Profile.Id);
            Profile.ExecutablePath = Path.Combine(Data.RootPath, "never-executed-original.synthetic");
            File.WriteAllText(Profile.ExecutablePath, "Synthetic file; no process may execute it.");
            Data.SaveSettings(settings);
            capture = new(Data.RootPath, Profile, fileCount);
            publisher = new(Data, capture);
            Current = PublishSameBytes();
            // This generated fixture deliberately has no exact recovery cache
            // for its fresh publications, nor an owner registry entry.
            Directory.Delete(Path.Combine(Data.RootPath, "world-peer-recovery-sources"), recursive: true);
            SaveGroups(Data, [Membership(Base.Other.Root, Base.Other.Roster, Guid.Empty, Base.Other.Route)]);
            var manager = new HostManager(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly));
            HttpClient Client(string endpoint, IEnumerable<string> pins)
            {
                Require(endpoint.TrimEnd('/') == Base.Target.Route.Address && pins.SequenceEqual([Base.Target.Route.CertificateSha256]),
                    "original Start contacted another world or changed its approved TLS pin");
                return new(Base.Target.Handler, disposeHandler: false) { BaseAddress = new Uri(Base.Target.Route.Address + "/") };
            }
            friends = new(Data, Client);
            Peers = new(Data, manager, friends, new PairingService(Data), Client);
            manager.SharedHostingObservation = Peers.LatestObservationHash;
            manager.SharedHostingStartBlock = (root, hash) => Peers.CanonicalStartBlock(root, hash)?.Message;
            Owner = new(Data, manager, Peers, () => throw new InvalidOperationException("Original Start must not enable a listener."));
        }

        internal SharedWorldVersion PublishSameBytes()
        {
            var published = publisher.PublishAfterStop(Profile, Guid.NewGuid());
            Require(published is { Ok: true, Version: not null }, "synthetic original publication failed: " + published.Code + "; " + published.Message);
            Current = published.Version;
            Base.Target.Announce(Current);
            return Current;
        }

        internal string PublicationFile(SharedWorldVersion version) => Path.Combine(Data.RootPath, "shared-worlds",
            Profile.Id.ToString("N"), Root.GroupId.ToString("N"), version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SharedWorldService.PayloadDirectory, version.Files[0].Path.Replace('/', Path.DirectorySeparatorChar));

        internal void CorruptRoster()
        {
            var path = Path.Combine(Data.RootPath, "shared-worlds", Profile.Id.ToString("N"), Root.GroupId.ToString("N") + ".roster.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Base.Target.Roster with
            { Signature = Convert.ToBase64String(new byte[64]) }, Json));
        }

        internal void RequireNoCopyOrTransfer() => Require(
            !Directory.Exists(Path.Combine(Data.RootPath, "world-peer-recovery-sources")) &&
            Base.Target.Handler.PayloadRequests == 0 && Base.Other.Handler.Challenges == 0 && Data.LoadRuns().Count == 0 &&
            !Data.LoadSettings().CompanionListeningEnabled,
            "original Start created a recovery copy, transferred payloads, contacted another world, launched a process or enabled listening");

        public void Dispose() { Peers.Dispose(); friends.Dispose(); Base.Dispose(); }
    }

    private sealed class OriginalFilesCapture : ISharedWorldCaptureAdapter
    {
        private readonly string payload;
        private readonly SharedWorldFile[] files;
        internal OriginalFilesCapture(string root, ServerProfile profile, int fileCount)
        {
            payload = Path.Combine(root, "synthetic-original-315");
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(profile.WorldDirectory);
            files = Enumerable.Range(0, fileCount).Select(index =>
            {
                var path = index == 0 ? "world.dat" : $"synthetic-{index:D3}.dat";
                var bytes = Encoding.UTF8.GetBytes("strict generated original file " + index);
                File.WriteAllBytes(Path.Combine(payload, path), bytes);
                File.WriteAllBytes(Path.Combine(profile.WorldDirectory, path), bytes);
                return new SharedWorldFile(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            }).ToArray();
        }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId) =>
            new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, DateTimeOffset.UtcNow, files, payload,
                new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "Synthetic315Files", [], [], profile.GamePort));
    }

    private sealed class OriginalStartDriver : IGameServerDriver
    {
        internal Action<ServerProfile>? Validate { get; set; }
        internal Action<ServerProfile>? Prepare { get; set; }
        internal Action<ServerProfile>? AtStart { get; set; }
        internal bool AllowPreparation { get; set; }
        internal int ValidationAttempts { get; private set; }
        internal int PrepareAttempts { get; private set; }
        internal int StartAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "File-only original Start boundary";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile)
        {
            ValidationAttempts++;
            Validate?.Invoke(profile);
            return AllowPreparation ? null : new("OriginalSourceVerified", "The file-only driver deliberately refuses process launch.");
        }
        public void PrepareStart(ServerProfile profile, ManagedRun run) { PrepareAttempts++; Prepare?.Invoke(profile); }
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) { StartAttempts++; AtStart?.Invoke(profile); throw NoProcess(); }
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private static InvalidOperationException NoProcess() => new("No process operation is permitted.");
    }
}
