using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    private sealed record SoloRecoveryPreparedOffer(WorldAuthorityOffer Offer,
        WorldSeparateCopyBranch? SourceBranch = null, WorldPeerRecoverySource? SelectedSource = null);
    private string SoloOfferName(Guid profileId) =>
        $"solo-recovery-offer-{SharedWorldDeviceId:N}-{profileId:N}.protected";

    private SharedWorldRoster SoloRecoveryRoster(Guid profileId)
    {
        if (config is null || !HasSharedWorldReceiveConsent(profileId))
            throw new InvalidDataException("Allow shared saves on this PC first.");
        if (config.KnownRevoked ||
            config.PendingSharedWorldGroups?.ContainsKey(profileId) == true)
            throw new InvalidDataException("Review removed access or the changed signed world history first.");
        var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
        var owner = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
        if (floor is null || owner is null ||
            config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != floor.GroupId)
            throw new InvalidDataException("A trusted owner-signed roster is unavailable on this PC.");
        var bytes = data.LoadProtected(
            $"shared-world-roster-{SharedWorldDeviceId:N}-{profileId:N}.protected");
        if (bytes is null || bytes.Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("The saved signed roster is unavailable.");
        var roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json);
        if (roster is null || !SharedWorldRosterTrust.Verify(roster) ||
            roster.ProfileId != profileId || roster.GroupId != floor.GroupId ||
            roster.OwnerPublicKey != owner || roster.Epoch != floor.Epoch ||
            roster.Revision != floor.Revision || roster.Signature != floor.Signature ||
            !new WorldAuthorityStore(data).TrustsCurrentRoster(roster))
            throw new InvalidDataException("The saved roster no longer matches the approved world and revision.");
        using var key = LoadPcSigningKey();
        if (!SharedWorldRosterTrust.HasRole(roster, SharedWorldDeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                grants => grants.Receive && grants.EligibleHost))
            throw new InvalidDataException("This PC needs active Receive and Eligible host grants.");
        return roster;
    }

    public async Task<WorldAuthorityOfferResult> PrepareSoloRecoveryOfferAsync(Guid profileId,
        string? sourceBranchHash = null, WorldPeerRecoverySource? selectedSource = null)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync();
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var roster = SoloRecoveryRoster(profileId);
                var settings = data.LoadSettings();
                if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) ||
                    endpoint.GetLeftPart(UriPartial.Authority) != settings.CompanionEndpoint.TrimEnd('/'))
                    return new(false, "CandidateRouteUnavailable",
                        "Configure this PC's literal HTTPS address before preparing recovery. A local loopback address can be used for solo play.");
                using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
                using var key = LoadPcSigningKey();
                if (selectedSource is not null)
                {
                    if (selectedSource.BranchHash != sourceBranchHash)
                        throw new InvalidDataException("The explicitly selected history changed before preparation.");
                    selectedSource.Verify(data, new(roster.OwnerPublicKey, profileId, roster.GroupId));
                }
                var sourceBranch = selectedSource?.Descriptor.Branch ?? (sourceBranchHash is null || selectedSource is not null ? null :
                    SharedWorldBranchVault.ReadBranch(data, roster, sourceBranchHash));
                var sourceRoot = selectedSource?.Vault ?? (sourceBranch is null ? ReceivedRoot(profileId) :
                    SharedWorldBranchVault.Root(data, sourceBranch));
                var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(sourceRoot,
                    roster, config!.SharedRosterFloors![profileId], SharedWorldDeviceId, key,
                    settings.CompanionEndpoint, HostIdentity.Fingerprint(certificate),
                    new WorldAuthorityStore(data), sourceBranch);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new SoloRecoveryPreparedOffer(offer,
                    sourceBranch, selectedSource), Json);
                if (bytes.Length > 1024 * 1024)
                    throw new InvalidDataException("The signed recovery offer is oversized.");
                data.SaveProtected(SoloOfferName(profileId), bytes);
                return new(true, "SoloRecoveryPrepared",
                    "Review this recovery branch's exact verified save. Another server may still be running; this action does not select the group's default world.",
                    offer);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "SoloRecoveryUnavailable", ex.Message); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<WorldSeparateCopyResult> DeclareSoloRecoveryAsync(Guid profileId,
        bool acceptSplitWarning)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync();
        try
        {
            if (!acceptSplitWarning)
                return new(false, "SplitWarningRequired",
                    "Confirm that another server may still be running and both histories will be preserved.");
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var roster = SoloRecoveryRoster(profileId);
                var bytes = data.LoadProtected(SoloOfferName(profileId));
                var prepared = bytes is { Length: > 0 and <= 1024 * 1024 }
                    ? JsonSerializer.Deserialize<SoloRecoveryPreparedOffer>(bytes, Json) : null;
                var offer = prepared?.Offer;
                if (!SharedWorldElection.VerifyOffer(offer) ||
                    offer!.Proposal.ProposerDeviceId != SharedWorldDeviceId ||
                    WorldAuthorityTrust.RosterHash(offer.Roster) != WorldAuthorityTrust.RosterHash(roster))
                    return new(false, "RecoveryOfferRejected", "Prepare and review this PC's current recovery branch first.");
                var settings = data.LoadSettings();
                using var certificate = new HostIdentity(data).Load();
                if (settings.CompanionEndpoint != offer.Proposal.CandidateAddress ||
                    certificate is null || !certificate.HasPrivateKey ||
                    HostIdentity.Fingerprint(certificate) != offer.CandidateTlsFingerprint)
                    return new(false, "CandidateIdentityChanged", "This PC's reviewed address or pinned HTTPS identity changed.");
                using var key = LoadPcSigningKey();
                if (prepared!.SelectedSource is { } selected)
                {
                    selected.Verify(data, new(roster.OwnerPublicKey, profileId, roster.GroupId));
                    if (selected.Descriptor.Version.VersionHash != offer.Version.VersionHash ||
                        selected.Descriptor.Branch?.BranchHash != prepared.SourceBranch?.BranchHash)
                        throw new InvalidDataException("The prepared recovery source no longer names the reviewed exact copy.");
                }
                var branch = new SharedWorldSeparateCopyStore(data).Declare(offer,
                    prepared.SelectedSource?.Vault ?? (prepared.SourceBranch is { } source ? SharedWorldBranchVault.Root(data, source) :
                        ReceivedRoot(profileId)), key, acceptSplitWarning, prepared.SourceBranch);
                return new(true, "SeparateCopyRecorded",
                    "The recovery branch is recorded and its verified source is retained. Restore it into a fresh local world, then Start manually. The original Host keeps its own history.",
                    branch);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "SoloRecoveryUnavailable", ex.Message); }
        finally { gate.Release(); ReleaseRetained(); }
    }
}

