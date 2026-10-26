using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerHostingDiscoveryChecks
{
    // Reuses disposable signed worlds and fake HTTPS. These checks invoke only
    // the sharing loop, never the application/lifecycle/process entry points.
    internal static async Task RunBackgroundSchedulingAsync(string parent)
    {
        await PreparationLeaseBoundariesAsync(parent);
        await ChangedRegistrationCatchUpAsync(parent);
        await SharedRosterRejoinChecks.ExplicitSyncChecksAsync(parent);
        await ExactExplicitSyncDuringOtherWorldAsync(parent);
        await PreparationAndCoalescingAsync(parent);
        await SharingDuringBlockedHostPollAsync(parent);
    }

    private static async Task ExactExplicitSyncDuringOtherWorldAsync(string parent)
    {
        using var fixture = new Fixture(parent, "exact-explicit-sync");
        fixture.Other.Handler.HoldMetadata = true;
        fixture.Target.Handler.HoldMetadata = true;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var background = fixture.Service.SyncNowAsync(stopping.Token);
        Task<WorldPeerActionResult>? exact = null;
        try
        {
            await fixture.Other.Handler.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            exact = fixture.Service.SyncWorldNowAsync(fixture.Target.Root.GroupKey, stopping.Token);
            await fixture.Target.Handler.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!background.IsCompleted && fixture.Other.Handler.PayloadRequests == 0,
                "an unrelated world's held metadata delayed or was canceled by the exact explicit sync");
            fixture.Target.Handler.ReleaseMetadata.TrySetResult(true);
            var result = await exact.WaitAsync(TimeSpan.FromSeconds(10));
            Require(result.Ok && !background.IsCompleted && fixture.Target.Handler.ProofFailures == 0,
                "the exact explicit sync waited for unrelated work or failed its fresh signed proof");
        }
        finally
        {
            fixture.Target.Handler.ReleaseMetadata.TrySetResult(true);
            fixture.Other.Handler.ReleaseMetadata.TrySetResult(true);
            await background;
            if (exact is not null) await exact;
            await fixture.Service.WaitForSyncAsync();
        }
        fixture.Target.VerifyCachedCopy(); fixture.Other.VerifyCachedCopy();
    }

    private static async Task ChangedRegistrationCatchUpAsync(string parent)
    {
        using var fixture = new Fixture(parent, "changed-registration");
        // The checkpoint measures this world's first exchange, independent of
        // another world's complete catalog/seed work in the round-robin pass.
        SaveGroups(fixture.Data,
            [Membership(fixture.Target.Root, fixture.Target.Roster, Guid.Empty, fixture.Target.Route),
             Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route)]);
        var registrationGate = (SemaphoreSlim)(typeof(WorldPeerService).GetField("refreshGate",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fixture.Service)
            ?? throw new InvalidOperationException("the bounded membership-registration gate seam is unavailable"));
        await registrationGate.WaitAsync();
        var held = true;
        fixture.Target.Handler.HoldMetadata = true;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var background = fixture.Service.SyncNowAsync(stopping.Token);
        try
        {
            await fixture.Target.Handler.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require((await fixture.Service.SetReceivingAsync(fixture.Target.Root.GroupKey, false, default)).Ok,
                "the material membership change was not recorded during blocked registration");
            var targetMetadata = fixture.Target.Handler.MetadataRequests;
            Require(fixture.Other.Handler.MetadataRequests == 0,
                "the material-change checkpoint accidentally waited for another world's complete transfer");
            fixture.Target.Handler.ReleaseMetadata.TrySetResult(true);
            registrationGate.Release(); held = false;
            await background.WaitAsync(TimeSpan.FromSeconds(10));
            Require(fixture.Target.Handler.MetadataRequests == targetMetadata &&
                fixture.Target.Handler.PayloadRequests == 0 && fixture.Other.Handler.MetadataRequests == 1 &&
                !fixture.Service.Views().Single(item => item.GroupKey == fixture.Target.Root.GroupKey).ReceiveConsented,
                "ordinary consent progress repeated the per-call metadata/transfer budget or lost the current deny");
        }
        finally
        {
            fixture.Target.Handler.ReleaseMetadata.TrySetResult(true);
            if (held) registrationGate.Release();
            await background;
            await fixture.Service.WaitForSyncAsync();
        }
        fixture.Target.VerifyCachedCopy(); fixture.Other.VerifyCachedCopy();
    }

    private static async Task PreparationLeaseBoundariesAsync(string parent)
    {
        using var fixture = new Fixture(parent, "whole-preparation-priority");
        using var operation = fixture.Service.BeginHostingPreparation(fixture.Target.Root);
        using var source = fixture.Service.BeginHostingPreparation(fixture.Target.Root);
        operation.Dispose();
        await fixture.Service.SyncNowAsync(default);
        Require(fixture.Target.Handler.PayloadRequests == 0 && fixture.Other.Handler.PayloadRequests > 0,
            "a remaining preparation scope did not yield new background work only for its exact world");
        source.Dispose(); source.Dispose();
        await fixture.Service.SyncNowAsync(default);
        Require(fixture.Target.Handler.PayloadRequests > 0 && fixture.Target.Handler.ProofFailures == 0 &&
            fixture.Data.LoadRuns().Count == 0,
            "idempotent preparation-scope release failed to resume ordinary background copying");
        fixture.Target.VerifyCachedCopy(); fixture.Other.VerifyCachedCopy();
    }

    private static async Task PreparationAndCoalescingAsync(string parent)
    {
        using var fixture = new Fixture(parent, "background-preparation");
        fixture.Other.Handler.HoldMetadata = true;
        fixture.Other.Handler.HoldCatalog = true;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var background = fixture.Service.SyncNowAsync(stopping.Token);
        Task<WorldPeerLatestSourceResult>? joinedPreparation = null;
        try
        {
            await fixture.Other.Handler.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var repeated = Enumerable.Range(0, 8).Select(_ => fixture.Service.SyncNowAsync(default)).ToArray();
            Require(repeated.All(task => ReferenceEquals(task, repeated[0])) && !repeated[0].IsCompleted,
                "repeated shared-save checks queued additional background passes");
            using (var canceled = new CancellationTokenSource())
            {
                var waiter = fixture.Service.SyncNowAsync(canceled.Token);
                canceled.Cancel();
                try { await waiter; throw new InvalidOperationException("a canceled sync waiter completed successfully"); }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
                Require(!background.IsCompleted, "canceling a joining check canceled the existing background pass");
            }
            var target = await fixture.Service.ResolveLatestRecoverySourceAsync(fixture.Target.Root, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(target.Ok && target.Source?.Descriptor.Version.VersionHash == fixture.Target.Version.VersionHash &&
                !background.IsCompleted, "another world's blocked background transfer delayed explicit preparation");
            fixture.Other.Handler.ReleaseMetadata.TrySetResult(true);
            await fixture.Other.Handler.CatalogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var canceled = new CancellationTokenSource())
            {
                var waiter = fixture.Service.ResolveLatestRecoverySourceAsync(fixture.Other.Root, canceled.Token);
                canceled.Cancel();
                await RequireCancellationAsync(waiter, canceled.Token);
                Require(!background.IsCompleted && fixture.Other.Handler.PayloadRequests == 1,
                    "canceling a joining preparation canceled or repeated the background payload transfer");
            }
            joinedPreparation = fixture.Service.ResolveLatestRecoverySourceAsync(fixture.Other.Root, default);
            Require(!joinedPreparation.IsCompleted && fixture.Other.Handler.PayloadRequests == 1,
                "same-world preparation did not join the existing bounded payload transfer");
            fixture.Other.Handler.ReleaseCatalog.TrySetResult(true);
            var joined = await joinedPreparation.WaitAsync(TimeSpan.FromSeconds(10));
            await background.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(repeated).WaitAsync(TimeSpan.FromSeconds(5));
            Require(joined.Ok && fixture.Other.Handler.CompletedCatalogs == 1 &&
                fixture.Other.Handler.ProofFailures == 0 && fixture.Target.Handler.ProofFailures == 0,
                "background/preparation repeated a catalog transfer or invalidated a single-use peer proof");
            fixture.Target.VerifyCachedCopy(); fixture.Other.VerifyCachedCopy();
            Require(fixture.Data.LoadRuns().Count == 0, "background scheduling recorded a game run");
        }
        finally
        {
            fixture.Other.Handler.ReleaseMetadata.TrySetResult(true);
            fixture.Other.Handler.ReleaseCatalog.TrySetResult(true);
            await background;
            if (joinedPreparation is not null) await joinedPreparation;
            await fixture.Service.WaitForSyncAsync();
        }
    }

    private static async Task SharingDuringBlockedHostPollAsync(string parent)
    {
        using var fixture = new Fixture(parent, "blocked-host-poll");
        using var friendData = new LocalData(Path.Combine(fixture.Data.RootPath, "unavailable-original-host"));
        friendData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DeviceId = Guid.NewGuid(),
            Endpoint = "https://192.0.2.199:5131",
            Fingerprint = new string('F', 64),
            Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(60)
        }, Json));
        using var blocked = new BlockedHeartbeat();
        using var friends = new FriendService(friendData, (endpoint, _) => new(blocked, disposeHandler: false)
        { BaseAddress = new Uri(endpoint + "/") });
        HttpClient PeerClient(string endpoint, IEnumerable<string> pins)
        {
            var world = endpoint.TrimEnd('/') == fixture.Target.Route.Address ? fixture.Target : fixture.Other;
            Require(endpoint.TrimEnd('/') == world.Route.Address && pins.SequenceEqual([world.Route.CertificateSha256]),
                "background sharing changed an enrolled peer's endpoint or TLS pin");
            return new(world.Handler, disposeHandler: false) { BaseAddress = new Uri(world.Route.Address + "/") };
        }
        using var peers = new WorldPeerService(fixture.Data, new HostManager(fixture.Data), friends,
            new PairingService(fixture.Data), PeerClient);
        var work = new AppBackgroundTasks(friends, null!, null!, null!, null!, fixture.Data,
            null, () => false, worldPeers: peers);
        var poll = friends.PollAsync();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Other.Handler.HoldCatalog = true;
        var sharing = (Task)(typeof(AppBackgroundTasks).GetMethod("RunSharedCatchUpAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(work, null)
            ?? throw new InvalidOperationException("the isolated sharing loop seam is unavailable"));
        try
        {
            await fixture.Other.Handler.CatalogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!poll.IsCompleted && fixture.Other.Handler.PayloadRequests == 1,
                "completed-save discovery waited for the original Host's heartbeat or roster gate");
        }
        finally
        {
            blocked.Release.TrySetResult(true);
            fixture.Other.Handler.ReleaseCatalog.TrySetResult(true);
            await poll;
            await work.DisposeAsync();
            await sharing;
        }
        Require(fixture.Other.Handler.ProofFailures == 0 && fixture.Data.LoadRuns().Count == 0,
            "independent sharing invalidated peer authentication or invoked a game run");
    }

    private sealed class BlockedHeartbeat : HttpMessageHandler
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.ServiceUnavailable);
        }
    }
}
