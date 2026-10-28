using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class LocalHostingChecks
{
    internal static async Task PreparedStartGateAsync(string root)
    {
        foreach (var change in new[] { "none", "selection", "mode", "unavailable", "late-selection", "late-unavailable", "late-transfer" })
            await PreparedStartGateAsync(root, change);
    }

    private static async Task PreparedStartGateAsync(string root, string change)
    {
        using var fixture = new Fixture(root, granted: true, alternateConnection: true);
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared is { ReadyForManualStart: true, LocalProfileId: not null, BranchHash: not null },
            "The prepared Start gate regression did not prepare its signed local world.");
        var stamp = new string('D', 64);
        var latest = fixture.CompleteLatestObservation(prepared.BranchHash!, stamp);
        fixture.Manager.SharedHostingObservation = _ => stamp;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = 0;
        var service = fixture.WithStartAdapters((_, _) => Task.FromResult(new WorldPeerActionResult(true,
            "HostingAddressPrepared", "Generated approved peer announcement.")), async (_, token) =>
        {
            Interlocked.Increment(ref resolutions);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return latest;
        });
        using var modeGate = new SemaphoreSlim(1, 1);
        var friendMode = true;
        var unavailable = false;
        if (change.StartsWith("late-", StringComparison.Ordinal))
            fixture.Driver.DuringPrepareForChecks = () =>
            {
                if (change == "late-selection") fixture.SelectOtherStartConnection();
                else if (change == "late-unavailable") unavailable = true;
                else fixture.ReportPeerTransfer("Receipt");
            };
        var body = JsonSerializer.SerializeToUtf8Bytes(new LocalHostingStartRequest(
            prepared.LocalProfileId!.Value, prepared.BranchHash, null, true), Json);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        var pending = FriendLocalHostingRoutes.StartPreparedAsync(context, fixture.ProfileId, service,
            fixture.StartConnections, modeGate, () => friendMode, () => unavailable);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!pending.IsCompleted && resolutions == 1 && fixture.Driver.ProcessAttempts == 0,
                "The prepared Start did not hold its real latest-source work before launch.");
            Require(await modeGate.WaitAsync(TimeSpan.FromSeconds(2)),
                "A Friend prepared Start held the app mode gate through latest metadata or file work.");
            try
            {
                // A real safe manager control runs while signed-source work is
                // held. No run or process exists for this unrelated ID.
                await fixture.Manager.StopAsync(Guid.NewGuid());
                if (change == "selection") fixture.SelectOtherStartConnection();
                else if (change == "mode") friendMode = false;
                else if (change == "unavailable") unavailable = true;
            }
            finally { modeGate.Release(); }
            Require(!pending.IsCompleted, "An unrelated control finished the held prepared Start.");
            release.TrySetResult();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            var result = (response as IValueHttpResult)?.Value as LocalHostingActionResult;
            var expected = change switch
            {
                "none" => "LaunchFailed",
                "selection" => "ConnectionChanged",
                "mode" or "unavailable" => "LocalHostingAuthorizationChanged",
                _ => "RemoteStartAuthorizationChanged"
            };
            Require(result is { Ok: false } && result.Code == expected && resolutions == 1 &&
                fixture.Driver.ProcessAttempts == (change == "none" ? 1 : 0) && fixture.Data.LoadRuns().Count == 0,
                $"Prepared Start lost its retained connection or final live mode/availability/transfer guard: {change}; {result?.Code}.");
            Require(fixture.Data.LoadSettings().Profiles.Count == 1 &&
                new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 1,
                "A prepared Start changed its branch or created another local profile.");
        }
        finally
        {
            release.TrySetResult();
            await pending;
            fixture.ClearPeerTransfer();
        }
    }

    private sealed partial class Fixture
    {
        internal FriendService StartConnections => friends;
        internal void SelectOtherStartConnection()
        {
            Require(alternateConnectionId is not null && friends.Select(alternateConnectionId.Value).Ok,
                "The prepared Start regression could not select its other saved connection.");
        }
    }
}
