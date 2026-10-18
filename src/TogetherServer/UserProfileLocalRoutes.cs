using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace TogetherServer;

internal static class UserProfileLocalRoutes
{
    internal const int MaximumRequestBytes = 1024;

    internal static void Map(IEndpointRouteBuilder app, UserProfileStore profile, SemaphoreSlim modeGate,
        Func<bool> changesAllowed)
    {
        app.MapGet("/api/local/profile", (HttpContext context) => Read(context, profile));
        app.MapPut("/api/local/profile", (Func<HttpContext, Task<IResult>>)
            (context => SaveAsync(context, profile, modeGate, changesAllowed)));
    }

    internal static bool FixedRequest(HttpContext context)
    {
        var header = context.Request.Headers["X-TogetherServer-Local"];
        return header.Count == 1 && header[0] == "1" && !context.Request.QueryString.HasValue &&
            context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address) &&
            context.Request.Host.Host == "127.0.0.1" && context.Connection.LocalPort > 0 &&
            context.Request.Host.Port == context.Connection.LocalPort;
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumRequestBytes) throw new InvalidDataException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var fields = document.RootElement.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "displayName" ||
            fields[0].Value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var value = fields[0].Value.GetString()!;
        if (!UserProfileName.TryNormalize(value, out _)) throw new InvalidDataException();
        return value;
    }

    internal static IResult Read(HttpContext context, UserProfileStore profile)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !FixedRequest(context)) return Results.StatusCode(403);
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
            context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            return Rejected();
        return Results.Json(profile.View());
    }

    internal static async Task<IResult> SaveAsync(HttpContext context, UserProfileStore profile,
        SemaphoreSlim modeGate, Func<bool> changesAllowed)
    {
        if (!HttpMethods.IsPut(context.Request.Method) || !FixedRequest(context)) return Results.StatusCode(403);
        var origin = $"http://127.0.0.1:{context.Connection.LocalPort}";
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || origins[0] != origin) return Results.StatusCode(403);
        try
        {
            if (!context.Request.HasJsonContentType()) return Rejected();
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, MaximumRequestBytes, context.RequestAborted);
            if (bytes is null) return Rejected();
            var name = Decode(bytes);
            await modeGate.WaitAsync(context.RequestAborted);
            try
            {
                // Profile belongs to this person in both Host and Join modes.
                if (!changesAllowed()) return Results.Conflict(new UserProfileResult(false,
                    "ProfileUnavailable", "Finish the current app operation before changing your name.", profile.View().DisplayName));
                return Results.Json(profile.Save(name));
            }
            finally { modeGate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or CryptographicException or ArgumentException)
        { return Rejected(); }
    }

    private static IResult Rejected() => Results.BadRequest(new UserProfileResult(false,
        "ProfileRequestRejected", "Your name could not be verified or saved. The previous name was kept.", ""));
}
