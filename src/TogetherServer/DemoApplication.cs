using System.Net;
using System.Reflection;
using System.Text.Json;

namespace TogetherServer;

// A demo is only a native shell around browser-owned sample data. In particular,
// constructing the real runtime and merely skipping its loops would still run
// recovery, process identity, storage and membership work in constructors.
internal static class DemoApplication
{
    internal static async Task RunAsync(string[] arguments)
    {
        AppInstance instance;
        int port;
        try
        {
            instance = AppInstance.Resolve(arguments);
            if (!instance.IsDemo) throw new ArgumentException("Open the explicit demo executable or use --demo.");
            var portIndex = Array.FindIndex(arguments, item => item.Equals("--port", StringComparison.OrdinalIgnoreCase));
            port = portIndex >= 0 ? int.Parse(arguments[portIndex + 1]) : instance.DefaultLocalPort;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            DesktopLaunch.ShowError("TogetherServer could not safely open the demo.\n\n" + error.Message);
            Environment.ExitCode = 1;
            return;
        }

        FileStream ownership;
        try
        {
            instance.PrepareDataRoot();
            ownership = new FileStream(Path.Combine(instance.DataRoot, "host.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            if (await TryShowExistingAsync(instance, port)) return;
            DesktopLaunch.ShowError("TogetherServer could not open its separate demo data.\n\n" + error.Message);
            Environment.ExitCode = 1;
            return;
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or
                                      System.Security.SecurityException)
        {
            DesktopLaunch.ShowError("TogetherServer could not safely open the demo data.\n\n" + error.Message);
            Environment.ExitCode = 1;
            return;
        }
        using var ownedRoot = ownership;
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        await using var app = builder.Build();
        var address = new Uri($"http://127.0.0.1:{port}/?demo=sharing");
        var desktop = new DesktopWindow(address, instance.DataRoot, app.Lifetime.StopApplication,
            closeToTray: false, startInTray: false, displayName: instance.DisplayName,
            isStaging: true, isDemo: true);

        app.Use((HttpContext context, Func<Task> next) => HandleRequestAsync(context, instance, port, next));
        app.MapGet("/api/local/instance", () => Results.Json(instance.View(port)));
        app.MapGet("/api/local/window", () => Results.Json(new
        {
            available = true,
            visible = desktop.Visible,
            rendered = desktop.Rendered,
            loadState = desktop.LoadState,
            loadErrorCode = desktop.LoadErrorCode,
            loadFailureKind = desktop.LoadFailureKind,
            loadFailureHResult = desktop.LoadFailureHResult,
            fileDialogOpen = false,
            customChrome = desktop.CustomChrome
        }));
        app.MapPost("/api/local/show", async () => await desktop.ShowAsync()
            ? Results.Json(new { ok = true, code = "WindowShown" })
            : Results.Conflict(new { ok = false, code = "WindowUnavailable" }));
        app.MapPost("/api/local/quit", (HttpContext context) =>
        {
            context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
            return Results.Json(new { ok = true, code = "Closing", message = "TogetherServer demo is closing." });
        });
        BundledUi.Map(app);
        app.Lifetime.ApplicationStarted.Register(desktop.Start);
        app.Lifetime.ApplicationStopping.Register(desktop.Exit);
        DiagnosticOutput.WriteLine($"{instance.DisplayName} local GUI: {address}");
        try { await app.RunAsync(); }
        catch (Exception error)
        {
            DesktopLaunch.ShowError("TogetherServer could not start its separate demo window.\n\n" + error.Message);
            Environment.ExitCode = 1;
        }
    }

    internal static async Task HandleRequestAsync(HttpContext context, AppInstance instance, int port, Func<Task> next)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            !string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Host.Port != port)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers["Cache-Control"] = "no-store";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (!instance.AllowsLocalApiRequest(context.Request.Method, context.Request.Path.Value ?? "") ||
                context.Request.QueryString.HasValue || context.Request.ContentLength is > 0 ||
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("""{"ok":false,"code":"DemoRealServicesDisabled","message":"Demo actions use sample data in this window. Real games, network checks, installations and local data changes are disabled."}""");
                return;
            }
            if (context.Request.Method != "GET" &&
                (!string.Equals(context.Request.Headers.Origin, $"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                 context.Request.Headers["X-TogetherServer-Local"] != "1"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next();
    }

    private static async Task<bool> TryShowExistingAsync(AppInstance instance, int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var address = $"http://127.0.0.1:{port}";
        try
        {
            using var response = await client.GetAsync(address + "/api/local/instance");
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!json.RootElement.TryGetProperty("isDemo", out var demo) || demo.ValueKind != JsonValueKind.True ||
                !json.RootElement.TryGetProperty("dataRoot", out var root) || root.ValueKind != JsonValueKind.String ||
                !string.Equals(root.GetString(), instance.DataRoot, StringComparison.OrdinalIgnoreCase)) return false;
            using var show = new HttpRequestMessage(HttpMethod.Post, address + "/api/local/show");
            show.Headers.TryAddWithoutValidation("Origin", address);
            show.Headers.Add("X-TogetherServer-Local", "1");
            using var shown = await client.SendAsync(show);
            return shown.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        { return false; }
    }
}

internal static class BundledUi
{
    internal static void Map(WebApplication app)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var assets = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Ui/", StringComparison.Ordinal))
            .ToDictionary(name => name[3..].Replace('\\', '/'), name => name, StringComparer.OrdinalIgnoreCase);
        app.MapGet("/{**path}", async (HttpContext context, string? path) =>
        {
            var requested = string.IsNullOrWhiteSpace(path) ? "index.html" : path.TrimStart('/');
            if (requested.Contains("..", StringComparison.Ordinal) || !assets.TryGetValue(requested, out var resource))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("UI asset not found. Build ui/ before publishing.");
                return;
            }
            context.Response.ContentType = Path.GetExtension(requested).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            await stream.CopyToAsync(context.Response.Body);
        });
    }
}
