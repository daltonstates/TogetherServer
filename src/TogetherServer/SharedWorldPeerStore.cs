using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// A bounded append-only metadata journal. Caps refuse new input rather than
// pruning an unresolved branch, and there is deliberately no age cutoff.
// It contains no game files, credentials, TLS keys, passwords or local paths.
internal sealed class SharedWorldPeerStore
{
    internal const int MaximumAnnouncements = 4096;
    internal const int MaximumStoreBytes = 16 * 1024 * 1024;
    internal const int MaximumPeers = 256;
    internal const int MaximumKnownBranches = 512;
    private const int MaximumKnownSessions = 4096;
    private const long DiskReserveBytes = 64L * 1024 * 1024;
    private sealed record Document(int Schema, SharedWorldPeerRoot Root,
        IReadOnlyList<SharedWorldPeerAnnouncement> Announcements,
        IReadOnlyList<SharedWorldPeerContact> Contacts);
    private sealed record Anchor(int Schema, string GroupKey);
    private readonly LocalData data;
    private readonly SharedWorldPeerRoot root;
    private readonly SharedWorldPeerTrust trust;
    private readonly TimeProvider clock;
    private readonly object sync;
    internal string StateName { get; }
    private string AnchorName => StateName + ".anchor";

    internal SharedWorldPeerStore(LocalData data, SharedWorldPeerRoot root,
        SharedWorldPeerTrust trust, TimeProvider? clock = null)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root))
            throw new InvalidDataException("The shared world identity is invalid.");
        this.data = data;
        this.root = root;
        this.trust = trust;
        this.clock = clock ?? TimeProvider.System;
        sync = SharedWorldMutationGate.For(data.RootPath);
        StateName = "shared-world-peers-" + root.GroupKey + ".protected";
    }

    private byte[]? ReadProtected(string name, int maximumBytes)
    {
        var path = Path.Combine(data.RootPath, name);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, data.RootPath);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > (long)maximumBytes * 2 + 8192)
            throw new InvalidDataException("Protected peer metadata is linked or oversized.");
        var bytes = data.LoadProtected(name);
        if (bytes is null || bytes.Length > maximumBytes)
            throw new InvalidDataException("Protected peer metadata could not be read.");
        return bytes;
    }

    private Document Read()
    {
        try
        {
            var anchorBytes = ReadProtected(AnchorName, 4096);
            var bytes = ReadProtected(StateName, MaximumStoreBytes);
            if (anchorBytes is null && bytes is null) return new(1, root, [], []);
            var anchor = anchorBytes is null ? null : JsonSerializer.Deserialize<Anchor>(anchorBytes, SharedWorldPeerTrust.Json);
            if (anchor is not { Schema: 1 } || anchor.GroupKey != root.GroupKey || bytes is null)
                throw new InvalidDataException("Protected peer metadata needs review.");
            var document = JsonSerializer.Deserialize<Document>(bytes, SharedWorldPeerTrust.Json);
            if (document is null || document.Schema != 1 || document.Root != root ||
                document.Announcements is null || document.Contacts is null ||
                document.Announcements.Count > MaximumAnnouncements || document.Contacts.Count > MaximumPeers ||
                document.Announcements.Any(value => value is null) ||
                document.Announcements.Select(value => value.AnnouncementHash).Distinct(StringComparer.Ordinal).Count() !=
                    document.Announcements.Count ||
                document.Announcements.Any(value => value.Root != root || !SharedWorldPeerTrust.VerifySignature(value)) ||
                document.Contacts.Any(value => value is null ||
                    !SharedWorldRosterTrust.ValidKey(value.PublicKey) || value.LastAttemptUtc == default ||
                    value.LastAttemptUtc.Offset != TimeSpan.Zero ||
                    value.LastSuccessUtc is { } success && (success.Offset != TimeSpan.Zero || success == default) ||
                    value.RemoteTotal is < 0 or > MaximumAnnouncements) ||
                document.Contacts.Select(value => SharedWorldPeerTrust.SignerKey(value.DeviceId, value.PublicKey))
                    .Distinct(StringComparer.Ordinal).Count() != document.Contacts.Count)
                throw new InvalidDataException("Protected peer metadata failed verification.");
            ValidateLimits(document);
            var prefixes = new List<SharedWorldPeerAnnouncement>();
            foreach (var announcement in document.Announcements)
            {
                if (RequiredRevision(prefixes, announcement) is not null)
                    throw new InvalidDataException("Protected peer metadata has a broken revision chain.");
                prefixes.Add(announcement);
            }
            return document;
        }
        catch (JsonException ex) { throw new InvalidDataException("Protected peer metadata is invalid.", ex); }
    }

    private static string Signer(SharedWorldPeerAnnouncement value) =>
        SharedWorldPeerTrust.SignerKey(value.DeviceId, value.PublicKey);

    private static void ValidateLimits(Document document)
    {
        if (document.Announcements.Select(Signer).Distinct(StringComparer.Ordinal).Count() > MaximumPeers ||
            document.Announcements.SelectMany(value => value.Branches.Select(branch => (Signer(value), branch.BranchHash)))
                .Distinct().Count() > MaximumKnownBranches ||
            document.Announcements.SelectMany(value => value.Sessions.Select(session => (Signer(value), session.SessionId)))
                .Distinct().Count() > MaximumKnownSessions)
            throw new InvalidDataException("The peer metadata capacity is full; existing histories were retained.");
    }

    private void Save(Document document)
    {
        ValidateLimits(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SharedWorldPeerTrust.Json);
        if (document.Announcements.Count > MaximumAnnouncements || bytes.Length > MaximumStoreBytes)
            throw new InvalidDataException("The peer metadata capacity is full; existing histories were retained.");
        var drive = new DriveInfo(Path.GetPathRoot(data.RootPath)!);
        if (drive.AvailableFreeSpace < DiskReserveBytes + bytes.Length * 2L + 8192)
            throw new IOException("Peer metadata needs free space; existing histories were retained.");
        if (!data.HasProtected(AnchorName))
            data.SaveProtected(AnchorName, JsonSerializer.SerializeToUtf8Bytes(new Anchor(1, root.GroupKey), SharedWorldPeerTrust.Json));
        data.SaveProtected(StateName, bytes);
    }

    // Old records do not advance a head. A second signed record at an already
    // known revision is retained as equivocation evidence, including an old
    // revision learned after its descendants. Missing parents request catchup.
    private static long? RequiredRevision(IReadOnlyList<SharedWorldPeerAnnouncement> known,
        SharedWorldPeerAnnouncement value)
    {
        var same = known.Where(item => item.DeviceId == value.DeviceId && item.PublicKey == value.PublicKey).ToArray();
        if (value.Revision == 1) return null;
        if (same.Any(item => item.Revision == value.Revision - 1 && item.AnnouncementHash == value.PreviousHash)) return null;
        var highest = same.Length == 0 ? 0 : same.Max(item => item.Revision);
        return Math.Min(value.Revision - 1, highest == long.MaxValue ? highest : highest + 1);
    }

    private SharedWorldPeerImportResult ImportCore(SharedWorldPeerAnnouncement announcement)
    {
        if (!trust.Verify(announcement)) return new(false, "PeerRejected", "The signed peer metadata is not authorized for this world.");
        var document = Read();
        if (document.Announcements.Any(value => value.AnnouncementHash == announcement.AnnouncementHash))
            return new(false, "AlreadyKnown", "The signed peer metadata is already stored.");
        if (RequiredRevision(document.Announcements, announcement) is { } needed)
            return new(false, "PeerCatchUpRequired", "Earlier signed peer metadata is needed first.", RequiredRevision: needed);
        var next = document with { Announcements = [.. document.Announcements, announcement] };
        Save(next);
        return HasSignerConflict(next.Announcements.Where(value => Signer(value) == Signer(announcement)).ToArray())
            ? new(true, "PeerConflictPreserved", "Competing signed peer metadata was kept for review.", 1)
            : new(true, "PeerMetadataStored", "Signed peer metadata was stored.", 1);
    }

    internal SharedWorldPeerImportResult Import(SharedWorldPeerAnnouncement announcement)
    {
        lock (sync) return ImportCore(announcement);
    }

    internal SharedWorldPeerImportResult ImportPage(SharedWorldPeerPage page)
    {
        lock (sync)
        {
            // Reapply the same bounded decoder for direct in-process callers.
            page = SharedWorldPeerTrust.DecodePage(JsonSerializer.SerializeToUtf8Bytes(page, SharedWorldPeerTrust.Json));
            if (page.Root != root) return new(false, "PeerRejected", "Peer metadata belongs to another world.");
            var accepted = 0;
            var rejected = 0;
            foreach (var announcement in page.Announcements)
            {
                var result = ImportCore(announcement);
                accepted += result.Accepted;
                if (result.Code == "PeerCatchUpRequired") return result with { Changed = accepted > 0, Accepted = accepted };
                if (result.Code == "PeerRejected") rejected++;
            }
            return new(accepted > 0, rejected == 0 ? "PeerMetadataSynced" : "PeerMetadataPartial",
                rejected == 0 ? "Signed peer metadata was reconciled." : "Only currently authorized peer metadata was reconciled.", accepted);
        }
    }

    internal SharedWorldPeerPage ReadPage(int offset = 0, int limit = SharedWorldPeerTrust.PageSize)
    {
        lock (sync)
        {
            if (offset < 0 || limit is < 1 or > SharedWorldPeerTrust.PageSize)
                throw new InvalidDataException("The peer metadata page request is invalid.");
            var document = Read();
            if (offset > document.Announcements.Count)
                throw new InvalidDataException("The peer metadata cursor is ahead of this peer.");
            return new(1, root, offset, document.Announcements.Count,
                document.Announcements.Skip(offset).Take(limit).ToArray());
        }
    }

    internal SharedWorldPeerAnnouncement Publish(Guid deviceId, ECDsa key,
        IReadOnlyList<SharedWorldPeerRoute> routes, IReadOnlyList<SharedWorldPeerSession> sessions,
        IReadOnlyList<SharedWorldPeerBranch> branches, IReadOnlyList<string>? availableVersionHashes = null)
    {
        lock (sync)
        {
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var document = Read();
            var prior = document.Announcements.Where(value => value.DeviceId == deviceId && value.PublicKey == publicKey).ToArray();
            if (HasSignerConflict(prior)) throw new InvalidDataException("Competing peer announcements need review before publishing.");
            var last = prior.MaxBy(value => value.Revision);
            routes = routes.OrderBy(value => value.Address, StringComparer.Ordinal).ToArray();
            sessions = sessions.OrderBy(value => value.SessionId).ToArray();
            branches = branches.OrderBy(value => value.BranchHash, StringComparer.Ordinal).ToArray();
            availableVersionHashes = (availableVersionHashes ?? []).Order(StringComparer.Ordinal).ToArray();
            if (last is not null && JsonSerializer.Serialize(new
                { last.Routes, last.Sessions, last.Branches, last.AvailableVersionHashes }, SharedWorldPeerTrust.Json) ==
                JsonSerializer.Serialize(new
                { Routes = routes, Sessions = sessions, Branches = branches, AvailableVersionHashes = availableVersionHashes }, SharedWorldPeerTrust.Json))
                return last;
            if (last?.Revision == long.MaxValue) throw new InvalidDataException("The peer revision limit was reached.");
            var announcement = SharedWorldPeerTrust.Sign(new(1, root, deviceId, publicKey,
                (last?.Revision ?? 0) + 1, last?.AnnouncementHash, routes, sessions, branches,
                availableVersionHashes ?? [], clock.GetUtcNow(), "", ""), key);
            var result = ImportCore(announcement);
            if (!result.Changed) throw new InvalidDataException(result.Message);
            return announcement;
        }
    }

    internal void RecordSync(Guid deviceId, string publicKey, bool succeeded, int remoteTotal = 0)
    {
        lock (sync)
        {
            if (trust.Access(deviceId, publicKey) is null || remoteTotal is < 0 or > MaximumAnnouncements)
                throw new InvalidDataException("The peer sync observation is invalid.");
            var document = Read();
            var old = document.Contacts.SingleOrDefault(value => value.DeviceId == deviceId && value.PublicKey == publicKey);
            var now = clock.GetUtcNow();
            var contact = new SharedWorldPeerContact(deviceId, publicKey, now,
                succeeded ? now : old?.LastSuccessUtc, succeeded, remoteTotal);
            var contacts = document.Contacts.Where(value => value.DeviceId != deviceId || value.PublicKey != publicKey)
                .Append(contact).ToArray();
            if (contacts.Length > MaximumPeers) throw new InvalidDataException("The peer contact capacity is full.");
            Save(document with { Contacts = contacts });
        }
    }

    private static bool HasSignerConflict(IReadOnlyList<SharedWorldPeerAnnouncement> records) =>
        records.GroupBy(value => value.Revision).Any(group => group.Select(value => value.AnnouncementHash).Distinct().Count() > 1) ||
        records.SelectMany(value => value.Branches).GroupBy(value => value.BranchHash)
            .Any(group => group.Select(value => value.BaseVersionHash).Distinct().Count() > 1) ||
        records.SelectMany(value => value.Sessions).GroupBy(value => (value.SessionId, value.Revision))
            .Any(group => group.Distinct().Count() > 1);

    internal SharedWorldPeerGroupState Project(IReadOnlyList<WorldAuthorityRecord>? acceptedAuthorityHeads = null)
    {
        lock (sync)
        {
            var document = Read();
            var peers = new List<SharedWorldPeerView>();
            var branches = new List<SharedWorldPeerBranchView>();
            var sessions = new List<SharedWorldPeerSessionView>();
            foreach (var signer in document.Announcements.GroupBy(Signer).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var records = signer.ToArray();
                var last = records.OrderByDescending(value => value.Revision)
                    .ThenBy(value => value.AnnouncementHash, StringComparer.Ordinal).First();
                var conflict = HasSignerConflict(records);
                var access = trust.Access(last.DeviceId, last.PublicKey);
                var authorized = access is not null;
                var hostingAuthorized = access is { Receive: true, EligibleHost: true };
                var contact = document.Contacts.SingleOrDefault(value => value.DeviceId == last.DeviceId && value.PublicKey == last.PublicKey);
                peers.Add(new(last.DeviceId, last.PublicKey, last.Revision,
                    authorized && !conflict ? last.Routes : [], conflict, authorized,
                    access?.Receive == true && !conflict ? last.AvailableVersionHashes : [], last.AnnouncedUtc, contact));
                foreach (var branch in records.SelectMany(value => value.Branches).Distinct().GroupBy(value => value.BranchHash))
                {
                    var all = branch.ToArray();
                    // Only an explicit immediate parent edge retires an older
                    // descriptor. A number or a PC clock never selects a head.
                    var heads = all.Where(value => !all.Any(child => child.BaseVersionHash == value.BaseVersionHash &&
                        child.ParentVersionHash == value.HeadVersionHash && child.VersionNumber == value.VersionNumber + 1)).ToArray();
                    foreach (var head in heads.OrderBy(value => value.HeadVersionHash, StringComparer.Ordinal))
                        branches.Add(new(last.DeviceId, last.PublicKey, head, conflict || heads.Length > 1, hostingAuthorized));
                }
                foreach (var session in records.SelectMany(value => value.Sessions).Distinct().GroupBy(value => value.SessionId))
                {
                    var revision = session.Max(value => value.Revision);
                    var heads = session.Where(value => value.Revision == revision).ToArray();
                    foreach (var head in heads)
                        sessions.Add(new(last.DeviceId, last.PublicKey, head, conflict || heads.Length > 1, hostingAuthorized));
                }
            }
            // The supplied heads must already be accepted by WorldAuthorityStore
            // with complete lineage. Announcement references alone cannot create
            // a default, settle a split, or grant hosting authority.
            var authorityHeads = acceptedAuthorityHeads ?? [];
            if (authorityHeads.Count > WorldAuthorityTrust.MaximumCompetingHeads || authorityHeads.Any(value =>
                !WorldAuthorityTrust.Verify(value, rosterChainVerified: true) ||
                value.Roster.OwnerPublicKey != root.OwnerPublicKey || value.Proposal.ProfileId != root.ProfileId ||
                value.Proposal.GroupId != root.GroupId))
                throw new InvalidDataException("The accepted shared default heads are invalid.");
            var choices = authorityHeads.Select(value => new SharedWorldPeerDefaultChoice(value.RecordHash, value.Version.VersionHash))
                .Distinct().OrderBy(value => value.AuthorityHash, StringComparer.Ordinal).ToArray();
            var lastSync = document.Contacts.Where(value => value.LastSuccessUtc is not null)
                .Select(value => value.LastSuccessUtc).Max();
            var hasConflicts = peers.Any(value => value.HasConflict) || branches.Any(value => value.HasConflict) || choices.Length > 1;
            var capacity = document.Announcements.Count >= MaximumAnnouncements ||
                document.Announcements.SelectMany(value => value.Branches.Select(branch => (Signer(value), branch.BranchHash)))
                    .Distinct().Count() >= MaximumKnownBranches ||
                document.Announcements.SelectMany(value => value.Sessions.Select(session => (Signer(value), session.SessionId)))
                    .Distinct().Count() >= MaximumKnownSessions ||
                JsonSerializer.SerializeToUtf8Bytes(document, SharedWorldPeerTrust.Json).Length >
                    MaximumStoreBytes - SharedWorldPeerTrust.MaximumAnnouncementBytes;
            var health = new SharedWorldPeerReplicationHealth(peers.Count, peers.Count(value => value.Routes.Count > 0),
                peers.Count(value => value.Authorized && value.Contact?.LastAttemptSucceeded == true),
                peers.Count(value => value.AvailableVersionHashes.Count > 0), lastSync, hasConflicts, capacity,
                capacity ? "Metadata capacity needs review" : hasConflicts ? "Competing metadata preserved" :
                lastSync is null ? "No peer sync recorded" : "Signed metadata cached; routes need live checks");
            var knownSessions = sessions.Count;
            return new(root, root.GroupKey, peers, branches,
                sessions.OrderByDescending(value => value.Session.StartedUtc).ThenBy(value => value.Session.SessionId).Take(64).ToArray(),
                choices, choices.Length > 1 || choices.Length == 0 &&
                    branches.Select(value => value.Branch.HeadVersionHash).Distinct(StringComparer.Ordinal).Count() > 1,
                knownSessions, document.Announcements.Count, health);
        }
    }
}
