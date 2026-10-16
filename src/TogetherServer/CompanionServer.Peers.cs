using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class CompanionServer
{
    internal WorldPeerService? PeerWorlds { get; set; }
    private readonly AuthenticatedDeviceRateLimiter peerDeviceRateLimiter = new(360, TimeSpan.FromMinutes(1));

    private void MapPeerRoutes(WebApplication app)
    {
        static bool Compatible(HttpContext context) =>
            int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) &&
            CompanionProtocol.IsCompatible(protocol) &&
            context.Request.Headers["X-TogetherServer-Capability"] == CompanionProtocol.SharedWorldPeersCapability;

        string? ActivePin()
        {
            try { return certificate is { } current ? HostIdentity.Fingerprint(current) : null; }
            catch (CryptographicException) { return null; }
        }

        bool Available(HttpContext context, string? pin) => PeerWorlds is not null && pin is not null &&
            context.Request.QueryString.HasValue == false && context.Request.HasJsonContentType() && isUpdating?.Invoke() != true &&
            isShuttingDown?.Invoke() != true;

        app.MapPost("/api/companion/world-peers/{groupKey}/challenge", async (HttpContext context, string groupKey) =>
        {
            if (!Compatible(context)) return Results.Json(new { code = "PeerUpdateRequired" }, statusCode: 409);
            var pin = ActivePin();
            if (!Available(context, pin) || !SharedWorldPeerTrust.ValidHash(groupKey))
                return Results.Json(new { code = "PeerUnavailable" }, statusCode: 403);
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 2 * 1024 * 1024, context.RequestAborted);
            if (bytes is null) return Results.BadRequest(new { code = "PeerRequestRejected" });
            try
            {
                var request = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024);
                if (!SharedWorldRosterTrust.ValidKey(request.PublicKey)) return Results.BadRequest(new { code = "PeerRequestRejected" });
                if (!peerDeviceRateLimiter.TryAcquire(request.DeviceId))
                    return Results.Json(new { code = "PeerRateLimited" }, statusCode: 429);
                return Results.Json(PeerWorlds!.PrepareChallenge(groupKey, request,
                    pin));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or CryptographicException or ArgumentException)
            { return Results.Json(new { code = "PeerRequestRejected" }, statusCode: 403); }
        });

        app.MapPost("/api/companion/world-peers/{groupKey}/exchange", async (HttpContext context, string groupKey) =>
        {
            if (!Compatible(context)) return Results.Json(new { code = "PeerUpdateRequired" }, statusCode: 409);
            var pin = ActivePin();
            if (!Available(context, pin) || !SharedWorldPeerTrust.ValidHash(groupKey))
                return Results.Json(new { code = "PeerUnavailable" }, statusCode: 403);
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 2 * 1024 * 1024, context.RequestAborted);
            if (bytes is null) return Results.BadRequest(new { code = "PeerRequestRejected" });
            try
            {
                var envelope = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
                if (envelope.Request is null || envelope.Payload is null || envelope.Payload.Length > SharedWorldPeerTrust.MaximumPageBytes)
                    return Results.BadRequest(new { code = "PeerRequestRejected" });
                var request = WorldPeerProtocol.DecodeRequest(JsonSerializer.SerializeToUtf8Bytes(envelope.Request, SharedWorldPeerTrust.Json));
                if (!peerDeviceRateLimiter.TryAcquire(request.Challenge.DeviceId))
                    return Results.Json(new { code = "PeerRateLimited" }, statusCode: 429);
                var result = await PeerWorlds!.ExchangeAsync(groupKey, request, envelope.Payload,
                    context.RequestAborted, pin);
                return Results.Bytes(result, request.Operation == WorldPeerOperations.Chunk ? "application/octet-stream" : "application/json");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or CryptographicException or ArgumentException)
            { return Results.Json(new { code = "PeerRequestRejected" }, statusCode: 403); }
        });
    }
}
