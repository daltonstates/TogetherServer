using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace TogetherServer;

internal static class RequiredFeatureSetupLocalRoutes
{
    internal const int MaximumRequestBytes = 256;

    internal static void Map(IEndpointRouteBuilder app, RequiredFeatureSetupService setup,
        SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        app.MapGet("/api/local/required-setup", (HttpContext context) => Read(context, setup));
        app.MapPost("/api/local/required-setup/{id}/complete", (HttpContext context, string id) =>
            CompleteAsync(context, id, setup, modeGate, changesAllowed));
    }

    internal static int Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var fields = document.RootElement.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "revision" ||
            fields[0].Value.ValueKind != JsonValueKind.Number || !fields[0].Value.TryGetInt32(out var revision) ||
            revision is < 1 or > 10000) throw new InvalidDataException();
        return revision;
    }

    internal static IResult Read(HttpContext context, RequiredFeatureSetupService setup)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !UserProfileLocalRoutes.FixedRequest(context))
            return Results.StatusCode(403);
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            return Rejected();
        return Results.Json(setup.View());
    }

    internal static async Task<IResult> CompleteAsync(HttpContext context, string id,
        RequiredFeatureSetupService setup, SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !UserProfileLocalRoutes.FixedRequest(context))
            return Results.StatusCode(403);
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || origins[0] != $"http://127.0.0.1:{context.Connection.LocalPort}")
            return Results.StatusCode(403);
        try
        {
            if (!context.Request.HasJsonContentType()) return Rejected();
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, MaximumRequestBytes, context.RequestAborted);
            if (bytes is null) return Rejected();
            var revision = Decode(bytes);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                if (!changesAllowed()) return Results.Conflict(new RequiredFeatureSetupResult(false,
                    "RequiredSetupUnavailable", "Finish the current app operation before completing setup.", setup.View().Features));
                return Results.Json(setup.Complete(id, revision));
            }
            finally { modeGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or CryptographicException or ArgumentException)
        { return Rejected(); }
    }

    private static IResult Rejected() => Results.BadRequest(new
    { ok = false, code = "RequiredSetupRequestRejected", message = "Setup progress could not be verified or saved. Retry setup." });
}
