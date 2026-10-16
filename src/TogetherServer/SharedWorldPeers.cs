using System.Text.Json.Serialization;

namespace TogetherServer;

// The owner key, source profile and source group identify a world forever.
// Neither a new endpoint nor a separate local hosting profile changes this key.
public sealed record SharedWorldPeerRoot(string OwnerPublicKey, Guid ProfileId, Guid GroupId)
{
    [JsonIgnore]
    public string GroupKey => SharedWorldPeerTrust.RootKey(this);
}

// Only a literal-IP HTTPS origin is allowed. The transport must use this exact
// pinned origin and its fixed world routes; this is never an arbitrary proxy URL.
public sealed record SharedWorldPeerRoute(string Address, string CertificateSha256);

// These are signed discovery hints, not manifest verification or permission to
// start a game. BranchHash identifies the immutable separate-copy declaration.
public sealed record SharedWorldPeerBranch(string BranchHash, string BaseVersionHash,
    string HeadVersionHash, string? ParentVersionHash, long VersionNumber,
    string? AuthorityHash = null);

public sealed record SharedWorldPeerSession(Guid SessionId, string BranchHash, long Revision,
    string State, string BaseVersionHash, string? HeadVersionHash = null,
    DateTimeOffset? StartedUtc = null, DateTimeOffset? EndedUtc = null);

// An announcement is immutable and may be forwarded unchanged by any group
// member. AvailableVersionHashes describes only the signer's claimed copies;
// actual payload access still requires Receive, consent and verified manifests.
public sealed record SharedWorldPeerAnnouncement(int Schema, SharedWorldPeerRoot Root,
    Guid DeviceId, string PublicKey, long Revision, string? PreviousHash,
    IReadOnlyList<SharedWorldPeerRoute> Routes,
    IReadOnlyList<SharedWorldPeerSession> Sessions,
    IReadOnlyList<SharedWorldPeerBranch> Branches,
    IReadOnlyList<string> AvailableVersionHashes, DateTimeOffset AnnouncedUtc,
    string AnnouncementHash, string Signature);

public sealed record SharedWorldPeerPage(int Schema, SharedWorldPeerRoot Root,
    int Offset, int Total, IReadOnlyList<SharedWorldPeerAnnouncement> Announcements)
{
    [JsonIgnore]
    public int NextOffset => Offset + Announcements.Count;
}

// This value is created by trusted local membership resolution, never decoded
// from an announcement. Following metadata is independent of save permissions.
internal sealed record SharedWorldPeerAccess(bool ViewMetadata, bool Receive,
    bool EligibleHost, bool RecoveryVoter, bool ManageSharing, bool Reshare = false);

public sealed record SharedWorldPeerImportResult(bool Changed, string Code,
    string Message, int Accepted = 0, long? RequiredRevision = null);
public sealed record SharedWorldPeerContact(Guid DeviceId, string PublicKey,
    DateTimeOffset LastAttemptUtc, DateTimeOffset? LastSuccessUtc, bool LastAttemptSucceeded,
    int RemoteTotal);
public sealed record SharedWorldPeerView(Guid DeviceId, string PublicKey, long Revision,
    IReadOnlyList<SharedWorldPeerRoute> Routes, bool HasConflict, bool Authorized,
    IReadOnlyList<string> AvailableVersionHashes, DateTimeOffset AnnouncedUtc,
    SharedWorldPeerContact? Contact);
public sealed record SharedWorldPeerBranchView(Guid DeviceId, string PublicKey,
    SharedWorldPeerBranch Branch, bool HasConflict, bool Authorized);
public sealed record SharedWorldPeerSessionView(Guid DeviceId, string PublicKey,
    SharedWorldPeerSession Session, bool HasConflict, bool Authorized);
public sealed record SharedWorldPeerDefaultChoice(string AuthorityHash, string VersionHash);
public sealed record SharedWorldPeerReplicationHealth(int KnownPeers, int PeersWithRoutes,
    int PeersLastSyncSucceeded, int ClaimedCopyPeers, DateTimeOffset? LastSuccessfulSyncUtc,
    bool HasConflicts, bool CapacityReached, string State);
public sealed record SharedWorldPeerGroupState(SharedWorldPeerRoot Root, string GroupKey,
    IReadOnlyList<SharedWorldPeerView> Peers,
    IReadOnlyList<SharedWorldPeerBranchView> Branches,
    IReadOnlyList<SharedWorldPeerSessionView> Sessions,
    IReadOnlyList<SharedWorldPeerDefaultChoice> DefaultChoices,
    bool NeedsChoice, int KnownSessions, int AnnouncementCount,
    SharedWorldPeerReplicationHealth Replication);
