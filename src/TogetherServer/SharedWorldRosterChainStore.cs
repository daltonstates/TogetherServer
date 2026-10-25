using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

internal static class SharedWorldRosterProtocol
{
    internal const string AfterHeader = "X-TogetherServer-Roster-After";
    internal const string AnchorHeader = "X-TogetherServer-Roster-Anchor";
}

// Protected index pages commit to 32 immutable revision files apiece. The
// protected floor has constant size regardless of the length of the journal.
internal sealed class SharedWorldRosterChainStore(LocalData data, TimeProvider? clock = null)
{
    internal const int PageSize = 12; // 12 * 256 KiB is below the 4 MiB HTTP cap.
    private const int IndexSize = 32;
    private const long ReserveBytes = 1024L * 1024 * 1024;
    private sealed record Floor(int Schema, Guid ProfileId, Guid GroupId, string OwnerPublicKey,
        IReadOnlyList<string>? Hashes = null, long Count = 0,
        IReadOnlyList<string>? Heads = null, bool UnreviewedOwnerEdit = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? LegacyProjectionPosition = null);
    private sealed record Index(int Schema, long Offset, IReadOnlyList<string> Hashes);
    private sealed record Lookup(int Schema, Guid ProfileId, Guid GroupId,
        string OwnerPublicKey, string Hash, long Position,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SharedWorldRoster? LegacyParent = null);
    private sealed record Pending(int Schema, string OldFloorHash, string RosterHash, SharedWorldRoster Roster,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SharedWorldRoster? LegacyParent = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = SharedWorldMutationGate.For(data.RootPath);
    private static string FloorName(Guid id) => $"shared-roster-chain-{id:N}.protected";
    private static string PendingName(Guid id) => $"shared-roster-pending-{id:N}.protected";
    private static string IndexName(Guid id, long page) => $"shared-roster-index-{id:N}-{page:D12}.protected";
    private static string LookupName(Guid id, string hash) =>
        $"shared-roster-lookup-{id:N}-{hash}.protected";
    private string Root(Guid id)
    {
        var root = Path.Combine(data.RootPath, "shared-worlds", id.ToString("N"), "roster-chain");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        return root;
    }
    private string RevisionPath(Guid id, string hash) => Path.Combine(Root(id), hash + ".json");
    internal bool HasState(Guid id) => data.HasProtected(FloorName(id)) || data.HasProtected(PendingName(id)) ||
        data.HasProtected(IndexName(id, 0)) ||
        Directory.Exists(Root(id)) && Directory.EnumerateFileSystemEntries(Root(id)).Any();
    private Floor? LoadFloor(Guid id)
    {
        var bytes = data.LoadProtected(FloorName(id));
        return bytes is null ? null : JsonSerializer.Deserialize<Floor>(bytes, Json);
    }
    private void SaveFloor(Guid id, Floor floor) => data.SaveProtected(FloorName(id),
        JsonSerializer.SerializeToUtf8Bytes(floor, Json));
    private static string Digest(Floor floor) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(floor, Json)));
    private static string LegacyDigest(Floor floor) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            floor.Schema,
            floor.ProfileId,
            floor.GroupId,
            floor.OwnerPublicKey,
            floor.Hashes
        }, Json)));
    private static bool ValidHash(string? hash) => hash is { Length: 64 } &&
        hash.All(ch => char.IsAsciiHexDigit(ch) && !char.IsLower(ch));
    private static void ValidateFloor(Floor floor, Guid id)
    {
        if (floor.ProfileId != id || floor.GroupId == Guid.Empty ||
            !SharedWorldRosterTrust.ValidKey(floor.OwnerPublicKey) ||
            floor.Schema == 1 && (floor.Hashes is null || floor.Hashes.Count > 256 ||
                floor.Hashes.Any(hash => !ValidHash(hash)) ||
                floor.Hashes.Distinct(StringComparer.Ordinal).Count() != floor.Hashes.Count) ||
            floor.Schema == 2 && (floor.Hashes is not null || floor.Count < 0 ||
                floor.Heads is null || floor.Heads.Count > 2 ||
                floor.Heads.Count == 0 && floor.Count != 0 ||
                floor.Heads.Any(hash => !ValidHash(hash)) ||
                floor.Heads.Distinct(StringComparer.Ordinal).Count() != floor.Heads.Count) ||
            floor.LegacyProjectionPosition is { } projection &&
                (floor.Schema != 2 || projection < 1 || projection >= floor.Count) ||
            floor.Schema is not (1 or 2))
            throw new InvalidDataException("Protected roster floor is invalid.");
    }
    private SharedWorldRoster LoadRevision(Guid id, string hash)
    {
        if (!ValidHash(hash)) throw new InvalidDataException("Roster revision hash is invalid.");
        var path = RevisionPath(id, hash);
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
    private Index LoadIndex(Guid id, long number)
    {
        var bytes = data.LoadProtected(IndexName(id, number)) ??
            throw new InvalidDataException("Protected roster index is missing.");
        var index = JsonSerializer.Deserialize<Index>(bytes, Json);
        if (index is null || index.Schema != 2 || index.Offset != number * IndexSize ||
            index.Hashes is null || index.Hashes.Count is < 1 or > IndexSize ||
            index.Hashes.Any(hash => !ValidHash(hash)) ||
            index.Hashes.Distinct(StringComparer.Ordinal).Count() != index.Hashes.Count)
            throw new InvalidDataException("Protected roster index is invalid.");
        return index;
    }
    private void SaveIndex(Guid id, long number, IReadOnlyList<string> hashes) =>
        data.SaveProtected(IndexName(id, number), JsonSerializer.SerializeToUtf8Bytes(
            new Index(2, number * IndexSize, hashes), Json));
    private void EnsureLookup(Guid id, Floor floor, string hash, long position,
        SharedWorldRoster? legacyParent = null)
    {
        var expected = new Lookup(2, id, floor.GroupId, floor.OwnerPublicKey, hash, position, legacyParent);
        var prior = data.LoadProtected(LookupName(id, hash));
        if (prior is not null)
        {
            var existing = JsonSerializer.Deserialize<Lookup>(prior, Json);
            if (existing is null || JsonSerializer.Serialize(existing, Json) != JsonSerializer.Serialize(expected, Json))
                throw new InvalidDataException("Protected roster lookup changed.");
            return;
        }
        data.SaveProtected(LookupName(id, hash), JsonSerializer.SerializeToUtf8Bytes(expected, Json));
    }
    private bool HasLookup(Guid id, Floor floor, string hash)
    {
        var bytes = data.LoadProtected(LookupName(id, hash));
        if (bytes is null) return false;
        var lookup = JsonSerializer.Deserialize<Lookup>(bytes, Json);
        if (lookup is null || lookup.Schema != 2 || lookup.ProfileId != id ||
            lookup.GroupId != floor.GroupId || lookup.OwnerPublicKey != floor.OwnerPublicKey ||
            lookup.Hash != hash || lookup.Position < 0 || lookup.Position >= floor.Count)
            throw new InvalidDataException("Protected roster lookup is invalid.");
        var index = LoadIndex(id, lookup.Position / IndexSize);
        if (index.Hashes.Count != Math.Min(IndexSize, floor.Count - index.Offset) ||
            index.Hashes[(int)(lookup.Position % IndexSize)] != hash)
            throw new InvalidDataException("Protected roster lookup disagrees with the journal.");
        if (lookup.LegacyParent is { } parent)
        {
            if (lookup.Position == 0 || floor.LegacyProjectionPosition != lookup.Position)
                throw new InvalidDataException("Legacy roster projection has no retained anchor.");
            var priorIndex = LoadIndex(id, (lookup.Position - 1) / IndexSize);
            if (priorIndex.Hashes.Count != Math.Min(IndexSize, floor.Count - priorIndex.Offset))
                throw new InvalidDataException("Protected roster projection index is invalid.");
            var anchorHash = priorIndex.Hashes[(int)((lookup.Position - 1) % IndexSize)];
            var anchor = LoadRevision(id, anchorHash);
            if (!SharedWorldRosterTrust.VerifyLegacyOwnerProjection(LoadRevision(id, hash),
                    anchor, parent, floor.OwnerPublicKey) || !HasLookup(id, floor, anchorHash))
                throw new InvalidDataException("Legacy roster projection proof is invalid.");
        }
        else if (floor.LegacyProjectionPosition == lookup.Position)
            throw new InvalidDataException("Legacy roster projection proof is missing.");
        return true;
    }
    private SharedWorldRoster? LegacyParent(Guid id, string hash)
    {
        var bytes = data.LoadProtected(LookupName(id, hash)) ??
            throw new InvalidDataException("Protected roster lookup is missing.");
        return JsonSerializer.Deserialize<Lookup>(bytes, Json)?.LegacyParent;
    }
    internal bool ProvesLegacyOwnerProjection(Guid id, SharedWorldRoster legacy, SharedWorldRoster projection)
    {
        lock (sync)
        {
            if (!Contains(id, projection)) return false;
            var floor = Ready(id);
            var parent = LegacyParent(id, SharedWorldRosterTrust.Hash(projection));
            if (parent is null && ValidHash(projection.PreviousRosterHash) &&
                HasLookup(id, floor, projection.PreviousRosterHash!))
                parent = LoadRevision(id, projection.PreviousRosterHash!);
            return parent is not null &&
                SharedWorldRosterTrust.VerifyLegacyOwnerProjection(projection, legacy, parent, floor.OwnerPublicKey);
        }
    }
    // Legacy owners signed complete snapshots without a parent field. Retain
    // those exact signed snapshots in order, rather than replacing an earlier
    // protected floor. This exception ends when the first schema-3 child exists.
    internal static bool LegacyOwnerAdvance(SharedWorldRoster next, SharedWorldRoster prior) =>
        prior.Schema is 1 or 2 && next.Schema == 2 &&
        next.ProfileId == prior.ProfileId && next.GroupId == prior.GroupId &&
        next.OwnerPublicKey == prior.OwnerPublicKey && next.Epoch > prior.Epoch &&
        next.Revision > prior.Revision && SharedWorldRosterTrust.Verify(prior) &&
        SharedWorldRosterTrust.Verify(next);

    private static string[] NextHeads(IReadOnlyList<string> heads, SharedWorldRoster roster, string hash) =>
        heads.Count == 0 || roster.Schema == 2 || heads[0] == roster.PreviousRosterHash
            ? [hash] : [heads[0], hash];
    private void ValidateNext(SharedWorldRoster roster, Floor floor, bool firstAcceptance,
        SharedWorldRoster? legacyParent = null)
    {
        if (roster.ProfileId != floor.ProfileId || roster.GroupId != floor.GroupId ||
            roster.OwnerPublicKey != floor.OwnerPublicKey)
            throw new InvalidDataException("Roster revision crossed a world boundary.");
        if (floor.Count == 0)
        {
            if (legacyParent is not null)
                throw new InvalidDataException("Legacy roster projection has no retained anchor.");
            if (!(roster.Schema is 1 or 2 && SharedWorldRosterTrust.Verify(roster)) &&
                !SharedWorldRosterTrust.VerifyRevision(roster, null, floor.OwnerPublicKey,
                    (clock ?? TimeProvider.System).GetUtcNow(), firstAcceptance))
                throw new InvalidDataException("Roster chain has no owner-signed root.");
            return;
        }
        if (floor.Heads is null || floor.Heads.Count != 1)
            throw new InvalidDataException("Competing roster revisions require owner review.");
        var head = LoadRevision(roster.ProfileId, floor.Heads[0]);
        if (legacyParent is not null)
        {
            if (!SharedWorldRosterTrust.VerifyLegacyOwnerProjection(roster, head, legacyParent, floor.OwnerPublicKey))
                throw new InvalidDataException("Legacy roster projection proof is invalid.");
            return;
        }
        if (LegacyOwnerAdvance(roster, head)) return;
        if (!ValidHash(roster.PreviousRosterHash))
            throw new InvalidDataException("Roster revision is not authorized by its exact parent.");
        SharedWorldRoster? parent = null;
        if (roster.PreviousRosterHash == floor.Heads[0]) parent = head;
        else if (head.PreviousRosterHash == roster.PreviousRosterHash &&
            head.Revision == roster.Revision && head.Epoch == roster.Epoch)
            parent = LoadRevision(roster.ProfileId, roster.PreviousRosterHash!);
        if (parent is null || !SharedWorldRosterTrust.VerifyRevision(roster, parent,
            floor.OwnerPublicKey, (clock ?? TimeProvider.System).GetUtcNow(), firstAcceptance))
            throw new InvalidDataException("Roster revision is not authorized by its exact parent.");
    }
    private void Reserve(Guid id, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root(id)))!);
        if (drive.AvailableFreeSpace < ReserveBytes || drive.AvailableFreeSpace - ReserveBytes < bytes)
            throw new IOException("Roster history needs a 1 GiB disk reserve.");
    }
    private void WriteRevision(Guid id, string hash, SharedWorldRoster roster)
    {
        var path = RevisionPath(id, hash);
        if (File.Exists(path)) { _ = LoadRevision(id, hash); return; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(roster, Json);
        if (bytes.Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("Roster revision is oversized.");
        Reserve(id, bytes.Length + 16 * 1024);
        Directory.CreateDirectory(Root(id));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private void Advance(Guid id, Floor floor, SharedWorldRoster roster, string hash,
        SharedWorldRoster? legacyParent = null)
    {
        var number = floor.Count / IndexSize;
        var staged = data.LoadProtected(IndexName(id, number));
        var expectedCount = (int)(floor.Count % IndexSize);
        if (staged is null)
        {
            if (expectedCount != 0) throw new InvalidDataException("Roster index is missing.");
            SaveIndex(id, number, [hash]);
        }
        else
        {
            var index = LoadIndex(id, number);
            if (index.Hashes.Count == expectedCount)
                SaveIndex(id, number, [.. index.Hashes, hash]);
            else if (index.Hashes.Count != expectedCount + 1 || index.Hashes[^1] != hash)
                throw new InvalidDataException("Staged roster index changed.");
        }
        EnsureLookup(id, floor, hash, floor.Count, legacyParent);
        SaveFloor(id, floor with
        {
            Count = floor.Count + 1,
            Heads = legacyParent is null ? NextHeads(floor.Heads!, roster, hash) : [hash],
            LegacyProjectionPosition = legacyParent is null ? floor.LegacyProjectionPosition : floor.Count,
            UnreviewedOwnerEdit = floor.UnreviewedOwnerEdit ||
                floor.Count > 0 && roster.Schema == 3 && roster.SignerDeviceId == Guid.Empty &&
                roster.OwnerLocalBaselineMembers is null
        });
    }
    private void RecoverPending(Guid id, Floor floor)
    {
        var bytes = data.LoadProtected(PendingName(id));
        if (bytes is null) return;
        var pending = JsonSerializer.Deserialize<Pending>(bytes, Json);
        if (pending is null || pending.Schema != 1 || !ValidHash(pending.RosterHash) ||
            pending.Roster is null || pending.Roster.ProfileId != id ||
            pending.Roster.GroupId != floor.GroupId ||
            pending.Roster.OwnerPublicKey != floor.OwnerPublicKey ||
            SharedWorldRosterTrust.Hash(pending.Roster) != pending.RosterHash)
            throw new InvalidDataException("Pending roster revision is invalid.");
        if (floor.Count > 0 &&
            LoadIndex(id, (floor.Count - 1) / IndexSize) is { } committed &&
            committed.Hashes.Count == (floor.Count - 1) % IndexSize + 1 &&
            committed.Hashes[^1] == pending.RosterHash)
        {
            if (!HasLookup(id, floor, pending.RosterHash))
                throw new InvalidDataException("Committed roster lookup is missing.");
            EnsureLookup(id, floor, pending.RosterHash, floor.Count - 1, pending.LegacyParent);
            _ = LoadRevision(id, pending.RosterHash);
            data.DeleteProtected(PendingName(id));
            return;
        }
        if (pending.OldFloorHash != Digest(floor))
            throw new InvalidDataException("Pending roster revision disagrees with the protected floor.");
        ValidateNext(pending.Roster, floor, false, pending.LegacyParent);
        WriteRevision(id, pending.RosterHash, pending.Roster);
        Advance(id, floor, pending.Roster, pending.RosterHash, pending.LegacyParent);
        data.DeleteProtected(PendingName(id));
    }
    private void Migrate(Guid id, Floor old)
    {
        var pendingBytes = data.LoadProtected(PendingName(id));
        var pending = pendingBytes is null ? null : JsonSerializer.Deserialize<Pending>(pendingBytes, Json);
        if (pendingBytes is not null && (pending is null || pending.Schema != 1 || pending.LegacyParent is not null ||
            pending.OldFloorHash != LegacyDigest(old) || !ValidHash(pending.RosterHash) ||
            SharedWorldRosterTrust.Hash(pending.Roster) != pending.RosterHash))
            throw new InvalidDataException("Legacy pending roster revision is invalid.");
        var state = new Floor(2, id, old.GroupId, old.OwnerPublicKey, Count: 0, Heads: []);
        var hashes = old.Hashes!.ToList();
        foreach (var hash in hashes)
        {
            var roster = LoadRevision(id, hash);
            ValidateNext(roster, state, false);
            state = state with
            {
                Count = state.Count + 1,
                Heads = NextHeads(state.Heads!, roster, hash),
                UnreviewedOwnerEdit = state.UnreviewedOwnerEdit ||
                    state.Count > 0 && roster.Schema == 3 && roster.SignerDeviceId == Guid.Empty &&
                    roster.OwnerLocalBaselineMembers is null
            };
        }
        if (pending is not null)
        {
            ValidateNext(pending.Roster, state, false);
            WriteRevision(id, pending.RosterHash, pending.Roster);
            hashes.Add(pending.RosterHash);
            state = state with
            {
                Count = state.Count + 1,
                Heads = NextHeads(state.Heads!, pending.Roster, pending.RosterHash),
                UnreviewedOwnerEdit = state.UnreviewedOwnerEdit ||
                    state.Count > 0 && pending.Roster.Schema == 3 &&
                    pending.Roster.SignerDeviceId == Guid.Empty &&
                    pending.Roster.OwnerLocalBaselineMembers is null
            };
        }
        var files = Directory.Exists(Root(id)) ?
            Directory.EnumerateFiles(Root(id), "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal) : [];
        if (!files.SetEquals(hashes))
            throw new InvalidDataException("Roster chain files changed or were rolled back.");
        Reserve(id, hashes.Count * 1024L + 16 * 1024);
        for (var i = 0; i < hashes.Count; i += IndexSize)
            SaveIndex(id, i / IndexSize, hashes.Skip(i).Take(IndexSize).ToArray());
        for (var i = 0; i < hashes.Count; i++)
            EnsureLookup(id, state, hashes[i], i);
        SaveFloor(id, state);
        if (pending is not null) data.DeleteProtected(PendingName(id));
    }
    private Floor Ready(Guid id)
    {
        var floor = LoadFloor(id);
        if (floor is null)
        {
            if (data.HasProtected(PendingName(id)) || data.HasProtected(IndexName(id, 0)) ||
                Directory.Exists(Root(id)) && Directory.EnumerateFileSystemEntries(Root(id)).Any())
                throw new InvalidDataException("Protected roster floor is missing.");
            return new Floor(2, id, Guid.Empty, "", Count: 0, Heads: []);
        }
        ValidateFloor(floor, id);
        if (floor.Schema == 1) { Migrate(id, floor); floor = LoadFloor(id)!; }
        RecoverPending(id, floor);
        floor = LoadFloor(id)!;
        ValidateFloor(floor, id);
        if (floor.Count > 0)
        {
            var last = LoadIndex(id, (floor.Count - 1) / IndexSize);
            if (last.Hashes.Count != (floor.Count - 1) % IndexSize + 1 ||
                !floor.Heads!.Contains(last.Hashes[^1]))
                throw new InvalidDataException("Roster floor and index disagree.");
            if (!HasLookup(id, floor, last.Hashes[^1]))
                throw new InvalidDataException("Current roster lookup is missing.");
        }
        if (floor.LegacyProjectionPosition is { } projectionPosition)
        {
            var projection = RevisionAt(id, floor, projectionPosition);
            if (LegacyParent(id, SharedWorldRosterTrust.Hash(projection)) is null)
                throw new InvalidDataException("Legacy roster projection proof is missing.");
        }
        return floor;
    }
    internal long Count(Guid id) { lock (sync) return Ready(id).Count; }
    internal bool HasUnreviewedOwnerEdit(Guid id) { lock (sync) return Ready(id).UnreviewedOwnerEdit; }
    internal SharedWorldRoster? First(Guid id)
    {
        lock (sync) return Ready(id).Count == 0 ? null :
            LoadRevision(id, LoadIndex(id, 0).Hashes[0]);
    }
    internal IReadOnlyList<SharedWorldRoster> Heads(Guid id)
    {
        lock (sync) return Ready(id).Heads!.Select(hash => LoadRevision(id, hash)).ToArray();
    }
    internal bool Contains(Guid id, SharedWorldRoster roster)
    {
        lock (sync)
        {
            var floor = Ready(id);
            if (roster.ProfileId != id || roster.GroupId != floor.GroupId ||
                roster.OwnerPublicKey != floor.OwnerPublicKey ||
                !SharedWorldRosterTrust.VerifySignature(roster)) return false;
            var hash = SharedWorldRosterTrust.Hash(roster);
            return HasLookup(id, floor, hash) &&
                SharedWorldRosterTrust.Hash(LoadRevision(id, hash)) == hash;
        }
    }
    internal bool ContainsFloor(Guid id, SharedRosterFloor trusted)
    {
        lock (sync)
        {
            var floor = Ready(id);
            if (trusted.GroupId != floor.GroupId || floor.Count == 0) return false;
            for (long page = 0; page <= (floor.Count - 1) / IndexSize; page++)
            {
                var index = LoadIndex(id, page);
                if (index.Hashes.Count != Math.Min(IndexSize, floor.Count - index.Offset))
                    throw new InvalidDataException("Roster index was truncated.");
                foreach (var hash in index.Hashes)
                {
                    if (!HasLookup(id, floor, hash))
                        throw new InvalidDataException("Protected roster lookup is missing.");
                    var revision = LoadRevision(id, hash);
                    if (revision.GroupId == trusted.GroupId &&
                        revision.Epoch == trusted.Epoch &&
                        revision.Revision == trusted.Revision &&
                        revision.Signature == trusted.Signature)
                        return SharedWorldRosterTrust.VerifySignature(revision);
                }
            }
            return false;
        }
    }
    internal IReadOnlyList<SharedWorldRoster> ReadPage(Guid id, long offset)
    {
        lock (sync)
        {
            if (offset < 0) throw new InvalidDataException("Roster offset is invalid.");
            var floor = Ready(id);
            var result = new List<SharedWorldRoster>();
            for (var i = offset; i < floor.Count && i - offset < PageSize; i++)
            {
                var index = LoadIndex(id, i / IndexSize);
                if (index.Hashes.Count != Math.Min(IndexSize, floor.Count - index.Offset))
                    throw new InvalidDataException("Roster index was truncated.");
                var hash = index.Hashes[(int)(i % IndexSize)];
                if (!HasLookup(id, floor, hash))
                    throw new InvalidDataException("Protected roster lookup is missing.");
                var roster = LoadRevision(id, hash);
                if (roster.ProfileId != id || roster.GroupId != floor.GroupId ||
                    roster.OwnerPublicKey != floor.OwnerPublicKey)
                    throw new InvalidDataException("Roster revision crossed a world boundary.");
                // On the wire use the schema-3 child's real parent in place of
                // its equivalent legacy anchor. Local Read still retains the
                // exact legacy record; offsets and page bounds stay unchanged.
                var projection = floor.LegacyProjectionPosition == i + 1 ? RevisionAt(id, floor, i + 1) : null;
                var parent = projection is null ? null : LegacyParent(id, SharedWorldRosterTrust.Hash(projection));
                result.Add(parent ?? roster);
            }
            return result;
        }
    }
    internal IReadOnlyList<SharedWorldRoster>? ReadAfter(Guid id, string hash)
    {
        lock (sync)
        {
            if (!ValidHash(hash)) throw new InvalidDataException("Roster anchor is invalid.");
            var floor = Ready(id);
            if (!HasLookup(id, floor, hash))
            {
                // A page may end at the signed parent exported in place of the
                // equivalent legacy anchor. Its protected proof gives that hash
                // an exact cursor position without inventing a journal lookup.
                if (floor.LegacyProjectionPosition is not { } position) return null;
                var projection = RevisionAt(id, floor, position);
                var parent = LegacyParent(id, SharedWorldRosterTrust.Hash(projection));
                return parent is not null && SharedWorldRosterTrust.Hash(parent) == hash
                    ? ReadPage(id, position) : null;
            }
            var lookup = JsonSerializer.Deserialize<Lookup>(data.LoadProtected(LookupName(id, hash))!, Json)!;
            _ = LoadRevision(id, hash);
            var next = checked(lookup.Position + 1);
            if (floor.LegacyProjectionPosition == next)
                next = lookup.Position;
            return ReadPage(id, next);
        }
    }
    internal IReadOnlyList<SharedWorldRoster> ReadAfterRevision(Guid id, long revision)
    {
        lock (sync)
        {
            if (revision < 0) throw new InvalidDataException("Roster revision cursor is invalid.");
            var floor = Ready(id);
            if (floor.Heads!.Count > 1)
                throw new InvalidDataException("Competing roster revisions require owner review.");
            var position = PositionAfterRevision(id, floor, revision);
            if (position > 1)
            {
                var boundary = RevisionAt(id, floor, position - 1);
                var parent = RevisionAt(id, floor, position - 2);
                // A numeric peer cursor cannot distinguish the legacy snapshot
                // from the owner's equal-counter schema-3 representation. Send
                // its signed parent as well, even from a canonical owner journal
                // that never needed a local projection proof.
                if (boundary.Revision == revision && boundary.Schema == 3 &&
                    boundary.SignerDeviceId == Guid.Empty &&
                    (LegacyParent(id, SharedWorldRosterTrust.Hash(boundary)) is not null ||
                     parent.Schema == 2 && SharedWorldRosterTrust.VerifyRevision(boundary, parent,
                         floor.OwnerPublicKey, DateTimeOffset.MinValue, false)))
                    position -= 2;
            }
            return ReadPage(id, position);
        }
    }
    private SharedWorldRoster RevisionAt(Guid id, Floor floor, long position)
    {
        var index = LoadIndex(id, position / IndexSize);
        if (index.Hashes.Count != Math.Min(IndexSize, floor.Count - index.Offset))
            throw new InvalidDataException("Roster index was truncated.");
        var hash = index.Hashes[(int)(position % IndexSize)];
        if (!HasLookup(id, floor, hash))
            throw new InvalidDataException("Protected roster lookup is missing.");
        var item = LoadRevision(id, hash);
        if (item.ProfileId != id || item.GroupId != floor.GroupId || item.OwnerPublicKey != floor.OwnerPublicKey)
            throw new InvalidDataException("Roster revision crossed a world boundary.");
        return item;
    }
    private long PositionAfterRevision(Guid id, Floor floor, long revision)
    {
        // Legacy snapshot numbers can jump. Locate their protected journal
        // positions instead of treating a signed revision as a file offset.
        long lower = 0, upper = floor.Count;
        while (lower < upper)
        {
            var position = lower + (upper - lower) / 2;
            var item = RevisionAt(id, floor, position);
            if (item.Revision <= revision) lower = position + 1;
            else upper = position;
        }
        return lower;
    }
    internal bool SupersededLegacySnapshot(Guid id, SharedWorldRoster snapshot)
    {
        lock (sync)
        {
            if (snapshot.Schema is not (1 or 2) || !SharedWorldRosterTrust.Verify(snapshot)) return false;
            var floor = Ready(id);
            if (floor.Heads!.Count != 1) return false;
            var position = PositionAfterRevision(id, floor, snapshot.Revision);
            if (position > 0 && ProvesLegacyOwnerProjection(id, snapshot,
                    RevisionAt(id, floor, position - 1))) return true;
            if (position >= floor.Count || !LegacyOwnerAdvance(RevisionAt(id, floor, position), snapshot)) return false;
            // A same-counter sibling is a fork, not redundant old metadata.
            if (position > 0)
            {
                var previous = RevisionAt(id, floor, position - 1);
                if (previous.Epoch >= snapshot.Epoch || previous.Revision >= snapshot.Revision) return false;
            }
            return true;
        }
    }
    internal IReadOnlyList<SharedWorldRoster> Read(Guid id)
    {
        lock (sync)
        {
            var floor = Ready(id);
            var state = floor with { Count = 0, Heads = [], UnreviewedOwnerEdit = false, LegacyProjectionPosition = null };
            var result = new List<SharedWorldRoster>();
            for (long i = 0; i < floor.Count; i++)
            {
                var index = LoadIndex(id, i / IndexSize);
                if (index.Hashes.Count != Math.Min(IndexSize, floor.Count - index.Offset))
                    throw new InvalidDataException("Roster index was truncated.");
                var hash = index.Hashes[(int)(i % IndexSize)];
                if (!HasLookup(id, floor, hash))
                    throw new InvalidDataException("Protected roster lookup is missing.");
                var roster = LoadRevision(id, hash);
                var legacyParent = LegacyParent(id, hash);
                ValidateNext(roster, state, false, legacyParent);
                result.Add(roster);
                state = state with
                {
                    Count = state.Count + 1,
                    Heads = legacyParent is null ? NextHeads(state.Heads!, roster, hash) : [hash],
                    LegacyProjectionPosition = legacyParent is null ? state.LegacyProjectionPosition : i,
                    UnreviewedOwnerEdit = state.UnreviewedOwnerEdit ||
                        i > 0 && roster.Schema == 3 && roster.SignerDeviceId == Guid.Empty &&
                        roster.OwnerLocalBaselineMembers is null
                };
            }
            if (!state.Heads!.SequenceEqual(floor.Heads!) ||
                state.UnreviewedOwnerEdit != floor.UnreviewedOwnerEdit ||
                state.LegacyProjectionPosition != floor.LegacyProjectionPosition ||
                Directory.Exists(Root(id)) &&
                Directory.EnumerateFiles(Root(id), "*.json", SearchOption.TopDirectoryOnly).LongCount() != floor.Count)
                throw new InvalidDataException("Roster chain files changed or were rolled back.");
            return result;
        }
    }
    internal bool TryAppendLegacyOwnerProjection(SharedWorldRoster roster, SharedWorldRoster parent,
        string pinnedOwnerPublicKey, bool stopAfterPendingForChecks = false, bool stopAfterFileForChecks = false)
    {
        if (roster is null || parent is null) return false;
        lock (sync)
        {
            var floor = Ready(roster.ProfileId);
            if (floor.Heads is not { Count: 1 } ||
                !SharedWorldRosterTrust.VerifyLegacyOwnerProjection(roster,
                    LoadRevision(roster.ProfileId, floor.Heads[0]), parent, pinnedOwnerPublicKey)) return false;
            AppendCore(roster, pinnedOwnerPublicKey, stopAfterPendingForChecks, stopAfterFileForChecks, parent);
            return true;
        }
    }
    internal void Append(SharedWorldRoster roster, string pinnedOwnerPublicKey,
        bool stopAfterPendingForChecks = false, bool stopAfterFileForChecks = false)
        => AppendCore(roster, pinnedOwnerPublicKey, stopAfterPendingForChecks, stopAfterFileForChecks);
    private void AppendCore(SharedWorldRoster roster, string pinnedOwnerPublicKey,
        bool stopAfterPendingForChecks, bool stopAfterFileForChecks, SharedWorldRoster? legacyParent = null)
    {
        lock (sync)
        {
            var floor = Ready(roster.ProfileId);
            var hash = SharedWorldRosterTrust.Hash(roster);
            if (floor.GroupId != Guid.Empty && (floor.GroupId != roster.GroupId ||
                floor.OwnerPublicKey != pinnedOwnerPublicKey || roster.OwnerPublicKey != pinnedOwnerPublicKey))
                throw new InvalidDataException("Roster chain identity changed.");
            // A companion response and peer catch-up can import the same
            // revision concurrently. Recheck committed membership under this
            // lock even when another importer already advanced beyond it.
            // Retain the exact protected lookup and file; never recreate either.
            if (floor.Count > 0 && HasLookup(roster.ProfileId, floor, hash))
            {
                var retained = LoadRevision(roster.ProfileId, hash);
                if (!SharedWorldRosterTrust.VerifySignature(retained))
                    throw new InvalidDataException("Retained roster signature is invalid.");
                return;
            }
            Reserve(roster.ProfileId, 1024 * 1024);
            if (floor.GroupId == Guid.Empty)
            {
                floor = new Floor(2, roster.ProfileId, roster.GroupId, pinnedOwnerPublicKey,
                    Count: 0, Heads: []);
                SaveFloor(roster.ProfileId, floor);
            }
            if (floor.GroupId != roster.GroupId || floor.OwnerPublicKey != pinnedOwnerPublicKey ||
                JsonSerializer.SerializeToUtf8Bytes(roster, Json).Length > SharedWorldService.MaximumManifestBytes)
                throw new InvalidDataException("Roster chain identity or revision size changed.");
            ValidateNext(roster, floor, true, legacyParent);
            data.SaveProtected(PendingName(roster.ProfileId), JsonSerializer.SerializeToUtf8Bytes(
                new Pending(1, Digest(floor), hash, roster, legacyParent), Json));
            if (stopAfterPendingForChecks) return;
            WriteRevision(roster.ProfileId, hash, roster);
            if (stopAfterFileForChecks) return;
            RecoverPending(roster.ProfileId, floor);
        }
    }
}
