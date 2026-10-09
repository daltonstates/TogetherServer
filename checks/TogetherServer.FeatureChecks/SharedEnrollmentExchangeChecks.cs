using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Actual FriendLink calls over injected HTTP. No process, listener or network.
internal static class SharedEnrollmentExchangeChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
    private const string ClientHeader = "X-Synthetic-Enrollment-Client";
    private enum Operation { Check, Pull, Sharing, Review }
    private enum Fault { None, ChallengeTransport, ProofDenied }

    public static async Task RunAsync(string root)
    {
        foreach (var other in new[] { Operation.Pull, Operation.Sharing, Operation.Review })
        {
            await using var fixture = await Fixture.CreateAsync(root);
            fixture.Host.HoldChallenge = true;
            var check = fixture.Start(Operation.Check);
            await fixture.Host.ChallengeEntered.Task.WaitAsync(Watchdog);
            var competing = fixture.Start(other);
            Require(fixture.Host.Challenges == 1 && !competing.IsCompleted,
                $"{other} issued a challenge inside another Check's enrollment exchange");
            fixture.Host.ReleaseChallenge.TrySetResult(true);
            var results = await Task.WhenAll(check, competing).WaitAsync(Watchdog);
            // Review's second roster GET maps absent verified membership to RosterRejected.
            var expectedOther = other == Operation.Review ? "RosterRejected" : "RosterUnavailable";
            Require(results[0] == "RosterUnavailable" && results[1] == expectedOther,
                $"post-enrollment result: Check={results[0]}, {other}={results[1]}; expected Check=RosterUnavailable, {other}={expectedOther}");
            fixture.Host.AssertCompleted(2);
        }

        await CheckAutomaticPullAsync(root);
        await CheckProofAndVerificationBoundaryAsync(root);
        await CheckWaitingCancellationAsync(root);
        foreach (var operation in Enum.GetValues<Operation>())
            await CheckHolderCancellationAsync(root, operation);
        foreach (var operation in new[] { Operation.Check, Operation.Sharing, Operation.Review })
            foreach (var fault in new[] { Fault.ChallengeTransport, Fault.ProofDenied })
                await CheckFailureReleaseAsync(root, operation, fault);
    }

    private static async Task CheckAutomaticPullAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        fixture.Host.HoldChallenge = true;
        var automatic = fixture.StartAutomaticPull();
        await fixture.Host.ChallengeEntered.Task.WaitAsync(Watchdog);
        var check = fixture.Start(Operation.Check);
        var sharing = fixture.Start(Operation.Sharing);
        Require(fixture.Host.Challenges == 1 && !check.IsCompleted && !sharing.IsCompleted,
            "manual Check/Sharing interleaved enrollment with automatic Pull");
        fixture.Host.ReleaseChallenge.TrySetResult(true);
        await Task.WhenAll(automatic, check, sharing).WaitAsync(Watchdog);
        Require(check.Result == "RosterUnavailable" && sharing.Result == "RosterUnavailable",
            "automatic enrollment changed manual result mapping");
        fixture.Host.AssertCompleted(3);
    }

    private static async Task CheckProofAndVerificationBoundaryAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        fixture.Host.HoldProof = fixture.Host.HoldCurrentRoster = true;
        var check = fixture.Start(Operation.Check);
        await fixture.Host.ProofEntered.Task.WaitAsync(Watchdog);
        var sharing = fixture.Start(Operation.Sharing);
        Require(fixture.Host.Challenges == 1 && !sharing.IsCompleted,
            "enrollment gate ended before the proof POST response");
        fixture.Host.ReleaseProof.TrySetResult(true);
        await fixture.Host.CurrentRosterEntered.Task.WaitAsync(Watchdog);
        await fixture.Host.SecondChallengeEntered.Task.WaitAsync(Watchdog);
        Require(await sharing.WaitAsync(Watchdog) == "RosterUnavailable" && !check.IsCompleted,
            "enrollment remained locked during later roster verification/main-gate work");
        fixture.Host.ReleaseCurrentRoster.TrySetResult(true);
        Require(await check.WaitAsync(Watchdog) == "RosterUnavailable", "Check changed its roster result");
        fixture.Host.AssertCompleted(2);
    }

    private static async Task CheckWaitingCancellationAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        fixture.Host.HoldChallenge = true;
        var first = fixture.Start(Operation.Check);
        await fixture.Host.ChallengeEntered.Task.WaitAsync(Watchdog);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var waiting = fixture.Start(Operation.Check, cancelled.Token);
        cancelled.Cancel();
        await RequireCancelledAsync(waiting);
        var sharing = fixture.Start(Operation.Sharing);
        Require(fixture.Host.Challenges == 1 && !first.IsCompleted && !sharing.IsCompleted,
            "a cancelled enrollment waiter released another caller's exchange");
        fixture.Host.ReleaseChallenge.TrySetResult(true);
        Require((await Task.WhenAll(first, sharing).WaitAsync(Watchdog)).All(code => code == "RosterUnavailable"),
            "waiting cancellation blocked later enrollment");
        fixture.Host.AssertCompleted(2);
    }

    private static async Task CheckHolderCancellationAsync(string root, Operation operation)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        fixture.Host.HoldChallenge = true;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var holder = fixture.Start(operation, cancelled.Token);
        await fixture.Host.ChallengeEntered.Task.WaitAsync(Watchdog);
        cancelled.Cancel();
        // Review historically maps TaskCanceledException to HistoryRejected.
        if (operation == Operation.Review)
            Require(await holder.WaitAsync(Watchdog) == "HistoryRejected", "Review changed its cancellation result");
        else await RequireCancelledAsync(holder);
        Require(await fixture.Start(Operation.Check).WaitAsync(Watchdog) == "RosterUnavailable",
            $"cancelled {operation} retained the enrollment gate");
        fixture.Host.AssertCompleted(2, completedProofs: 1);
    }

    private static async Task CheckFailureReleaseAsync(string root, Operation operation, Fault fault)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        fixture.Host.FirstFault = fault;
        var expected = fault == Fault.ProofDenied
            ? operation == Operation.Review ? "EnrollmentDenied" : "KeyReviewRequired"
            : operation == Operation.Check ? "CheckFailed" : operation == Operation.Sharing ? "RosterUnavailable" : "HistoryRejected";
        Require(await fixture.Start(operation).WaitAsync(Watchdog) == expected,
            $"{operation} changed its {fault} result mapping");
        Require(await fixture.Start(Operation.Check).WaitAsync(Watchdog) == "RosterUnavailable",
            $"{operation} failed to release enrollment after {fault}");
        fixture.Host.AssertCompleted(2, completedProofs: fault == Fault.ProofDenied ? 2 : 1);
    }

    private static async Task RequireCancelledAsync(Task task)
    {
        try { await task.WaitAsync(Watchdog); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("a cancelled enrollment call still completed");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LocalData data;
        private readonly FriendLink link;
        private readonly CancellationTokenSource lifetime = new();
        private readonly List<Task> operations = [];
        private readonly Guid profileId = Guid.NewGuid();
        private int clientOrdinal;
        public FakeHost Host { get; }
        public CancellationToken Token => lifetime.Token;

        private Fixture(string root)
        {
            data = new LocalData(Path.Combine(root, "enrollment-exchange-" + Guid.NewGuid().ToString("N")));
            const string file = "enrollment-friend.protected";
            var configuration = new FriendConfiguration
            {
                HostId = Guid.NewGuid(),
                DeviceId = Guid.NewGuid(),
                Credential = new string('E', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
                Endpoint = "https://127.0.0.1:5131",
                Fingerprint = new string('A', 64),
                ConsentedSharedWorldProfiles = [profileId],
                CachedProfiles = [new(profileId, "Synthetic enrollment server", "Offline", null, Kind: GameKinds.Valheim)]
            };
            data.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
            Host = new FakeHost(profileId, configuration.DeviceId, configuration.Credential);
            link = new FriendLink(data, file, (endpoint, pins) =>
            {
                Require(endpoint == configuration.Endpoint && pins.Contains(configuration.Fingerprint),
                    "enrollment escaped its configured pinned transport");
                var client = new HttpClient(Host, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
                client.DefaultRequestHeaders.Add(ClientHeader, Interlocked.Increment(ref clientOrdinal)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
                return client;
            });
        }

        public static async Task<Fixture> CreateAsync(string root)
        {
            var fixture = new Fixture(root);
            try
            {
                var view = await fixture.link.PollAsync();
                Require(view.Profiles.Single().Id == fixture.profileId &&
                    view.HostCapabilities.Contains(CompanionProtocol.SharedWorldsCapability),
                    "fake heartbeat did not establish the assigned shared-world profile");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public Task<string> Start(Operation operation, CancellationToken? cancellationToken = null)
        {
            var task = InvokeAsync(operation, cancellationToken ?? Token);
            operations.Add(task);
            return task;
        }

        private async Task<string> InvokeAsync(Operation operation, CancellationToken cancellationToken) => operation switch
        {
            Operation.Check => (await link.CheckSharedWorldAsync(profileId, cancellationToken)).Code,
            Operation.Pull => (await link.PullSharedWorldAsync(profileId, cancellationToken)).Code,
            Operation.Sharing => (await link.CheckSharedWorldSharingAsync(profileId, cancellationToken)).Code,
            Operation.Review => (await link.ReviewSharedHistoryAsync(profileId, cancellationToken: cancellationToken)).Code,
            _ => throw new InvalidOperationException("Unknown synthetic enrollment operation.")
        };

        public Task StartAutomaticPull()
        {
            link.ScheduleSharedCatchUp(Token);
            var task = link.ScheduledSharedCatchUp();
            operations.Add(task);
            return task;
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            Host.ReleaseChallenge.TrySetResult(true);
            Host.ReleaseProof.TrySetResult(true);
            Host.ReleaseCurrentRoster.TrySetResult(true);
            try { await Task.WhenAll(operations).WaitAsync(Watchdog); }
            catch (OperationCanceledException) { }
            finally { link.Dispose(); Host.Dispose(); data.Dispose(); lifetime.Dispose(); }
        }
    }

    private sealed class FakeHost(Guid profileId, Guid deviceId, string credential) : HttpMessageHandler
    {
        private readonly object sync = new();
        private string? latestNonce, firstProofClient;
        private int challenges, proofs;
        private bool exchangeActive, overlapped;
        public bool HoldChallenge { get; set; }
        public bool HoldProof { get; set; }
        public bool HoldCurrentRoster { get; set; }
        public Fault FirstFault { get; set; }
        public int Challenges { get { lock (sync) return challenges; } }
        public TaskCompletionSource<bool> ChallengeEntered { get; } = Barrier();
        public TaskCompletionSource<bool> SecondChallengeEntered { get; } = Barrier();
        public TaskCompletionSource<bool> ReleaseChallenge { get; } = Barrier();
        public TaskCompletionSource<bool> ProofEntered { get; } = Barrier();
        public TaskCompletionSource<bool> ReleaseProof { get; } = Barrier();
        public TaskCompletionSource<bool> CurrentRosterEntered { get; } = Barrier();
        public TaskCompletionSource<bool> ReleaseCurrentRoster { get; } = Barrier();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.Authorization;
            Require(authorization?.Scheme == "Bearer" && authorization.Parameter == credential &&
                request.Headers.TryGetValues("X-Device-Id", out var devices) && devices.Single() == deviceId.ToString(),
                "enrollment lost the actual saved device authentication");
            var requestClient = request.Headers.GetValues(ClientHeader).Single();
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/companion/heartbeat" && request.Method == HttpMethod.Post)
                return Reply(new CompanionStatus(false, null,
                    [new(profileId, "Synthetic enrollment server", "Offline", null, Kind: GameKinds.Valheim)],
                    false, false, DateTimeOffset.UtcNow,
                    new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum, [CompanionProtocol.SharedWorldsCapability])));
            var prefix = $"/api/companion/servers/{profileId}/shared-world";
            if (path == prefix + "/enrollment" && request.Method == HttpMethod.Get)
            {
                int number;
                string nonce;
                lock (sync)
                {
                    overlapped |= exchangeActive;
                    exchangeActive = true;
                    number = ++challenges;
                    latestNonce = nonce = Convert.ToBase64String(SHA256.HashData(BitConverter.GetBytes(number)));
                }
                if (number == 1) ChallengeEntered.TrySetResult(true);
                if (number == 2) SecondChallengeEntered.TrySetResult(true);
                try
                {
                    if (number == 1 && FirstFault == Fault.ChallengeTransport)
                        throw new HttpRequestException("Synthetic challenge transport failure.");
                    if (number == 1 && HoldChallenge) await ReleaseChallenge.Task.WaitAsync(cancellationToken);
                    return Reply(new SharedWorldEnrollmentChallenge(nonce));
                }
                catch
                {
                    lock (sync) if (latestNonce == nonce) exchangeActive = false;
                    throw;
                }
            }
            if (path == prefix + "/enrollment" && request.Method == HttpMethod.Post)
            {
                var proof = JsonSerializer.Deserialize<SharedWorldEnrollmentRequest>(
                    await request.Content!.ReadAsByteArrayAsync(cancellationToken), Json);
                int number;
                lock (sync)
                {
                    Require(proof is not null && exchangeActive && proof.Nonce == latestNonce &&
                        SharedWorldRosterTrust.VerifyEnrollment(deviceId, proof),
                        "a legitimate enrollment proof was invalidated or lost its device signature");
                    number = ++proofs;
                    if (number == 1) firstProofClient = requestClient;
                }
                if (number == 1) ProofEntered.TrySetResult(true);
                try
                {
                    if (number == 1 && HoldProof) await ReleaseProof.Task.WaitAsync(cancellationToken);
                    return Reply(new { ok = true }, number == 1 && FirstFault == Fault.ProofDenied
                        ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
                }
                finally { lock (sync) exchangeActive = false; }
            }
            if (request.Method == HttpMethod.Get && path == prefix + "/roster/revisions/current")
            {
                // Hold the first proof's own Check client, regardless of whether
                // Sharing resumes on another thread before Check fetches its roster.
                if (HoldCurrentRoster && requestClient == firstProofClient)
                {
                    CurrentRosterEntered.TrySetResult(true);
                    await ReleaseCurrentRoster.Task.WaitAsync(cancellationToken);
                }
                return Reply(new { code = "RosterUnavailable" }, HttpStatusCode.Forbidden);
            }
            if (request.Method == HttpMethod.Get && path == prefix + "/roster")
                return Reply(new { code = "RosterUnavailable" }, HttpStatusCode.Forbidden);
            throw new InvalidOperationException("Enrollment check attempted an unexpected HTTP route.");
        }

        public void AssertCompleted(int issuedChallenges, int? completedProofs = null)
        {
            lock (sync) Require(!overlapped && !exchangeActive && challenges == issuedChallenges &&
                proofs == (completedProofs ?? issuedChallenges), "challenge/proof exchanges interleaved or failed to release");
        }
    }

    private static TaskCompletionSource<bool> Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new(status) { Content = content };
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
