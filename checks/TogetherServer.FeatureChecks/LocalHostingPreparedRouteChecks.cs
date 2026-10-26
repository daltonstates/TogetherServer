using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class LocalHostingChecks
{
    private static async Task PreparedRouteWarningsAsync(string root)
    {
        foreach (var route in new[] { "listener-off", "loopback", "outside-unreachable", "checker-unavailable" })
            await PreparedRouteWarningAsync(root, route, null);
        foreach (var fault in new[] { "grant", "revoked", "expired", "consent", "late-revoked", "cancel",
            "PeerMembershipRequired", "HostingPermissionChanged" })
            await PreparedRouteWarningAsync(root, "listener-off", fault);
    }

    private static async Task PreparedRouteWarningAsync(string root, string route, string? fault)
    {
        using var fixture = new Fixture(root, granted: true, automaticReceiving: true, initialVersion: 3);
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared.ReadyForManualStart && prepared.LocalProfileId is not null && prepared.BranchHash is not null,
            "the advisory-route fixture did not prepare its exact local save");
        var publication = fixture.PublishStoppedContinuation(prepared);
        using var peers = fixture.PreparedPublicationPeers();
        await peers.RefreshMembershipsAsync(false, default);
        fixture.PublishPreparedClaim(publication.Branch, publication.Version);
        fixture.Manager.SharedHostingObservation = peers.LatestObservationHash;
        using var canceled = new CancellationTokenSource();
        var snapshot = new LocalHostingRouteSnapshot($"https://8.8.8.8:{fixture.ControlPort}", fixture.ControlPort,
            "0.0.0.0", true, true, CompanionListenerStates.Listening, new string('A', 64), true);
        if (route == "listener-off") snapshot = snapshot with { Enabled = false, Active = false };
        else if (route == "loopback") snapshot = snapshot with
        {
            Endpoint = $"https://127.0.0.1:{fixture.ControlPort}",
            BindAddress = "127.0.0.1"
        };
        var probes = 0;
        var checker = new LocalHostingRouteCheck(_ => Task.FromResult(snapshot), (endpoint, port, _) =>
        {
            probes++;
            if (route == "checker-unavailable") throw new HttpRequestException("Generated optional checker failure.");
            return Task.FromResult(new ExternalPortProbeResult("Not reachable", "Generated TCP observation only.",
                port, DateTimeOffset.UtcNow, endpoint));
        });
        var evidence = await checker.CheckAsync();
        Require(evidence.State == (route == "checker-unavailable" ? "Unknown" : "ActionRequired"),
            "the advisory-route fixture did not retain its honest route warning");
        var expectedProbes = probes;
        var announcements = 0;
        var resolutions = 0;
        var service = fixture.WithPreparedRouteSource(checker, async (world, localId, branch, token, progress) =>
        {
            resolutions++;
            return await peers.ResolveLatestPreparedStartSourceAsync(world, localId, branch, token, progress);
        }, async (world, token) =>
        {
            announcements++;
            if (fault is "PeerMembershipRequired" or "HostingPermissionChanged")
                return new(false, fault, "Generated signed-membership refusal.");
            var result = await peers.AnnounceLocalHostingAsync(world, token);
            Require(!result.Ok && result.Code == "RecipientConnectionRequired",
                "the real local announcement did not preserve this PC's disabled incoming policy");
            if (fault == "late-revoked") await fixture.ChangePreparedGrantAsync("revoked");
            if (fault == "cancel") canceled.Cancel();
            return result;
        });
        var status = FriendLocalHostingService.WithRemedy(await service.StatusAsync(fixture.ProfileId));
        Require(status.Ok && status.ReadyForManualStart && status.Code == "LocalHostingPrepared" &&
            status.Remedy is null && status.Steps.Take(4).All(step => step.State == "Ready") &&
            status.Steps[4] == evidence.Step && status.LocalProfileId == prepared.LocalProfileId &&
            status.BranchHash == prepared.BranchHash && probes == expectedProbes && announcements == 0 && resolutions == 0,
            "an ordinary prepared local Start was blocked by advisory route evidence: " + route);
        foreach (var historical in new[] { "Declined", "Canceled", "Failed" })
            Require(FriendLocalHostingService.WithRemedy(status with
            {
                HandoffRequest = new(false, "HistoricalMove", "Generated historical request.",
                    RequestId: Guid.NewGuid(), RecipientState: historical)
            }).Remedy is null, "a historical hosting request made an ordinary route warning blocking");
        foreach (var active in new[] { "Requested", "Accepted", "Prepared", "Completed" })
            Require(FriendLocalHostingService.WithRemedy(status with
            {
                HandoffRequest = new(false, "ActiveMove", "Generated active request.",
                    RequestId: Guid.NewGuid(), RecipientState: active),
                Steps = [.. status.Steps.Take(4), new("connection", "ActionRequired", "A planned move requires its current route.")]
            }).Remedy is { Kind: "Connection" or "Respond" },
                "an active planned move lost its deliberate connection requirement");
        Require(FriendLocalHostingService.WithRemedy(status with { Code = "RecipientConnectionRequired" }).Remedy?.Kind == "Connection",
            "an explicit route refusal lost its typed connection remedy");

        if (fault is "grant" or "revoked" or "expired") await fixture.ChangePreparedGrantAsync(fault);
        else if (fault == "consent")
            Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.PublicationRoot, fixture.DeviceId, false),
                "the advisory-route fixture could not withdraw local save consent");
        var heldAtDriver = false;
        fixture.Driver.BeforeProcessAttempt = () =>
        {
            try
            {
                using var write = new FileStream(Path.Combine(publication.Vault, publication.Version.VersionHash,
                    SharedWorldService.PayloadDirectory, "world.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { heldAtDriver = true; }
        };
        LocalHostingActionResult? result = null;
        var reachedDriver = false;
        var canceledStart = false;
        try
        {
            result = await service.StartPreparedAsync(fixture.ProfileId,
                new(prepared.LocalProfileId.Value, prepared.BranchHash, null, true), canceled.Token);
        }
        catch (InvalidOperationException error) when (error.Message == "No process operation is permitted.")
        { reachedDriver = true; }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { canceledStart = true; }
        Require(!fixture.Data.LoadSettings().CompanionListeningEnabled && !fixture.Data.LoadSettings().RemoteControlsEnabled &&
            fixture.Data.LoadSettings().CompanionBindAddress == "127.0.0.1" &&
            fixture.Data.LoadSettings().CompanionEndpoint == $"https://127.0.0.1:{fixture.ControlPort}" && fixture.Data.LoadRuns().Count == 0 &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 1 &&
            !Directory.Exists(Path.Combine(fixture.Data.RootPath, "world-peer-recovery-sources")) && probes == expectedProbes,
            "advisory-route Start changed listener policy, histories, processes, or repeated the outside checker");
        if (fault is null)
            Require(reachedDriver && heldAtDriver && fixture.Driver.ProcessAttempts == 1 && announcements == 1 && resolutions == 1,
                "an ordinary route warning did not reach only the refusing driver with an exact held source: " + route + ": " + result?.Code);
        else
        {
            Require(!reachedDriver && !heldAtDriver && fixture.Driver.ProcessAttempts == 0,
                "advisory routing bypassed current signed permission, consent, or cancellation: " + fault);
            if (fault == "cancel") Require(canceledStart, "advisory routing swallowed Start cancellation");
            else Require(result is { Ok: false }, "advisory routing lost its current-permission refusal: " + fault);
            if (fault is "PeerMembershipRequired" or "HostingPermissionChanged")
                Require(result?.Code == fault && announcements == 1 && resolutions == 0,
                    "a signed announcement refusal was treated as an advisory route warning");
        }
        using var released = new FileStream(Path.Combine(publication.Vault, publication.Version.VersionHash,
            SharedWorldService.PayloadDirectory, "world.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Require(released.CanWrite, "advisory-route Start leaked its prepared-source lease");
    }

    private sealed partial class Fixture
    {
        internal FriendLocalHostingService WithPreparedRouteSource(LocalHostingRouteCheck checker,
            Func<SharedWorldPeerRoot, Guid, string, CancellationToken, Action<WorldPeerSourceProgress>?, Task<WorldPeerPreparedStartResult>> source,
            Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerActionResult>> announce) =>
            new(Data, friends, Manager, _ => [Path.Combine(Data.RootPath, "never-run.synthetic")], routeCheck: checker,
                announceHosting: announce, preparedStartSource: source);
    }
}
