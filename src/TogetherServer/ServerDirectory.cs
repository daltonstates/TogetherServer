namespace TogetherServer;

// Display-only summaries. They deliberately contain no address, credential,
// game password, file path, action permission, or lifecycle authority.
public sealed record ServerDirectoryServer(Guid Id, string Name, string Kind, string State,
    int? OnlinePlayers, int? MaxPlayers, bool PlayerCountTrusted,
    string? PlayerObservationSource, DateTimeOffset? PlayerCountObservedUtc,
    bool PlayerCountRefreshing, int PlayerCountFailureCount,
    Guid? SeparateCopySourceProfileId = null, string? SourceName = null, string? SourceHostName = null,
    string? SharedWorldId = null, Guid? OriginalProfileId = null, bool IsSuccessor = false);

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

    internal static ServerDirectoryServer WithWorld(ServerDirectoryServer row, SharedWorldRoster roster,
        bool successor)
    {
        if (!SharedWorldRosterTrust.Verify(roster)) return row;
        var root = new SharedWorldPeerRoot(roster.OwnerPublicKey, roster.ProfileId, roster.GroupId);
        return row with { SharedWorldId = root.GroupKey, OriginalProfileId = root.ProfileId, IsSuccessor = successor };
    }

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
        ServerDirectoryServer Origin(ServerDirectoryServer server)
        {
            var sourceId = server.OriginalProfileId ?? server.SeparateCopySourceProfileId;
            if (sourceId is null || !server.IsSuccessor && server.SeparateCopySourceProfileId is null) return server;
            bool Matches(ServerDirectoryServer row) => server.SharedWorldId is { } world
                ? row.SharedWorldId == world && row.Id == sourceId && !row.IsSuccessor
                : row.Id == sourceId && !row.IsSuccessor;
            var source = visibleHosts.SelectMany(host => host.Servers
                .Where(Matches).Select(profile => (Host: host, Server: profile)))
                .FirstOrDefault();
            var localSource = local.FirstOrDefault(profile => Matches(profile) && profile.SeparateCopySourceProfileId is null);
            return server with
            {
                SourceName = source.Server?.Name ?? localSource?.Name,
                SourceHostName = source.Host?.HostProfileName ?? source.Host?.ConnectionName
            };
        }
        return new(local.Take(MaximumEntries).Select(Origin).ToArray(), hosts.Take(MaximumEntries)
            .Select(host => host with { Servers = host.Servers.Select(Origin).ToArray() }).ToArray());
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
        return ServerDirectory.LocalRows(snapshot, clock.GetUtcNow()).Select(row =>
        {
            var profile = snapshot.Settings.Profiles.Single(item => item.Id == row.Id);
            try
            {
                lock (SharedWorldMutationGate.For(data.RootPath))
                {
                    if (profile.SeparateCopySourceProfileId is { } sourceId && profile.SeparateCopyBranchHash is { } hash)
                    {
                        var branch = new SharedWorldSeparateCopyStore(data).Read(sourceId)
                            .SingleOrDefault(item => item.BranchHash == hash);
                        return branch is not null && SharedWorldSeparateCopyStore.Verify(branch)
                            ? ServerDirectory.WithWorld(row, branch.Offer.Roster, true) : row;
                    }
                    var authority = new WorldAuthorityStore(data).LocalAuthorizedHead(profile.Id);
                    // A passive directory must not create a replacement owner
                    // identity when its protected key is missing or unreadable.
                    if (authority is null && data.LoadProtected("shared-world-signing-key.protected") is null) return row;
                    var roster = sharedWorlds.ReadRoster(profile);
                    return roster is null ? row : ServerDirectory.WithWorld(row, roster, authority is not null);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or
                UnauthorizedAccessException or ArgumentException)
            { return row; }
        }).ToArray();
    }
}
