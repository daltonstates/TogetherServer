using System.Text.Json;

namespace TogetherServer;

// Each saved invite has its own protected credential and heartbeat sequence.
// The visible connection can change without pausing others.
public sealed class FriendService : IDisposable
{
    private const string IndexFile = "friend-connections.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly LocalData data;
    private readonly List<(Guid Id, FriendLink Link)> links = [];
    private Guid selectedId;
    private bool disposed;
    private int retainedOperations;
    private sealed record SavedConnections(List<Guid> Ids, Guid SelectedId);

    public FriendService(LocalData data)
    {
        this.data = data;
        var state = LoadIndex(data);
        var ids = state.Ids;
        foreach (var id in ids.Distinct())
        {
            var file = FileName(id);
            if (!data.HasProtected(file)) continue;
            var link = new FriendLink(data, file);
            if (link.Configured) links.Add((id, link));
            else link.Dispose();
        }
        selectedId = links.Any(item => item.Id == state.SelectedId)
            ? state.SelectedId : links.FirstOrDefault().Id;
    }

    public FriendView View()
    {
        lock (sync)
        {
            var connections = links.Select(item => item.Link.View() with { ConnectionId = item.Id }).ToList();
            var selected = connections.FirstOrDefault(item => item.ConnectionId == selectedId)
                ?? connections.FirstOrDefault();
            return selected is null
                ? new FriendView("Friend", "Not paired", "Paste the server invite code from the Host PC.",
                    "", null, false, false, false, [], Connections: connections)
                : selected with { Connections = connections };
        }
    }

    public async Task<FriendActionResult> PairAsync(string invitation, string? hostAddress = null)
    {
        if (!TryRetain()) return ClosedAction();
        try
        {
            var id = Guid.NewGuid();
            var link = new FriendLink(data, FileName(id));
            var result = await link.PairAsync(invitation, hostAddress);
            if (!result.Ok)
            {
                link.Dispose();
                return result;
            }
            lock (sync)
            {
                links.Add((id, link));
                selectedId = id;
                SaveIndex();
            }
            return result;
        }
        finally { ReleaseRetained(); }
    }

