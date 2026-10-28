using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;
using CopyFixture = TogetherServer.FeatureChecks.WorldPeerExistingCopyChecks.Fixture;

namespace TogetherServer.FeatureChecks;

// Generated signed histories and real peer handlers over fake HTTP only. No
// socket, application, native process, browser or production data is used.
internal static class WorldPeerPublicationPriorityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        await NewPublicationBeforeSlowIncomingAsync(parent);
        await PausedOfferYieldsAsync(parent);
        await ProgressBudgetYieldsAsync(parent);
        await ReceiptScopeAsync(parent, ReceiptMode.Forwarded);
        await ReceiptScopeAsync(parent, ReceiptMode.SameNumberOtherHash);
        await ImportedRevocationAsync(parent);
        await CatalogInventoryReuseAsync(parent);
        await ActiveLatestPullRemainsPendingAsync(parent);
    }

    private static async Task NewPublicationBeforeSlowIncomingAsync(string parent)
    {
        using var fixture = new DeliveryFixture(parent);
        fixture.Wire.BlockCatalog = true;
        using var cancel = new CancellationTokenSource();
        var sync = fixture.AutomaticAsync(cancel.Token);
        try
        {
            try
            {
                var completed = await Task.WhenAny(fixture.Wire.IncomingCatalog.Task, sync).WaitAsync(TimeSpan.FromSeconds(8));
                Require(completed != sync || fixture.Wire.IncomingCatalog.Task.IsCompleted,
                    "The automatic flight finished before the held incoming Catalog: " + fixture.Diagnostic(sync));
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The automatic flight did not reach the held incoming Catalog within its unchanged budget: " +
                    fixture.Diagnostic(sync));
            }
            Require(fixture.OwnerReceipt(fixture.Latest.Version.VersionHash) && fixture.Wire.LatestChunkRequests > 0 &&
                fixture.Wire.Operations.IndexOf("Seed9") < fixture.Wire.Operations.IndexOf("Catalog"),
                "Slow incoming history prevented automatic delivery of the exact new local publication.");
            var current = fixture.Source.Views().Single().BackgroundTransfers!.Single().Current;
            Require(current is { ExactReceiptAccepted: true, ReceiptPublicationNumber: 9 },
                "An exact authenticated original-recipient receipt was not retained during later incoming work.");
        }
        finally { cancel.Cancel(); await sync.WaitAsync(TimeSpan.FromSeconds(8)); }
        var offers = fixture.Wire.LatestSeedRequests;
        var chunks = fixture.Wire.LatestChunkRequests;
        fixture.Wire.BlockCatalog = false;
        fixture.Wire.Catalog = new(fixture.Root, [fixture.Previous]);
        await fixture.AutomaticAsync();
        Require(fixture.Wire.LatestSeedRequests == offers && fixture.Wire.LatestChunkRequests == chunks &&
            fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
            "An unchanged automatic follow-up offered or resent the already exactly acknowledged new save.");
        Require(fixture.PublishedHead() == fixture.Latest.Version.VersionHash,
            "Receiving old peer history changed the local publication head.");
    }

    private static async Task PausedOfferYieldsAsync(string parent)
    {
        using var fixture = new DeliveryFixture(parent);
        fixture.Wire.PauseLatest = true;
        await fixture.AutomaticAsync();
        Require(fixture.Wire.LatestSeedRequests == 1 && fixture.Wire.IncomingChunk.Task.IsCompleted &&
            fixture.ReceivedOther() && !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
            "A paused new-publication offer starved legal incoming competing history or invented a receipt.");
        var result = fixture.Source.Views().Single().BackgroundTransfers!.Single();
        Require(result.LastResult is { State: "Pending", Error: "None", Result: "SeedPaused", ExactReceiptAccepted: false } &&
            result.LastFailure is null && fixture.PublishedHead() == fixture.Latest.Version.VersionHash,
            "A paused offer became a terminal failure, exact receipt or replacement of competing local progress.");
    }

    private static async Task ProgressBudgetYieldsAsync(string parent)
    {
        using var fixture = new DeliveryFixture(parent, largeLatest: true);
        fixture.Wire.BlockAfterFirstLatestChunk = true;
        await fixture.AutomaticAsync().WaitAsync(TimeSpan.FromSeconds(18));
        var result = fixture.Source.Views().Single().BackgroundTransfers!.Single();
        Require(fixture.Wire.LatestChunkRequests >= 2 && fixture.Wire.IncomingChunk.Task.IsCompleted && fixture.ReceivedOther() &&
            result.LastResult is { State: "Pending", Error: "None", Result: "WorkBudget", SubmittedBytes: > 0, ReceivedBytes: > 0 } &&
            result.LastResult.PublicationNumber == 9 && result.LastResult.RequestedFileIndex == 0 &&
            result.LastResult.RequestedOffset == SharedWorldService.ChunkBytes && fixture.Other.Version.Number == 10 &&
            result.LastFailure is null && !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
            "The priority work budget starved incoming work, invented completion, or classified its resumable yield as cancellation.");
    }

    private static async Task ReceiptScopeAsync(string parent, ReceiptMode mode)
    {
        using var fixture = new DeliveryFixture(parent);
        fixture.Wire.ReceiptOverride = mode;
        fixture.Wire.Catalog = new(fixture.Root, [fixture.Previous]);
        await fixture.AutomaticAsync();
        var result = fixture.Source.Views().Single().BackgroundTransfers!.Single();
        Require(fixture.Wire.ChangedReceipts == 1 && !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash) &&
            result.LastResult?.ExactReceiptAccepted != true,
            "A forwarded or same-number other-hash receipt certified this route's exact newest copy.");
        if (mode == ReceiptMode.Forwarded)
            Require(result.LastResult is { Result: "KnownWithoutReceipt", Error: "None" } &&
                fixture.Receipts().Any(receipt => receipt == fixture.ForwardedReceipt),
                "A valid forwarded receipt was discarded or promoted to the authenticated responder's receipt.");
        else
            Require(result.LastFailure is { Error: "InvalidResponse" } &&
                !fixture.Receipts().Any(receipt => receipt.VersionHash == fixture.Other.Version.VersionHash),
                "A different exact hash was accepted merely because its signed publication number was also nine.");
    }

    private static async Task ImportedRevocationAsync(string parent)
    {
        using var fixture = new DeliveryFixture(parent);
        Require(fixture.Source.Views().Single().CanReceive, "The generated sender was not initially permitted to receive.");
        await fixture.RemoveRemoteReceiveGrantAsync();
        await fixture.AutomaticAsync();
        Require(fixture.Wire.Challenges > 0 && fixture.Wire.LatestSeedRequests == 0 &&
            fixture.Wire.LatestChunkRequests == 0 && !fixture.Source.Views().Single().CanReceive &&
            !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
            "Priority delivery reused the old local grant after its fresh handshake imported a signed removal.");
    }

    private static async Task CatalogInventoryReuseAsync(string parent)
    {
        using (var fixture = new DeliveryFixture(parent))
        {
            fixture.Wire.Catalog = new(fixture.Root, [fixture.Previous]);
            await fixture.AutomaticAsync();
            Require(fixture.OwnerReceipt(fixture.Latest.Version.VersionHash), "The generated unchanged-catalog check lacked its exact prior receipt.");
            var before = InventoryCount(fixture.Source);
            await fixture.DirectSyncAsync();
            // One outgoing catalog and the independent incoming-copy lookup.
            // Already acknowledged priority must not add a third inventory.
            Require(InventoryCount(fixture.Source) - before == 2,
                "An already acknowledged priority rebuilt its outgoing catalog for fallback forwarding.");
        }
        using (var fixture = new CopyFixture(parent, "Retained"))
        {
            Require(fixture.Descriptor.Version.SigningPublicKey == fixture.Root.OwnerPublicKey &&
                fixture.Descriptor.Branch!.CandidatePublicKey != fixture.Root.OwnerPublicKey,
                "The generated retained-only case unexpectedly had a local publication.");
            var before = InventoryCount(fixture.TargetService);
            await fixture.SyncExactAsync();
            Require(InventoryCount(fixture.TargetService) - before == 2,
                "A route without a locally produced publication inventoried separately for priority and forwarding.");
        }
    }

    private static long InventoryCount(WorldPeerService service) => service.WorkSnapshot().Work
        .Single(item => item.Stage == nameof(HostingWorkStage.Inventory)).Count;

    private static async Task ActiveLatestPullRemainsPendingAsync(string parent)
    {
        var clock = new PullClock();
        using var fixture = new DeliveryFixture(parent, latestNumber: 11, pullClock: clock);
        fixture.Wire.Catalog = new(fixture.Root, [fixture.Previous]);
        await fixture.RecordPreviousOwnerReceiptAsync();
        Require(fixture.OwnerReceipt(fixture.Previous.Version.VersionHash) && fixture.Previous.Version.Number == 8,
            "The pending-latest regression lacked its genuine exact old owner receipt.");
        var firstChunk = await fixture.RequestLatestChunkAsync();
        Require(firstChunk.Length > 0 && !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
            "Preparing a real authenticated chunk invented a completed latest-copy receipt.");
        await fixture.AutomaticAsync();
        var pending = fixture.Source.Views().Single().BackgroundTransfers!.Single().LastResult;
        Require(pending is
        {
            State: "Pending", Result: "SeedPaused", PublicationNumber: 11, ExactReceiptAccepted: false,
            ReceiptPublicationNumber: null, RequestedFileIndex: null, RequestedOffset: null, SubmittedBytes: 0, ReceivedBytes: 0
        } &&
            fixture.Wire.LatestSeedRequests == 0 && !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
            "An active unacknowledged pull of eleven fell through to an older receipt-eight success or invented progress.");
        clock.Timestamp = TimeSpan.FromSeconds(30).Ticks;
        Require((await fixture.RequestLatestChunkAsync()).SequenceEqual(firstChunk), "The generated replay did not request the same exact signed chunk.");
        clock.Timestamp = TimeSpan.FromSeconds(61).Ticks;
        var receiveGate = fixture.RemoteReceiveGate;
        await receiveGate.WaitAsync();
        try
        {
            await fixture.AutomaticAsync();
            var probed = fixture.Source.Views().Single().BackgroundTransfers!.Single().LastResult;
            Require(fixture.Wire.LatestSeedRequests == 1 && fixture.Wire.LatestChunkRequests == 0 &&
                probed is
                {
                    State: "Pending", Result: "SeedPaused", PublicationNumber: 11, ExactReceiptAccepted: false,
                    RequestedFileIndex: null, RequestedOffset: null, SubmittedBytes: 0, ReceivedBytes: 0
                } &&
                fixture.RemoteHeadIsOther() && !fixture.OwnerReceipt(fixture.Latest.Version.VersionHash),
                "An expired replay hint failed to allow one bounded probe, or the occupied real receive gate allowed duplicate payload/receipt.");
        }
        finally { receiveGate.Release(); }
    }

    private enum ReceiptMode { Exact, Forwarded, SameNumberOtherHash }
    private static void Require([DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class DeliveryFixture : IDisposable
    {
        private readonly CopyFixture copies;
        private readonly FriendService friends;
        private readonly string published;
        private readonly string incoming;
        private readonly string remoteVault;
        private readonly SharedWorldRoster roster;
        private readonly SharedWorldPeerRoute route;
        private readonly byte[] latestBytes;
        internal WorldPeerService Source { get; }
        internal Wire Wire { get; }
        internal SharedWorldPeerRoot Root => copies.Root;
        internal WorldPeerSaveDescriptor Previous { get; }
        internal WorldPeerSaveDescriptor Latest { get; }
        internal WorldPeerSaveDescriptor Other { get; }
        internal SharedWorldReceipt ForwardedReceipt { get; }

        internal DeliveryFixture(string parent, bool largeLatest = false, int latestNumber = 9, TimeProvider? pullClock = null)
        {
            copies = new(parent, "Branch");
            published = Path.GetDirectoryName(copies.DirectoryPath)!;
            var branch = copies.Descriptor.Branch!;
            var existingBytes = File.ReadAllBytes(copies.PayloadPath);
            latestBytes = largeLatest ? new byte[SharedWorldService.ChunkBytes * 3 + 17] : existingBytes;
            using var hosting = ECDsa.Create();
            hosting.ImportPkcs8PrivateKey(copies.B.LoadProtected(WorldAuthorityStore.HostingKeyName(Root.ProfileId))!, out _);
            var versions = new List<SharedWorldVersion> { branch.Offer.Version, copies.Descriptor.Version };
            var head = copies.Descriptor.Version;
            for (var number = 3; number <= latestNumber; number++)
            {
                var payload = number == latestNumber ? latestBytes : existingBytes;
                head = SharedWorldService.SignVersion(head with
                {
                    Number = number,
                    ParentHash = head.VersionHash,
                    BackupId = Guid.NewGuid(),
                    Files = [new("world.dat", payload.Length, Convert.ToHexString(SHA256.HashData(payload)))]
                }, hosting);
                WriteCopy(published, head, payload);
                versions.Add(head);
            }
            Previous = new(versions.Single(version => version.Number == 8), branch);
            Latest = new(head, branch);
            var sibling = SharedWorldService.SignVersion(head with { BackupId = Guid.NewGuid() }, hosting);
            var otherHead = largeLatest ? SharedWorldService.SignVersion(sibling with
            { Number = latestNumber + 1, ParentHash = sibling.VersionHash, BackupId = Guid.NewGuid() }, hosting) : sibling;
            Other = new(otherHead, branch);
            // The peer retains exact eight and a competing signed continuation. It
            // cannot silently replace either history when our nine arrives.
            remoteVault = SharedWorldBranchVault.Root(copies.A, branch);
            Directory.CreateDirectory(remoteVault);
            foreach (var version in versions.Where(version => version.Number < latestNumber))
                FriendLink.KeepSignedManifest(remoteVault, version);
            WriteCopy(remoteVault, Previous.Version, existingBytes);
            WriteCopy(remoteVault, sibling, latestBytes);
            if (largeLatest) WriteCopy(remoteVault, otherHead, latestBytes);
            File.WriteAllBytes(Path.Combine(remoteVault, "branch.json"), JsonSerializer.SerializeToUtf8Bytes(branch, Json));
            incoming = SharedWorldBranchVault.Root(copies.B, branch);
            // Fairness checks exercise a held outgoing request and incoming
            // payload ownership, not repeated signed ancestry round trips.
            // These are valid parent manifests only: no complete incoming copy,
            // latest pointer or receipt exists before the real receive path.
            Directory.CreateDirectory(incoming);
            foreach (var version in versions.Where(version => version.Number < latestNumber))
                FriendLink.KeepSignedManifest(incoming, version);
            FriendLink.KeepSignedManifest(incoming, sibling);
            roster = (SharedWorldRoster)typeof(CopyFixture).GetField("roster", Private)!.GetValue(copies)!;
            var memberId = copies.TargetService.Views().Single().SelfDeviceId;
            ForwardedReceipt = copies.SignSelfReceipt(new(1, Root.GroupId, Root.ProfileId, head.VersionHash,
                memberId, roster.Epoch, roster.Revision, Guid.NewGuid(), ""));
            using var owner = ECDsa.Create();
            owner.ImportPkcs8PrivateKey(copies.A.LoadProtected("shared-world-signing-key.protected")!, out _);
            var otherDraft = new SharedWorldReceipt(SharedWorldReceiptTrust.PeerOwnerSchema, Root.GroupId,
                Root.ProfileId, sibling.VersionHash, Guid.Empty, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
            var otherReceipt = otherDraft with
            {
                Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldReceiptTrust.PeerOwnerBasis(otherDraft, Root), HashAlgorithmName.SHA256))
            };
            var config = JsonSerializer.Deserialize<FriendConfiguration>(copies.B.LoadProtected("friend.protected")!, Json)!;
            route = new(config.Endpoint, config.Fingerprint);
            Wire = new(copies.RemoteHandler, Latest, Other, ForwardedReceipt, otherReceipt)
            { Catalog = new(Root, [Previous, Other]) };
            friends = new(copies.B, (_, _) => throw new HttpRequestException("Generated original heartbeat is offline."));
            Source = new(copies.B, new HostManager(copies.B), friends, new(copies.B), (endpoint, pins) =>
            {
                Require(endpoint.TrimEnd('/') == route.Address && pins.SequenceEqual([route.CertificateSha256]),
                    "The fake priority exchange crossed its exact origin or pin.");
                return new(Wire, false) { BaseAddress = new Uri(route.Address + "/"), Timeout = TimeSpan.FromSeconds(30) };
            });
            if (pullClock is not null)
                typeof(WorldPeerService).GetField("inboundPulls", Private)!.SetValue(Source, new PeerPullOwnership(pullClock));
            copies.SourceHandler.Service = Source;
        }

        internal Task AutomaticAsync(CancellationToken token = default)
        {
            typeof(WorldPeerService).GetMethod("RequestBackgroundSync", Private)!.Invoke(Source, null);
            Source.ScheduleSync(token);
            return (Task)typeof(WorldPeerService).GetField("backgroundSync", Private)!.GetValue(Source)!;
        }
        internal Task DirectSyncAsync()
        {
            var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(Source, [Root.GroupKey])!;
            return (Task)typeof(WorldPeerService).GetMethod("SyncPeerAsync", Private)!
                .Invoke(Source, [group, route, CancellationToken.None, true, false])!;
        }
        internal IReadOnlyList<SharedWorldReceipt> Receipts()
        {
            var bytes = copies.B.LoadProtected($"world-peer-receipts-{Root.GroupKey}.protected");
            return bytes is null ? [] : JsonSerializer.Deserialize<List<SharedWorldReceipt>>(bytes, Json) ?? [];
        }
        internal bool OwnerReceipt(string hash) => Receipts().Any(receipt => receipt.VersionHash == hash &&
            receipt.RosterEpoch == roster.Epoch && receipt.RosterRevision == roster.Revision &&
            SharedWorldReceiptTrust.VerifyPeerOwner(receipt, Root));
        internal string? PublishedHead() => JsonSerializer.Deserialize<SharedWorldVersion>(
            File.ReadAllBytes(Path.Combine(published, "latest.json")), Json)?.VersionHash;
        internal bool ReceivedOther() => File.Exists(Path.Combine(incoming, "latest.json")) &&
            FriendLink.ReadReceivedLatest(incoming)?.VersionHash == Other.Version.VersionHash;
        internal SemaphoreSlim RemoteReceiveGate => FriendLink.ReceiveGateFor(remoteVault);
        internal bool RemoteHeadIsOther() => FriendLink.ReadReceivedLatest(remoteVault)?.VersionHash == Other.Version.VersionHash;
        internal Task<byte[]> RequestLatestChunkAsync()
        {
            var requester = copies.RequesterService;
            var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(requester, [Root.GroupKey])!;
            return (Task<byte[]>)typeof(WorldPeerService).GetMethod("SendPeerAsync", Private)!
                .Invoke(requester, [group, copies.SourceRoute, WorldPeerOperations.Chunk, CancellationToken.None, null, 0,
                    Latest.Version.VersionHash, 0, 0L])!;
        }
        internal async Task RecordPreviousOwnerReceiptAsync()
        {
            var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(Source, [Root.GroupKey])!;
            var response = await (Task<WorldPeerSeedResponse>)typeof(WorldPeerService).GetMethod("SendSeedAsync", Private)!
                .Invoke(Source, [group, route, Previous, null, null, null, null, CancellationToken.None])!;
            Require(response is { Code: "SeedKnown", Receipt: not null } &&
                response.Receipt.VersionHash == Previous.Version.VersionHash && SharedWorldReceiptTrust.VerifyPeerOwner(response.Receipt, Root),
                "The genuine fake owner did not acknowledge its exact complete retained eighth copy.");
            var accepted = (WorldPeerActionResult)typeof(WorldPeerService).GetMethod("AcceptReceipt", Private)!
                .Invoke(Source, [group, JsonSerializer.SerializeToUtf8Bytes(response.Receipt, Json)])!;
            Require(accepted.Ok, "The generated old owner receipt failed the actual current-scope receipt boundary.");
        }
        internal string Diagnostic(Task flight)
        {
            var group = Source.Views().Single();
            var transfer = group.BackgroundTransfers?.FirstOrDefault();
            var progress = transfer?.Current ?? transfer?.LastResult;
            return JsonSerializer.Serialize(new
            {
                flightCompleted = flight.IsCompleted,
                flightCanceled = flight.IsCanceled,
                flightFaulted = flight.IsFaulted,
                exactNewestOwnerReceipt = OwnerReceipt(Latest.Version.VersionHash),
                incomingCatalogReached = Wire.IncomingCatalog.Task.IsCompleted,
                incomingChunkReached = Wire.IncomingChunk.Task.IsCompleted,
                group.CanReceive,
                group.CanReshare,
                Wire.Challenges,
                Wire.LatestSeedRequests,
                Wire.LatestChunkRequests,
                Wire.ExchangeFailures,
                Wire.LastFailureCode,
                operations = Wire.OperationSnapshot(),
                state = progress?.State,
                phase = progress?.Phase,
                result = progress?.Result,
                error = progress?.Error,
                httpStep = progress?.HttpStep,
                httpStatus = progress?.HttpStatus,
                publication = progress?.PublicationNumber,
                receiptPublication = progress?.ReceiptPublicationNumber,
                submittedBytes = progress?.SubmittedBytes,
                receivedBytes = progress?.ReceivedBytes,
                requestedFile = progress?.RequestedFileIndex,
                requestedOffset = progress?.RequestedOffset
            }, Json);
        }

        internal async Task RemoveRemoteReceiveGrantAsync()
        {
            var pairing = (PairingService)typeof(CopyFixture).GetField("pairing", Private)!.GetValue(copies)!;
            var member = roster.Members.Single();
            Require(pairing.SetSharedWorldGrants(member.DeviceId, Root.ProfileId,
                member.Grants with { Receive = false, Reshare = false }).Ok, "The generated owner could not remove its grant.");
            var profile = copies.A.LoadSettings().Profiles.Single();
            var next = new SharedWorldService(copies.A, new WorldBackupService(copies.A, TimeProvider.System))
                .PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
            pairing.ConfirmSharedRosterPublished(profile.Id, next);
            // Only the remote owner refreshes. The sender must learn the new
            // signed revision through its real challenge exchange below.
            await copies.RequesterService.RefreshMembershipsAsync(false, CancellationToken.None);
            Require(Source.Views().Single().CanReceive, "The generated sender learned removal before its network handshake.");
        }
        public void Dispose() { Source.Dispose(); friends.Dispose(); Wire.Dispose(); copies.Dispose(); }
    }

    private static void WriteCopy(string vault, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(vault, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }

    private sealed class PullClock : TimeProvider
    {
        internal long Timestamp;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class Wire(HttpMessageHandler remote, WorldPeerSaveDescriptor latest,
        WorldPeerSaveDescriptor other, SharedWorldReceipt forwarded, SharedWorldReceipt otherReceipt) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker target = new(remote, false);
        internal WorldPeerCatalog? Catalog;
        internal bool BlockCatalog, PauseLatest, BlockAfterFirstLatestChunk;
        internal ReceiptMode ReceiptOverride;
        internal int LatestSeedRequests, LatestChunkRequests, ChangedReceipts, Challenges;
        internal int ExchangeFailures;
        internal string LastFailureCode = "None";
        internal List<string> Operations { get; } = [];
        internal TaskCompletionSource<bool> IncomingCatalog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> IncomingChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[] OperationSnapshot() { lock (Operations) return Operations.Take(64).ToArray(); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
            {
                Challenges++;
                return await target.SendAsync(request, token);
            }
            var envelope = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(
                await request.Content!.ReadAsByteArrayAsync(token), 2 * 1024 * 1024);
            var ownSeed = envelope.Request.Operation == WorldPeerOperations.Seed &&
                envelope.Request.VersionHash == latest.Version.VersionHash;
            var otherChunk = envelope.Request.Operation == WorldPeerOperations.Chunk &&
                envelope.Request.VersionHash == other.Version.VersionHash;
            var operation = ownSeed ? "Seed9" : otherChunk ? "ChunkOther" : envelope.Request.Operation switch
            {
                WorldPeerOperations.Metadata => "Metadata",
                WorldPeerOperations.Catalog => "Catalog",
                WorldPeerOperations.Version => "Manifest",
                WorldPeerOperations.Chunk => "ChunkOtherSave",
                WorldPeerOperations.Seed => "SeedOther",
                WorldPeerOperations.Receipt => "Receipt",
                _ => "Other"
            };
            lock (Operations) if (Operations.Count < 64) Operations.Add(operation);
            if (envelope.Request.Operation == WorldPeerOperations.Catalog)
            {
                IncomingCatalog.TrySetResult(true);
                if (BlockCatalog) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (ownSeed)
            {
                LatestSeedRequests++;
                if (envelope.Request.FileIndex is not null)
                {
                    LatestChunkRequests++;
                    if (BlockAfterFirstLatestChunk && LatestChunkRequests > 1)
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
            if (otherChunk)
            {
                IncomingChunk.TrySetResult(true);
            }
            HttpResponseMessage delivered;
            try { delivered = await target.SendAsync(request, token); }
            catch (Exception error)
            {
                ExchangeFailures++;
                LastFailureCode = error switch
                {
                    InvalidDataException invalid when invalid.Message == "The exact historical copy has no authorized retained source head." => "HistoricalHeadUnavailable",
                    InvalidDataException => "InvalidData",
                    CryptographicException => "Cryptographic",
                    IOException => "Io",
                    OperationCanceledException => "Canceled",
                    _ => "Other"
                };
                throw;
            }
            using var response = delivered;
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            if (envelope.Request.Operation == WorldPeerOperations.Catalog && Catalog is not null)
                bytes = JsonSerializer.SerializeToUtf8Bytes(Catalog, Json);
            if (ownSeed)
            {
                var result = WorldPeerProtocol.DecodeEnvelope<WorldPeerSeedResponse>(bytes,
                    WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Seed));
                if (PauseLatest) bytes = JsonSerializer.SerializeToUtf8Bytes(new WorldPeerSeedResponse("SeedPaused", latest.Version.VersionHash), Json);
                else if (ReceiptOverride != ReceiptMode.Exact && result is { Code: "SeedKnown" or "SeedComplete", Receipt: not null })
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(result with
                    { Code = "SeedComplete", Receipt = ReceiptOverride == ReceiptMode.Forwarded ? forwarded : otherReceipt }, Json);
                    ChangedReceipts++;
                }
            }
            return new(response.StatusCode) { Content = new ByteArrayContent(bytes) };
        }
        protected override void Dispose(bool disposing) { if (disposing) target.Dispose(); base.Dispose(disposing); }
    }
}
