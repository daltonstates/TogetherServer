using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TogetherServer;
using Fixture = TogetherServer.FeatureChecks.WorldPeerExistingCopyChecks.Fixture;

namespace TogetherServer.FeatureChecks;

// Generated certificates, files and fake HTTP only. No socket, application,
// managed game process, browser or production state is used.
internal static class WorldPeerTransportReuseChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(string parent)
    {
        await PoolLifetimeAsync();
        PoolBudget();
        CertificateExpiry();
        await SignedOperationsAsync(parent);
        await ExactAcknowledgementSkipsOffersAsync(parent);
        await AuthenticatedPullOwnsPayloadAsync(parent);
        PullOwnershipBounds();
    }

    private static async Task PoolLifetimeAsync()
    {
        var time = new Clock();
        var handlers = new List<Handler>();
        using var pool = new WorldPeerService.PeerClientPool((endpoint, _) =>
        {
            var handler = new Handler(); handlers.Add(handler);
            return new(handler) { BaseAddress = new Uri(endpoint + "/") };
        }, time);
        var route = Route(1);
        using (var first = pool.Rent(route))
        using (var second = pool.Rent(route with { Address = route.Address + "/" }))
        {
            Require(ReferenceEquals(first.Client, second.Client) && handlers.Count == 1, "An unchanged exact origin/pin did not reuse its client.");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await RejectAsync(() => first.Client.GetAsync("fixed", canceled.Token));
            using var response = await second.Client.GetAsync("fixed");
            Require(response.IsSuccessStatusCode && handlers.Count == 1, "One canceled request poisoned a reused client.");
        }
        time.Utc += WorldPeerService.PeerClientPool.IdleLifetime;
        using var renewed = pool.Rent(route);
        Require(handlers.Count == 2 && handlers[0].Disposed, "An idle client survived its bounded retention window.");
        using var changedPin = pool.Rent(route with { CertificateSha256 = new string('B', 64) });
        using var changedOrigin = pool.Rent(Route(2));
        Require(handlers.Count == 4 && !ReferenceEquals(renewed.Client, changedPin.Client) &&
            !ReferenceEquals(renewed.Client, changedOrigin.Client), "A changed TLS pin/origin reused an old client.");
        pool.Invalidate(route);
        Require(!handlers[1].Disposed, "Invalidation disposed an active request lease.");
        renewed.Dispose();
        Require(handlers[1].Disposed, "An invalidated client survived its final request lease.");
        pool.Dispose();
        Require(!handlers[2].Disposed && !handlers[3].Disposed, "Pool shutdown interrupted an active request lease.");
        changedPin.Dispose(); changedOrigin.Dispose();
        Require(handlers.All(handler => handler.Disposed), "Pool shutdown leaked a client after its final lease.");
        Reject(() => pool.Rent(route));
    }

    private static void PoolBudget()
    {
        var handlers = new List<Handler>();
        using var pool = new WorldPeerService.PeerClientPool((endpoint, _) =>
        {
            var handler = new Handler(); handlers.Add(handler);
            return new(handler) { BaseAddress = new Uri(endpoint + "/") };
        });
        var leases = Enumerable.Range(10, WorldPeerService.PeerClientPool.MaximumClients).Select(index => pool.Rent(Route(index))).ToArray();
        try
        {
            Reject(() => pool.Rent(Route(99)));
            pool.Invalidate(Route(10));
            Reject(() => pool.Rent(Route(99)));
            Require(handlers.Count == WorldPeerService.PeerClientPool.MaximumClients, "Active or retired leases escaped the bounded live-client budget.");
            leases[0].Dispose();
            using var replacement = pool.Rent(Route(99));
            Require(handlers.Count == WorldPeerService.PeerClientPool.MaximumClients + 1 && handlers[0].Disposed,
                "A released retired client did not return its resource budget.");
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        using var evicted = pool.Rent(Route(100));
        Require(handlers.Count(handler => !handler.Disposed) <= WorldPeerService.PeerClientPool.MaximumClients,
            "Idle LRU eviction exceeded the retained-client budget.");
    }

    private static void CertificateExpiry()
    {
        var time = new Clock();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=SyntheticPeer", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(time.Utc.AddMinutes(-1), time.Utc.AddMinutes(1));
        var pin = SHA256.HashData(certificate.RawData);
        var window = new WorldPeerService.PeerCertificateWindow([pin], time);
        Require(window.Accept(certificate), "An exact current generated TLS certificate was refused.");
        window.EnsureCurrent();
        Require(!new WorldPeerService.PeerCertificateWindow([new byte[32]], time).Accept(certificate), "A wrong TLS pin was accepted.");
        time.Utc = time.Utc.AddMinutes(2);
        Reject(window.EnsureCurrent);
        Require(!window.Accept(certificate), "A pooled TLS certificate remained valid after its expiry.");
        time.Utc = time.Utc.AddMinutes(-4);
        Reject(window.EnsureCurrent);
        Require(!window.Accept(certificate), "A pooled TLS certificate remained valid before its validity window.");
    }

    private static async Task SignedOperationsAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        using var friends = new FriendService(fixture.RequesterData, (_, _) => throw new HttpRequestException("Synthetic original route is offline."));
        var handlers = new List<Handler>();
        using var service = new WorldPeerService(fixture.RequesterData, new HostManager(fixture.RequesterData), friends,
            new PairingService(fixture.RequesterData), (endpoint, pins) =>
            {
                Require(endpoint == fixture.SourceRoute.Address && pins.SequenceEqual([fixture.SourceRoute.CertificateSha256]), "A pooled request changed its exact route/pin.");
                var handler = new Handler(fixture.SourceHandler); handlers.Add(handler);
                return new(handler) { BaseAddress = new Uri(endpoint + "/") };
            });
        var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(service, [fixture.Root.GroupKey])!;
        Task<byte[]> Send(CancellationToken token = default) => (Task<byte[]>)typeof(WorldPeerService).GetMethod("SendPeerAsync", Private)!
            .Invoke(service, [group, fixture.SourceRoute, WorldPeerOperations.Catalog, token, null, 0, null, null, null])!;
        await Send(); await Send(); await Send();
        Require(handlers.Count == 1 && handlers[0].Nonces.Count == 3 && handlers[0].Nonces.Distinct(StringComparer.Ordinal).Count() == 3 &&
            fixture.SourceHandler.Operations.Count(operation => operation == WorldPeerOperations.Catalog) == 3,
            "Client reuse skipped a fresh signed single-use challenge/exchange.");
        handlers[0].FailNext = true;
        await RejectAsync(() => Send());
        Require(handlers[0].Disposed, "A failed transport remained in the connection pool.");
        await Send();
        Require(handlers.Count == 2 && handlers[1].Nonces.Count == 1, "A retired transport was reused or the replacement skipped authentication.");
        Task<byte[]> Chunk() => (Task<byte[]>)typeof(WorldPeerService).GetMethod("SendPeerAsync", Private)!
            .Invoke(service, [group, fixture.SourceRoute, WorldPeerOperations.Chunk, CancellationToken.None, null, 0,
                fixture.Descriptor.Version.VersionHash, 0, 0L])!;
        Require((await Chunk()).SequenceEqual(File.ReadAllBytes(fixture.PayloadPath)), "A reused connection lost the exact authorized chunk.");
        fixture.AdvanceRoster(receive: false);
        try { await Chunk(); throw new InvalidOperationException("A reused connection retained old save permission after a signed grant change."); }
        catch (InvalidDataException) { }
        service.Dispose();
        Require(handlers.All(handler => handler.Disposed), "Service disposal leaked its pooled transport.");
    }

    private static async Task ExactAcknowledgementSkipsOffersAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        await fixture.SyncExactAsync();
        var bytes = fixture.Target.LoadProtected($"world-peer-receipts-{fixture.Root.GroupKey}.protected");
        var receipts = bytes is null ? [] : System.Text.Json.JsonSerializer.Deserialize<List<SharedWorldReceipt>>(bytes,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
        Require(receipts.Any(receipt => receipt.DeviceId == Guid.Empty &&
            receipt.VersionHash == fixture.Descriptor.Version.VersionHash && SharedWorldReceiptTrust.VerifyPeerOwner(receipt, fixture.Root)),
            "The fake completed-save exchange did not record the exact authenticated original-recipient receipt.");
        // One completed payload per pass is deliberate. The next pass can
        // acknowledge an older retained parent that the recipient already has.
        await fixture.SyncExactAsync();
        var offers = fixture.RemoteHandler.Operations.Count(operation => operation == WorldPeerOperations.Seed);
        Require(offers > 0, "The first fake delivery never exercised the fixed Seed conversation.");
        await fixture.SyncExactAsync();
        Require(fixture.RemoteHandler.Operations.Count(operation => operation == WorldPeerOperations.Seed) == offers,
            "Repeated synchronization offered an already exactly acknowledged version again.");
        var result = fixture.TargetService.Views().Single().BackgroundTransfers!.Single().LastResult;
        Require(result is { Result: "KnownReceiptAccepted", SubmittedBytes: 0, ReceivedBytes: 0 },
            "An exact retained recipient receipt was lost or presented as another payload transfer.");
    }

    private static async Task AuthenticatedPullOwnsPayloadAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Original");
        var requester = fixture.RequesterService;
        var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(requester, [fixture.Root.GroupKey])!;
        var served = await (Task<byte[]>)typeof(WorldPeerService).GetMethod("SendPeerAsync", Private)!
            .Invoke(requester, [group, fixture.SourceRoute, WorldPeerOperations.Chunk, CancellationToken.None, null, 0,
                fixture.Descriptor.Version.VersionHash, 0, 0L])!;
        Require(served.SequenceEqual(File.ReadAllBytes(fixture.PayloadPath)), "The fake authenticated pull never obtained the exact source chunk.");
        var offers = fixture.RemoteHandler.Operations.Count(operation => operation == WorldPeerOperations.Seed);
        await fixture.SyncExactAsync();
        Require(fixture.RemoteHandler.Operations.Count(operation => operation == WorldPeerOperations.Seed) == offers,
            "A publisher started an independent Seed conversation while that exact recipient was actively pulling the same save.");
        var receiptBytes = fixture.Target.LoadProtected($"world-peer-receipts-{fixture.Root.GroupKey}.protected");
        Require(receiptBytes is null, "One served chunk was promoted into a completed-recipient receipt.");
    }

    private static void PullOwnershipBounds()
    {
        var time = new Clock();
        var ownership = new PeerPullOwnership(time);
        ownership.Served("world", "version", "recipient");
        Require(ownership.Active("world", "version", "recipient") &&
            !ownership.Active("other-world", "version", "recipient") &&
            !ownership.Active("world", "other-version", "recipient") &&
            !ownership.Active("world", "version", "other-recipient"), "A pull hint escaped its exact world/version/recipient.");
        time.Timestamp = TimeSpan.FromSeconds(30).Ticks;
        ownership.Served("world", "version", "recipient");
        time.Timestamp = TimeSpan.FromSeconds(89).Ticks;
        Require(ownership.Active("world", "version", "recipient"), "A progressing pull lost its bounded ownership hint.");
        time.Timestamp = TimeSpan.FromSeconds(90).Ticks;
        Require(!ownership.Active("world", "version", "recipient"), "An abandoned pull prevented later Seed fallback beyond its bound.");
    }

    private static SharedWorldPeerRoute Route(int suffix) => new($"https://192.0.2.{suffix}:5131", new string('A', 64));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception error) when (error is HttpRequestException or ObjectDisposedException) { return; } throw new InvalidOperationException("An invalid connection lease/certificate was accepted."); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return; } throw new InvalidOperationException("A failed or canceled request succeeded."); }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        internal long Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class Handler(HttpMessageHandler? inner = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker? invoker = inner is null ? null : new(inner, false);
        internal bool Disposed { get; private set; }
        internal bool FailNext { get; set; }
        internal List<string> Nonces { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNext) { FailNext = false; throw new HttpRequestException("Synthetic transport failure."); }
            if (invoker is null) return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            Require(request.Headers.GetValues(CompanionProtocol.HeaderName).SequenceEqual([CompanionProtocol.Current.ToString(System.Globalization.CultureInfo.InvariantCulture)]) &&
                request.Headers.GetValues("X-TogetherServer-Capability").SequenceEqual([CompanionProtocol.SharedWorldPeersCapability]), "Reused client mutated or duplicated fixed protocol headers.");
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
            {
                var hello = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(await request.Content!.ReadAsByteArrayAsync(cancellationToken), 2 * 1024 * 1024);
                Nonces.Add(hello.Nonce!);
            }
            return await invoker.SendAsync(request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; invoker?.Dispose(); } base.Dispose(disposing); }
    }
}