    public async Task<FriendView> PollAsync()
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        await Task.WhenAll(current.Select(item => item.Link.PollAsync()));
        return View();
    }

    public FriendActionResult Select(Guid connectionId)
    {
        lock (sync)
        {
            if (disposed) return ClosedAction();
            if (!links.Any(item => item.Id == connectionId))
                return new(false, "UnknownConnection", "Choose a saved Friend connection.", null);
            selectedId = connectionId;
            SaveIndex();
            return new(true, "ConnectionSelected", "Friend connection selected.", null);
        }
    }

    public async Task<FriendActionResult> RenameAsync(Guid connectionId, string? name)
    {
        FriendLink? link;
        lock (sync)
        {
            if (disposed) return ClosedAction();
            link = links.SingleOrDefault(item => item.Id == connectionId).Link;
        }
        return link is null
            ? new(false, "UnknownConnection", "Choose a saved Host connection.", null)
            : await link.RenameAsync(name);
    }

    public async Task<FriendActionResult> ForgetAsync(Guid connectionId)
    {
        FriendLink? link;
        lock (sync)
        {
            if (disposed) return ClosedAction();
            link = links.SingleOrDefault(item => item.Id == connectionId).Link;
        }
        if (link is null) return new(false, "UnknownConnection", "Choose a saved Host connection.", null);
        var result = await link.ForgetAsync();
        if (!result.Ok) return result;
        lock (sync)
        {
            if (disposed)
            {
                link.Dispose();
                return result;
            }
            links.RemoveAll(item => item.Id == connectionId);
            if (selectedId == connectionId) selectedId = links.FirstOrDefault().Id;
            SaveIndex();
            link.Dispose();
        }
        return result;
    }

    public Task<FriendActionResult> RecoverEndpointAsync(Guid connectionId, string endpoint)
    {
        lock (sync)
        {
            if (disposed) return Task.FromResult(ClosedAction());
            var link = links.SingleOrDefault(item => item.Id == connectionId).Link;
            return link is null
                ? Task.FromResult(new FriendActionResult(false, "UnknownConnection", "Choose a saved Host connection.", null))
                : link.RecoverEndpointAsync(endpoint);
        }
    }

    public Task<GameEndpointProbeResult> ProbeGameEndpointAsync(Guid profileId)
    {
        FriendLink? link;
        lock (sync)
        {
            if (disposed)
                return Task.FromResult(new GameEndpointProbeResult(false, "ConnectionClosed",
                    "Saved Host connections are closing.", DateTimeOffset.UtcNow));
            var matching = links.Where(item => item.Link.View().Profiles.Any(profile => profile.Id == profileId)).ToList();
            link = matching.FirstOrDefault(item => item.Id == selectedId).Link ?? matching.FirstOrDefault().Link;
        }
        return Task.Run(() => link is null
            ? new GameEndpointProbeResult(false, "UnknownProfile", "This server is not available from a saved Host connection.", DateTimeOffset.UtcNow)
            : link.ProbeGameEndpoint(profileId));
    }

    public Task<FriendActionResult> RequestAsync(Guid profileId, string action)
    {
        lock (sync)
        {
            if (disposed) return Task.FromResult(ClosedAction());
            var matching = links.Where(item => item.Link.View().Profiles.Any(profile => profile.Id == profileId)).ToList();
            var link = matching.FirstOrDefault(item => item.Id == selectedId).Link ?? matching.FirstOrDefault().Link;
            if (link is null && links.Count == 1) link = links[0].Link;
            return link is null
                ? Task.FromResult(new FriendActionResult(false, "UnknownProfile", "This server is not available from a saved Friend connection.", null))
                : link.RequestAsync(profileId, action);
        }
    }

    private static string FileName(Guid id) => id == Guid.Empty
        ? "friend.protected" : $"friend-{id:N}.protected";

    private void SaveIndex() => data.SaveProtected(IndexFile,
        JsonSerializer.SerializeToUtf8Bytes(new SavedConnections(links.Select(item => item.Id).ToList(), selectedId), Json));

    private static FriendActionResult ClosedAction() =>
        new(false, "ConnectionClosed", "Saved Host connections are closing.", null);

    private bool TryRetain()
    {
        lock (sync)
        {
            if (disposed) return false;
            retainedOperations++;
            return true;
        }
    }

    private void ReleaseRetained()
    {
        lock (sync)
        {
            retainedOperations--;
            if (retainedOperations < 0) throw new InvalidOperationException("Friend service lifetime underflow.");
            if (disposed && retainedOperations == 0) CleanupLinksLocked();
        }
    }

    private void CleanupLinksLocked()
    {
        foreach (var (_, link) in links) link.Dispose();
        links.Clear();
        selectedId = Guid.Empty;
    }

    private static SavedConnections LoadIndex(LocalData data)
    {
        var currentIndexExisted = data.HasProtected(IndexFile);
        // A damaged current index must not resurrect an older single-Host
        // credential that happens to remain on disk from before migration.
        var fallback = new SavedConnections(!currentIndexExisted && data.HasProtected("friend.protected")
            ? [Guid.Empty] : [], Guid.Empty);
        var saved = data.LoadProtectedJson<JsonElement>(IndexFile);
        if (saved.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return fallback;
        try
        {
            SavedConnections state;
            if (saved.ValueKind == JsonValueKind.Array)
                state = new(saved.Deserialize<List<Guid>>(Json)
                    ?? throw new InvalidDataException("Invalid saved Friend connections"), Guid.Empty);
            else if (saved.ValueKind == JsonValueKind.Object)
                state = saved.Deserialize<SavedConnections>(Json)
                    ?? throw new InvalidDataException("Invalid saved Friend connections");
            else
                throw new InvalidDataException("Invalid saved Friend connections");
            if (state.Ids is null || state.Ids.Count > 100)
                throw new InvalidDataException("Invalid saved Friend connections");
            return new(state.Ids.Where(id => id != Guid.Empty || data.HasProtected("friend.protected"))
                .Distinct().Take(100).ToList(), state.SelectedId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            // JSON syntax/decryption failures are quarantined by LocalData. A
            // syntactically valid but unsafe index is disabled fail closed.
            data.QuarantineState(IndexFile,
                "TogetherServer disabled an invalid saved Friend connection index for owner review.", false);
            data.TryAudit($"friend-index-disabled {ex.GetType().Name} {DateTimeOffset.UtcNow:O}");
            return fallback;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (retainedOperations == 0) CleanupLinksLocked();
        }
    }
}
