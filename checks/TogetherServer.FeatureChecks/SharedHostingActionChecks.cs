using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure input/projection checks. No processes, listeners, sockets or state are
// created. The coordinator registers this group in the focus-safe check entry.
internal static class SharedHostingActionChecks
{
    internal static Task RunAsync()
    {
        var localId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var hash = new string('A', 64);
        var separate = FriendLocalHostingService.ParseHostingStart(Encoding.UTF8.GetBytes(
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":\"{hash}\",\"requestId\":null,\"acceptSplitWarning\":true}}"));
        Require(separate.LocalProfileId == localId && separate.BranchHash == hash && separate.RequestId is null && separate.AcceptSplitWarning,
            "the exact separate-copy Start input did not decode");
        var planned = FriendLocalHostingService.ParseHostingStart(Encoding.UTF8.GetBytes(
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":null,\"requestId\":\"{requestId}\",\"acceptSplitWarning\":false}}"));
        Require(planned.RequestId == requestId && planned.BranchHash is null && !planned.AcceptSplitWarning,
            "the exact completed-move Start input did not decode");
        foreach (var input in new[]
        {
            "{}", "[]", "null",
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":null,\"requestId\":null,\"acceptSplitWarning\":true}}",
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":\"{hash}\",\"requestId\":\"{requestId}\",\"acceptSplitWarning\":true}}",
            $"{{\"localProfileId\":\"{Guid.Empty}\",\"branchHash\":\"{hash}\",\"requestId\":null,\"acceptSplitWarning\":true}}",
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":\"{hash}\",\"requestId\":null,\"acceptSplitWarning\":\"true\"}}",
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":\"{hash}\",\"requestId\":null,\"acceptSplitWarning\":true,\"path\":\"C:/other\"}}",
            $"{{\"localProfileId\":\"{localId}\",\"branchHash\":\"{hash}\",\"branchHash\":\"{hash}\",\"requestId\":null,\"acceptSplitWarning\":true}}"
        })
        {
            var rejected = false;
            try { FriendLocalHostingService.ParseHostingStart(Encoding.UTF8.GetBytes(input)); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "ambiguous or caller-directed local Start input was accepted");
        }
        var prepare = FriendLocalHostingService.ParseRequest(Encoding.UTF8.GetBytes(
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"requirePeerConnection\":true,\"acceptedTerms\":false}"));
        Require(prepare.RequirePeerConnection && !prepare.AcceptedTerms,
            "requiring a deliberate peer connection silently accepted game terms");
        var unavailable = FriendLocalHostingService.Unavailable("PermissionDenied", "Ask the owner to allow hosting.");
        Require(FriendLocalHostingService.WithRemedy(unavailable).Remedy?.Kind == "Permission",
            "a denied host grant did not lead to its owner permission remedy");
        var baseStatus = unavailable with { Allowed = true, Consented = true, BranchHash = hash,
            Steps = unavailable.Steps.Select(step => step with { State = "Ready" }).ToArray() };
        Require(FriendLocalHostingService.WithRemedy(baseStatus with { NeedsServerFile = true }).Remedy?.Kind == "ChooseServer" &&
            FriendLocalHostingService.WithRemedy(baseStatus with { Code = "MinecraftTermsRequired" }).Remedy?.Kind == "Terms" &&
            FriendLocalHostingService.WithRemedy(baseStatus with { Code = "LocalConnectionSetupRequired" }).Remedy?.Kind == "Connection" &&
            FriendLocalHostingService.WithRemedy(baseStatus with { Code = "LocalPortsRequired" }).Remedy?.Kind == "Ports",
            "an actionable hosting failure did not map to its fixed local remedy");
        Require(FriendLocalHostingService.WithRemedy(baseStatus).Remedy is null &&
            baseStatus.Steps.All(step => step.State == "Ready") && !baseStatus.Running && !baseStatus.ReadyForManualStart,
            "projecting checked evidence fabricated readiness or a managed game");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
