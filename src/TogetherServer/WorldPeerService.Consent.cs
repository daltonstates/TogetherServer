namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    internal Task<WorldPeerActionResult> SetReceivingAsync(string groupKey, bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            Group? group;
            lock (stateSync)
            {
                group = Find(groupKey);
                if (group is null) return Task.FromResult(new WorldPeerActionResult(false,
                    "WorldMembershipRequired", "Verify this world's membership on this PC first."));
                if (enabled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var key = LocalKey(group);
                    if (key is null || Access(group, group.DeviceId,
                        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))?.Receive != true)
                        return Task.FromResult(new WorldPeerActionResult(false, "ReceiveGrantRequired",
                            "This PC needs the world's current Receive permission before it can store saves."));
                }
                // A local deny is immediate and independent of the old Host,
                // inbound sharing, refreshGate and every per-link network gate.
                new WorldPeerReceiveConsentStore(data).Set(group.Root, group.DeviceId, enabled);
                SaveGroup(group with { ReceiveConsented = enabled });
            }
            friend.RefreshWorldReceiveConsent(group.Root, group.DeviceId);
            return Task.FromResult(new WorldPeerActionResult(true, "ReceivingConsentSaved", enabled
                ? "This PC may receive verified completed saves when its current permissions allow it."
                : "This PC will no longer receive this world's saves. Existing copies were kept."));
        }
        catch (Exception error) when (PeerFailure(error))
        {
            return Task.FromResult(new WorldPeerActionResult(false, "ReceivingConsentNeedsReview",
                "Receiving remains disabled because the protected consent decision could not be verified or saved."));
        }
    }
}
