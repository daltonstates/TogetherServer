using System.Text.Json;

namespace TogetherServer;

// Each saved invite has its own protected credential and heartbeat sequence.
// The visible connection can change without pausing others.
public sealed class FriendService
{
    private const string IndexFile = "friend-connections.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly LocalData data;
    private readonly List<(Guid Id, FriendLink Link)> links = [];
    private Guid selectedId;
    private sealed record SavedConnections(List<Guid> Ids, Guid SelectedId);

    public FriendService(LocalData data)
    {
        this.data = data;
        var saved = data.LoadProtected(IndexFile);
        var state = saved is null
            ? new SavedConnections(data.HasProtected("friend.protected") ? [Guid.Empty] : [], Guid.Empty)
            : LoadIndex(saved);
        var ids = state.Ids;
        foreach (var id in ids.Distinct())
        {
            var file = FileName(id);
            if (data.HasProtected(file)) links.Add((id, new FriendLink(data, file)));
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

    public async Task<FriendView> PollAsync()
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = [.. links];
        await Task.WhenAll(current.Select(item => item.Link.PollAsync()));
        return View();
    }

    public FriendActionResult Select(Guid connectionId)
    {
        lock (sync)
        {
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
        lock (sync) link = links.SingleOrDefault(item => item.Id == connectionId).Link;
        return link is null
            ? new(false, "UnknownConnection", "Choose a saved Host connection.", null)
            : await link.RenameAsync(name);
    }

    public async Task<FriendActionResult> ForgetAsync(Guid connectionId)
    {
        FriendLink? link;
        lock (sync) link = links.SingleOrDefault(item => item.Id == connectionId).Link;
        if (link is null) return new(false, "UnknownConnection", "Choose a saved Host connection.", null);
        var result = await link.ForgetAsync();
        if (!result.Ok) return result;
        lock (sync)
        {
            links.RemoveAll(item => item.Id == connectionId);
            if (selectedId == connectionId) selectedId = links.FirstOrDefault().Id;
            SaveIndex();
        }
        return result;
    }

    public Task<FriendActionResult> RecoverEndpointAsync(Guid connectionId, string endpoint)
    {
        lock (sync)
        {
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

    private static SavedConnections LoadIndex(byte[] saved)
    {
        using var document = JsonDocument.Parse(saved);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return new(JsonSerializer.Deserialize<List<Guid>>(saved, Json)
                ?? throw new InvalidDataException("Invalid saved Friend connections"), Guid.Empty);
        return JsonSerializer.Deserialize<SavedConnections>(saved, Json)
            ?? throw new InvalidDataException("Invalid saved Friend connections");
    }
}
