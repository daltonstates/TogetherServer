using System.Text.Json;

namespace TogetherServer;

internal static class GamePasswordLocalRoutes
{
    internal const int MaximumRequestBytes = 128;
    internal static void Map(IEndpointRouteBuilder app, FriendService friend, SemaphoreSlim modeGate, Func<bool> allowed) =>
        app.MapPost("/api/local/friend/{profileId:guid}/game-password/reveal", (HttpContext context, Guid profileId) =>
            RevealAsync(context, profileId, friend, modeGate, allowed));

    internal static Guid Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var fields = document.RootElement.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "connectionId" || fields[0].Value.ValueKind != JsonValueKind.String ||
            !fields[0].Value.TryGetGuid(out var connectionId) || connectionId == Guid.Empty) throw new InvalidDataException();
        return connectionId;
    }

    internal static async Task<IResult> RevealAsync(HttpContext context, Guid profileId, FriendService friend,
        SemaphoreSlim modeGate, Func<bool> allowed)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(context.Request.Method) || !UserProfileLocalRoutes.FixedRequest(context))
            return Results.StatusCode(403);
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || origins[0] != $"http://127.0.0.1:{context.Connection.LocalPort}") return Results.StatusCode(403);
        try
        {
            if (profileId == Guid.Empty || !context.Request.HasJsonContentType()) return Rejected(profileId);
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, MaximumRequestBytes, context.RequestAborted);
            if (bytes is null) return Rejected(profileId);
            var connectionId = Decode(bytes);
            await modeGate.WaitAsync(context.RequestAborted);
            try { if (!allowed()) return Results.Conflict(GamePasswords.Failure(profileId, "GamePasswordOperationUnavailable")); }
            finally { modeGate.Release(); }
            // A slow Host must not hold the app's safe Stop/navigation gate.
            var result = await friend.RevealGamePasswordAsync(connectionId, profileId, context.RequestAborted);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                return allowed() ? Results.Json(result) :
                    Results.Conflict(GamePasswords.Failure(profileId, "GamePasswordOperationUnavailable"));
            }
            finally { modeGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or System.Security.SecurityException)
        { return Rejected(profileId); }
    }

    private static IResult Rejected(Guid profileId) =>
        Results.BadRequest(GamePasswords.Failure(profileId, "GamePasswordRequestRejected"));
}
