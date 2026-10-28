namespace TogetherServer;

public sealed partial class HostManager
{
    private sealed record PublishedPeerRead(ServerProfile Profile, SharedWorldRoster Roster,
        SharedWorldVersion Version);

    // These reads consume durable profile copies and already published signed
    // files only. Waiting for the running game's next save must not block
    // discovery or serving its previous complete immutable publication.
    private ServerProfile? DurablePeerProfile(Guid profileId) => data.LoadSettings().Profiles
        .SingleOrDefault(profile => profile.Id == profileId && profile.SeparateCopySourceProfileId is null);

    private static bool SamePeerSource(ServerProfile before, ServerProfile after) =>
        before.Id == after.Id && before.Kind == after.Kind && before.WorldId == after.WorldId &&
        before.SeparateCopySourceProfileId == after.SeparateCopySourceProfileId &&
        before.WorldLoadRehearsalId == after.WorldLoadRehearsalId &&
        before.SharedSavesEnabled == after.SharedSavesEnabled &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(before.WorldDirectory)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(after.WorldDirectory)), StringComparison.OrdinalIgnoreCase);

    private PublishedPeerRead? ReadPublishedPeer(SharedWorldPeerRoot root)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root)) return null;
        var profile = DurablePeerProfile(root.ProfileId);
        if (profile is null) return null;
        var roster = sharedWorlds.ReadRoster(profile, requireRetainedSigningKey: true);
        if (roster?.OwnerPublicKey != root.OwnerPublicKey || roster.GroupId != root.GroupId) return null;
        var latest = sharedWorlds.Status(profile).Latest;
        if (latest is null || latest.ProfileId != root.ProfileId || latest.GroupId != root.GroupId ||
            latest.Game != profile.Kind || latest.WorldId != profile.WorldId) return null;
        return new(profile, roster, latest);
    }

    private static bool SamePublishedPeer(PublishedPeerRead before, PublishedPeerRead? after) =>
        after is not null && SamePeerSource(before.Profile, after.Profile) &&
        SharedWorldRosterTrust.Hash(before.Roster) == SharedWorldRosterTrust.Hash(after.Roster) &&
        before.Version.VersionHash == after.Version.VersionHash && before.Version.Signature == after.Version.Signature;

    internal Task<SharedWorldRoster?> PeerPublishedRosterAsync(Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var profile = DurablePeerProfile(profileId);
            var roster = profile is null ? null : sharedWorlds.ReadRoster(profile, requireRetainedSigningKey: true);
            if (profile is null || roster is null) return Task.FromResult<SharedWorldRoster?>(null);
            var currentProfile = DurablePeerProfile(profileId);
            if (currentProfile is null || !SamePeerSource(profile, currentProfile)) return Task.FromResult<SharedWorldRoster?>(null);
            var currentRoster = sharedWorlds.ReadRoster(currentProfile, requireRetainedSigningKey: true);
            return Task.FromResult<SharedWorldRoster?>(currentRoster is not null &&
                SharedWorldRosterTrust.Hash(roster) == SharedWorldRosterTrust.Hash(currentRoster) ? roster : null);
        }
    }

    internal Task<SharedWorldVersion?> PeerPublishedVersionAsync(SharedWorldPeerRoot root,
        string? versionHash = null, long? number = null) =>
        Task.FromResult(PeerPublishedVersion(root, versionHash, number));

    internal bool PeerHasNoPublishedSave(SharedWorldPeerRoot root)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var profile = DurablePeerProfile(root.ProfileId);
            if (profile is null || !sharedWorlds.HasNoPublishedSaveForRoot(profile, root)) return false;
            var current = DurablePeerProfile(root.ProfileId);
            return current is not null && SamePeerSource(profile, current) &&
                sharedWorlds.HasNoPublishedSaveForRoot(current, root);
        }
    }

    // The same fenced read is also needed by an exact-copy receipt commit,
    // which already holds the membership mutation monitor and cannot await.
    internal SharedWorldVersion? PeerPublishedVersion(SharedWorldPeerRoot root,
        string? versionHash = null, long? number = null)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var source = ReadPublishedPeer(root);
            if (source is null) return null;
            var selected = source.Version;
            if (versionHash is not null && (selected.VersionHash != versionHash || number is not null && selected.Number != number))
            {
                if (number is null || number >= selected.Number || number < 1) return null;
                selected = sharedWorlds.ReadEarlierVersion(selected, number.Value);
                if (selected.VersionHash != versionHash) return null;
            }
            return SamePublishedPeer(source, ReadPublishedPeer(root)) ? selected : null;
        }
    }

    internal Task<byte[]?> ReadPeerPublishedChunkAsync(SharedWorldPeerRoot root,
        string versionHash, int fileIndex, long offset)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var source = ReadPublishedPeer(root);
            if (source is null || source.Version.VersionHash != versionHash) return Task.FromResult<byte[]?>(null);
            // Chunk digests and signed file hashes are checked by the existing
            // immutable reader. This never reads the running world's files.
            var bytes = sharedWorlds.ReadChunk(source.Version, fileIndex, offset);
            return Task.FromResult<byte[]?>(SamePublishedPeer(source, ReadPublishedPeer(root)) ? bytes : null);
        }
    }
}
