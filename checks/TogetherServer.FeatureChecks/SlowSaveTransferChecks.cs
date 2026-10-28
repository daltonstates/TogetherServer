using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using TogetherServer;
using Fixture = TogetherServer.FeatureChecks.WorldPeerExistingCopyChecks.Fixture;

namespace TogetherServer.FeatureChecks;

// Actual signed fake-HTTP receive paths with paused partial response bodies.
// No listener, app, game, browser, native dialog or process fixture is started.
internal static class SlowSaveTransferChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task RunAsync(string parent)
    {
        await SharedRosterBootstrapChecks.RunSlowReceiveProgressAsync(parent);
        await PeerPullAsync(parent, "Original");
        await PeerPullAsync(parent, "Branch");
        await SeedResumeGzipAsync(parent);
        JobsPersistAndCoalesce(parent);
        CachedSchedulingAndResume(parent);
        await ProgressReadsDuringPersistenceAsync(parent);
        LockedHistoryRemainsOptional(parent);
    }

    private static void LockedHistoryRemainsOptional(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "locked-job-history-" + Guid.NewGuid().ToString("N")));
        var persisted = new BackgroundJobs(data);
        var profileId = Guid.NewGuid();
        using (var old = persisted.Begin("old", profileId, "Server", "World", "Save download")) old.Complete("SaveReceived");
        var path = Path.Combine(data.RootPath, "background-jobs.json");
        var previous = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var memory = new BackgroundJobs(data);
            using var current = memory.Begin("current", profileId, "Server", "World", "Save download");
            current.Report("Receiving", 5, 10);
            Require(memory.Read() is { ActiveCount: 1, Jobs: [{ ReceivedBytes: 5, TotalBytes: 10 }] },
                "Unreadable optional history stopped current in-memory background work.");
            current.Complete("SaveReceived");
            Require(memory.Read() is { ActiveCount: 0, Jobs: [{ State: "Succeeded" }] },
                "An unreadable history file prevented current work from completing.");
        }
        Require(File.ReadAllBytes(path).SequenceEqual(previous), "Fallback current jobs overwrote unreadable prior history.");
        Require(new BackgroundJobs(data).Read().Jobs is [{ State: "Succeeded" }],
            "The original job history was not readable after releasing the synthetic file lock.");
    }

    private static async Task ProgressReadsDuringPersistenceAsync(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "progress-during-persistence-" + Guid.NewGuid().ToString("N")));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Guid.NewGuid(), Guid.NewGuid());
        var jobs = BackgroundJobs.For(data);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        jobs.BeforePersistForChecks = () =>
        {
            entered.Set();
            Require(release.Wait(TimeSpan.FromSeconds(10)), "The test did not release blocked job persistence.");
        };
        var begin = Task.Run(() => SharedSaveTransferProgress.Begin(data, root, new string('A', 64)));
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "The new download did not reach its durable checkpoint.");
            var observed = await Task.Run(() => (Transfer: SharedSaveTransferProgress.Read(data, root), Jobs: jobs.Read()))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(observed.Transfer is { TransferPhase: "Receiving" } && observed.Jobs.ActiveCount == 1 && !begin.IsCompleted,
                "Status/job observation waited for blocked persistence or lost the active transfer lease.");
        }
        finally { jobs.BeforePersistForChecks = null; release.Set(); }
        using var activity = await begin.WaitAsync(TimeSpan.FromSeconds(5));
        SharedSaveTransferProgress.Clear(data, root, new string('A', 64));
        activity.Dispose();
        Require(jobs.Read().Jobs is [{ State: "Succeeded" }], "An isolated durable checkpoint lost finalization.");
    }

    private static async Task SeedResumeGzipAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication(forceLargeChanged: true);
        fixture.SourceHandler.AcceptGzipSeed = true;
        using var cancel = new CancellationTokenSource();
        var samples = new List<long>();
        fixture.SourceHandler.AfterExchange = exchange =>
        {
            if (exchange.Request.Operation != WorldPeerOperations.Seed) return;
            var job = BackgroundJobs.For(fixture.Target).Read().Jobs.FirstOrDefault(item => item.Operation == "Save download");
            if (job is null) return;
            samples.Add(job.ReceivedBytes);
            if (job.ReceivedBytes >= SharedWorldService.ChunkBytes && job.ReceivedBytes < job.TotalBytes) cancel.Cancel();
        };
        var canceled = false;
        try { await fixture.SeedAllAsync(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { canceled = true; }
        Require(canceled && samples.Any(bytes => bytes > 0) && !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")),
            "The real compressed peer seed could not be interrupted after committed partial payload bytes.");
        var baseline = samples[^1];
        var id = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(item => item.Operation == "Save download").Id;
        fixture.SourceHandler.AfterExchange = exchange =>
        {
            if (exchange.Request.Operation != WorldPeerOperations.Seed) return;
            var job = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(item => item.Operation == "Save download");
            samples.Add(job.ReceivedBytes);
            Require(job.ReceivedBytes >= baseline && job.Id == id, "Resumed peer seed regressed retained logical bytes or duplicated its job.");
        };
        await fixture.SeedAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var complete = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(item => item.Operation == "Save download");
        Require(complete.State == "Succeeded" && complete.Phase == "Receipt" && complete.ReceivedBytes == complete.TotalBytes &&
            fixture.SourceHandler.CompressedRequests > 0 && samples.SequenceEqual(samples.Order()) &&
            FriendLink.ReadReceivedLatest(fixture.DuplicateVault)?.VersionHash == fixture.Descriptor.Version.VersionHash,
            "Compressed peer push lost monotonic logical multi-file/resumed bytes, final verification or receipt.");
    }
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task PeerPullAsync(string parent, string shape)
    {
        using var fixture = new Fixture(parent, shape, matchingFiles: shape == "Original");
        if (shape == "Branch") fixture.PrepareChangedForwardedSave();
        SlowBody? slow = null;
        fixture.SourceHandler.ChunkContent = bytes => new StreamContent(slow = new(bytes));
        var service = fixture.RequesterService;
        var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(service, [fixture.Root.GroupKey]);
        var receive = (Task)typeof(WorldPeerService).GetMethod("PullCatalogAsync", Private)!.Invoke(service,
            [group, fixture.SourceRoute, CancellationToken.None])!;
        for (var index = 0; slow is null && index < 100; index++) await Task.Delay(10);
        Require(slow is not null, "The actual peer pull did not reach its response body.");
        await slow!.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var status = SharedSaveTransferProgress.Read(fixture.RequesterData, fixture.Root);
        Require(!receive.IsCompleted && status is { ReceivedBytes: > 0, TransferPhase: "Receiving" } &&
            status.ReceivedBytes < status.TotalBytes,
            "The actual peer receive hid all logical bytes until its HTTP chunk completed.");
        Require(BackgroundJobs.For(fixture.RequesterData).Read().Jobs.Any(job =>
            job.Operation == "Save download" && job.ReceivedBytes == status!.ReceivedBytes && job.TotalBytes == status.TotalBytes),
            "Peer logical progress did not reach application-owned jobs.");
        // Further chunks run slowly but need no manual gate. The first partial
        // observation was taken before any complete response body was available.
        fixture.SourceHandler.ChunkContent = bytes => new StreamContent(new SlowBody(bytes, pause: false));
        slow.Release.TrySetResult();
        await receive.WaitAsync(TimeSpan.FromSeconds(30));
        var vault = fixture.Descriptor.Branch is { } branch ? SharedWorldBranchVault.Root(fixture.RequesterData, branch) :
            Path.GetDirectoryName(fixture.CanonicalReceivedPointer)!;
        Require(FriendLink.ReadReceivedLatest(vault)?.VersionHash ==
            fixture.Descriptor.Version.VersionHash, "The slow peer receive did not verify/promote the complete multi-file save.");
        Require(BackgroundJobs.For(fixture.RequesterData).Read().Jobs.Any(job => job.Operation == "Save download" &&
            job.State == "Succeeded" && job.ReceivedBytes == job.TotalBytes), "The completed peer job lost exact logical totals.");
    }

    private static void JobsPersistAndCoalesce(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "background-jobs-" + Guid.NewGuid().ToString("N")));
        var clock = new JobClock();
        var jobs = new BackgroundJobs(data, clock);
        var profile = Guid.NewGuid();
        var first = jobs.Begin("scope", profile, "Server", "World", "Periodic save check", periodic: true);
        var joined = jobs.Begin("scope", profile, "Server", "World", "Periodic save check", periodic: true);
        Require(first.Id == joined.Id && jobs.Read().Jobs.Count == 1, "Overlapping check/preparation observers duplicated a scoped job.");
        first.Report("Receiving", 10, 100);
        first.Complete("Complete");
        Require(jobs.Read().ActiveCount == 1, "One observer ended an application job still owned by another worker.");
        joined.Fail("Unreachable", "The approved PC did not answer.");
        for (var i = 0; i < 5; i++)
        {
            using var retry = jobs.Begin("scope", profile, "Server", "World", "Periodic save check", periodic: true);
            retry.Fail("Unreachable", "The approved PC did not answer.");
        }
        Require(jobs.Read().Jobs is [{ Attempts: 6, State: "Failed" }], "Periodic retries created history spam or lost retry counts.");
        using var active = jobs.Begin("receiving", profile, "Server", "World", "Save download");
        active.Report("Receiving", 12, null);
        clock.Advance(TimeSpan.FromSeconds(15));
        active.Report("Receiving", 12, null);
        clock.Advance(TimeSpan.FromSeconds(16));
        Require(jobs.Read().Jobs.Any(job => job.State == "Stalled" && job.TotalBytes is null),
            "A silent active job claimed completion or invented a total.");
        var restarted = new BackgroundJobs(data, clock);
        Require(restarted.Read().Jobs.Any(job => job.State == "Interrupted" && job.ResultCode == "AppInterrupted") &&
            restarted.Read().ActiveCount == 0, "App restart resurrected an active job or lost interruption evidence.");
        for (var i = 0; i < BackgroundJobs.MaximumJobs + 10; i++)
        {
            using var job = restarted.Begin("bounded" + i, profile, "Server", "World", "Save download");
            job.Complete("SaveReceived");
        }
        Require(restarted.Read().Jobs.Count <= BackgroundJobs.MaximumJobs, "Background job history exceeded its bound.");
        var all = new List<BackgroundJobs.Attempt>();
        for (var i = 0; i < 128; i++) all.Add(restarted.Begin("active" + i, profile, "Server", "World", "Save download"));
        Require(restarted.Read().ActiveCount == 128 && restarted.Read().Jobs.Count <= BackgroundJobs.MaximumVisibleJobs &&
            restarted.Read().Jobs.Count(job => job.State == "Running") == 128,
            "The recent-history cap hid actual active jobs or undercounted application work.");
        foreach (var attempt in all) attempt.Complete("SaveReceived");
    }

    private static void CachedSchedulingAndResume(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "cached-save-observation-" + Guid.NewGuid().ToString("N")));
        using var link = new FriendLink(data, "friend.protected", (_, _) => throw new InvalidOperationException("Status/scheduling must not perform discovery."));
        var profileId = Guid.NewGuid();
        var profile = new PublicProfile(profileId, "Server", "Offline", null, Kind: GameKinds.Valheim);
        var snapshot = new FriendView("Friend", "Connected", "", "https://192.0.2.1:5131", DateTimeOffset.UtcNow,
            true, true, false, [profile], [], HostId: Guid.NewGuid());
        Require(link.ObserveSharedCatchUpScheduling(snapshot), "Initial cached discovery was not scheduled.");
        var due = (Dictionary<Guid, DateTimeOffset>)typeof(FriendLink).GetField("sharedRetryAfter", Private)!.GetValue(link)!;
        due[profileId] = DateTimeOffset.UtcNow.AddMinutes(5);
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<Guid, ReceivedSharedWorldStatus>)typeof(FriendLink)
            .GetField("sharedLocalObservations", Private)!.GetValue(link)!;
        cache[profileId] = new(true, 1, 1, "Up to date when last checked");
        Require(!link.ObserveSharedCatchUpScheduling(snapshot with
        {
            LastConnectedUtc = DateTimeOffset.UtcNow.AddSeconds(5),
            Profiles = [profile with { OnlinePlayers = 2, PlayerCountObservedUtc = DateTimeOffset.UtcNow }]
        }) &&
            due.ContainsKey(profileId) && cache.ContainsKey(profileId),
            "Cheap heartbeat/player observation restarted expensive discovery or invalidated an unchanged cache.");
        Require(link.ObserveSharedCatchUpScheduling(snapshot with { State = "Disconnected/Unknown" }) && due.Count == 0 && cache.IsEmpty &&
            link.ObserveSharedCatchUpScheduling(snapshot), "Reconnection failed to invalidate stale status/deadlines.");
        due[profileId] = DateTimeOffset.UtcNow.AddMinutes(5);
        var published = new ActivityEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "Backup", "SharedSavePublished", "Save available",
            ActivitySeverity.Info, profileId, Visibility: ActivityVisibility.AssignedFriends);
        Require(link.ObserveSharedCatchUpScheduling(snapshot with { Activity = [published] }) && due.Count == 0,
            "Publication did not bypass the old idle deadline.");

        var payload = RandomNumberGenerator.GetBytes(SharedWorldService.ChunkBytes + 37);
        var file = new SharedWorldFile("world.dat", payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var path = Path.Combine(data.RootPath, file.Path);
        File.WriteAllBytes(path, payload);
        Require(FriendLink.ResumeOffset(path, file) == payload.Length && FriendLink.ExistingPartialBytes(data.RootPath, file) == payload.Length,
            "A completed non-chunk-aligned file was lost from a multi-file retry baseline.");
        File.WriteAllBytes(path, payload[..(SharedWorldService.ChunkBytes + 5)]);
        Require(FriendLink.ResumeOffset(path, file) == SharedWorldService.ChunkBytes &&
            FriendLink.ExistingPartialBytes(data.RootPath, file) == SharedWorldService.ChunkBytes,
            "An interrupted write mixed a partial chunk into logical resumed progress.");
    }

    internal sealed class SlowBody(byte[] bytes, bool pause = true) : Stream
    {
        private int position;
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position > 0 && pause && !Release.Task.IsCompleted)
            { Paused.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await Task.Delay(2, cancellationToken);
            var quantum = Math.Max(1, Math.Min(4096, bytes.Length / 2));
            var count = Math.Min(Math.Min(buffer.Length, quantum), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class JobClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan interval) => now += interval;
    }
}
