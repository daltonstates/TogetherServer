namespace TogetherServer;

// Presentation history for completed observations of one exact managed run.
// It never supplies a count or participates in lifecycle authorization.
internal sealed record PlayerCountRefreshState(Guid OperationId, bool Refreshing = false,
    int FailureCount = 0)
{
    internal const int WarningThreshold = 2;

    internal static bool IsCountObservationUnavailable(string state, string? source) =>
        state == "Unknown" && source is "ObservationPending" or "ObservationStale" or "HealthProbeFailed" or "CustomStatusTimedOut";

    // Apply at the recipient too: an older paired Host may still send these as
    // Important/Warning. Retain history without a new native notification.
    internal static bool IsRoutineCountActivity(string category, string action) =>
        category == "Players" && action is "CountIncreased" or "CountDecreased" or "CountObserved" or "CountUnavailable" ||
        category == "Countdown" && action is "Started" or "Canceled";

    internal PlayerCountRefreshState Completed(GameHealthResult health, bool wasReady)
    {
        var trusted = health.Ok && health.State == "Ready" && health.PlayerCountTrusted &&
            health.OnlinePlayers is >= 0;
        var failedCount = !trusted && health.Code != "CustomScriptReady" &&
            (health.State == "Ready" || wasReady && (health.State is "Starting" or "Unknown") ||
             health.State == "Unknown" && IsCountCheckWait(health.State, health.Code));
        return this with
        {
            Refreshing = false,
            FailureCount = failedCount ? (int)Math.Min((long)FailureCount + 1, int.MaxValue) : 0
        };
    }

    internal static bool ShouldRecordLifecycleChange(ServerObservation? previous,
        GameHealthResult current, bool wasReady, int previousFailureCount)
    {
        if (previous is null || previous.State == current.State) return false;
        // These fixed driver states mean a count/status request is waiting.
        // Keep the authoritative Unknown/Starting projection, while avoiding
        // lifecycle feed and notification churn as that request recovers.
        if (IsCountCheckWait(current.State, current.Code) && (wasReady || current.State == "Unknown"))
            return false;
        if (wasReady && current.State == "Ready" && previousFailureCount > 0 &&
            IsCountCheckWait(previous.State, previous.Source)) return false;
        return true;
    }

    private static bool IsCountCheckWait(string state, string source) =>
        state == "Unknown" && (source is "HealthProbeFailed" or "CustomStatusTimedOut") ||
        state == "Starting" && (source is "MinecraftStarting" or "FactorioStarting" or "ValheimStarting");
}
