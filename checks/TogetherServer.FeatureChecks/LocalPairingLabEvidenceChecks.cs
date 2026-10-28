using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class LocalPairingLabEvidenceChecks
{
    internal static void Run()
    {
        var known = LocalPairingLabEvidence.UxFailure(Encoding.UTF8.GetBytes("""
            {"failure":{"stage":"signed first-run discovery and Connect on owner","code":"UiTimeout"},
             "stages":[{"name":"required setup on both PCs","passed":true}],
             "hostingRequestDiagnostics":[{"code":"LocalPortsRequired"}],"failedRequests":[{}],"errors":[]}
            """));
        Require(known is
        {
            Stage: "DiscoveryConnect", Code: "UiTimeout", PassedStages: 1,
            LastHostingCode: "LocalPortsRequired", FailedRequests: 1, BrowserErrors: 0
        },
            "The failed Node child lost its useful allowlisted stage/code evidence.");
        var hostile = LocalPairingLabEvidence.UxFailure(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            failure = new { stage = "password=private-token C:\\private\\world.db", code = "private-token" },
            diagnostic = "https://192.0.2.5/private password=private-token",
            stages = Enumerable.Repeat(new { name = "required setup on both PCs", passed = true }, 40),
            hostingRequestDiagnostics = new[] { new { code = "PrivateTokenValue" } },
            failedRequests = Enumerable.Repeat(new { secret = "private-token" }, 80),
            errors = Enumerable.Repeat(new { message = "private-token" }, 80)
        })));
        var safe = JsonSerializer.Serialize(hostile);
        Require(hostile is
        {
            Stage: "UnknownStage", Code: "UiAssertionFailed", PassedStages: 10,
            LastHostingCode: "Other", FailedRequests: 40, BrowserErrors: 20
        } &&
            !safe.Contains("private", StringComparison.OrdinalIgnoreCase) && !safe.Contains("192.0.2.5", StringComparison.Ordinal),
            "Private child report text escaped the bounded parent projection.");
        Require(LocalPairingLabEvidence.UxFailure("{invalid"u8).Code == "UxReportUnavailable" &&
            LocalPairingLabEvidence.UxFailure(new byte[256 * 1024 + 1]).Code == "UxReportUnavailable",
            "Malformed or oversized child reports escaped their bounded failure projection.");
        var status = FriendLocalHostingService.Unavailable("LocalPortsRequired",
            "An approved game port is in use or overlaps this PC's Friend connection. Free it or choose another approved plan; ports were not changed automatically.") with
        {
            Name = "private server name",
            ServerFile = "C:\\private\\server.exe",
            SelectedApprovedPortId = Guid.NewGuid(),
            Steps = [new("ports", "ActionRequired", "private details")]
        };
        var ports = LocalPairingLabEvidence.Hosting(status);
        Require(ports is { HasApprovedPlan: true, PortState: "ActionRequired", PortFailure: "SelectedApprovalUnavailable" } &&
            !JsonSerializer.Serialize(ports).Contains("private", StringComparison.OrdinalIgnoreCase),
            "Hosting failure evidence lost its exact port phase or exported names, paths or messages.");
        var now = DateTimeOffset.UtcNow;
        var transfer = new WorldPeerTransferProgress("Failed", "Metadata", "Metadata", "Challenge", 403, "Failed", "HttpRejected",
            now, now, 123, 0, 0, 0, null, null, false);
        var group = new WorldPeerGroupView("private-group-key", "private server", Guid.NewGuid(), Guid.NewGuid(), true, true, true, true, true,
            new(new("private-public-key", Guid.NewGuid(), Guid.NewGuid()), "private-group-key", [], [], [], [], false, 0, 0,
                new(0, 0, 0, 0, null, false, false, "private replication status")), Warning: "private raw warning",
            BackgroundTransfers: [new(1, "OriginalHost", null, transfer, transfer)]);
        var sync = LocalPairingLabEvidence.PeerSync(group, new(1, 1, "Completed", 25, 2, 2, "RosterCatchUpPending", false));
        Require(sync is { GroupPresent: true, CanHost: true, HasWarning: true, Transfers.Count: 1 } &&
            sync.Transfers[0].LastFailure is { Phase: "Metadata", HttpStep: "Challenge", HttpStatus: 403, Error: "HttpRejected" } &&
            sync.Registration?.LookupOutcome == "RosterCatchUpPending" &&
            !JsonSerializer.Serialize(sync).Contains("private", StringComparison.OrdinalIgnoreCase),
            "Failed peer synchronization lost its actual HTTP/phase evidence or exported a private world/route value.");
        var untrusted = transfer with
        {
            State = "private-state",
            Phase = "private-phase",
            Operation = "private-operation",
            HttpStep = "private-step",
            HttpStatus = 999,
            Result = "private-result",
            Error = "private-error",
            ElapsedMilliseconds = long.MaxValue
        };
        var boundedSync = LocalPairingLabEvidence.PeerSync(group with
        { BackgroundTransfers = Enumerable.Repeat(new WorldPeerTransferView(1, "private-role", untrusted, untrusted, untrusted), 20).ToArray() },
            new(long.MaxValue, -1, "private-pass", long.MaxValue, -1, long.MaxValue, "private-lookup", false));
        Require(boundedSync.Transfers.Count == 8 && boundedSync.Transfers[0].Current is
        { Phase: "Other", HttpStatus: null, Error: "Other", ElapsedMilliseconds: 86_400_000 } &&
            boundedSync.Registration is { PassesStarted: 1_000_000, PassesCompleted: 0, PassOutcome: "Other" } &&
            !JsonSerializer.Serialize(boundedSync).Contains("private", StringComparison.OrdinalIgnoreCase),
            "Untrusted peer observations escaped fixed enums, numeric bounds or route-count retention.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
