namespace TogetherServer;

internal enum RemoteActionKind
{
    Start,
    Stop,
    Restart,
    Replace,
    Extend
}

internal static class RemoteActionPolicy
{
    public static string Name(RemoteActionKind action) => action switch
    {
        RemoteActionKind.Start => "start",
        RemoteActionKind.Stop => "stop",
        RemoteActionKind.Restart => "restart",
        RemoteActionKind.Replace => "replace",
        RemoteActionKind.Extend => "extend",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public static bool IsKnown(string action) => action is "start" or "stop" or "restart" or "replace" or "extend";

    public static bool Allowed(PairingService pairing, PairedDevice device, Guid profileId, RemoteActionKind action)
    {
        if (!pairing.CanAccess(device, profileId)) return false;
        return action switch
        {
            RemoteActionKind.Start or RemoteActionKind.Replace => pairing.CanStart(device, profileId),
            RemoteActionKind.Stop => pairing.CanStop(device, profileId),
            RemoteActionKind.Restart => pairing.CanStart(device, profileId) && pairing.CanStop(device, profileId),
            RemoteActionKind.Extend => pairing.CanExtendTimer(device, profileId),
            _ => false
        };
    }
}
