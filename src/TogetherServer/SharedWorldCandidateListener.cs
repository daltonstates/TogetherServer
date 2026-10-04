namespace TogetherServer;

internal static class SharedWorldCandidateListener
{
    internal static async Task<WorldAuthorityOfferResult> ActivateAsync(
        WorldAuthorityOfferResult result, HostManager manager, CompanionServer companion)
    {
        if (!result.Ok) return result;
        if (result.Offer is null || !SharedWorldElection.VerifyOffer(result.Offer))
            return new(false, "CandidateListenerUnavailable",
                "The signed recovery offer could not be verified. Prepare it again.");

        var enabled = await manager.UpdateControlPolicyAsync(
            new HostControlPolicyChange(CompanionListeningEnabled: true));
        if (!enabled.Ok)
            return new(false, "CandidateListenerUnavailable",
                "The signed offer was saved, but this PC could not enable its Friend control listener. " +
                enabled.Message);

        await companion.SyncAsync();
        var settings = (await manager.SnapshotAsync()).Settings;
        if (companion.OwnsListener(result.Offer.Proposal.CandidateAddress,
                settings.CompanionPort, result.Offer.CandidateTlsFingerprint,
                settings.CompanionBindAddress))
            return result;
        return new(false, "CandidateListenerUnavailable",
            "The signed offer was saved, but this PC's Friend control listener is not reachable " +
            "at its configured address and port. Check that setup, then prepare the offer again.");
    }
}
