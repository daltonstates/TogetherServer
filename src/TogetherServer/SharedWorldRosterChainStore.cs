using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// Stores signed roster history without changing the current owner-only
// publication path. Transport and delegated publication are later slices.
internal sealed class SharedWorldRosterChainStore(LocalData data, TimeProvider? clock = null)
{
    private sealed record Floor(int Schema, Guid ProfileId, Guid GroupId,
        string OwnerPublicKey, IReadOnlyList<string> Hashes);
    private sealed record Pending(int Schema, string OldFloorHash, string RosterHash,
        SharedWorldRoster Roster);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private static string FloorName(Guid profileId) => $"shared-roster-chain-{profileId:N}.protected";
    private static string PendingName(Guid profileId) => $"shared-roster-pending-{profileId:N}.protected";
    private string Root(Guid profileId)
    {
        var root = Path.Combine(data.RootPath, "shared-worlds", profileId.ToString("N"), "roster-chain");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        return root;
    }
    private string RevisionPath(Guid profileId, string hash) => Path.Combine(Root(profileId), hash + ".json");
    internal bool HasState(Guid profileId) => data.HasProtected(FloorName(profileId)) ||
        data.HasProtected(PendingName(profileId)) ||
        Directory.Exists(Root(profileId)) && Directory.EnumerateFileSystemEntries(Root(profileId)).Any();
    private static string Digest(Floor floor) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(floor, Json)));
    private Floor? LoadFloor(Guid profileId)
    {
        var bytes = data.LoadProtected(FloorName(profileId));
        return bytes is null ? null : JsonSerializer.Deserialize<Floor>(bytes, Json);
    }
    private void SaveFloor(Guid profileId, Floor floor) => data.SaveProtected(FloorName(profileId),
        JsonSerializer.SerializeToUtf8Bytes(floor, Json));
    private static bool ValidHash(string? value) => value is { Length: 64 } &&
        value.All(ch => char.IsAsciiHexDigit(ch) && !char.IsLower(ch));
    private SharedWorldRoster LoadRevision(Guid profileId, string hash)
    {
        if (!ValidHash(hash)) throw new InvalidDataException("Roster revision hash is invalid.");
        var path = RevisionPath(profileId, hash);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("Roster revision is missing, linked, or oversized.");
        SharedWorldRoster? roster;
        try { roster = JsonSerializer.Deserialize<SharedWorldRoster>(File.ReadAllBytes(path), Json); }
        catch (JsonException ex) { throw new InvalidDataException("Roster revision is unreadable.", ex); }
        if (roster is null || SharedWorldRosterTrust.Hash(roster) != hash)
            throw new InvalidDataException("Roster revision failed its hash check.");
        return roster;
    }
    private static void ValidateNext(SharedWorldRoster roster, IReadOnlyList<SharedWorldRoster> existing,
        string pinnedOwnerKey, DateTimeOffset now, bool firstAcceptance)
    {
        if (existing.Count == 0)
        {
            var legacy = roster.Schema is 1 or 2 && SharedWorldRosterTrust.Verify(roster) &&
                roster.OwnerPublicKey == pinnedOwnerKey;
            if (!legacy && !SharedWorldRosterTrust.VerifyRevision(roster, null,
                pinnedOwnerKey, now, firstAcceptance))
                throw new InvalidDataException("Roster chain has no owner-signed root.");
            return;
        }
        var heads = existing.Where(item => !existing.Any(child =>
            child.Schema == 3 && child.PreviousRosterHash == SharedWorldRosterTrust.Hash(item))).ToArray();
        if (heads.Length != 1)
            throw new InvalidDataException("Competing roster revisions require owner review.");
        var parent = existing.SingleOrDefault(item =>
            SharedWorldRosterTrust.Hash(item) == roster.PreviousRosterHash);
        if (parent is null || !SharedWorldRosterTrust.VerifyRevision(roster, parent,
            pinnedOwnerKey, now, firstAcceptance))
            throw new InvalidDataException("Roster revision is not authorized by its exact parent.");
        // A second child of the same parent is retained, then governance fences.
        if (parent != heads[0] &&
            (heads[0].PreviousRosterHash != roster.PreviousRosterHash ||
             heads[0].Revision != roster.Revision || heads[0].Epoch != roster.Epoch))
            throw new InvalidDataException("Roster revision does not extend the current head.");
    }
    private void WriteRevision(Guid profileId, string hash, SharedWorldRoster roster)
    {
        var path = RevisionPath(profileId, hash);
        if (File.Exists(path))
        {
            _ = LoadRevision(profileId, hash);
            return;
        }
        Directory.CreateDirectory(Root(profileId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(roster, Json);
        if (bytes.Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("Roster revision is oversized.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private void RecoverPending(Guid profileId, Floor floor)
    {
        var bytes = data.LoadProtected(PendingName(profileId));
        if (bytes is null) return;
        var pending = JsonSerializer.Deserialize<Pending>(bytes, Json);
        if (pending is null || pending.Schema != 1 || pending.Roster is null ||
            !ValidHash(pending.RosterHash) ||
            pending.Roster.ProfileId != profileId ||
            pending.Roster.GroupId != floor.GroupId ||
            pending.Roster.OwnerPublicKey != floor.OwnerPublicKey ||
            SharedWorldRosterTrust.Hash(pending.Roster) != pending.RosterHash)
            throw new InvalidDataException("Pending roster revision is invalid.");
        if (floor.Hashes.Contains(pending.RosterHash))
        {
            _ = LoadRevision(profileId, pending.RosterHash);
            data.DeleteProtected(PendingName(profileId));
            return;
        }
        if (pending.OldFloorHash != Digest(floor))
            throw new InvalidDataException("Pending roster revision disagrees with the protected floor.");
        var prior = LoadKnown(profileId, floor);
        ValidateNext(pending.Roster, prior, floor.OwnerPublicKey,
            (clock ?? TimeProvider.System).GetUtcNow(), false);
        WriteRevision(profileId, pending.RosterHash, pending.Roster);
        SaveFloor(profileId, floor with { Hashes = [.. floor.Hashes, pending.RosterHash] });
        data.DeleteProtected(PendingName(profileId));
    }
    private IReadOnlyList<SharedWorldRoster> LoadKnown(Guid profileId, Floor floor)
    {
        if (floor.Schema != 1 || floor.ProfileId != profileId || floor.GroupId == Guid.Empty ||
            !SharedWorldRosterTrust.ValidKey(floor.OwnerPublicKey) || floor.Hashes is null ||
            floor.Hashes.Count > 256 ||
            floor.Hashes.Any(hash => !ValidHash(hash)) ||
            floor.Hashes.Distinct(StringComparer.Ordinal).Count() != floor.Hashes.Count)
            throw new InvalidDataException("Protected roster chain floor is invalid.");
        var known = new List<SharedWorldRoster>();
        foreach (var hash in floor.Hashes)
        {
            var roster = LoadRevision(profileId, hash);
            if (roster.ProfileId != profileId || roster.GroupId != floor.GroupId ||
                roster.OwnerPublicKey != floor.OwnerPublicKey)
                throw new InvalidDataException("Roster revision crossed a world boundary.");
            ValidateNext(roster, known, floor.OwnerPublicKey,
                (clock ?? TimeProvider.System).GetUtcNow(), false);
            known.Add(roster);
        }
        return known;
    }
    internal IReadOnlyList<SharedWorldRoster> Read(Guid profileId)
    {
        lock (sync)
        {
            var floor = LoadFloor(profileId);
            var root = Root(profileId);
            if (floor is null)
            {
                if (data.HasProtected(PendingName(profileId)) ||
                    Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new InvalidDataException("Protected roster chain floor is missing.");
                return [];
            }
            _ = LoadKnown(profileId, floor);
            RecoverPending(profileId, floor);
            floor = LoadFloor(profileId) ?? throw new InvalidDataException("Protected roster floor disappeared.");
            var known = LoadKnown(profileId, floor);
            var actual = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension).ToArray() : [];
            if (actual.Length != floor.Hashes.Count ||
                actual.Any(hash => !floor.Hashes.Contains(hash, StringComparer.Ordinal)))
                throw new InvalidDataException("Roster chain files changed or were rolled back.");
            return known;
        }
    }
    internal IReadOnlyList<SharedWorldRoster> Heads(Guid profileId)
    {
        var all = Read(profileId);
        return all.Where(item => !all.Any(child => child.Schema == 3 &&
            child.PreviousRosterHash == SharedWorldRosterTrust.Hash(item))).ToArray();
    }
    internal void Append(SharedWorldRoster roster, string pinnedOwnerPublicKey,
        bool stopAfterPendingForChecks = false, bool stopAfterFileForChecks = false)
    {
        lock (sync)
        {
            var existing = Read(roster.ProfileId);
            var hash = SharedWorldRosterTrust.Hash(roster);
            if (existing.Any(item => SharedWorldRosterTrust.Hash(item) == hash)) return;
            ValidateNext(roster, existing, pinnedOwnerPublicKey,
                (clock ?? TimeProvider.System).GetUtcNow(), true);
            var floor = LoadFloor(roster.ProfileId);
            if (floor is null)
            {
                floor = new Floor(1, roster.ProfileId, roster.GroupId, pinnedOwnerPublicKey, []);
                SaveFloor(roster.ProfileId, floor);
            }
            if (floor.GroupId != roster.GroupId || floor.OwnerPublicKey != pinnedOwnerPublicKey ||
                floor.Hashes.Count >= 256)
                throw new InvalidDataException("Roster chain identity or retention bound changed.");
            var pending = new Pending(1, Digest(floor), hash, roster);
            data.SaveProtected(PendingName(roster.ProfileId), JsonSerializer.SerializeToUtf8Bytes(pending, Json));
            if (stopAfterPendingForChecks) return;
            WriteRevision(roster.ProfileId, hash, roster);
            if (stopAfterFileForChecks) return;
            SaveFloor(roster.ProfileId, floor with { Hashes = [.. floor.Hashes, hash] });
            data.DeleteProtected(PendingName(roster.ProfileId));
        }
    }
}
