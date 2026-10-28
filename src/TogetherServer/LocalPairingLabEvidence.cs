using System.Text.Json;

namespace TogetherServer;

internal sealed record LocalPairingLabHostingObservation(bool Allowed, bool Consented, bool HasApprovedPlan,
    bool NeedsPortApproval, bool HasLocalProfile, bool HasBranch, bool ReadyForManualStart, bool Running,
    string PortState, string PortFailure);
internal sealed record LocalPairingLabUxFailure(string Stage, string Code, int PassedStages,
    string LastHostingCode, int FailedRequests, int BrowserErrors);
internal sealed record LocalPairingLabPeerProgress(string State, string Phase, string Operation,
    string HttpStep, int? HttpStatus, string Result, string Error, long ElapsedMilliseconds);
internal sealed record LocalPairingLabPeerRoute(string RecipientRole, LocalPairingLabPeerProgress? Current,
    LocalPairingLabPeerProgress? LastResult, LocalPairingLabPeerProgress? LastFailure);
internal sealed record LocalPairingLabPeerSyncObservation(bool GroupPresent, bool? ReceiveConsented, bool? CanReceive,
    bool? CanHost, bool? CanReshare, int AuthorizedPeers, int RoutedPeers, int ConflictedPeers, bool HasWarning,
    IReadOnlyList<LocalPairingLabPeerRoute> Transfers, HostingMembershipDiagnostic? Registration);

