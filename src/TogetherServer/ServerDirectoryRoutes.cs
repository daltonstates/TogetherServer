namespace TogetherServer;

internal static class ServerDirectoryRoutes
{
    internal static IResult? Reject(HttpContext context)
    {
        if (context.Request.Headers["X-TogetherServer-Local"] != "1")
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (context.Request.QueryString.HasValue || context.Request.ContentLength is > 0 ||
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Results.BadRequest(new
            {
                code = "InvalidServerDirectoryRequest",
                message = "The saved server list accepts no query or request body."
            });
        return null;
    }

    internal static void Map(WebApplication app, HostManager manager, FriendService friend)
    {
        app.MapGet("/api/local/servers", (HttpContext context) => Reject(context) ??
            Results.Json(ServerDirectory.Project(manager.ServerDirectoryRows(), friend.ServerDirectoryHosts())));
    }
}
