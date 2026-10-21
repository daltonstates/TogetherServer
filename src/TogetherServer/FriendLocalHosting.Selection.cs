using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLocalHostingService
{
    private sealed record HostingSelection(int Schema, Guid ConnectionId,
        SharedWorldPeerRoot Root, string BranchHash);

    private static string SelectionName(Guid connectionId, Guid profileId) =>
        $"local-hosting-choice-{connectionId:N}-{profileId:N}.protected";

    private async Task<LocalHostingActionResult?> PrepareReceivingRouteAsync(LocalHostingPrepareRequest request,
        SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        if (!request.RequirePeerConnection) return null;
        if (!request.AllowReceiving || !request.AcceptSplitWarning)
            return new(false, "SplitWarningRequired", "Confirm that other players may connect to this PC before hosting.");
        var route = routeCheck is null ? null : await routeCheck.CurrentAsync(cancellationToken);
        if (route?.Identity is not { } identity || !SharedWorldRouteTrust.DirectIpAddress(identity.Endpoint) ||
            !HostIdentity.TryEndpoint(identity.Endpoint, out var endpoint) || endpoint.Port != identity.ControlPort ||
            System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(endpoint.Host)))
            return new(false, "LocalConnectionSetupRequired", "Set this PC's connection address in Connection help, then continue hosting.");
        if (!identity.Enabled)
        {
            var enabled = await manager.UpdateControlPolicyAsync(new(CompanionListeningEnabled: true));
            if (!enabled.Ok) return new(false, enabled.Code, enabled.Message);
        }
        using var certificate = new HostIdentity(data).Ensure(identity.Endpoint);
        if (!SharedWorldPeerTrust.ValidRoute(new(identity.Endpoint, HostIdentity.Fingerprint(certificate))))
            return new(false, "LocalConnectionSetupRequired", "Check this PC's saved connection address before hosting.");
        if (announceHosting is not null)
        {
            var announced = await announceHosting(root, cancellationToken);
            if (!announced.Ok) return new(false, announced.Code, announced.Message);
        }
        if (synchronizeListener is not null) await synchronizeListener();
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    // This remembers only the app's verified selection, never a Start request.
    // Old worlds remain intact when another PC has continued their history.
    private WorldSeparateCopyBranch? SelectedBranch(FriendService.LocalHostingConnection connection,
        Guid profileId, SharedWorldPeerRoot? root, IReadOnlyList<WorldSeparateCopyBranch> branches)
    {
        var bytes = data.LoadProtected(SelectionName(connection.Id, profileId));
        if (bytes is null) return null;
        if (bytes.Length > 4096) throw new InvalidDataException("The saved hosting selection needs review.");
        var selected = JsonSerializer.Deserialize<HostingSelection>(bytes);
        if (selected is not { Schema: 1 } || selected.ConnectionId != connection.Id ||
            selected.Root != root || root?.ProfileId != profileId ||
            !SharedWorldPeerTrust.ValidHash(selected.BranchHash))
            throw new InvalidDataException("The saved hosting selection belongs to another world.");
        return branches.SingleOrDefault(branch => branch.BranchHash == selected.BranchHash &&
            branch.Offer.Roster.OwnerPublicKey == root.OwnerPublicKey &&
            branch.Offer.Roster.GroupId == root.GroupId && SharedWorldSeparateCopyStore.Verify(branch)) ??
            throw new InvalidDataException("The selected hosting save needs review.");
    }

    private void SelectBranch(FriendService.LocalHostingConnection connection,
        SharedWorldPeerRoot root, WorldSeparateCopyBranch branch)
    {
        if (!friend.CurrentLocalHostingConnection(connection) ||
            branch.Offer.Proposal.ProfileId != root.ProfileId ||
            branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey ||
            branch.Offer.Roster.GroupId != root.GroupId || !SharedWorldSeparateCopyStore.Verify(branch))
            throw new InvalidDataException("The selected server changed before its save was recorded.");
        data.SaveProtected(SelectionName(connection.Id, root.ProfileId),
            JsonSerializer.SerializeToUtf8Bytes(new HostingSelection(1, connection.Id, root, branch.BranchHash)));
    }
}
