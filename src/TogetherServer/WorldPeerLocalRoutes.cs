using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerContactInput(string Code);
internal sealed record WorldPeerConnectInput(Guid DeviceId, Guid SessionId);
internal sealed record WorldSoloPrepareInput(string? SourceBranchHash);
internal sealed record WorldOwnerRecoveryInput(string? SourceBranchHash, bool AcceptSplitWarning);

internal static class WorldPeerLocalRoutes
{
    private static bool Fixed(HttpContext context, bool get = false) =>
        context.Request.Headers["X-TogetherServer-Local"] == "1" && !context.Request.QueryString.HasValue &&
        (!get || context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true);

    private static async Task<T> Read<T>(HttpContext context, int maximum = 16384) where T : class
    {
        if (!Fixed(context)) throw new InvalidDataException("A fixed local request is required.");
        var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
            context.Request.ContentLength, maximum, context.RequestAborted);
        if (bytes is null) throw new InvalidDataException("The local request is missing or oversized.");
        return WorldPeerProtocol.DecodeEnvelope<T>(bytes, maximum);
    }

    internal static void Map(WebApplication app, WorldPeerService peers, FriendService friend,
        HostManager manager, CompanionServer companion, SemaphoreSlim modeGate,
        Func<bool> friendMode, Func<bool> unavailable)
    {
        async Task<IResult> Change(HttpContext context, Func<Task<IResult>> action)
        {
            if (!Fixed(context)) return Results.StatusCode(403);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                if (unavailable()) return Results.Conflict(new WorldPeerActionResult(false, "AppBusy", "Wait for the current app operation."));
                return await action();
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                ArgumentException or System.Security.Cryptography.CryptographicException)
            { return Results.BadRequest(new WorldPeerActionResult(false, "WorldPeerRequestRejected", "The fixed world request could not be verified.")); }
            finally { modeGate.Release(); }
        }

        app.MapGet("/api/local/world-peers", (HttpContext context) =>
        {
            if (!Fixed(context, true)) return Results.StatusCode(403);
            try { return Results.Json(peers.Views()); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            { return Results.Conflict(new WorldPeerActionResult(false, "WorldPeerReviewRequired", "World membership or cached peer information needs review. Existing saves were kept.")); }
        });
        app.MapPost("/api/local/world-peers/{groupKey}/sync", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            if ((await Read<Dictionary<string, JsonElement>>(context, 32)).Count != 0) return Results.BadRequest();
            if (!SharedWorldPeerTrust.ValidHash(groupKey)) return Results.BadRequest();
            await peers.SyncNowAsync(context.RequestAborted);
            return Results.Json(new WorldPeerActionResult(true, "WorldPeersChecked", "Reachable approved peers were checked. Unreachable peers and game joins remain unverified."));
        }));
        app.MapPut("/api/local/world-peers/{groupKey}/sharing", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            var request = await Read<WorldPeerSharingRequest>(context, 256);
            var result = await peers.SetSharingAsync(groupKey, request.Enabled, context.RequestAborted);
            if (result.Ok) await companion.SyncAsync();
            return Results.Json(result);
        }));
        app.MapPut("/api/local/world-peers/{groupKey}/receiving", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            var request = await Read<WorldPeerSharingRequest>(context, 256);
            return Results.Json(await peers.SetReceivingAsync(groupKey, request.Enabled, context.RequestAborted));
        }));
        app.MapGet("/api/local/world-peers/{groupKey}/contact-code", (HttpContext context, string groupKey) =>
        {
            if (!Fixed(context, true)) return Results.StatusCode(403);
            try { return Results.Json(new { code = peers.CreateContactCode(groupKey) }); }
            catch (InvalidDataException error) { return Results.Conflict(new WorldPeerActionResult(false, "PeerRouteUnavailable", error.Message)); }
        });
        app.MapGet("/api/local/world-peers/{groupKey}/live-tests", (HttpContext context, string groupKey) =>
            !Fixed(context, true) ? Results.StatusCode(403) : Results.Json(peers.ReceivedLiveTests(groupKey, context.RequestAborted)));
        app.MapPost("/api/local/world-peers/{groupKey}/contact", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            var request = await Read<WorldPeerContactInput>(context, 20000);
            return Results.Json(peers.ImportContactCode(groupKey, request.Code));
        }));
        app.MapPost("/api/local/world-peers/{groupKey}/connect", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            var request = await Read<WorldPeerConnectInput>(context, 512);
            return Results.Json(await peers.ConnectSessionAsync(groupKey, request.DeviceId, request.SessionId, context.RequestAborted));
        }));
        app.MapPost("/api/local/world-peers/{groupKey}/default", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            var request = await Read<WorldPeerConnectInput>(context, 512);
            return Results.Json(peers.SelectDefault(groupKey, request.DeviceId, request.SessionId));
        }));
        app.MapPost("/api/local/friend/{profileId:guid}/shared-world/recovery/solo/prepare",
            (HttpContext context, Guid profileId) => Change(context, async () =>
            {
                if (!friendMode()) return Results.Conflict(new { code = "HostMode" });
                var request = await Read<WorldSoloPrepareInput>(context, 256);
                return Results.Json(await peers.PrepareSoloRecoveryOfferAsync(profileId, request.SourceBranchHash, context.RequestAborted));
            }));
        app.MapPost("/api/local/world-peers/{groupKey}/recovery-source", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            if (!friendMode()) return Results.Conflict(new { code = "HostMode" });
            if ((await Read<Dictionary<string, JsonElement>>(context, 32)).Count != 0) return Results.BadRequest();
            return Results.Json(await peers.SelectRecoverySourceAsync(groupKey, context.RequestAborted));
        }));
        app.MapGet("/api/local/profiles/{profileId:guid}/shared-world/freshness", async (HttpContext context, Guid profileId) =>
            !Fixed(context, true) ? Results.StatusCode(403) :
            Results.Json(await manager.SharedFreshnessStatusAsync(profileId, context.RequestAborted)));
        const string ownerRecovery = "/api/local/world-peers/{groupKey}/owner-recovery";
        app.MapPost(ownerRecovery, (HttpContext context, string groupKey) => Change(context, async () =>
        {
            if (friendMode() || peers.OwnerRecoveryProfile(groupKey) is null) return Results.StatusCode(403);
            var input = await Read<WorldOwnerRecoveryInput>(context, 512);
            return Results.Json(await peers.PrepareOwnerRecoveryAsync(groupKey, input.SourceBranchHash,
                input.AcceptSplitWarning, context.RequestAborted));
        }));
        app.MapGet(ownerRecovery, (HttpContext context, string groupKey) =>
            !Fixed(context, true) || friendMode() || peers.OwnerRecoveryProfile(groupKey) is null ?
                Results.StatusCode(403) : Results.Json(peers.OwnerRecoveryBranches(groupKey)));
        app.MapGet(ownerRecovery + "/hosting/{branchHash}", async (HttpContext context, string groupKey, string branchHash) =>
            !Fixed(context, true) || friendMode() || peers.OwnerRecoveryProfile(groupKey) is not { } profileId ||
            !peers.OwnerRecoveryBranches(groupKey).Any(branch => branch.BranchHash == branchHash) ? Results.StatusCode(403) :
                Results.Json(await manager.SeparateCopyHostStatusAsync(profileId, branchHash)));
        app.MapPost(ownerRecovery + "/restore", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            if (friendMode() || peers.OwnerRecoveryProfile(groupKey) is not { } profileId) return Results.StatusCode(403);
            var input = await Read<SeparateCopyHostRestoreRequest>(context, 65536);
            if (!peers.OwnerRecoveryBranches(groupKey).Any(branch => branch.BranchHash == input.BranchHash)) return Results.NotFound();
            return Results.Json(await manager.RestoreSeparateCopyAsync(profileId, input, context.RequestAborted));
        }));
        app.MapPost(ownerRecovery + "/finish", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            if (friendMode() || peers.OwnerRecoveryProfile(groupKey) is not { } profileId) return Results.StatusCode(403);
            var input = await Read<SeparateCopyHostFinishRequest>(context, 65536);
            if (!peers.OwnerRecoveryBranches(groupKey).Any(branch => branch.BranchHash == input.BranchHash)) return Results.NotFound();
            return Results.Json(await manager.FinishSeparateCopyAsync(profileId, input));
        }));
        app.MapPost(ownerRecovery + "/start", (HttpContext context, string groupKey) => Change(context, async () =>
        {
            if (friendMode() || peers.OwnerRecoveryProfile(groupKey) is null) return Results.StatusCode(403);
            var input = await Read<SeparateCopyHostStartRequest>(context, 512);
            if (!input.AcceptSplitWarning || !peers.OwnerRecoveryBranches(groupKey).Any(branch => branch.BranchHash == input.BranchHash))
                return Results.BadRequest(new WorldPeerActionResult(false, "SplitWarningRequired", "Review the recorded branch and confirm the split warning."));
            return Results.Json(await manager.StartSeparateCopyAsync(input.LocalProfileId, input.BranchHash));
        }));
    }
}
