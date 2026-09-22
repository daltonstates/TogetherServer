namespace TogetherServer;

// Friend Stop is allowed only when the selected built-in game driver returns a
// fresh, authoritative zero-player count. The count is checked once for the UI
// decision and again inside HostManager's lifecycle gate immediately before the
// graceful stop signal. Unknown fails closed; local Host Stop is independent.
public static class RemoteStopSafety
{
    public static StopPermit TryAcquire(HostSnapshot snapshot, Guid profileId, LocalData data,
        GameServerRegistry games)
    {
        var profile = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId);
        if (profile is null) return StopPermit.Denied("InvalidProfile", "The saved server is unavailable.");
        var view = snapshot.Runs.SingleOrDefault(item => item.ProfileId == profileId);
        if (view?.State != "Ready")
            return StopPermit.Denied("ServerNotReady", "Remote Stop needs a running, ready server.");
        if (view.OnlinePlayers is null)
            return StopPermit.Denied("PlayerCountUnknown",
                "The server did not report a current online-player count. Remote Stop is blocked; the Host can stop it locally.");
        if (view.OnlinePlayers > 0)
            return StopPermit.Denied("PlayersOnline",
                $"Remote Stop is blocked while {view.OnlinePlayers} {PlayerWord(view.OnlinePlayers.Value)} online. The Host can stop it locally.");

        var recorded = data.LoadRuns().SingleOrDefault(item => item.ProfileId == profileId);
        if (recorded is null || recorded.OperationId == Guid.Empty || recorded.Kind != profile.Kind ||
            !games.TryGet(recorded.Kind, out var driver))
            return StopPermit.Denied("PlayerCountUnknown",
                "The managed server identity or player-count driver is unavailable. Remote Stop is blocked.");

        return StopPermit.AllowedFor(recorded.OperationId, driver,
            "Server reports 0 players online. TogetherServer will check again immediately before Stop.");
    }

    private static string PlayerWord(int count) => count == 1 ? "player is" : "players are";
}

public sealed class StopPermit : IDisposable
{
    private readonly Guid operationId;
    private readonly IGameServerDriver? driver;

    private StopPermit(bool allowed, string code, string reason, Guid operationId = default,
        IGameServerDriver? driver = null)
    {
        Allowed = allowed;
        Code = code;
        Reason = reason;
        this.operationId = operationId;
        this.driver = driver;
    }

    public bool Allowed { get; }
    public string Code { get; }
    public string Reason { get; }

    internal static StopPermit AllowedFor(Guid operationId, IGameServerDriver driver, string reason) =>
        new(true, "NoPlayersOnline", reason, operationId, driver);

    public static StopPermit Denied(string code, string reason) => new(false, code, reason);

    public bool StillSafe(ManagedRun run) => Allowed && run.OperationId == operationId && driver is not null &&
        driver.Health(run) is { Ok: true, State: "Ready", OnlinePlayers: 0 };

    public void Dispose() { }
}
