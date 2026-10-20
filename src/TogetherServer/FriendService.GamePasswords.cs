namespace TogetherServer;

public sealed partial class FriendService
{
    public async Task<GamePasswordResult> RevealGamePasswordAsync(Guid connectionId, Guid profileId,
        CancellationToken ct = default)
    {
        if (!TryRetain()) return GamePasswords.Failure(profileId, "ConnectionClosed");
        try
        {
            FriendLink? link;
            lock (sync) link = !disposed && selectedId == connectionId
                ? links.FirstOrDefault(item => item.Id == connectionId).Link : null;
            if (link is null) return GamePasswords.Failure(profileId, "ConnectionChanged");
            var result = await link.RevealGamePasswordAsync(profileId, ct);
            lock (sync)
                return !disposed && selectedId == connectionId && links.Any(item => item.Id == connectionId && ReferenceEquals(item.Link, link))
                    ? result : GamePasswords.Failure(profileId, "ConnectionChanged");
        }
        finally { ReleaseRetained(); }
    }
}
