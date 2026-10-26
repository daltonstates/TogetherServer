using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerPreparedStartResult(bool Ok, string Code, string Message,
    WorldPeerPreparedStartSource? Source = null, string? ObservationHash = null);

// A prepared Start reads its own protected local continuation. This proof is
// separate from recovery retention: no caller may supply a directory, and no
// recovery-cache path is accepted or created here.
internal sealed class WorldPeerPreparedStartSource : IDisposable
{
    private readonly LiveSaveSourceContext context;
    private readonly string directory;
    private readonly string setupHash;
    private WorldPeerVerifiedCopy? lease;
    internal SharedWorldPeerRoot Root => context.Root;
    internal Guid LocalProfileId => context.LocalProfileId;
    internal string BranchHash => context.Branch!.BranchHash;
    internal WorldPeerSaveDescriptor Descriptor { get; }

    private WorldPeerPreparedStartSource(LiveSaveSourceContext context, SharedWorldVersion version,
        string directory, string setupHash, WorldPeerVerifiedCopy lease)
    {
        this.context = context;
        this.directory = directory;
        this.setupHash = setupHash;
        this.lease = lease;
        Descriptor = new(version, context.Branch);
    }

    internal static WorldPeerPreparedStartSource Open(LocalData data, SharedWorldPeerRoot root,
        Guid localProfileId, string branchHash, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = ReadScope(data, root, localProfileId, branchHash);
        var held = WorldPeerVerifiedCopy.Open(data.RootPath, scope.Directory, scope.Version, cancellationToken,
            (bytes, total, files, count) => WorldPeerSourceProgress.Report(progress,
                new("Verifying", "Verifying and holding this PC's complete prepared source.", bytes, total, files, count)));
        var result = new WorldPeerPreparedStartSource(scope.Context, scope.Version, scope.Directory, scope.SetupHash, held);
        try
        {
            var vault = Path.GetDirectoryName(scope.Directory)!;
            WorldPeerFileTransfer.VerifyHistory(vault, result.Descriptor, scope.Context.Branch!.Offer.Version);
            result.RequireCurrent(data, root, localProfileId, branchHash, cancellationToken);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void RequireCurrent(LocalData data, SharedWorldPeerRoot expectedRoot,
        Guid expectedLocalProfileId, string expectedBranchHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (Root != expectedRoot || LocalProfileId != expectedLocalProfileId || BranchHash != expectedBranchHash ||
                lease?.Covers(data.RootPath, directory, Descriptor.Version) != true)
                throw new InvalidDataException("The held prepared source or its exact local scope changed.");
            var current = ReadScope(data, expectedRoot, expectedLocalProfileId, expectedBranchHash);
            if (!SharedWorldBranchLiveSource.SameSource(context, current.Context) ||
                current.Version.VersionHash != Descriptor.Version.VersionHash ||
                current.SetupHash != setupHash ||
                !current.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The current prepared publication, setup or sharing permission changed.");
        }
    }

    private static (LiveSaveSourceContext Context, SharedWorldVersion Version, string Directory, string SetupHash) ReadScope(
        LocalData data, SharedWorldPeerRoot root, Guid localProfileId, string branchHash)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (!SharedWorldPeerTrust.ValidRoot(root) || localProfileId == Guid.Empty ||
                !SharedWorldPeerTrust.ValidHash(branchHash)) throw new InvalidDataException("Choose the exact prepared local source.");
            var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == localProfileId) ??
                throw new InvalidDataException("The prepared local profile is unavailable.");
            var current = SharedWorldBranchLiveSource.Resolve(data, profile);
            if (current.Root != root || current.Branch?.BranchHash != branchHash ||
                !new WorldPeerReceiveConsentStore(data).Effective(root, current.SourceDeviceId, legacyConsent: true))
                throw new InvalidDataException("The prepared source's current signed permission or consent changed.");
            var branch = current.Branch ?? throw new InvalidDataException("The prepared branch is unavailable.");
            var state = SharedWorldBranchLiveSource.ReadState(data, localProfileId) ??
                throw new InvalidDataException("The protected local preparation is unavailable.");
            var store = new SharedWorldSeparateCopyStore(data);
            var published = state.Started ? store.ReadPublishedManifestHead(branch) : null;
            var version = published ?? branch.Offer.Version;
            if (!SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, version))
                throw new InvalidDataException("The prepared source does not extend this exact branch.");
            var vault = published is null ? store.RetainedRoot(branch) : store.PublishedRoot(branch);
            // Compare decoded setup semantics: DPAPI ciphertext is randomized,
            // and the portable sharing setup deliberately omits local choices.
            var setup = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
            var setupHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(setup)));
            return (current, version, SharedWorldService.SafeChild(vault, version.VersionHash), setupHash);
        }
    }

    public void Dispose() { lease?.Dispose(); lease = null; }
}
