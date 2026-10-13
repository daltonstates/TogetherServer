using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic protected storage only; no listener, process or desktop interaction.
internal static class RevokedDeviceDeletionChecks
{
    internal static void Run(string root)
    {
        using var data = new LocalData(Path.Combine(root, "device-delete-" + Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile { Id = Guid.NewGuid(), SharedSavesEnabled = true };
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var pairing = new PairingService(data);
        var invite = pairing.IssueServer(profile.Id, false, false, "https://127.0.0.1:5131", new string('A', 64), refresh: false);
        var first = pairing.Activate(new(profile.Id, invite.Code, ServerScope: true)) ??
            throw new InvalidOperationException("first synthetic pairing failed");
        var second = pairing.Activate(new(profile.Id, invite.Code, ServerScope: true)) ??
            throw new InvalidOperationException("second synthetic pairing failed");
        Require(pairing.DeleteRevokedDevice(first!.DeviceId).Code == "DeviceNotRevoked", "an active PC was deleted");
        Require(pairing.DeleteRevokedDevice(Guid.NewGuid()).Code == "UnknownDevice", "an unknown PC was accepted");
        Require(pairing.AuthorizeActiveDevice(first.DeviceId, out var stale).Ok, "active PC was not authorized");
        var renewal = pairing.Renew(stale!, new(first.DeviceId, Guid.NewGuid()), false);
        Require(renewal is not null && data.LoadPairingState().CredentialRenewals.Count == 1, "renewal receipt was not seeded");
        Require(pairing.RecordHeartbeat(stale!, new(first.DeviceId, Guid.NewGuid(), 1, "synthetic")).Ok,
            "heartbeat was not seeded");
        Require(pairing.Revoke(first.DeviceId).Ok && pairing.DeleteRevokedDevice(first.DeviceId).Code == "DeviceDeleted",
            "revoked PC was not deleted");
        Require(pairing.Views().All(item => item.Id != first.DeviceId) &&
            data.LoadPairingState().Devices.All(item => item.Id != first.DeviceId) &&
            data.LoadPairingState().CredentialRenewals.Count == 0, "deleted pairing or renewal state survived");
        Require(!pairing.RecordHeartbeat(stale!, new(first.DeviceId, Guid.NewGuid(), 2, "synthetic")).Ok &&
            pairing.Renew(stale!, new(first.DeviceId, Guid.NewGuid()), false) is null &&
            !pairing.CanAccess(stale!, profile.Id), "a stale loaded PC retained access");
        Require(!pairing.Authenticate(first.DeviceId, first.Credential, out _).Ok &&
            !pairing.Authenticate(first.DeviceId, renewal!.Credential, out _).Ok,
            "an old credential authenticated after deletion");
        var restarted = new PairingService(data);
        Require(restarted.Views().All(item => item.Id != first.DeviceId) &&
            !restarted.Authenticate(first.DeviceId, first.Credential, out _).Ok &&
            restarted.Authenticate(second!.DeviceId, second.Credential, out _).Ok,
            "restart revived the deleted PC or removed a different PC");
        Require(restarted.DeleteRevokedDevice(first.DeviceId).Code == "UnknownDevice", "a repeat delete claimed an existing record");

        // A signed local member changes the roster baseline when deleted.
        var state = data.LoadPairingState();
        state.Devices.Single(item => item.Id == second.DeviceId).SharedWorldPublicKey = "synthetic-key";
        data.SavePairingState(state);
        var shared = new PairingService(data);
        Require(shared.Revoke(second.DeviceId).Ok && shared.DeleteRevokedDevice(second.DeviceId).Ok &&
            shared.SharedRosterDirty(profile.Id) && shared.SharedRosterMembers(profile.Id).Count == 0,
            "signed membership deletion did not retain the publication fence");

        // Damaged inherited authority cannot establish nonmembership. Keep its
        // revoked pairing tombstone and all immutable authority bytes intact.
        var protectedState = data.LoadPairingState();
        protectedState.Devices.Add(new PairedDevice { Id = second.DeviceId, ProfileId = profile.Id,
            AssignedProfileIds = [profile.Id], Revoked = true, CredentialHash = "synthetic" });
        data.SavePairingState(protectedState);
        data.SaveProtected($"authority-floor-{profile.Id:N}.protected", [1, 2, 3]);
        var inherited = new PairingService(data);
        Require(inherited.DeleteRevokedDevice(second.DeviceId).Code == "SuccessorRosterReadOnly" &&
            inherited.Views().Any(item => item.Id == second.DeviceId) && inherited.SharedRosterDirty(profile.Id) &&
            data.LoadProtected($"authority-floor-{profile.Id:N}.protected")!.SequenceEqual(new byte[] { 1, 2, 3 }),
            "damaged inherited authority lost its revoked record or fence");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
