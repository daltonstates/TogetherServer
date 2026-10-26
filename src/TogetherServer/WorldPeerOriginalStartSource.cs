using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerOriginalStartResult(bool Ok, string Code, string Message,
    WorldPeerOriginalStartSource? Source = null, string? ObservationHash = null,
    bool RecoveryRequired = false);

// A local original Start holds its existing immutable publication. This is not
// a recovery source, and no caller supplies a directory or retained-copy path.
internal sealed class WorldPeerOriginalStartSource : IDisposable
{
    private readonly string directory;
    private readonly string profileHash;
    private readonly string scopeHash;
    private WorldPeerVerifiedCopy? lease;
    internal SharedWorldPeerRoot Root { get; }
    internal WorldPeerSaveDescriptor Descriptor { get; }

    private WorldPeerOriginalStartSource(SharedWorldPeerRoot root, SharedWorldVersion version,
        string directory, string profileHash, string scopeHash, WorldPeerVerifiedCopy lease)
    {
        Root = root;
        Descriptor = new(version);
        this.directory = directory;
        this.profileHash = profileHash;
        this.scopeHash = scopeHash;
        this.lease = lease;
    }

    internal static WorldPeerOriginalStartSource Open(LocalData data, SharedWorldPeerRoot root,
        SharedWorldVersion expected, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (SharedWorldVersion Version, string Directory, string ProfileHash, string ScopeHash) scope;
        lock (SharedWorldMutationGate.For(data.RootPath))
            scope = ReadScope(data, root);
        if (scope.Version.VersionHash != expected.VersionHash || scope.Version.Signature != expected.Signature)
            throw new InvalidDataException("The original publication changed after latest-save selection.");
        var held = WorldPeerVerifiedCopy.Open(data.RootPath, scope.Directory, scope.Version, cancellationToken,
            (bytes, total, files, count) => WorldPeerSourceProgress.Report(progress,
                new("Verifying", "Verifying and holding this PC's original publication.", bytes, total, files, count)));
        var result = new WorldPeerOriginalStartSource(root, scope.Version, scope.Directory,
            scope.ProfileHash, scope.ScopeHash, held);
        try { result.RequireCurrent(data, root, cancellationToken); return result; }
        catch { result.Dispose(); throw; }
    }

    internal void RequireCurrent(LocalData data, SharedWorldPeerRoot expectedRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (Root != expectedRoot || lease?.Covers(data.RootPath, directory, Descriptor.Version) != true)
                throw new InvalidDataException("The held original publication or its exact world changed.");
            var current = ReadScope(data, expectedRoot);
            if (current.Version.VersionHash != Descriptor.Version.VersionHash || current.ScopeHash != scopeHash ||
                !current.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The current original publication, settings or sharing permission changed.");
        }
    }

    internal void RequireProfile(LocalData data, ServerProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Id != Root.ProfileId || ProfileHash(profile) != profileHash)
            throw new InvalidDataException("The original Start profile changed after review.");
        RequireCurrent(data, Root, cancellationToken);
    }

    private static (SharedWorldVersion Version, string Directory, string ProfileHash, string ScopeHash) ReadScope(
        LocalData data, SharedWorldPeerRoot root)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root)) throw new InvalidDataException("Choose the exact original shared world.");
        var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == root.ProfileId);
        if (profile is null || !profile.SharedSavesEnabled || profile.SeparateCopySourceProfileId is not null ||
            profile.SeparateCopyBranchHash is not null || profile.WorldLoadRehearsalId is not null ||
            File.Exists(Path.Combine(data.RootPath, "shared-worlds", root.ProfileId.ToString("N"), "roster-dirty")) ||
            SharedWorldService.RetainedOwnerPublicKey(data) != root.OwnerPublicKey ||
            !new WorldPeerReceiveConsentStore(data).Effective(root, Guid.Empty, legacyConsent: true))
            throw new InvalidDataException("The original owner's retained identity or current consent changed.");
        var worlds = new SharedWorldService(data, PublishedReadOnlyCapture.Instance);
        var roster = worlds.ReadRoster(profile, requireRetainedSigningKey: true);
        var version = worlds.Status(profile).Latest;
        if (roster?.OwnerPublicKey != root.OwnerPublicKey || roster.GroupId != root.GroupId || version is null ||
            version.ProfileId != root.ProfileId || version.GroupId != root.GroupId || version.Game != profile.Kind ||
            version.WorldId != profile.WorldId)
            throw new InvalidDataException("The current original publication or signed roster is unavailable.");
        var path = Path.Combine(data.RootPath, "shared-worlds", root.ProfileId.ToString("N"), root.GroupId.ToString("N"),
            version.Number.ToString(CultureInfo.InvariantCulture));
        var setup = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
        var identity = ProfileHash(profile);
        var scope = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Profile = identity, Setup = setup, Roster = SharedWorldRosterTrust.Hash(roster), Version = version.VersionHash })));
        return (version, path, identity, scope);
    }

    private static string ProfileHash(ServerProfile profile) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile)));

    // Reading a publication must not initialize backup recovery or create any
    // new capture. This adapter makes that scope explicit and fails closed.
    private sealed class PublishedReadOnlyCapture : ISharedWorldCaptureAdapter
    {
        internal static readonly PublishedReadOnlyCapture Instance = new();
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId) =>
            throw new InvalidOperationException("Original Start only reads an existing immutable publication.");
    }

    public void Dispose() { lease?.Dispose(); lease = null; }
}
