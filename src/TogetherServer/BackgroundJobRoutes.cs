namespace TogetherServer;

internal static class BackgroundJobRoutes
{
    internal static void Map(WebApplication app, LocalData data)
    {
        // Construct once at app startup so interrupted jobs are resolved before
        // any page observes them. Both local Host and Friend work belong here.
        var jobs = BackgroundJobs.For(data);
        app.MapGet("/api/local/background-jobs", (HttpContext context) =>
            context.Request.Headers["X-TogetherServer-Local"] != "1" || context.Request.QueryString.HasValue ||
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                ? Results.StatusCode(403) : Results.Json(jobs.Read()));
    }
}
