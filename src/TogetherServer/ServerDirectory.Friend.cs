namespace TogetherServer;

public sealed partial class FriendService
{
    internal IReadOnlyList<ServerDirectoryHost> ServerDirectoryHosts()
    {
        if (!TryRetain()) return [];
        var retained = new List<(Guid Id, FriendLink Link)>();
        try
        {
            Guid selected;
            lock (sync)
            {
                if (disposed) return [];
                selected = selectedId;
                foreach (var item in links.Take(ServerDirectory.MaximumEntries))
                    if (item.Link.RetainLocalHosting()) retained.Add(item);
            }

            // A row reads the current protected roster under the world lock.
            // Share's final scope check takes the connection-list lock while
            // holding that world lock, so never project a row under sync.
            var projected = retained.Select(item => item.Link.ServerDirectoryHost(item.Id)).ToArray();
            lock (sync)
            {
                // A new selection or forgotten/replaced connection requires a
                // fresh poll; never return a completed read for that old scope.
                if (disposed || selectedId != selected || retained.Any(item => !item.Link.Configured ||
                    !links.Any(current => current.Id == item.Id && ReferenceEquals(current.Link, item.Link)))) return [];
                return projected;
            }
        }
        finally
        {
            try { foreach (var item in retained) item.Link.ReleaseLocalHosting(); }
            finally { ReleaseRetained(); }
        }
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
                var row = new ServerDirectoryServer(profile.Id, profile.Name, profile.GameKind ?? profile.Kind,
                    state, trusted ? profile.OnlinePlayers : null, trusted ? profile.MaxPlayers : null,
                    trusted, currentResponse ? profile.PlayerObservationSource : null,
                    currentResponse ? profile.PlayerCountObservedUtc : null,
                    currentResponse && profile.PlayerCountRefreshing,
                    currentResponse ? profile.PlayerCountFailureCount : 0);
                try
                {
                    lock (SharedWorldMutationGate.For(data.RootPath))
                    {
                        if (saved?.PeerWorld is { } root)
                        {
                            // A peer enrollment binds exactly one local server
                            // to the pinned original world. No name-based merge.
                            var localId = saved.PeerWorldLocalProfileId ??
                                (profiles.Count == 1 ? profiles[0].Id : (Guid?)null);
                            var peerRoster = localId == profile.Id ? CachedPeerRoster(root.ProfileId) : null;
                            return peerRoster is null ? row : ServerDirectory.WithWorld(row, peerRoster, true);
                        }
                        if (saved?.SharedRosterFloors.GetValueOrDefault(profile.Id) is not { } floor ||
                            saved.SharedWorldSigningKeys.GetValueOrDefault(profile.Id) is not { } owner ||
                            saved.ApprovedSharedWorldGroups.GetValueOrDefault(profile.Id) != floor.GroupId ||
                            saved.PendingSharedWorldGroups.ContainsKey(profile.Id)) return row;
                        var bytes = data.LoadProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{profile.Id:N}.protected");
                        var roster = bytes is { Length: > 0 } && bytes.Length <= SharedWorldService.MaximumManifestBytes
                            ? System.Text.Json.JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json) : null;
                        return roster is not null && roster.OwnerPublicKey == owner && roster.ProfileId == profile.Id &&
                            roster.GroupId == floor.GroupId && roster.Epoch == floor.Epoch && roster.Revision == floor.Revision &&
                            roster.Signature == floor.Signature && new WorldAuthorityStore(data).TrustsCurrentRoster(roster)
                            ? ServerDirectory.WithWorld(row, roster, false) : row;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
                    System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException)
                { return row; }
            }).ToArray());
    }
}
