using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace TogetherServer;

internal static class GettingStartedLocalRoutes
{
    internal const int MaximumRequestBytes = 256;

    internal static void Map(IEndpointRouteBuilder app, GettingStartedService guide,
        SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        app.MapGet("/api/local/getting-started", (HttpContext context) => Read(context, guide));
        app.MapPut("/api/local/getting-started", (Func<HttpContext, Task<IResult>>)
            (context => SaveAsync(context, guide, modeGate, changesAllowed)));
    }

    internal static GettingStartedRequest Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var fields = document.RootElement.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            fields.Any(field => field.Name is not ("intent" or "dismissed"))) throw new InvalidDataException();
        var intent = document.RootElement.GetProperty("intent");
        var dismissed = document.RootElement.GetProperty("dismissed");
        if (intent.ValueKind != JsonValueKind.String || !GettingStartedService.ValidIntent(intent.GetString()) ||
            dismissed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException();
        return new(intent.GetString()!, dismissed.GetBoolean());
    }

    internal static IResult Read(HttpContext context, GettingStartedService guide)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !UserProfileLocalRoutes.FixedRequest(context))
            return Results.StatusCode(403);
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            return Rejected();
        return Results.Json(guide.View());
    }

    internal static async Task<IResult> SaveAsync(HttpContext context, GettingStartedService guide,
        SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        if (!HttpMethods.IsPut(context.Request.Method) || !UserProfileLocalRoutes.FixedRequest(context))
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
            var request = Decode(bytes);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                if (!changesAllowed()) return Results.Conflict(new
                {
                    ok = false, code = "GettingStartedUnavailable",
                    message = "Finish the current app operation before saving the guide."
                });
                return Results.Json(guide.Save(request.Intent, request.Dismissed));
            }
            finally { modeGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or System.Security.SecurityException)
        { return Rejected(); }
    }

    private static IResult Rejected() => Results.BadRequest(new
    {
        ok = false, code = "GettingStartedRequestRejected",
        message = "The getting started guide could not be saved. Retry the guide."
    });
}
