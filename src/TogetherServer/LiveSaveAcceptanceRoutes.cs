using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveSaveTestReceiptInput([property: JsonRequired] SharedWorldReceipt Receipt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LiveSaveTestReviewInput([property: JsonRequired] bool Acknowledge);

internal static class LiveSaveAcceptanceRoutes
{
    private const string Base = "/api/local/profiles/{profileId:guid}/live-save-acceptance";

    internal static bool FixedLocalRequest(HttpContext context, bool read)
    {
        var header = context.Request.Headers["X-TogetherServer-Local"];
        return header.Count == 1 && string.Equals(header[0], "1", StringComparison.Ordinal) &&
            !context.Request.QueryString.HasValue &&
            (!read || context.Request.ContentLength is null or 0 &&
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true);
    }

    internal static T Decode<T>(byte[] bytes) where T : class
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        void RejectDuplicates(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate local fields are not permitted.");
                    RejectDuplicates(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        }
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        RejectDuplicates(json.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, LiveSaveAcceptanceTrust.Json) ?? throw new InvalidDataException();
    }

    private static async Task<T> Read<T>(HttpContext context, int maximum) where T : class
    {
        if (!context.Request.HasJsonContentType()) throw new InvalidDataException("Use a fixed JSON request.");
        var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
            context.Request.ContentLength, maximum, context.RequestAborted);
        return bytes is null ? throw new InvalidDataException("The local request is missing or oversized.") : Decode<T>(bytes);
    }

    // Both callbacks are mandatory. Root supplies its current loopback owner,
    // exact Origin, Host-role and update guards; neither may be a permissive
    // substitute for the app's normal local-request boundary.
    internal static void Map(WebApplication app, HostManager host,
        Func<HttpContext, bool> allowLocalRead, Func<HttpContext, bool> allowLocalMutation)
    {
        async Task<IResult> Guard(HttpContext context, bool mutation, Func<Task<IResult>> action)
        {
            if (!FixedLocalRequest(context, !mutation) ||
                !(mutation ? allowLocalMutation(context) : allowLocalRead(context))) return Results.StatusCode(403);
            try { return await action(); }
            catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex))
            { return Results.BadRequest(new LiveSaveAcceptanceResult(false, "LiveSaveAcceptanceRequestRejected", "The fixed private-test request could not be verified.")); }
        }
        static IResult ActionResult(LiveSaveAcceptanceResult result) => result.Ok ? Results.Json(result) : Results.Conflict(result);

        app.MapGet(Base, (HttpContext context, Guid profileId) => Guard(context, false, async () =>
            Results.Json(await host.LiveSaveAcceptanceAsync(profileId, context.RequestAborted))));
        app.MapPost(Base + "/tests", (HttpContext context, Guid profileId) => Guard(context, true, async () =>
            ActionResult(await host.RequestLiveSaveAcceptanceTestAsync(profileId, await Read<LiveSaveRequest>(context, 128), context.RequestAborted))));
        app.MapGet(Base + "/tests/{testId:guid}", (HttpContext context, Guid profileId, Guid testId) => Guard(context, false, async () =>
            await host.LiveSaveTestArtifactAsync(profileId, testId, context.RequestAborted) is { } artifact ? Results.Json(artifact) : Results.NotFound()));
        app.MapGet(Base + "/tests/{testId:guid}/manifest", (HttpContext context, Guid profileId, Guid testId) => Guard(context, false, async () =>
            await host.LiveSaveTestManifestAsync(profileId, testId, context.RequestAborted) is { } manifest ? Results.Json(manifest) : Results.NotFound()));
        app.MapGet(Base + "/tests/{testId:guid}/chunks/{index:int}", (HttpContext context, Guid profileId, Guid testId, int index) => Guard(context, false, async () =>
            await host.LiveSaveTestChunkAsync(profileId, testId, index, context.RequestAborted) is { } chunk ?
                Results.Bytes(chunk, "application/octet-stream") : Results.NotFound()));
        app.MapPost(Base + "/tests/{testId:guid}/receipt", (HttpContext context, Guid profileId, Guid testId) => Guard(context, true, async () =>
        {
            var request = await Read<LiveSaveTestReceiptInput>(context, SharedWorldReceiptTrust.MaximumRequestBytes);
            return ActionResult(await host.RegisterLiveSaveTestReceiptAsync(profileId, testId, request.Receipt, context.RequestAborted));
        }));
        app.MapPost(Base + "/tests/{testId:guid}/confirm", (HttpContext context, Guid profileId, Guid testId) => Guard(context, true, async () =>
            ActionResult(await host.ConfirmLiveSaveAcceptanceAsync(profileId, testId, await Read<LiveSaveTestConfirmation>(context, 512), context.RequestAborted))));
        app.MapPost(Base + "/tests/{testId:guid}/review", (HttpContext context, Guid profileId, Guid testId) => Guard(context, true, async () =>
        {
            if (!(await Read<LiveSaveTestReviewInput>(context, 64)).Acknowledge) return Results.BadRequest();
            return ActionResult(await host.ReviewLiveSaveAcceptanceTestAsync(profileId, testId, context.RequestAborted));
        }));
    }
}
