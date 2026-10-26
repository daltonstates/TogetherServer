using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerHostingDiscoveryChecks
{
    private static async Task MetadataRouteBudgetChecksAsync(string parent)
    {
        using (var fixture = new MetadataBudgetFixture(parent, "blocked-bootstrap"))
        {
            var progress = new List<WorldPeerSourceProgress>();
            var elapsed = Stopwatch.StartNew();
            var result = await fixture.Peers.ResolveLatestPreparedSourceAsync(fixture.World.Root, default, progress.Add)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Require(result.Ok && result.Source?.Descriptor.Version.VersionHash == fixture.World.Version.VersionHash &&
                fixture.World.Handler.MetadataRequests > 0 && fixture.Stalled.CanceledRequests == 1 &&
                fixture.World.Handler.PayloadRequests == 0 && progress.All(item => item.Phase != "PreparingCopy") &&
                elapsed.Elapsed < TimeSpan.FromSeconds(8),
                $"blocked bootstrap: result={result.Code}, exactSource={result.Source?.Descriptor.Version.VersionHash == fixture.World.Version.VersionHash}, " +
                $"metadata={fixture.World.Handler.MetadataRequests}, canceled={fixture.Stalled.CanceledRequests}, " +
                $"payload={fixture.World.Handler.PayloadRequests}, copyReported={progress.Any(item => item.Phase == "PreparingCopy")}, elapsedMs={elapsed.ElapsedMilliseconds}");
            result.Source!.Verify(fixture.Source.Data, fixture.World.Root);
            fixture.World.VerifyCachedCopy();
        }

        using (var fixture = new MetadataBudgetFixture(parent, "partial-fresh"))
        {
            // Five slow routes require more than the unchanged ten-second
            // overall budget. The fast recorded peer is reached after the
            // first route, before the remaining signed contacts stall.
            fixture.AddStalledContacts();
            var elapsed = Stopwatch.StartNew();
            var result = await MetadataDiscoveryAsync(fixture.Peers, fixture.World.Root, default)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Require(result && fixture.World.Handler.MetadataRequests > 0 && fixture.Stalled.Requests >= 5 &&
                elapsed.Elapsed >= TimeSpan.FromSeconds(9) && elapsed.Elapsed < TimeSpan.FromSeconds(14) &&
                fixture.World.Handler.PayloadRequests == 0 &&
                fixture.Peers.Views().Single(item => item.GroupKey == fixture.World.Root.GroupKey).Warning is not null,
                $"partial metadata discovery: result={result}, metadata={fixture.World.Handler.MetadataRequests}, " +
                $"stalled={fixture.Stalled.Requests}, payload={fixture.World.Handler.PayloadRequests}, elapsedMs={elapsed.ElapsedMilliseconds}");
            fixture.World.VerifyCachedCopy();
        }

        using (var fixture = new MetadataBudgetFixture(parent, "no-response", allOffline: true))
        {
            var result = await fixture.Peers.SyncWorldNowAsync(fixture.World.Root.GroupKey, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(!result.Ok && result.Code == "WorldPeerSyncInconclusive" &&
                fixture.World.Handler.MetadataRequests == 0 && fixture.World.Handler.PayloadRequests == 0 &&
                fixture.Source.Data.LoadRuns().Count == 0 && !fixture.Source.Data.LoadSettings().CompanionListeningEnabled,
                "a world with no authenticated metadata response was labeled fresh or changed lifecycle/listener state");
            fixture.World.VerifyCachedCopy();
        }

        using (var fixture = new MetadataBudgetFixture(parent, "caller-cancel"))
        using (var cancel = new CancellationTokenSource())
        {
            var operation = fixture.Peers.ResolveLatestPreparedSourceAsync(fixture.World.Root, cancel.Token);
            await fixture.Stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var canceled = false;
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { canceled = true; }
            Require(canceled && fixture.World.Handler.MetadataRequests == 0 && fixture.World.Handler.PayloadRequests == 0 &&
                fixture.Source.Data.LoadRuns().Count == 0,
                "caller cancellation was swallowed as partial discovery success or continued to another route");
            fixture.World.VerifyCachedCopy();
        }
    }

    private static Task<bool> MetadataDiscoveryAsync(WorldPeerService service, SharedWorldPeerRoot root, CancellationToken token) =>
        (Task<bool>)(typeof(WorldPeerService).GetMethod("DiscoverHostingMetadataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [root, token]) ?? throw new InvalidOperationException("The metadata discovery operation is unavailable."));

    private sealed class MetadataBudgetFixture : IDisposable
    {
        internal Fixture Source { get; }
        internal World World => Source.Target;
        internal WorldPeerService Peers { get; }
        internal MetadataStallHandler Stalled { get; }
        private readonly FriendService friends;
        private readonly SharedWorldPeerRoute[] stalledRoutes;

        internal MetadataBudgetFixture(string parent, string name, bool allOffline = false)
        {
            Source = new(parent, "metadata-budget-" + name);
            stalledRoutes = Enumerable.Range(190, 5).Select(index =>
                new SharedWorldPeerRoute($"https://192.0.2.{index}:5131", new string('F', 64))).ToArray();
            Stalled = new(allOffline);
            SaveGroups(Source.Data, [Membership(Source.Other.Root, Source.Other.Roster, Guid.Empty, Source.Other.Route),
                Membership(World.Root, World.Roster, Guid.Empty, stalledRoutes[0])]);
            HttpClient Client(string endpoint, IEnumerable<string> pins)
            {
                var route = endpoint.TrimEnd('/') == World.Route.Address ? World.Route :
                    stalledRoutes.SingleOrDefault(item => item.Address == endpoint.TrimEnd('/'));
                Require(route is not null && pins.SequenceEqual([route.CertificateSha256]),
                    "metadata discovery probed an unrelated world or changed a pinned route");
                return new(allOffline || route != World.Route ? Stalled : World.Handler, disposeHandler: false)
                { BaseAddress = new Uri(route.Address + "/") };
            }
            friends = new(Source.Data, Client);
            Peers = new(Source.Data, new HostManager(Source.Data), friends, new PairingService(Source.Data), Client);
        }

        internal void AddStalledContacts()
        {
            var card = new WorldPeerContactCard(1, World.Root, Guid.Empty, World.Root.OwnerPublicKey, 100,
                stalledRoutes.Skip(1).ToArray(), "");
            card = card with
            {
                Signature = Convert.ToBase64String(Source.Owner.SignData(
                WorldPeerService.ContactBasis(card), HashAlgorithmName.SHA256))
            };
            Require(Peers.ImportContactCode(World.Root.GroupKey, "TSW1-" +
                Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(card, Json))).Ok,
                "the signed retained unavailable-route contacts could not be prepared");
        }

        public void Dispose() { Peers.Dispose(); friends.Dispose(); Stalled.Dispose(); Source.Dispose(); }
    }

    private sealed class MetadataStallHandler(bool offline) : HttpMessageHandler
    {
        private int requests;
        private int canceled;
        internal int Requests => Volatile.Read(ref requests);
        internal int CanceledRequests => Volatile.Read(ref canceled);
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            Entered.TrySetResult(true);
            if (offline) throw new HttpRequestException("Synthetic unreachable peer.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
            throw new InvalidOperationException("A stalled metadata fixture cannot produce an authenticated response.");
        }
    }
}
