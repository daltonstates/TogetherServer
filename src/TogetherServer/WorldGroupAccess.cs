namespace TogetherServer;

// World membership is independent of permission to keep a save. An enrolled,
// assigned PC can follow signed hosting information without accepting world files.
internal static class WorldGroupAccess
{
    internal static SharedWorldRosterMember? ActiveMember(SharedWorldRoster? roster,
        Guid deviceId, string publicKey, TimeProvider? clock = null)
    {
        if (!SharedWorldRosterTrust.Verify(roster) || deviceId == Guid.Empty ||
            !SharedWorldRosterTrust.ValidKey(publicKey)) return null;
        var member = roster!.Members.SingleOrDefault(item => item.DeviceId == deviceId);
        return member is { Revoked: false } && member.PublicKey == publicKey &&
            (member.AccessExpiresUtc is null ||
             member.AccessExpiresUtc > (clock ?? TimeProvider.System).GetUtcNow())
            ? member : null;
    }

    internal static bool MayFollow(SharedWorldRoster? roster, Guid deviceId,
        string publicKey, TimeProvider? clock = null) =>
        ActiveMember(roster, deviceId, publicKey, clock) is not null;

    internal static bool MayReceive(SharedWorldRoster? roster, Guid deviceId,
        string publicKey, TimeProvider? clock = null) =>
        ActiveMember(roster, deviceId, publicKey, clock)?.Grants.Receive == true;

    internal static bool MayRecover(SharedWorldRoster? roster, Guid deviceId,
        string publicKey, TimeProvider? clock = null) =>
        ActiveMember(roster, deviceId, publicKey, clock)?.Grants is
            { Receive: true, EligibleHost: true };

    internal static bool MayReshare(SharedWorldRoster? roster, Guid deviceId,
        string publicKey, TimeProvider? clock = null) =>
        ActiveMember(roster, deviceId, publicKey, clock)?.Grants is
            { Receive: true, Reshare: true };
}
