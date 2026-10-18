using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal static class ProtectionSettingsLocalRoutes
{
    internal const int MaximumRequestBytes = 2048;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    // The app's loopback/Host/Origin middleware is still mandatory. This fixed
    // local mutation adds exact header/body checks and never maps a companion API.
    internal static void Map(WebApplication app, HostManager manager, SemaphoreSlim modeGate,
        Func<bool> ownerAllowed) => app.MapPut("/api/local/profiles/{id:guid}/protection",
            (HttpContext context, Guid id) => HandleAsync(context, id, manager, modeGate, ownerAllowed));

    internal static bool FixedOwnerRequest(HttpContext context)
    {
        var sensitive = context.Request.Headers["X-TogetherServer-Local"];
        return sensitive.Count == 1 && sensitive[0] == "1" && !context.Request.QueryString.HasValue &&
            HttpMethods.IsPut(context.Request.Method) &&
            context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address) &&
            string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            context.Connection.LocalPort > 0 && context.Request.Host.Port == context.Connection.LocalPort;
    }

    internal static ProtectionSettingsRequest Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw new InvalidDataException("The protection request is missing or oversized.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Use one fixed protection object.");
        void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!fields.Add(property.Name)) throw new InvalidDataException("Duplicate protection fields are not permitted.");
                    Check(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                throw new InvalidDataException("Protection requests do not contain lists.");
        }
        Check(document.RootElement);
        var request = JsonSerializer.Deserialize<ProtectionSettingsRequest>(bytes, Json);
        if (request is null || !ProtectionPolicy.Valid(request.Expected) || !ProtectionPolicy.Valid(request.Next))
            throw new InvalidDataException("Use bounded backup and recovery policy values.");
        return request;
    }

    internal static async Task<IResult> HandleAsync(HttpContext context, Guid profileId,
        HostManager manager, SemaphoreSlim modeGate, Func<bool> ownerAllowed)
    {
        if (!FixedOwnerRequest(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        ProtectionSettingsRequest request;
        try
        {
            if (!context.Request.HasJsonContentType()) throw new InvalidDataException("Use the fixed JSON protection request.");
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, MaximumRequestBytes, context.RequestAborted);
            request = bytes is null ? throw new InvalidDataException("The protection request is missing or oversized.") : Decode(bytes);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
        { return Rejected(); }

        await modeGate.WaitAsync(context.RequestAborted);
        try
        {
            // Root's callback rechecks current Host role, update and shutdown
            // state after waiting, under the same gate as mode changes.
            if (!ownerAllowed()) return Results.Conflict(new
            {
                ok = false,
                code = "ProtectionSettingsUnavailable",
                message = "Switch to My server and finish the current app operation before changing protection settings."
            });
            var result = await manager.UpdateProtectionSettingsAsync(profileId, request, context.RequestAborted);
            return Results.Json(result); // Normal ActionResult includes the current conflict snapshot.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or CryptographicException or ArgumentException)
        { return Rejected(); }
        finally { modeGate.Release(); }
    }

    private static IResult Rejected() => Results.BadRequest(new
    {
        ok = false,
        code = "ProtectionSettingsRequestRejected",
        message = "The fixed protection request could not be verified or saved. Existing settings were kept."
    });
}
