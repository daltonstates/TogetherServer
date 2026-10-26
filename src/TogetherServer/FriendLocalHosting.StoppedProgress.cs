using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record LocalStoppedProgressShareRequest(Guid LocalProfileId, string BranchHash);

internal sealed partial class FriendLocalHostingService
{
    internal const string StoppedProgressCode = "LocalStoppedProgressNeedsSharing";
    internal const string StoppedProgressMessage =
        "This PC's last stopped progress has not been shared. Choose Share stopped progress to verify and share it. Existing worlds and backups are kept.";

    internal static LocalStoppedProgressShareRequest ParseStoppedProgressShare(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 1024) throw new InvalidDataException("Choose the exact stopped local save.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 2 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Use the fixed stopped-save sharing action.");
        var fields = root.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            fields.Any(field => field.Name is not ("localProfileId" or "branchHash")) ||
            !root.TryGetProperty("localProfileId", out var local) || local.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(local.GetString(), out var localId) || localId == Guid.Empty ||
            !root.TryGetProperty("branchHash", out var branch) || branch.ValueKind != JsonValueKind.String ||
            !SharedWorldPeerTrust.ValidHash(branch.GetString()))
            throw new InvalidDataException("The exact stopped local server and signed history are required.");
        return new(localId, branch.GetString()!);
    }

    // Explicit local-owner action only. It publishes a new verified snapshot;
    // neither this action nor its subsequent status read carries Start intent.
    internal async Task<LocalHostingActionResult> ShareStoppedProgressAsync(Guid profileId,
        LocalStoppedProgressShareRequest request, CancellationToken cancellationToken)
    {
        using var connection = friend.RetainLocalHostingConnection();
        if (connection is null) return new(false, "NotPaired", "Choose the saved Host connection first.");
        using var progress = hostingProgress.Begin(connection.Link, profileId);
        LocalHostingActionResult Finished(bool ok, string code, string message)
        {
            progress.Complete(ok, message);
            return new(ok, code, message);
        }
        try
        {
            var access = await connection.Link.LocalHostingAccessAsync(profileId, true, cancellationToken,
                permissionOnly: true);
            if (!friend.CurrentLocalHostingConnection(connection))
                return Finished(false, "ConnectionChanged", "The selected server changed. Review it before sharing progress.");
            if (!access.Allowed) return Finished(false, access.Code, access.Message);
            if (!access.Consented)
                return Finished(false, "LocalConsentRequired", "Allow shared saves on this PC before sharing its stopped progress.");
            if (access.Root is not { } root)
                return Finished(false, "StoppedProgressSharingChanged", "This save's current signed sharing identity needs review. Existing copies were kept.");
            var branch = connection.Link.SeparateCopyBranches(root.ProfileId)
                .SingleOrDefault(item => item.BranchHash == request.BranchHash);
            if (branch is null || branch.Offer.Version.ProfileId != root.ProfileId ||
                branch.Offer.Version.GroupId != root.GroupId || branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey ||
                SharedWorldBranchLiveSource.LocalProfileId(request.BranchHash) != request.LocalProfileId)
                return Finished(false, "StoppedProgressSharingChanged", "The exact local save or its sharing identity changed. Existing copies were kept.");
            var selectionStamp = StoppedSelectionStamp(connection.Id, root.ProfileId);
            var selected = await StatusForRetainedConnectionAsync(connection, profileId, cancellationToken,
                checkedAccess: access);
            if (!friend.CurrentLocalHostingConnection(connection) || !selected.Allowed || !selected.Consented ||
                selected.LocalProfileId != request.LocalProfileId || selected.BranchHash != request.BranchHash ||
                StoppedSelectionStamp(connection.Id, root.ProfileId) != selectionStamp)
                return Finished(false, "StoppedProgressSharingChanged", "The selected local save or current sharing access changed. Recheck it before sharing progress.");
            if (selected.Running)
                return Finished(false, "StoppedProgressBusy", "Keep this server stopped before sharing its stopped progress.");
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report("Verifying", "Verifying this PC's exact stopped save and setup before sharing its progress. Existing copies are kept.");
            void RequireCurrentSelection()
            {
                if (!friend.CurrentLocalHostingConnection(connection) ||
                    StoppedSelectionStamp(connection.Id, root.ProfileId) != selectionStamp)
                    throw new InvalidDataException("The selected stopped-save scope changed.");
            }
            var result = await manager.RetryStoppedSeparateSavePublicationAsync(root.ProfileId,
                request.LocalProfileId, request.BranchHash, cancellationToken, RequireCurrentSelection);
            return Finished(result.Ok, result.Code, result.Message);
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            return Finished(false, "StoppedProgressProofRequired",
                "This PC's stopped save or sharing proof could not be verified. Keep its world and backups and review shared-save status before retrying.");
        }
    }

    // Compare only this already verified selection's bytes. A recovery guard
    // must not quarantine or rebuild selection state while checking its scope.
    private string StoppedSelectionStamp(Guid connectionId, Guid profileId)
    {
        var path = Path.Combine(data.RootPath, SelectionName(connectionId, profileId));
        if (!File.Exists(path)) return "Absent";
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 10 * 1024)
            throw new InvalidDataException("The stopped-save selection needs review.");
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
