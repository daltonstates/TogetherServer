namespace TogetherServer;

public sealed partial class PairingService
{
    public PairingDecision DeleteRevokedDevice(Guid id)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == id);
            if (device is null) return new(false, "UnknownDevice", "This PC record was not found.");
            if (!IsRevoked(device))
                return new(false, "DeviceNotRevoked", "Revoke this PC's access before deleting its record.");
            var affected = SignedRosterProfiles(device);
            // Inherited signed membership includes revocation fences. A successor
            // cannot remove that baseline or safely publish a replacement roster.
            if (!CanChangeSharedRoster(affected)) return SuccessorRosterDenied();

            MarkSharedRostersDirty(affected);
            // Also invalidate objects held by requests that authenticated earlier.
            device.Revoked = true;
            device.InviteHash = null;
            device.InviteExpiresUtc = null;
            device.CredentialHash = null;
            device.PreviousCredentialHash = null;
            device.PreviousCredentialExpiresUtc = null;
            ForgetRenewalReceipt(id);
            heartbeats.TryRemove(id, out _);
            devices.Remove(device);
            SaveState();
            // Retain signed history and audit evidence; delete only local access.
            data.TryAudit($"device-delete {id} {UtcNow:O}");
            return new(true, "DeviceDeleted", "The revoked PC record was deleted.");
        }
    }
}
