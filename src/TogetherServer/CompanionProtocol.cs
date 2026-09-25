namespace TogetherServer;

public sealed record CompanionProtocolInfo(string AppVersion, int ProtocolVersion,
    int MinimumProtocolVersion, IReadOnlyList<string> Capabilities,
    bool Compatible = true, string? CompatibilityMessage = null);

public static class CompanionProtocol
{
    public const string HeaderName = "X-TogetherServer-Protocol";
    public const int Current = 3;
    public const int Minimum = 3;
    public static readonly IReadOnlyList<string> Capabilities =
    [
        "durable-operations",
        "operation-status",
        "restart",
        "cached-observations",
        "maintenance",
        "activity-feed",
        "timer-extension",
        "player-count-refresh",
        "pairing-windows",
        "action-protocol-header"
    ];

    public static string AppVersion => typeof(CompanionProtocol).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static CompanionProtocolInfo Describe(int? peerProtocol = null)
    {
        var compatible = peerProtocol is null || IsCompatible(peerProtocol.Value);
        return new(AppVersion, Current, Minimum, Capabilities, compatible,
            compatible ? null : peerProtocol < Minimum
                ? "Update required: this app is too old for the Host companion protocol."
                : "Update required: the Host app is too old for this Friend companion protocol.");
    }

    public static bool Supports(CompanionProtocolInfo? host) => host is null ||
        host.MinimumProtocolVersion <= Current && host.ProtocolVersion >= Minimum;

    public static bool IsCompatible(int peerProtocol) => peerProtocol >= Minimum && peerProtocol <= Current;

    public static string CompatibilityMessage(int peerProtocol) => Describe(peerProtocol).CompatibilityMessage ??
        "Update required before remote controls can be used.";
}
