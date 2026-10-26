using System.Text.Json;

namespace TogetherServer;

internal sealed record LocalHostingStartRequest(Guid LocalProfileId, string? BranchHash,
    Guid? RequestId, bool AcceptSplitWarning);
internal sealed record LocalHostingActionResult(bool Ok, string Code, string Message);

internal sealed partial class FriendLocalHostingService
{
    // A remedy is a fixed local destination, never a route, URL, path or command
    // supplied by a peer. Polling projects it without starting any work.
    internal static LocalHostingStatus WithRemedy(LocalHostingStatus status)
    {
        LocalHostingRemedy? remedy = status switch
        {
            { Allowed: false } when ConfirmedHostingDenial(status.Code) =>
                new("Permission", "Ask the owner to review this PC's access", status.Message),
            { HandoffRequest.RecipientState: "Requested" } => new("Respond", "Review the hosting request", "Accept or decline the move before preparing this PC."),
            { Code: "HostingHistoryChoiceRequired" or "WorldHistoryReviewRequired" } =>
                new("ChooseHistory", "Different progress needs review", "Two sessions have different progress. Every save is protected. Review the copies before hosting again."),
            { Code: "SharedSaveHistoryNeedsReview" } =>
                new("Review", "Review the completed shared save", "A complete latest save could not be verified. Keep approved PCs reachable, review shared-save status, and retry preparation after the problem is resolved."),
            { Code: StoppedProgressCode, Allowed: true, Consented: true, Running: false } =>
                new("ShareStoppedProgress", "Share this PC's stopped progress", StoppedProgressMessage),
            {
                Code: "LocalHostingReviewRequired" or "SharingReviewRequired" or "HandoffReviewRequired" or
                "RosterRollback" or "RosterRejected" or "SigningIdentityChanged" or "HostingIdentityRequired"
            } =>
                new("Review", "Review the saved sharing history", status.Message),
            { Allowed: false } => new("Review", "Check the current sharing permission", status.Message),
            { Code: "MinecraftTermsRequired" } => new("Terms", "Review Minecraft's terms", status.Message),
            { Code: "SharedSaveRequired" or "WaitingForSuccessorCopy" or "SharedSaveUnavailable" } => new("GetSave", "Get a completed save", "Keep an approved PC with the save reachable. If no completed save has been shared, ask the current Host to stop the server safely, then retry."),
            { NeedsServerFile: true } => new("ChooseServer", "Find the game server on this PC", "Install the matching dedicated server or choose its installed file, then continue. Existing worlds stay intact."),
            { Code: "LocalServerSetupRequired" } => new("GameSetup", "Finish this PC's game setup", status.Message),
            { Code: "LocalPortsRequired" or "GamePortInUse" or "PortInUse" or "PortConflict" } => new("Ports", "Free this PC's game ports", "Another app may be using a needed game port. Open local servers, stop the app using it if it is yours, or change this server's port in Setup. Then retry."),
            { Code: "LocalConnectionSetupRequired" or "RecipientConnectionRequired" or "PeerDiscoveryRequired" } => new("Connection", "Set up connections to this PC", status.Message),
            { Running: false, Consented: false } => new("ConfirmCopy", "Confirm local hosting", "Hosting keeps the latest verified save on this PC so approved friends can continue later."),
            { Running: false, BranchHash: null, HandoffRequest: null } => new("ConfirmCopy", "Confirm local hosting", "Other players will connect to this PC. The app checks the server, save and connection before starting."),
            _ => null
        };
        if (remedy is null && !status.Running && status.Steps.FirstOrDefault(step => step.Key == "ports")?.State == "ActionRequired")
            remedy = new("Ports", "Free this PC's game ports", "Open local servers to check what is using the game ports, or change the port in Setup. Then retry the automatic checks.");
        var ordinaryPrepared = status.ReadyForManualStart && status.BranchHash is not null &&
            status.HandoffRequest?.RecipientState is not ("Requested" or "Accepted" or "Prepared" or "Completed");
        if (remedy is null && !status.Running && !ordinaryPrepared &&
            status.Steps.FirstOrDefault(step => step.Key == "connection") is { State: "ActionRequired" } connection)
            remedy = new("Connection", "Let friends connect to this PC", connection.Message);
        return status with { Remedy = remedy };
    }

