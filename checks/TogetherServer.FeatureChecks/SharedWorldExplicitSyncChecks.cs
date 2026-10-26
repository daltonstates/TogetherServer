using System.Reflection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class SharedRosterRejoinChecks
{
    // A signed policy edit after an old background exchange must be observed
    // by the explicit world check. Fake HTTPS and disposable protected state only.
    internal static async Task ExplicitSyncChecksAsync(string parent)
    {
        using var host = new Host(parent);
        using var data = new LocalData(Path.Combine(parent, "explicit-world-sync-" + Guid.NewGuid().ToString("N")));
        HttpClient Client(string endpoint, IEnumerable<string> pins) => CreateClient(host, endpoint, pins);
        using var friends = new FriendService(data, Client);
        Require((await friends.PairAsync(host.ServerCode)).Ok &&
            (await friends.PollAsync()).Profiles.Single().Id == host.Profile.Id &&
            (await friends.CheckSharedWorldSharingAsync(host.Profile.Id)).Available,
            "the explicit-sync fixture could not verify initial hosting permission");
        var initial = host.Current;
        var root = new SharedWorldPeerRoot(initial.OwnerPublicKey, initial.ProfileId, initial.GroupId);
        host.Chain.Append(initial, initial.OwnerPublicKey);
        host.EnablePeerMetadata();
        using var peers = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data), Client);
        await peers.RefreshMembershipsAsync(false, default);
        Require(peers.Views().Single() is { CanHost: true, CanReceive: true, CanReshare: true },
            "initial signed permission was unavailable before the explicit-sync race");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        host.AfterPeerExchange = async exchange =>
        {
            if (exchange.Request.Operation != WorldPeerOperations.Catalog) return;
            entered.TrySetResult(true);
            await release.Task.WaitAsync(stopping.Token);
        };
        var background = peers.SyncNowAsync(stopping.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var registration = (Task?)(typeof(WorldPeerService).GetField("membershipRefresh",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(peers));
            Require(registration is not null, "the coalesced registration check was not started");
            await registration!.WaitAsync(TimeSpan.FromSeconds(5));
            host.DisableHosting();
            Require(host.Current.Members.All(member => member.Grants is { Receive: false, EligibleHost: false, Reshare: false }) &&
                peers.Views().Single().CanReceive,
                "the signed disable was not newer than the deliberately held cached exchange");
            var check = peers.SyncWorldNowAsync(root.GroupKey, stopping.Token);
            Require(!check.IsCompleted, "the explicit world check skipped its active transfer flight");
            release.TrySetResult(true);
            var result = await check.WaitAsync(TimeSpan.FromSeconds(10));
            var current = peers.Views().Single();
            Require(result.Ok && result.Code == "WorldPeersChecked" &&
                !current.CanHost && !current.CanReceive && !current.CanReshare &&
                host.PeerOperations.Count(item => item.Operation == WorldPeerOperations.Catalog) == 1 &&
                new SharedWorldRosterChainStore(data).Contains(host.Profile.Id, initial) &&
                new SharedWorldRosterChainStore(data).Contains(host.Profile.Id, host.Current) &&
                !data.LoadSettings().CompanionListeningEnabled && data.LoadRuns().Count == 0,
                "the explicit world check returned stale signed rights, repeated payload work, or enabled a listener/run");
            host.PeerOffline = true;
            var unavailable = await peers.SyncWorldNowAsync(root.GroupKey, stopping.Token);
            Require(!unavailable.Ok && unavailable.Code == "WorldPeerSyncInconclusive" &&
                !peers.Views().Single().CanHost && !peers.Views().Single().CanReceive,
                "an unavailable peer was presented as fresh permission or restored disabled rights");
        }
        finally
        {
            release.TrySetResult(true);
            await background;
            await peers.WaitForSyncAsync();
        }
    }
}
