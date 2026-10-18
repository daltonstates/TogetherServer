using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

internal sealed record LiveSaveSourceContext(Guid LocalProfileId, SharedWorldPeerRoot Root,
    WorldSeparateCopyBranch? Branch, Guid SourceDeviceId, string PublishingPublicKey,
    SharedWorldRoster CurrentRoster);

// A recovery source is authorized by its protected local restore and signed
// branch, independently of whichever session is the group's current default.
internal static class SharedWorldBranchLiveSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string StateName(Guid localId) => $"separate-host-{localId:N}.protected";

    internal static Guid LocalProfileId(string branchHash)
    {
        if (!SharedWorldPeerTrust.ValidHash(branchHash)) throw new InvalidDataException("Choose an exact recovery branch.");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("TogetherServer separate local profile v1\n" + branchHash));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal static string WorldRoot(LocalData data, SharedWorldVersion version, Guid localId) => version.Game switch
    {
        GameKinds.Valheim => Path.Combine(data.WorldImportsRoot, localId.ToString("N")),
        GameKinds.Factorio => Path.Combine(data.FactorioServersRoot, localId.ToString("N"), version.WorldId),
        GameKinds.MinecraftJava or GameKinds.MinecraftBedrock => Path.Combine(data.MinecraftInstallRoot, localId.ToString("N")),
        _ => data.NewWorldDirectory(localId)
    };

    internal static string SetupHash(ServerProfile profile, TakeoverLocalSetup setup) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.Id,
            profile.Kind,
            profile.WorldId,
            profile.WorldDirectory,
            ProfileGamePort = profile.GamePort,
            profile.ExecutablePath,
            profile.ServerName,
            profile.SeparateCopySourceProfileId,
            profile.SeparateCopyBranchHash,
            profile.Minecraft?.ServerJarPath,
            profile.Factorio?.RconPort,
            setup.ServerFile,
            setup.GameVersion,
            setup.EnabledAddOns,
            setup.ControlPort,
            setup.GamePort
        })));

    internal static SeparateCopyHostState? ReadState(LocalData data, Guid localId)
    {
        var name = StateName(localId);
        var bytes = data.LoadProtected(name);
        if (bytes is null)
        {
            if (data.HasProtected(name) || data.Recovery.Notices.Any(notice => notice.StateFile == name))
                throw new InvalidDataException("Separate-copy hosting state could not be read.");
            return null;
        }
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("Separate-copy hosting state is oversized.");
        var state = JsonSerializer.Deserialize<SeparateCopyHostState>(bytes);
        if (state is not { Schema: 1 } || state.LocalProfileId != localId ||
            state.SourceProfileId == Guid.Empty || state.SourceProfileId == localId || state.GroupId == Guid.Empty ||
            !SharedWorldPeerTrust.ValidHash(state.BranchHash) || !SharedWorldPeerTrust.ValidHash(state.VersionHash) ||
            !Path.IsPathFullyQualified(state.WorldDirectory))
            throw new InvalidDataException("Separate-copy hosting state is invalid.");
        return state;
    }

    internal static WorldSeparateCopyBranch RequireCurrentBranch(LocalData data, Guid sourceId,
        string branchHash, DateTimeOffset? now = null)
    {
        var branch = new SharedWorldSeparateCopyStore(data).Read(sourceId).SingleOrDefault(item => item.BranchHash == branchHash);
        if (branch is null || !SharedWorldSeparateCopyStore.Verify(branch) ||
            !SharedWorldSeparateCopyStore.CandidateCurrentlyAuthorized(data, branch.Offer, now) ||
            !SharedWorldSeparateCopyStore.HasLocalBranchKeys(data, branch))
            throw new InvalidDataException("The recovery branch's current signed access or local keys need review.");
        var proposal = branch.Offer.Proposal;
        var parent = new WorldAuthorityStore(data).Read(sourceId).SingleOrDefault(item => item.RecordHash == proposal.ParentAuthorityHash);
        if (proposal.ParentAuthorityHash is not null && parent is null ||
            proposal.Epoch != (parent?.Proposal.Epoch ?? 0) + 1 || branch.SourceBranch is null &&
            !(parent?.Version.VersionHash == branch.Offer.Version.VersionHash ||
              branch.Offer.Version.SigningPublicKey == (parent?.Proposal.CandidatePublicKey ?? branch.Offer.Roster.OwnerPublicKey)))
            throw new InvalidDataException("The recovery branch's captured source history needs review.");
        return branch;
    }

    internal static LiveSaveSourceContext Resolve(LocalData data, ServerProfile profile,
        DateTimeOffset? now = null, bool requireStarted = false)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (data.Recovery.LifecycleBlocked || profile.SeparateCopySourceProfileId is not { } sourceId ||
                profile.SeparateCopyBranchHash is not { } branchHash || profile.SharedSavesEnabled ||
                profile.WorldLoadRehearsalId is not null || profile.CrashRecovery.Enabled)
                throw new InvalidDataException("Choose a protected local recovery source.");
            var settings = data.LoadSettings();
            var saved = settings.Profiles.SingleOrDefault(item => item.Id == profile.Id);
            if (saved is null || JsonSerializer.Serialize(saved, Json) != JsonSerializer.Serialize(profile, Json))
                throw new InvalidDataException("The saved recovery profile changed.");
            var branch = RequireCurrentBranch(data, sourceId, branchHash, now);
            var version = branch.Offer.Version;
            var state = ReadState(data, profile.Id);
            var setup = new TakeoverLocalSetup(profile.Kind == GameKinds.MinecraftJava ? profile.Minecraft?.ServerJarPath : profile.ExecutablePath,
                version.PortableSetup.GameVersion, version.PortableSetup.AddOns, true, settings.CompanionPort, profile.GamePort);
            if (state is not { Ready: true } || requireStarted && !state.Started ||
                profile.Id != LocalProfileId(branchHash) || state.LocalProfileId != profile.Id ||
                state.SourceProfileId != sourceId || state.BranchHash != branchHash || state.GroupId != version.GroupId ||
                state.VersionHash != version.VersionHash || profile.Kind != version.Game || profile.WorldId != version.WorldId ||
                state.WorldDirectory != WorldRoot(data, version, profile.Id) || profile.WorldDirectory != state.WorldDirectory ||
                state.SetupHash != SetupHash(profile, setup))
                throw new InvalidDataException("The protected recovery world, manual setup or local identity changed.");
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, state.WorldDirectory);
            var roster = SharedWorldSeparateCopyStore.CurrentRoster(data, branch.Offer.Roster);
            return new(profile.Id, new(roster.OwnerPublicKey, sourceId, roster.GroupId), branch,
                branch.Offer.Proposal.ProposerDeviceId, branch.Offer.Proposal.CandidatePublicKey, roster);
        }
    }

    internal static bool SameSource(LiveSaveSourceContext expected, LiveSaveSourceContext actual) =>
        expected.LocalProfileId == actual.LocalProfileId && expected.Root == actual.Root &&
        expected.Branch?.BranchHash == actual.Branch?.BranchHash && expected.SourceDeviceId == actual.SourceDeviceId &&
        expected.PublishingPublicKey == actual.PublishingPublicKey &&
        expected.CurrentRoster.Signature == actual.CurrentRoster.Signature;

    internal static bool IsOwnedSaveSource(LocalData data, ServerProfile profile, string source)
    {
        try
        {
            var context = Resolve(data, profile);
            var expected = profile.Kind switch
            {
                GameKinds.MinecraftJava => Path.Combine(profile.WorldDirectory, profile.WorldId),
                GameKinds.MinecraftBedrock => Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId),
                GameKinds.Valheim or GameKinds.Factorio or GameKinds.Terraria or GameKinds.Fixture => profile.WorldDirectory,
                _ => throw new InvalidDataException("This game has no reviewed recovery save source.")
            };
            return context.Branch is not null && Path.IsPathFullyQualified(source) &&
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ExpectedFailure(ex)) { return false; }
    }

    internal static bool ExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or
        UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or
        InvalidOperationException or OverflowException or Win32Exception or NotSupportedException;

    // Directory provenance is held from LocalData's nonempty locked root. Each
    // subsequent open is relative, denies delete, and rejects links natively.
    internal sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal SafeFileHandle Handle => handles[^1];
        internal DirectoryPins(LocalData data, string path, bool create = false)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(data.RootPath, full);
                if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("The branch publication path escaped local managed storage.");
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, full);
                var root = OpenRoot(data.RootPath, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                handles.Add(root);
                if (root.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new InvalidDataException("The branch publication root is linked.");
                if (relative != ".")
                    foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                        handles.Add(create ? NativeSnapshotDirectory.CreateDirectory(Handle, component, namespaceWrites: true) :
                            NativeSnapshotDirectory.OpenDirectory(Handle, component, namespaceWrites: true));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); handles.Clear(); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle OpenRoot(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
    }
}
