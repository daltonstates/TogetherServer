using System.Text.Json;

namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    internal Task<SharedWorldRoster?> ReadOwnerHostingRosterAsync(Guid profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = SharedWorldService.RetainedOwnerPublicKey(data);
            if (owner is null) return Task.FromResult<SharedWorldRoster?>(null);
            var roster = manager.PeerPublishedRosterAsync(profileId).GetAwaiter().GetResult();
            return Task.FromResult(roster?.OwnerPublicKey == owner ? roster : null);
        }
    }

    // First owner Start must not depend on a background pass registering its
    // already-published local roster. This exact-world registration neither refreshes
    // Friend connections nor creates a signing key, roster or peer announcement.
    internal Task<WorldPeerActionResult> EnsureOwnerHostingMembershipAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        static Task<WorldPeerActionResult> Result(bool ok, string code, string message) =>
            Task.FromResult(new WorldPeerActionResult(ok, code, message));
        cancellationToken.ThrowIfCancellationRequested();
        if (!SharedWorldPeerTrust.ValidRoot(root))
            return Result(false, "WorldMembershipRequired", "Choose this PC's original shared server before hosting.");
        try
        {
            lock (stateSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Groups is intentionally strict: an unreadable registry must
                // remain a review case, never become an empty registration.
                var groups = Groups();
                var settings = data.LoadSettings();
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == root.ProfileId);
                if (data.Recovery.LifecycleBlocked || profile is null || !profile.SharedSavesEnabled ||
                    profile.SeparateCopySourceProfileId is not null || profile.SeparateCopyBranchHash is not null ||
                    profile.WorldLoadRehearsalId is not null ||
                    SharedWorldService.RetainedOwnerPublicKey(data) != root.OwnerPublicKey)
                    return Result(false, "WorldMembershipRequired", "This PC's original shared-server identity needs review before hosting.");
                if (pairing.SharedRosterDirty(profile.Id))
                    return Result(false, "SharedSaveHistoryNeedsReview", "Confirm this server's current signed sharing permission before hosting.");

                // This manager API is a synchronous durable local read returning
                // Task.FromResult. Keep it inside the common mutation lock so
                // the roster, owner key and profile cannot change before SaveGroup.
                var roster = manager.PeerPublishedRosterAsync(profile.Id).GetAwaiter().GetResult();
                if (!SharedWorldRosterTrust.Verify(roster) || roster!.ProfileId != root.ProfileId ||
                    roster.GroupId != root.GroupId || roster.OwnerPublicKey != root.OwnerPublicKey)
                    return Result(false, "SharedSaveHistoryNeedsReview", "The exact published shared-server identity needs review before hosting.");
                var old = groups.SingleOrDefault(item => item.Root == root);
                if (old is not null && (old.DeviceId != Guid.Empty || old.Roster.Epoch > roster.Epoch ||
                    old.Roster.Revision > roster.Revision || old.Roster.Revision == roster.Revision &&
                    old.Roster.Signature != roster.Signature))
                    return Result(false, "SharedSaveHistoryNeedsReview", "The retained world membership differs from this PC's current signed sharing history.");
                var consent = new WorldPeerReceiveConsentStore(data).Read(root, Guid.Empty);
                if (consent.NeedsReview || !(consent.Enabled ?? old?.ReceiveConsented ?? true))
                    return Result(false, "HostingPermissionRequired", "Enable receiving this world's saves before hosting its latest progress.");
                var next = new Group(root, roster, Guid.Empty,
                    profile.Name.Length <= 100 ? profile.Name : profile.Name[..100], true,
                    old?.SharingEnabled ?? settings.CompanionListeningEnabled, old?.Bootstrap);
                cancellationToken.ThrowIfCancellationRequested();
                if (old is null || !JsonSerializer.SerializeToUtf8Bytes(old, Json).AsSpan()
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(next, Json)))
                    SaveGroup(next);
                return Result(true, "OwnerHostingMembershipReady",
                    "This PC's original shared-server membership is current. Latest-save and Start checks remain required.");
            }
        }
        catch (Exception error) when (PeerFailure(error) || SharedWorldBranchLiveSource.ExpectedFailure(error))
        {
            return Result(false, "SharedSaveHistoryNeedsReview",
                "The local world membership or signed sharing history needs review. Existing records and saves were kept.");
        }
    }
}
