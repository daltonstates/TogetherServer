namespace TogetherServer;

public sealed partial class FriendService
{
    internal IReadOnlyList<ServerDirectoryHost> ServerDirectoryHosts()
    {
        lock (sync)
            return disposed ? [] : links.Take(ServerDirectory.MaximumEntries)
                .Select(item => item.Link.ServerDirectoryHost(item.Id)).ToArray();
    }
}

internal sealed partial class FriendLink
{
    // The view is already in memory. Cached names keep a saved connection useful
    // after restart, but cached counts and action permissions are never revived.
    internal ServerDirectoryHost ServerDirectoryHost(Guid connectionId)
    {
        var current = view;
        var saved = config;
        var now = DateTimeOffset.UtcNow;
        var denied = current.State is "Revoked" or "Access expired" ||
            current.ConnectionCode is "Revoked" or "AccessExpired" || saved?.KnownRevoked == true;
        var currentResponse = (current.State is "Connected" or "Disabled") &&
            ServerDirectory.Fresh(current.LastConnectedUtc, now);
        IReadOnlyList<PublicProfile> profiles = denied ? [] : current.Profiles.Count > 0
            ? current.Profiles : saved?.CachedProfiles ?? [];
        var personName = current.HostProfileName ?? saved?.HostProfileName;
        var connectionName = current.ConnectionName ?? saved?.DisplayName;
        // Legacy default labels embed the endpoint's hostname. The directory
        // keeps an actual owner-chosen alias, while leaving route details in the
        // explicit connection editor rather than this global list.
        if (string.IsNullOrWhiteSpace(connectionName) || saved is not null &&
            (connectionName == HostLabel(saved.Endpoint) || connectionName == "Successor " + HostLabel(saved.Endpoint)))
            connectionName = string.IsNullOrWhiteSpace(personName) ? "Friend Host" : personName + "'s Host";
        return new(connectionId, saved?.HostId ?? current.HostId,
            connectionName, personName, current.State, current.ConnectionCode,
            current.LastConnectedUtc, profiles.Take(ServerDirectory.MaximumEntries).Select(profile =>
            {
                var trusted = currentResponse && profile.State == "Ready" && profile.PlayerCountTrusted == true &&
                    profile.OnlinePlayers is >= 0 && ServerDirectory.Fresh(profile.PlayerCountObservedUtc, now);
                var state = currentResponse ? profile.State : "Unknown";
                if (state == "Ready" && !ServerDirectory.Fresh(profile.PlayerCountObservedUtc, now)) state = "Unknown";
                return new ServerDirectoryServer(profile.Id, profile.Name, profile.GameKind ?? profile.Kind,
                    state, trusted ? profile.OnlinePlayers : null, trusted ? profile.MaxPlayers : null,
                    trusted, currentResponse ? profile.PlayerObservationSource : null,
                    currentResponse ? profile.PlayerCountObservedUtc : null,
                    currentResponse && profile.PlayerCountRefreshing,
                    currentResponse ? profile.PlayerCountFailureCount : 0);
            }).ToArray());
    }
}
