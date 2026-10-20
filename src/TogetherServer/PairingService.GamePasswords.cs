namespace TogetherServer;

public sealed partial class PairingService
{
    public bool CanViewPassword(PairedDevice device, Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            return current is not null && AuthorizationDecision(current, UtcNow).Ok &&
                current.AssignedProfileIds?.Contains(profileId) == true && current.CanViewPasswordForProfile(profileId);
        }
    }

    internal GamePasswordResult ReadGamePassword(Guid deviceId, string? bearer, Guid profileId,
        Func<GamePasswordResult>? read = null)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            GamePasswordResult? Denial()
            {
                var decision = Authenticate(deviceId, bearer, out var current);
                if (!decision.Ok) return GamePasswords.Failure(profileId, decision.Code);
                // Secret reads require a known future credential deadline even
                // where shared legacy authentication accepts a missing one.
                if (current?.CredentialExpiresUtc is not { } expiry || expiry <= UtcNow)
                    return GamePasswords.Failure(profileId, "CredentialExpired");
                return current?.AssignedProfileIds?.Contains(profileId) == true && current.CanViewPasswordForProfile(profileId)
                    ? null : GamePasswords.Failure(profileId, "PermissionDenied");
            }
            if (Denial() is { } denied) return denied;
            var result = (read ?? (() => GamePasswords.Read(data, profileId)))();
            // Recheck the current credential, owner deadline and saved grant
            // after protected I/O. A retained device or temporary helper is
            // never enough to reveal the secret.
            return Denial() ?? result;
        }
    }
}
