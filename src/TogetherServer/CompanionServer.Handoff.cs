using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class CompanionServer
{
    private delegate bool HandoffAuthenticate(HttpContext context, out PairedDevice? device, out PairingDecision decision);

    private void MapHandoffRequestRoutes(WebApplication app, HandoffAuthenticate authenticate,
        Func<PairingDecision, int> authenticationStatus)
    {
        const string prefix = "/api/companion/servers/{profileId:guid}/shared-world/handoff";
        bool Authorize(HttpContext context, Guid profileId, out PairedDevice? current, out IResult? rejection)
        {
            current = null; rejection = null;
            if (!authenticate(context, out var device, out var decision))
            { rejection = Results.Json(decision, statusCode: authenticationStatus(decision)); return false; }
            if (context.Request.QueryString.HasValue ||
                !int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
            { rejection = Results.Conflict(new { code = "SharedWorldsUpdateRequired" }); return false; }
            decision = pairing.AuthorizeReceiveSaves(device!, profileId, out current);
            if (!decision.Ok || current?.SharedWorldPublicKey is null)
            { rejection = Results.Json(decision, statusCode: 403); return false; }
            return true;
        }

        app.MapGet(prefix + "/request", async (HttpContext context, Guid profileId) =>
        {
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
                return Results.BadRequest(new { code = "HostingRequestRejected" });
            if (!Authorize(context, profileId, out var current, out var rejection)) return rejection!;
            var result = await manager.RecipientHandoffRequestAsync(profileId, current!.Id);
            if (!Authorize(context, profileId, out _, out rejection)) return rejection!;
            return Results.Json(result);
        });
        app.MapPost(prefix + "/respond", async (HttpContext context, Guid profileId) =>
        {
            if (!Authorize(context, profileId, out var current, out var rejection)) return rejection!;
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 1024, context.RequestAborted);
            try
            {
                var request = PlannedHandoffInputs.Response(bytes ?? []);
                var result = await manager.RespondPlannedHandoffAsync(profileId, current!.Id, request);
                if (!Authorize(context, profileId, out _, out rejection)) return rejection!;
                return Results.Json(result, statusCode: result.Ok ? 200 : 409);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.BadRequest(new { ok = false, code = "HostingRequestRejected", message = "Use the fixed hosting confirmation." }); }
        });
        app.MapPost(prefix + "/prepared", async (HttpContext context, Guid profileId) =>
        {
            if (!Authorize(context, profileId, out var current, out var rejection)) return rejection!;
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 16384, context.RequestAborted);
            try
            {
                var request = PlannedHandoffInputs.Preparation(bytes ?? []);
                var result = await manager.ConfirmHandoffPreparationAsync(profileId, current!.Id, request);
                if (!Authorize(context, profileId, out _, out rejection)) return rejection!;
                return Results.Json(result, statusCode: result.Ok ? 200 : 409);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.BadRequest(new { ok = false, code = "HostingRequestRejected", message = "The prepared save or signed connection was rejected." }); }
        });
        app.MapPost(prefix + "/check-route", async (HttpContext context, Guid profileId) =>
        {
            if (!Authorize(context, profileId, out var current, out var rejection)) return rejection!;
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 1024, context.RequestAborted);
            try
            {
                var requestId = PlannedHandoffInputs.Continuation(bytes ?? []);
                var result = await manager.CheckPlannedSuccessorRouteAsync(profileId, current!.Id, requestId, context.RequestAborted);
                if (!Authorize(context, profileId, out _, out rejection)) return rejection!;
                return Results.Json(result, statusCode: result.Ok ? 200 : 409);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.BadRequest(new { ok = false, code = "HostingRequestRejected", message = "Review the current signed move." }); }
        });
        app.MapPost(prefix + "/preparing", async (HttpContext context, Guid profileId) =>
        {
            if (!Authorize(context, profileId, out var current, out var rejection)) return rejection!;
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 1024, context.RequestAborted);
            try
            {
                var requestId = PlannedHandoffInputs.Continuation(bytes ?? []);
                var result = await manager.BeginHandoffPreparationAsync(profileId, current!.Id, requestId);
                if (!Authorize(context, profileId, out _, out rejection)) return rejection!;
                return Results.Json(result, statusCode: result.Ok ? 200 : 409);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.BadRequest(new { ok = false, code = "HostingRequestRejected", message = "Review the current hosting request." }); }
        });
    }
}
