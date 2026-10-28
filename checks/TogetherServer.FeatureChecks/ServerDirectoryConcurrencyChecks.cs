using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable protected metadata and refusing adapters only. The held monitor
// models publication's root fence; no app, process, listener or real save runs.
internal static class ServerDirectoryConcurrencyChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "directory-lock-order-" + Guid.NewGuid().ToString("N")));
        data.SaveSettings(new HostSettings());
        var connectionIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var profileIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new
        { ids = connectionIds, selectedId = connectionIds[0] }, Json));
        for (var index = 0; index < connectionIds.Length; index++)
        {
            var configuration = new FriendConfiguration
            {
                HostId = Guid.NewGuid(),
                DeviceId = Guid.NewGuid(),
                DisplayName = "Synthetic saved Host " + index,
                Endpoint = "https://192.0.2.10:5131",
                Fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                CachedProfiles = [new(profileIds[index], "Synthetic shared server " + index, "Offline", null, Kind: GameKinds.Fixture)]
            };
            data.SaveProtected("friend-" + connectionIds[index].ToString("N") + ".protected",
                JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        }
        var clients = 0;
        using var friends = new FriendService(data, (_, _) =>
        {
            Interlocked.Increment(ref clients);
            throw new InvalidOperationException("A passive concurrency check attempted HTTP.");
        });
        var driver = new LocalHostingChecks.FilesOnlyDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var service = new FriendLocalHostingService(data, friends, manager);
        using var selected = friends.RetainLocalHostingConnection()
            ?? throw new InvalidOperationException("The generated selected connection was unavailable.");
        var progressStore = (LocalHostingProgressStore)(typeof(FriendLocalHostingService)
            .GetMethod("HostingProgressFor", BindingFlags.Instance | BindingFlags.NonPublic)?
            .Invoke(service, [selected.Link, profileIds[0]])
            ?? throw new InvalidOperationException("The expected per-server observation store is missing."));
        using var operation = progressStore.Begin(selected.Link, profileIds[0]);
        Require(friends.ServerDirectoryHosts().Count == 2 && friends.CurrentLocalHostingConnection(selected),
            "The generated directory did not expose both saved Hosts before contention.");

        var directory = new TaskCompletionSource<IReadOnlyList<ServerDirectoryHost>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var directoryStarted = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            directoryStarted.Set();
            try { directory.SetResult(friends.ServerDirectoryHosts()); }
            catch (Exception error) { directory.SetException(error); }
        })
        { IsBackground = true, Name = "Synthetic directory root wait" };
        var workers = new List<Task>();
        Task<bool>? snapshot = null, progress = null, selection = null, change = null, rejected = null;
        var responsive = false;
        var changedWhileBlocked = false;
        var rejectedWhileBlocked = false;
        var rootFence = SharedWorldMutationGate.For(data.RootPath);

        // Keep monitor acquisition/release on this synchronous test thread.
        // Every timeout releases the root before draining the legacy workers.
        Monitor.Enter(rootFence);
        try
        {
            reader.Start();
            Require(directoryStarted.Wait(Deadline) && SpinWait.SpinUntil(() =>
                (reader.ThreadState & ThreadState.WaitSleepJoin) != 0, Deadline) && !directory.Task.IsCompleted,
                "The directory did not reach its protected-roster root wait.");
            snapshot = Dedicated(() => friends.View().ConnectionId == connectionIds[0]);
            progress = Dedicated(() => service.Progress(profileIds[0]) is { State: "Working", Phase: "Permission" });
            selection = Dedicated(() => friends.CurrentLocalHostingConnection(selected));
            workers.AddRange([snapshot, progress, selection]);
            responsive = Task.WaitAll(workers.ToArray(), Deadline);
            if (responsive)
            {
                change = Dedicated(() => friends.Select(connectionIds[1]).Ok);
                workers.Add(change);
                changedWhileBlocked = change.Wait(Deadline) && change.Result;
                if (changedWhileBlocked)
                {
                    rejected = Dedicated(() => !friends.CurrentLocalHostingConnection(selected) &&
                        service.Progress(profileIds[0]) is null && friends.View().ConnectionId == connectionIds[1]);
                    workers.Add(rejected);
                    rejectedWhileBlocked = rejected.Wait(Deadline) && rejected.Result;
                }
            }
        }
        finally
        {
            Monitor.Exit(rootFence);
            Require(reader.Join(TimeSpan.FromSeconds(5)), "The released directory reader did not finish.");
            Require(Task.WaitAll(workers.ToArray(), TimeSpan.FromSeconds(5)),
                "A source-check observer remained blocked after releasing the world root.");
        }
        Require(responsive && snapshot!.Result && progress!.Result && selection!.Result,
            "A directory waiting for the world root blocked snapshot, progress or the current-selection fence.");
        Require(changedWhileBlocked && rejectedWhileBlocked,
            "A changed selected connection remained authorized while the directory waited for the world root.");
        Require(directory.Task.GetAwaiter().GetResult().Count == 0,
            "A completed directory read returned rows for a replaced selected scope.");
        Require(friends.ServerDirectoryHosts().Count == 2 && friends.View().ConnectionId == connectionIds[1] &&
            Field<int>(selected.Link, "retainedOperations") == 1 && Field<int>(friends, "retainedOperations") == 0,
            "A fresh directory lost saved Hosts or leaked a retained connection after the bounded schedule.");
        Require(clients == 0 && driver.ProcessAttempts == 0 && data.LoadRuns().Count == 0,
            "Directory lock-order checks used transport, processes or managed game state.");
        return Task.CompletedTask;
    }

    private static Task<bool> Dedicated(Func<bool> read) =>
        Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static T Field<T>(object value, string name) =>
        (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException("The expected synthetic observation field is missing."));

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
