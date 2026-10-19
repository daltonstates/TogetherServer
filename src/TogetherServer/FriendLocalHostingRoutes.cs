using System.Text.Json;

namespace TogetherServer;

internal static class FriendLocalHostingRoutes
{
    internal sealed record HostStartInput(string BranchHash, bool AcceptSplitWarning);

    internal static HostStartInput ParseStart(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 512) throw new InvalidDataException("The start request is invalid.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 2 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The start request is invalid.");
        var fields = root.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            fields.Any(field => field.Name is not ("branchHash" or "acceptSplitWarning")) ||
            !root.TryGetProperty("branchHash", out var branch) || branch.ValueKind != JsonValueKind.String ||
            !SharedWorldPeerTrust.ValidHash(branch.GetString()) ||
            !root.TryGetProperty("acceptSplitWarning", out var warning) ||
            warning.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The exact local history and warning choice are required.");
        return new(branch.GetString()!, warning.GetBoolean());
    }
    internal static bool Fixed(HttpContext context, bool get = false) =>
        context.Request.Headers["X-TogetherServer-Local"] == "1" &&
        !context.Request.QueryString.HasValue &&
        (!get || context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true);

    internal static void Map(WebApplication app, LocalData data, FriendService friend,
        HostManager manager, CompanionServer companion, ExternalPortProbe externalProbe,
        SemaphoreSlim modeGate, Func<bool> friendMode, Func<bool> unavailable)
    {
        var service = new FriendLocalHostingService(data, friend, manager,
            routeCheck: LocalHostingRouteCheck.Create(data, manager, companion, externalProbe));
        const string route = "/api/local/friend/{profileId:guid}/shared-world/hosting";
        app.MapGet(route, async (HttpContext context, Guid profileId) =>
        {
            if (!Fixed(context, true)) return Results.StatusCode(403);
            if (!friendMode()) return Results.Conflict(FriendLocalHostingService.Unavailable("HostMode", "Open Join to prepare a shared server on this PC."));
            try { return Results.Json(await service.StatusAsync(profileId, context.RequestAborted)); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException)
            { return Results.Json(FriendLocalHostingService.Unavailable("LocalHostingReviewRequired", "This PC's shared-save setup needs review. Existing saves were kept.")); }
        });
        app.MapPost(route + "/prepare", async (HttpContext context, Guid profileId) =>
        {
            if (!Fixed(context)) return Results.StatusCode(403);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                if (!friendMode()) return Results.Conflict(FriendLocalHostingService.Unavailable("HostMode", "Open Join to prepare a shared server on this PC."));
                if (unavailable()) return Results.Conflict(FriendLocalHostingService.Unavailable("AppBusy", "Wait for the current app operation."));
                var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                    context.Request.ContentLength, 8192, context.RequestAborted);
                var request = FriendLocalHostingService.ParseRequest(bytes ?? []);
                return Results.Json(await service.PrepareAsync(profileId, request, context.RequestAborted));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException)
            { return Results.BadRequest(FriendLocalHostingService.Unavailable("LocalHostingRequestRejected", "Use the fixed local hosting preparation action.")); }
            finally { modeGate.Release(); }
        });
        app.MapPost("/api/local/profiles/{localProfileId:guid}/shared-world/hosting/start",
            async (HttpContext context, Guid localProfileId) =>
        {
            if (!Fixed(context)) return Results.StatusCode(403);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                if (friendMode()) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Open Host to start this local server." });
                if (unavailable()) return Results.Conflict(new { ok = false, code = "AppBusy", message = "Wait for the current app operation." });
                var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                    context.Request.ContentLength, 512, context.RequestAborted);
                var request = ParseStart(bytes ?? []);
                if (!request.AcceptSplitWarning) return Results.Json(new { ok = false, code = "SplitWarningRequired",
                    message = "Confirm that another Host may still be running and both histories remain separate." });
                return Results.Json(await manager.StartSeparateCopyAsync(localProfileId, request.BranchHash));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
            { return Results.BadRequest(new { ok = false, code = "LocalHostingRequestRejected", message = "Use the fixed local Start action." }); }
            finally { modeGate.Release(); }
        });
    }
}
