using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace TogetherServer;

internal delegate bool GamePasswordAuthenticate(HttpContext context, out PairedDevice? device, out PairingDecision decision);

internal static class GamePasswordCompanionRoutes
{
    internal static void Map(IEndpointRouteBuilder app, LocalData data, PairingService pairing,
        GamePasswordAuthenticate authenticate, Func<PairingDecision, int> authenticationStatus,
        Func<Guid, Guid>? resolveProfile = null) =>
        app.MapPost("/api/companion/servers/{profileId:guid}/game-password/reveal", (HttpContext context, Guid profileId) =>
            RevealAsync(context, profileId, data, pairing, authenticate, authenticationStatus, resolveProfile));

    internal static async Task<IResult> RevealAsync(HttpContext context, Guid profileId, LocalData data,
        PairingService pairing, GamePasswordAuthenticate authenticate, Func<PairingDecision, int> authenticationStatus,
        Func<Guid, Guid>? resolveProfile = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.IsHttps)
            return Results.StatusCode(403);
        if (profileId == Guid.Empty || context.Request.QueryString.HasValue ||
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
            await context.Request.Body.ReadAsync(new byte[1], context.RequestAborted) != 0)
            return Results.BadRequest(GamePasswords.Failure(profileId, "GamePasswordRequestRejected"));
        if (!authenticate(context, out var device, out var decision))
            return Results.Json(GamePasswords.Failure(profileId, decision.Code), statusCode: authenticationStatus(decision));
        var protocol = context.Request.Headers[CompanionProtocol.HeaderName];
        var capability = context.Request.Headers["X-TogetherServer-Capability"];
        if (protocol.Count != 1 || !int.TryParse(protocol[0], out var version) || !CompanionProtocol.IsCompatible(version) ||
            capability.Count != 1 || capability[0] != CompanionProtocol.GamePasswordCapability)
            return Results.Conflict(GamePasswords.Failure(profileId, "GamePasswordUpdateRequired"));
        try
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var bearer = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : null;
            // The assigned identity remains the permission boundary. Resolving
            // inside its protected read can only follow a signed continuation
            // currently running on this same Host PC.
            var result = pairing.ReadGamePassword(device!.Id, bearer, profileId, resolveProfile is null ? null :
                () => GamePasswords.Read(data, resolveProfile(profileId)) with { ProfileId = profileId });
            return Results.Json(result, statusCode: result.Ok ? 200 : result.Code is "PermissionDenied" or
                "Revoked" or "AccessExpired" or "ApprovalPending" ? 403 : result.Code is "CredentialExpired" or "Unauthorized" ? 401 : 409);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or CryptographicException or ArgumentException or System.Security.SecurityException)
        { return Results.Json(GamePasswords.Failure(profileId, "GamePasswordUnavailable"), statusCode: 503); }
    }
}
