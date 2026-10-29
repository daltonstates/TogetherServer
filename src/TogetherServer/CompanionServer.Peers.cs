using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class CompanionServer
{
    internal WorldPeerService? PeerWorlds { get; set; }
    private readonly AuthenticatedDeviceRateLimiter peerDeviceRateLimiter = new(360, TimeSpan.FromMinutes(1));

    private void MapPeerRoutes(WebApplication app)
    {
        var requestFailures = new PeerRequestFailureDiagnostics(data);
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
            var stage = PeerRequestFailureStage.ReadChallenge;
            try
            {
                var request = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024);
                if (!SharedWorldRosterTrust.ValidKey(request.PublicKey)) return Results.BadRequest(new { code = "PeerRequestRejected" });
                if (!peerDeviceRateLimiter.TryAcquire(request.DeviceId))
                    return Results.Json(new { code = "PeerRateLimited" }, statusCode: 429);
                stage = PeerRequestFailureStage.PrepareChallenge;
                var reply = PeerWorlds!.PrepareChallenge(groupKey, request, pin);
                context.Response.Headers[SharedSaveCompression.SeedCapabilityHeader] = "gzip";
                return Results.Json(reply);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or CryptographicException or ArgumentException or SaveTransferPermissionChangedException)
            {
                requestFailures.Record(stage, error, context.RequestAborted);
                return Results.Json(new { code = "PeerRequestRejected" }, statusCode: 403);
            }
        });

        app.MapPost("/api/companion/world-peers/{groupKey}/exchange", async (HttpContext context, string groupKey) =>
        {
            if (!Compatible(context)) return Results.Json(new { code = "PeerUpdateRequired" }, statusCode: 409);
            var pin = ActivePin();
            if (!Available(context, pin) || !SharedWorldPeerTrust.ValidHash(groupKey))
                return Results.Json(new { code = "PeerUnavailable" }, statusCode: 403);
            var stage = PeerRequestFailureStage.ReadExchange;
            var operation = WorldPeerTransferOperation.None;
            var phase = WorldPeerTransferPhase.Metadata;
            try
            {
                var bytes = await SharedSaveCompression.ReadPeerRequestAsync(context, 2 * 1024 * 1024);
                if (bytes is null) return Results.BadRequest(new { code = "PeerRequestRejected" });
                var envelope = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
                if (envelope.Request is null || envelope.Payload is null || envelope.Payload.Length > SharedWorldPeerTrust.MaximumPageBytes)
                    return Results.BadRequest(new { code = "PeerRequestRejected" });
                var request = WorldPeerProtocol.DecodeRequest(JsonSerializer.SerializeToUtf8Bytes(envelope.Request, SharedWorldPeerTrust.Json));
                if (!peerDeviceRateLimiter.TryAcquire(request.Challenge.DeviceId))
                    return Results.Json(new { code = "PeerRateLimited" }, statusCode: 429);
                operation = request.Operation switch
                {
                    WorldPeerOperations.Catalog => WorldPeerTransferOperation.Catalog,
                    WorldPeerOperations.Version => WorldPeerTransferOperation.Manifest,
                    WorldPeerOperations.Chunk => WorldPeerTransferOperation.Chunk,
                    WorldPeerOperations.Seed => WorldPeerTransferOperation.Seed,
                    WorldPeerOperations.Receipt => WorldPeerTransferOperation.Receipt,
                    _ => WorldPeerTransferOperation.Metadata
                };
                phase = operation switch
                {
                    WorldPeerTransferOperation.Seed => WorldPeerTransferPhase.Preparing,
                    WorldPeerTransferOperation.Chunk => WorldPeerTransferPhase.Sending,
                    WorldPeerTransferOperation.Receipt => WorldPeerTransferPhase.Receipt,
                    _ => WorldPeerTransferPhase.Metadata
                };
                stage = PeerRequestFailureStage.ExecuteExchange;
                var result = await PeerWorlds!.ExchangeAsync(groupKey, request, envelope.Payload,
                    context.RequestAborted, pin);
                return request.Operation == WorldPeerOperations.Chunk ? SharedSaveCompression.Chunk(context, result) :
                    Results.Bytes(result, "application/json");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                UnauthorizedAccessException or CryptographicException or ArgumentException or SaveTransferPermissionChangedException)
            {
                requestFailures.Record(stage, error, context.RequestAborted, operation, phase);
                return Results.Json(new { code = "PeerRequestRejected" }, statusCode: 403);
            }
            catch (OperationCanceledException error) when (context.RequestAborted.IsCancellationRequested)
            {
                requestFailures.Record(stage, error, context.RequestAborted, operation, phase);
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
        });
    }
}
