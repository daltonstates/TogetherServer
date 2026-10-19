namespace TogetherServer;

// Display-only summaries. They deliberately contain no address, credential,
// game password, file path, action permission, or lifecycle authority.
public sealed record ServerDirectoryServer(Guid Id, string Name, string Kind, string State,
    int? OnlinePlayers, int? MaxPlayers, bool PlayerCountTrusted,
    string? PlayerObservationSource, DateTimeOffset? PlayerCountObservedUtc,
    bool PlayerCountRefreshing, int PlayerCountFailureCount,
    Guid? SeparateCopySourceProfileId = null, string? SourceName = null, string? SourceHostName = null);

public sealed record ServerDirectoryHost(Guid ConnectionId, Guid HostId, string ConnectionName,
    string? HostProfileName, string State, string? ConnectionCode, DateTimeOffset? LastConnectedUtc,
    IReadOnlyList<ServerDirectoryServer> Servers);

public sealed record ServerDirectoryView(IReadOnlyList<ServerDirectoryServer> LocalServers,
    IReadOnlyList<ServerDirectoryHost> FriendHosts);

internal static class ServerDirectory
{
    internal const int MaximumEntries = 100;
    internal static readonly TimeSpan MaximumObservationAge = TimeSpan.FromSeconds(10);

    internal static bool Fresh(DateTimeOffset? observed, DateTimeOffset now) =>
        observed is { } time && time <= now && now - time <= MaximumObservationAge;

    internal static IReadOnlyList<ServerDirectoryServer> LocalRows(HostSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Settings.Profiles.Take(MaximumEntries).Select(profile =>
        {
            var run = snapshot.Runs.FirstOrDefault(item => item.ProfileId == profile.Id);
            var trusted = run is { State: "Ready", PlayerCountTrusted: true, OnlinePlayers: >= 0 } &&
                Fresh(run.PlayerCountObservedUtc, now);
            var state = run?.State ?? "Unknown";
            if (state == "Ready" && !Fresh(run?.PlayerCountObservedUtc, now)) state = "Unknown";
            return new ServerDirectoryServer(profile.Id, profile.Name, profile.Kind, state,
                trusted ? run!.OnlinePlayers : null, trusted ? run!.MaxPlayers : null, trusted,
                run?.PlayerObservationSource, run?.PlayerCountObservedUtc,
                run?.PlayerCountRefreshing == true, run?.PlayerCountFailureCount ?? 0,
                profile.SeparateCopySourceProfileId);
        }).ToArray();

    internal static ServerDirectoryView Project(IReadOnlyList<ServerDirectoryServer> local,
        IReadOnlyList<ServerDirectoryHost> hosts)
    {
        var visibleHosts = hosts.Where(host => host.State != "Revoked" && host.ConnectionCode != "Revoked").ToArray();
        return new(local.Take(MaximumEntries).Select(server =>
        {
            if (server.SeparateCopySourceProfileId is not { } sourceId) return server;
            var source = visibleHosts.SelectMany(host => host.Servers
                .Where(profile => profile.Id == sourceId).Select(profile => (Host: host, Server: profile)))
                .FirstOrDefault();
            var localSource = local.FirstOrDefault(profile => profile.Id == sourceId &&
                profile.SeparateCopySourceProfileId is null);
            return server with
            {
                SourceName = source.Server?.Name ?? localSource?.Name,
                SourceHostName = source.Host?.HostProfileName ?? source.Host?.ConnectionName
            };
        }).ToArray(), hosts.Take(MaximumEntries).ToArray());
    }
}

public sealed partial class HostManager
{
    // SnapshotAsync calls Identity and constructs diagnostics. This list reads
    // only the already published supervisor snapshot and never probes a process,
    // reads the backup catalog, or advances lifecycle/countdown state.
    internal IReadOnlyList<ServerDirectoryServer> ServerDirectoryRows()
    {
        var snapshot = Volatile.Read(ref lastCompanionSnapshot)!;
        return ServerDirectory.LocalRows(snapshot, clock.GetUtcNow());
    }
}