// The child report contains private masked-browser diagnostics. Only reviewed
// stage/code values, counts and booleans may enter the parent lab report.
internal static class LocalPairingLabEvidence
{
    internal static LocalPairingLabPeerSyncObservation PeerSync(WorldPeerGroupView? group, HostingMembershipDiagnostic? membership)
    {
        static string Token<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, out var parsed) &&
            Enum.IsDefined(parsed) ? parsed.ToString() : "Other";
        static long Count(long value) => Math.Clamp(value, 0, 1_000_000);
        static LocalPairingLabPeerProgress? Progress(WorldPeerTransferProgress? value) => value is null ? null : new(
            value.State is "Working" or "Failed" or "Canceled" or "Succeeded" or "Pending" ? value.State : "Other",
            Token<WorldPeerTransferPhase>(value.Phase), Token<WorldPeerTransferOperation>(value.Operation),
            Token<WorldPeerTransferHttpStep>(value.HttpStep), value.HttpStatus is >= 100 and <= 599 ? value.HttpStatus : null,
            Token<WorldPeerTransferResult>(value.Result), Token<WorldPeerTransferError>(value.Error),
            Math.Clamp(value.ElapsedMilliseconds, 0, 86_400_000));
        var peers = group?.State.Peers.Take(128).ToArray() ?? [];
        var registration = membership is null ? null : new HostingMembershipDiagnostic(Count(membership.PassesStarted),
            Count(membership.PassesCompleted), Token<PeerMembershipPassOutcome>(membership.PassOutcome),
            Math.Clamp(membership.PassElapsedMilliseconds, 0, 86_400_000), Count(membership.LookupsStarted),
            Count(membership.LookupsCompleted), Token<PeerMembershipLookupOutcome>(membership.LookupOutcome), membership.LookupReturnedRoster);
        return new(group is not null, group?.ReceiveConsented, group?.CanReceive, group?.CanHost, group?.CanReshare,
            peers.Count(peer => peer.Authorized), peers.Count(peer => peer.Routes.Count > 0), peers.Count(peer => peer.HasConflict),
            group?.Warning is not null, (group?.BackgroundTransfers ?? []).Take(8).Select(route => new LocalPairingLabPeerRoute(
                Token<WorldPeerTransferRecipientRole>(route.RecipientRole), Progress(route.Current), Progress(route.LastResult), Progress(route.LastFailure))).ToArray(),
            registration);
    }

    private static readonly Dictionary<string, string> Stages = new(StringComparer.Ordinal)
    {
        ["launch"] = "Launch",
        ["required setup on both PCs"] = "RequiredSetup",
        ["server code pairing and owner approval"] = "PairingApproval",
        ["one server choice automatically authorizes verified receiving"] = "AutomaticReceiving",
        ["server policy withdrawal blocks stale hosting intent"] = "PolicyWithdrawal",
        ["network confirmation and cancel never prepare or Start"] = "ConfirmationCancel",
        ["bidirectional signed chat and mute"] = "Chat",
        ["owner fixture Start and graceful Stop publish copy"] = "PublishCopy",
        ["missing server blocks then one confirmation prepares and Starts"] = "GuidedHosting",
        ["signed first-run discovery and Connect on owner"] = "DiscoveryConnect",
        ["candidate owner Stop"] = "CandidateStop"
    };

    internal static LocalPairingLabHostingObservation Hosting(LocalHostingStatus status) => new(
        status.Allowed, status.Consented, status.SelectedApprovedPortId is not null, status.NeedsPortApproval,
        status.LocalProfileId is not null, status.BranchHash is not null, status.ReadyForManualStart, status.Running,
        status.Steps.FirstOrDefault(step => step.Key == "ports")?.State switch
        { "Ready" => "Ready", "ActionRequired" => "ActionRequired", "Unknown" => "Unknown", _ => "NotObserved" },
        status.Message switch
        {
            "An approved game port is in use or overlaps this PC's Friend connection. Free it or choose another approved plan; ports were not changed automatically." => "SelectedApprovalUnavailable",
            "The selected approved game port became unavailable during preparation. Review this PC's game ports and retry; no other plan was substituted." => "ChosenApprovalBecameUnavailable",
            "A complete local game-port plan is unavailable. Review the running servers and retry." => "FinalPlanUnavailable",
            "The selected local port approval changed during preparation. Review and approve this PC's game ports again." => "ApprovalChanged",
            "This request's game or Friend connection ports changed during preparation. Review this PC's setup and retry." => "RequestPortsChanged",
            _ => status.Code is "LocalPortsRequired" or "LocalPortChangeRequired" or "LocalPortApprovalRequired" ? "AvailabilityNotEstablished" : "None"
        });

    internal static LocalPairingLabUxFailure UxFailure(ReadOnlySpan<byte> bytes)
    {
        var unavailable = new LocalPairingLabUxFailure("UnknownStage", "UxReportUnavailable", 0, "NotObserved", 0, 0);
        if (bytes.Length is 0 or > 256 * 1024) return unavailable;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            var stage = "UnknownStage";
            var code = "UiAssertionFailed";
            if (root.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object)
            {
                if (failure.TryGetProperty("stage", out var rawStage) && rawStage.ValueKind == JsonValueKind.String)
                    stage = Stages.GetValueOrDefault(rawStage.GetString()!, "UnknownStage");
                if (failure.TryGetProperty("code", out var rawCode) && rawCode.ValueKind == JsonValueKind.String)
                    code = rawCode.GetString() switch
                    {
                        "UiTimeout" => "UiTimeout",
                        "BrowserRuntimeError" => "BrowserRuntimeError",
                        "BrowserRequestFailed" => "BrowserRequestFailed",
                        "ForbiddenNativeInteraction" => "ForbiddenNativeInteraction",
                        "UiRunnerFailed" => "UiRunnerFailed",
                        _ => "UiAssertionFailed"
                    };
            }
            var passed = 0;
            if (root.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
                passed = stages.EnumerateArray().Take(10).Count(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && Stages.ContainsKey(name.GetString()!) &&
                    item.TryGetProperty("passed", out var complete) && complete.ValueKind == JsonValueKind.True);
            var hostingCode = "NotObserved";
            if (root.TryGetProperty("hostingRequestDiagnostics", out var requests) && requests.ValueKind == JsonValueKind.Array)
            {
                var last = requests.EnumerateArray().Take(40).LastOrDefault();
                if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("code", out var lastCode) && lastCode.ValueKind == JsonValueKind.String)
                    hostingCode = lastCode.GetString() switch
                    {
                        "LocalPortsRequired" => "LocalPortsRequired",
                        "LocalPortApprovalRequired" => "LocalPortApprovalRequired",
                        "LocalPortChangeRequired" => "LocalPortChangeRequired",
                        "LocalHostingPrepared" => "LocalHostingPrepared",
                        "LocalHostingPreparationRequired" => "LocalHostingPreparationRequired",
                        "LocalHostingRunning" => "LocalHostingRunning",
                        "SharedSaveHistoryNeedsReview" => "SharedSaveHistoryNeedsReview",
                        "SharedSaveTransferInProgress" => "SharedSaveTransferInProgress",
                        "LatestSaveChanged" => "LatestSaveChanged",
                        "LaunchFailed" => "LaunchFailed",
                        "PermissionDenied" => "PermissionDenied",
                        "HostingPermissionRequired" => "HostingPermissionRequired",
                        "LocalConsentRequired" => "LocalConsentRequired",
                        "ConnectionChanged" => "ConnectionChanged",
                        "SeparateCopyStarted" => "SeparateCopyStarted",
                        "Unavailable" => "Unavailable",
                        _ => "Other"
                    };
            }
            static int Count(JsonElement root, string field, int maximum) => root.TryGetProperty(field, out var array) &&
                array.ValueKind == JsonValueKind.Array ? Math.Min(maximum, array.GetArrayLength()) : 0;
            return new(stage, code, passed, hostingCode, Count(root, "failedRequests", 40), Count(root, "errors", 20));
        }
        catch (JsonException) { return unavailable; }
    }
}
