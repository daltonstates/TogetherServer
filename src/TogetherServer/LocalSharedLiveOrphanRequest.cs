using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace TogetherServer;

// Fixed loopback request shape. No path, command, or arbitrary target is accepted.
internal static class LocalSharedLiveOrphanRequest
{
    private const int MaximumBodyBytes = 128;

    internal static IResult? RejectGet(HttpContext context, bool friendMode)
    {
        if (context.Request.Headers["X-TogetherServer-Local"] != "1")
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (friendMode)
            return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
        if (context.Request.QueryString.HasValue || context.Request.ContentLength is > 0 ||
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return InvalidShape();
        return null;
    }

    internal static async Task<(string? VersionHash, IResult? Rejection)> ReadPostAsync(
        HttpContext context, bool friendMode)
    {
        if (context.Request.Headers["X-TogetherServer-Local"] != "1")
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        if (friendMode)
            return (null, Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." }));
        if (context.Request.QueryString.HasValue ||
            context.Request.ContentLength is 0 or > MaximumBodyBytes ||
            !string.Equals(context.Request.ContentType?.Split(';')[0].Trim(),
                "application/json", StringComparison.OrdinalIgnoreCase))
            return (null, InvalidShape());
        try
        {
            var bytes = new byte[MaximumBodyBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await context.Request.Body.ReadAsync(
                    bytes.AsMemory(count, bytes.Length - count), context.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count is 0 or > MaximumBodyBytes) return (null, InvalidShape());
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count),
                new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, InvalidShape());
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 1 || fields[0].Name != "versionHash" ||
                fields[0].Value.ValueKind != JsonValueKind.String)
                return (null, InvalidShape());
            var hash = fields[0].Value.GetString();
            if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) ||
                !hash.Equals(hash.ToUpperInvariant(), StringComparison.Ordinal))
                return (null, InvalidShape());
            return (hash, null);
        }
        catch (JsonException) { return (null, InvalidShape()); }
    }

    private static IResult InvalidShape() => Results.BadRequest(new
    {
        code = "InvalidLiveOrphanRequest",
        message = "Use the exact reviewed save hash with no other input."
    });
}
