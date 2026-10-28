using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace TogetherServer;

internal static class ApprovedGamePortsLocalRoutes
{
    internal const int MaximumRequestBytes = 512;
    internal static void Map(IEndpointRouteBuilder app, ApprovedGamePortsStore store,
        SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        const string route = "/api/local/approved-game-ports";
        app.MapGet(route, (HttpContext context) => Read(context, store));
        app.MapPost(route, (Func<HttpContext, Task<IResult>>)(context => MutateAsync(context, store, null, modeGate, changesAllowed)));
        app.MapPut(route + "/{id:guid}", (Func<HttpContext, Guid, Task<IResult>>)((context, id) => MutateAsync(context, store, id, modeGate, changesAllowed)));
        app.MapDelete(route + "/{id:guid}", (Func<HttpContext, Guid, Task<IResult>>)((context, id) => MutateAsync(context, store, id, modeGate, changesAllowed)));
    }

    internal static ApprovedGamePortInput Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var root = document.RootElement;
        var fields = root.EnumerateObject().ToArray();
        if (fields.Length is < 2 or > 3 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(field => field.Name is not ("game" or "gamePort" or "factorioRconPort")) ||
            !root.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("gamePort", out var port) || port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var gamePort)) throw new InvalidDataException();
        int? rconPort = null;
        if (root.TryGetProperty("factorioRconPort", out var rcon) && rcon.ValueKind != JsonValueKind.Null)
        {
            if (rcon.ValueKind != JsonValueKind.Number || !rcon.TryGetInt32(out var value)) throw new InvalidDataException();
            rconPort = value;
        }
        var input = new ApprovedGamePortInput(game.GetString()!, gamePort, rconPort);
        if (!ApprovedGamePortsStore.Valid(input.Game, input.GamePort, input.FactorioRconPort)) throw new InvalidDataException();
        return input;
    }

    private static bool EmptyBody(HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true &&
        context.Request.ContentLength is not > 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding");

    internal static IResult Read(HttpContext context, ApprovedGamePortsStore store)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !UserProfileLocalRoutes.FixedRequest(context)) return Results.StatusCode(403);
        return EmptyBody(context) ? Results.Json(store.View()) : Rejected();
    }

    internal static async Task<IResult> MutateAsync(HttpContext context, ApprovedGamePortsStore store, Guid? id,
        SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        var deleting = HttpMethods.IsDelete(context.Request.Method);
        if (!(HttpMethods.IsPost(context.Request.Method) && id is null ||
              (HttpMethods.IsPut(context.Request.Method) || deleting) && id is not null) ||
            !UserProfileLocalRoutes.FixedRequest(context)) return Results.StatusCode(403);
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || origins[0] != $"http://127.0.0.1:{context.Connection.LocalPort}") return Results.StatusCode(403);
        try
        {
            ApprovedGamePortInput? input = null;
            if (deleting) { if (!EmptyBody(context)) return Rejected(); }
            else
            {
                if (!context.Request.HasJsonContentType()) return Rejected();
                var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                    context.Request.ContentLength, MaximumRequestBytes, context.RequestAborted);
                input = Decode(bytes ?? []);
            }
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                // This PC's choices are available from both Host and Join.
                if (!changesAllowed()) return Results.Conflict(new ApprovedGamePortsResult(false,
                    "ApprovedPortsUnavailable", "Finish the current app operation before changing approved game ports.", store.View().ApprovedPorts));
                return Results.Json(deleting ? store.Delete(id!.Value) : store.Save(input!, id));
            }
            finally { modeGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or CryptographicException or ArgumentException or InvalidOperationException)
        { return Rejected(); }
    }

    private static IResult Rejected() => Results.BadRequest(new ApprovedGamePortsResult(false,
        "ApprovedPortsRequestRejected", "The game ports could not be verified or saved. Review the port choices and retry.", []));
}