internal static class SharedWorldBranchVault
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Root(LocalData data, WorldSeparateCopyBranch branch) => Path.Combine(
        data.RootPath, "received-shared-branches", SharedWorldPeerTrust.RootKey(new(
            branch.Offer.Roster.OwnerPublicKey, branch.Offer.Proposal.ProfileId,
            branch.Offer.Proposal.GroupId)), branch.BranchHash);

    // Branch transport writes this proof alongside its latest.json and archive.
    // A branch hash cannot select a path outside this exact approved world.
    internal static WorldSeparateCopyBranch ReadBranch(LocalData data, SharedWorldRoster roster,
        string branchHash)
    {
        if (branchHash is not { Length: 64 } || !branchHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Choose a recorded recovery branch.");
        var root = Path.Combine(data.RootPath, "received-shared-branches",
            SharedWorldPeerTrust.RootKey(new(roster.OwnerPublicKey, roster.ProfileId, roster.GroupId)),
            branchHash);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        var path = Path.Combine(root, "branch.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The recovery branch's signed proof is unavailable.");
        var branch = JsonSerializer.Deserialize<WorldSeparateCopyBranch>(File.ReadAllBytes(path), Json);
        if (!SharedWorldSeparateCopyStore.Verify(branch) || branch!.BranchHash != branchHash ||
            branch.Offer.Roster.OwnerPublicKey != roster.OwnerPublicKey ||
            branch.Offer.Roster.ProfileId != roster.ProfileId || branch.Offer.Roster.GroupId != roster.GroupId)
            throw new InvalidDataException("The recovery branch does not match this approved world.");
        return branch;
    }
}