    internal static LocalHostingStartRequest ParseHostingStart(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 1024) throw new InvalidDataException("Use the exact prepared local server.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 2 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Use the fixed local Start action.");
        var fields = root.EnumerateObject().ToArray();
        if (fields.Length != 4 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 4 ||
            fields.Any(field => field.Name is not ("localProfileId" or "branchHash" or "requestId" or "acceptSplitWarning")) ||
            !root.TryGetProperty("localProfileId", out var profile) || profile.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(profile.GetString(), out var localId) || localId == Guid.Empty ||
            !root.TryGetProperty("acceptSplitWarning", out var split) || split.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The exact prepared server and warning choice are required.");
        var branch = root.GetProperty("branchHash");
        var request = root.GetProperty("requestId");
        var branchHash = branch.ValueKind == JsonValueKind.Null ? null : branch.ValueKind == JsonValueKind.String &&
            SharedWorldPeerTrust.ValidHash(branch.GetString()) ? branch.GetString() : throw new InvalidDataException("The signed save history is invalid.");
        Guid? requestId = request.ValueKind == JsonValueKind.Null ? null : request.ValueKind == JsonValueKind.String &&
            Guid.TryParse(request.GetString(), out var id) && id != Guid.Empty ? id : throw new InvalidDataException("The hosting request is invalid.");
        if ((branchHash is null) == (requestId is null)) throw new InvalidDataException("Choose one exact prepared hosting operation.");
        return new(localId, branchHash, requestId, split.GetBoolean());
    }

    // Only the local POST calls this method. It carries no durable start intent:
    // a late status response, app restart or background poll cannot retry Start.
    internal async Task<LocalHostingActionResult> StartPreparedAsync(Guid profileId, LocalHostingStartRequest input,
        CancellationToken cancellationToken)
    {
        using var connection = friend.RetainLocalHostingConnection();
        if (connection is null) return new(false, "NotPaired", "Choose the saved Host connection first.");
        using var progress = hostingProgress.Begin(connection.Link, profileId);
        var result = await StartPreparedForConnectionAsync(connection, profileId, input, progress, cancellationToken);
        progress.Complete(result.Ok, result.Message);
        return result;
    }

