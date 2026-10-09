namespace TogetherServer;

public sealed partial class PairingService
{
    // Assignment and current access are one decision under the existing pairing locks.
    public PairingDecision AuthorizeGameRequirements(Guid deviceId, Guid profileId, out PairedDevice? current)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            current = devices.SingleOrDefault(item => item.Id == deviceId && item.CredentialHash is not null);
            if (current is null) return new(false, "Unauthorized", "This PC's saved access was not accepted.");
            var decision = AuthorizationDecision(current, UtcNow);
            if (!decision.Ok) return decision;
            return current.AssignedProfileIds?.Contains(profileId) == true
                ? new(true, "RequirementsAllowed", "Game requirements may be read.")
                : new(false, "PermissionDenied", "This server is not assigned to this PC.");
        }
    }
}