    private async Task<LocalHostingActionResult> StartPreparedForConnectionAsync(FriendService.LocalHostingConnection connection,
        Guid profileId, LocalHostingStartRequest input, LocalHostingProgressStore.Operation progress,
        CancellationToken cancellationToken)
    {
        LocalHostingActionResult Refused(string code, string message) => new(false, code, message);
        static LocalHostingActionResult Basic(ActionResult result) => new(result.Ok, result.Code, result.Message);
        // The prepared source resolver exchanges bounded signed peer metadata.
        // A separate original-Host enrollment is not a Start prerequisite.
        var access = await connection.Link.LocalHostingAccessAsync(profileId, false, cancellationToken);
        if (!friend.CurrentLocalHostingConnection(connection))
            return Refused("ConnectionChanged", "The selected Host changed. Review this server again.");
        if (!access.Allowed) return Refused(access.Code, access.Message);
        if (!access.Consented) return Refused("LocalConsentRequired", "Allow shared saves on this PC before hosting.");
        using var preparation = access.Root is { } preparationRoot ? beginPreparation?.Invoke(preparationRoot) : null;
        var sourceProfileId = access.Root?.ProfileId ?? profileId;
        progress.Report("Verifying", "Repeating the prepared save, server and port checks.");
        var status = WithRemedy(await StatusForRetainedConnectionAsync(connection, profileId, cancellationToken,
            checkedAccess: access, checkRemoteHandoff: input.RequestId is not null));
        if (status.Code == StoppedProgressCode)
            return Refused(StoppedProgressCode, StoppedProgressMessage);
        if (!status.ReadyForManualStart || status.Running || status.LocalProfileId != input.LocalProfileId ||
            !friend.CurrentLocalHostingConnection(connection))
            return Refused("LocalHostingPreparationRequired", "Repeat this PC's automatic hosting checks before starting.");
        if (status.Remedy is { Kind: "Connection" or "Ports" or "Review" })
            return Refused("LocalHostingActionRequired", status.Remedy.Message);
        if (input.RequestId is { } exactRequest)
        {
            if (status.HandoffRequest is not { RecipientState: "Completed" } exactHandoff || exactHandoff.RequestId != exactRequest ||
                status.BranchHash is not null || input.LocalProfileId != profileId)
                return Refused("HostingRequestChanged", "The exact final hosting move needs review before starting.");
        }
        else if (!input.AcceptSplitWarning || status.BranchHash != input.BranchHash ||
            !connection.Link.SeparateCopyBranches(profileId).Any(branch => branch.BranchHash == input.BranchHash))
            return Refused("SplitWarningRequired", "Review the exact local save. Another Host may still be running and progress stays separate.");
        SharedWorldPeerRoot? root;
        if (input.RequestId is not null)
        {
            var record = new WorldAuthorityStore(data).LocalAuthorizedHead(profileId);
            root = record is null ? null : new(record.Roster.OwnerPublicKey, profileId, record.Proposal.GroupId);
        }
        else
        {
            var branch = connection.Link.SeparateCopyBranches(profileId).SingleOrDefault(item => item.BranchHash == input.BranchHash);
            root = branch is null ? null : new(branch.Offer.Roster.OwnerPublicKey, sourceProfileId, branch.Offer.Roster.GroupId);
        }
        if (root is null || announceHosting is null)
            return Refused("PeerDiscoveryRequired", "This PC's signed Host address could not be shared. Recheck shared-hosting setup before starting.");
        // The local Host instruction arms completed-save sharing with current
        // members without changing incoming connection settings.
        progress.Report("Connection", "Preparing automatic completed-save sharing with approved peers.");
        var announced = await announceHosting(root, cancellationToken);
        // Incoming reachability is advisory for an exact ordinary local copy.
        // The announcement still validates current membership and save consent;
        // planned moves retain their deliberate route requirement.
        if (!announced.Ok && !(input.RequestId is null && announced.Code == "RecipientConnectionRequired"))
            return Refused(announced.Code, announced.Message);
        if (!friend.CurrentLocalHostingConnection(connection))
            return Refused("ConnectionChanged", "The selected Host changed. Start was canceled; review this server again.");
        WorldPeerPreparedStartSource? heldPreparedSource = null;
        try
        {
            if (input.RequestId is null && (HasLatestSource || preparedStartSource is not null))
            {
                // Prepared Start verifies a held existing source. Missing or newer
                // bytes require explicit Prepare; Start never creates a new copy.
                progress.Report("LatestSave", "Confirming the latest verified progress immediately before Start.");
                var latest = preparedStartSource is null
                    ? new WorldPeerPreparedStartResult(false, "LocalHostingPreparationRequired", "Repeat preparation before starting this local save.")
                    : await preparedStartSource(root, input.LocalProfileId, input.BranchHash!, cancellationToken, progress.Source);
                heldPreparedSource = latest.Source;
                if (!latest.Ok || latest.Source is null)
                {
                    if (status.BranchHash is { } refusedBranch)
                        await manager.InvalidateLocalHostingPreparationAsync(sourceProfileId, refusedBranch, cancellationToken);
                    return Refused(latest.Code, latest.Message);
                }
                if (!friend.CurrentLocalHostingConnection(connection))
                    return Refused("ConnectionChanged", "The selected server changed. Start was canceled.");
                var sourceProof = latest.Source;
                try
                {
                    void RequireScope()
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!friend.CurrentLocalHostingConnection(connection))
                            throw new InvalidDataException("The prepared Start connection changed.");
                        sourceProof.RequireCurrent(data, root, input.LocalProfileId, input.BranchHash!, cancellationToken);
                    }
                    RequireScope();
                    var current = await manager.VerifiedLocalContinuationAsync(sourceProfileId, input.LocalProfileId, cancellationToken);
                    RequireScope();
                    if (current?.VersionHash != sourceProof.Descriptor.Version.VersionHash)
                    {
                        if (status.BranchHash is { } changedBranch)
                            await manager.InvalidateLocalHostingPreparationAsync(sourceProfileId, changedBranch, cancellationToken);
                        return Refused("LatestSaveChanged", "A newer save arrived. Continue hosting to check the latest copy before starting.");
                    }
                    await manager.RecordHostingObservationAsync(input.LocalProfileId, latest, RequireScope, cancellationToken);
                }
                catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
                {
                    if (status.BranchHash is { } failedBranch)
                        await manager.InvalidateLocalHostingPreparationAsync(sourceProfileId, failedBranch, cancellationToken);
                    return Refused("LocalHostingPreparationRequired", "The exact prepared source or working save changed. Repeat preparation; existing saves were kept.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report("Starting", "Starting the checked game server on this PC. Waiting for the Host's result.");
            if (input.RequestId is { } requestId)
            {
                if (status.HandoffRequest is not { RecipientState: "Completed" } handoff || handoff.RequestId != requestId ||
                    status.BranchHash is not null || input.LocalProfileId != profileId)
                    return Refused("HostingRequestChanged", "The exact final hosting move needs review before starting.");
                // StartUnderGate repeats signed authority, final-save/setup, route,
                // source fencing, process identity and all declared port checks.
                return Basic(await manager.StartAsync(input.LocalProfileId, cancellationToken));
            }
            if (!input.AcceptSplitWarning || status.BranchHash != input.BranchHash ||
                !connection.Link.SeparateCopyBranches(profileId).Any(branch => branch.BranchHash == input.BranchHash))
                return Refused("SplitWarningRequired", "Review the exact local save. Another Host may still be running and progress stays separate.");
            var started = await manager.StartSeparateCopyAsync(input.LocalProfileId, input.BranchHash!, cancellationToken);
            if (!started.Ok && started.Code == "LatestSaveChanged" && status.BranchHash is { } refused)
                await manager.InvalidateLocalHostingPreparationAsync(sourceProfileId, refused, cancellationToken);
            return Basic(started);
        }
        finally { heldPreparedSource?.Dispose(); }
    }
}
