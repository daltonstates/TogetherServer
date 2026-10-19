using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Only fake paths, synthetic files, static URI policies and in-memory HTTP.
// No app entry point, window, listener, process, native dialog or game runs.
internal static class DemoInstanceChecks
{
    internal static async Task RunAsync(string root)
    {
        var local = Path.Combine(root, "demo-instance-local");
        CheckIdentity(local);
        CheckRootsAndMarkers(local);
        CheckNativeRequests();
        await CheckApiBoundaryAsync(local);
    }

    private static void CheckIdentity(string local)
    {
        var demoPath = Path.Combine(local, AppInstance.DemoExecutableName);
        var demo = AppInstance.Resolve([], _ => null, local, demoPath);
        Require(demo.IsDemo && demo.IsStaging && AppInstance.RequestsStaging([], demoPath),
            "the exact demo filename failed to select an isolated development instance");
        Require(demo.DataRoot == Path.Combine(local, "TogetherServer-Demo") &&
            demo.DefaultLocalPort == 5129 && demo.DefaultCompanionPort == 5133,
            "demo collided with the ordinary development/production data or ports");
        Require(demo.DisplayName == "TogetherServer DEVELOPMENT DEMO" && !demo.StartupAvailable && !demo.UpdatesAvailable,
            "demo native identity or startup/update restrictions were lost");
        var view = demo.View(5419);
        Require(view.IsDemo && view.IsStaging && view.LocalPort == 5419 && view.CompanionPort == 5133 &&
            view.DataRoot == demo.DataRoot && view.DataIsolation.Contains("sample data", StringComparison.Ordinal),
            "demo instance metadata hid the explicit sample-data boundary");
        Require(AppInstance.Resolve(["--demo", "--desktop", "--port", "5419"], _ => null, local,
            Path.Combine(local, "TogetherServer.exe")).IsDemo,
            "explicit --demo did not select demo when using an ordinary executable name");
        Require(AppInstance.Resolve([], _ => null, local, demoPath.ToUpperInvariant()).IsDemo,
            "Windows filename casing changed demo identity");
        foreach (var name in new[] { "TogetherServer.exe", "TogetherServer DEVELOPMENT.exe", "TogetherServer DEVELOPMENT DEMO.exe.previous", "TogetherServer DEVELOPMENT DEMO copy.exe" })
            Require(!AppInstance.RequestsDemo([], Path.Combine(local, name)), "a non-demo filename unexpectedly activated demo");

        var production = AppInstance.Resolve([], _ => null, local, Path.Combine(local, "TogetherServer.exe"));
        var development = AppInstance.Resolve([], _ => null, local, Path.Combine(local, AppInstance.DevelopmentExecutableName));
        Require(!production.IsDemo && !production.IsStaging && production.DefaultLocalPort == 5127 &&
            production.DefaultCompanionPort == 5131 && production.DataRoot == Path.Combine(local, "TogetherServer") &&
            production.StartupAvailable && production.UpdatesAvailable,
            "adding demo changed production defaults");
        Require(!development.IsDemo && development.IsStaging && development.DefaultLocalPort == 5128 &&
            development.DefaultCompanionPort == 5132 && development.DataRoot == Path.Combine(local, "TogetherServer-Staging"),
            "adding demo changed ordinary development defaults");

        foreach (var arguments in new[]
        {
            new[] { "--startup" }, new[] { "--apply-update", "candidate.exe" },
            new[] { MinecraftConsoleCapture.Command, "payload", "pipe" },
            new[] { InstallerIntegration.ConfigureCommand }, new[] { InstallerIntegration.RemoveStartupCommand },
            new[] { "--port", "512" }, new[] { "--port" }, new[] { "--host", "--friend" },
            new[] { "--unreviewed-native-command" }
        })
            RequireThrows<ArgumentException>(() => AppInstance.Resolve(arguments, _ => null, local, demoPath),
                "demo accepted sign-in/native-helper/invalid launch arguments");

        Require(demo.ValidateSettings(new HostSettings()).Ok &&
            !demo.ValidateSettings(new HostSettings { CompanionListeningEnabled = true }).Ok &&
            !demo.ValidateSettings(new HostSettings { RemoteControlsEnabled = true }).Ok &&
            !demo.ValidateSettings(new HostSettings { Profiles = [new ServerProfile()] }).Ok,
            "demo accepted real profiles or enabled companion services");
    }

    private static void CheckRootsAndMarkers(string local)
    {
        var production = Path.Combine(local, "fake-production");
        var staging = Path.Combine(local, "fake-development");
        Directory.CreateDirectory(production);
        Directory.CreateDirectory(staging);
        var productionMarker = Path.Combine(production, "owner-save.db");
        var developmentMarker = Path.Combine(staging, "settings.json");
        File.WriteAllText(productionMarker, "synthetic production world remains unchanged");
        File.WriteAllText(developmentMarker, "synthetic existing development settings remain unchanged");
        AppInstance Demo(string demoRoot) => AppInstance.Resolve(["--demo"], name => name switch
        {
            "TOGETHERSERVER_DATA_DIR" => production,
            "TOGETHERSERVER_STAGING_DATA_DIR" => staging,
            "TOGETHERSERVER_DEMO_DATA_DIR" => demoRoot,
            _ => null
        }, local, Path.Combine(local, "TogetherServer.exe"));

        foreach (var other in new[] { production, staging, Path.Combine(local, "TogetherServer"), Path.Combine(local, "TogetherServer-Staging") })
        {
            RequireThrows<InvalidDataException>(() => Demo(other), "demo accepted another instance's data root");
            RequireThrows<InvalidDataException>(() => Demo(Path.Combine(other, "child")), "demo accepted a root inside another instance");
        }
        RequireThrows<InvalidDataException>(() => Demo(local), "demo accepted a root containing another instance");
        var populated = Path.Combine(local, "populated-demo");
        Directory.CreateDirectory(populated);
        File.WriteAllText(Path.Combine(populated, "keep.txt"), "keep unmarked data intact");
        RequireThrows<InvalidDataException>(() => Demo(populated).PrepareDataRoot(), "demo opened unmarked existing data");
        Require(File.ReadAllText(Path.Combine(populated, "keep.txt")) == "keep unmarked data intact" &&
            Directory.EnumerateFileSystemEntries(populated).Count() == 1, "demo changed rejected existing data");

        var demoRoot = Path.Combine(local, "fresh-demo");
        var demo = Demo(demoRoot);
        demo.PrepareDataRoot();
        demo.PrepareDataRoot();
        using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(demoRoot, ".togetherserver-instance.json"))))
            Require(marker.RootElement.GetProperty("kind").GetString() == "Demo", "demo reused the ordinary development marker");
        Require(Directory.EnumerateFileSystemEntries(demoRoot).Count() == 1 &&
            File.ReadAllText(productionMarker) == "synthetic production world remains unchanged" &&
            File.ReadAllText(developmentMarker) == "synthetic existing development settings remain unchanged",
            "demo copied or modified fake production/development files");

        var productionOnDemo = AppInstance.Resolve([], name => name == "TOGETHERSERVER_DATA_DIR" ? demoRoot : null,
            local, Path.Combine(local, "TogetherServer.exe"));
        RequireThrows<InvalidDataException>(productionOnDemo.PrepareDataRoot, "production opened a demo-marked root");
        var stagingOnDemo = AppInstance.Resolve(["--staging"], name => name switch
        {
            "TOGETHERSERVER_DATA_DIR" => production,
            "TOGETHERSERVER_STAGING_DATA_DIR" => demoRoot,
            _ => null
        }, local, Path.Combine(local, "TogetherServer.exe"));
        RequireThrows<InvalidDataException>(stagingOnDemo.PrepareDataRoot, "ordinary development opened a demo-marked root");

        var stagingMarked = Path.Combine(local, "staging-marked-for-refusal");
        var regular = AppInstance.Resolve(["--staging"], name => name switch
        {
            "TOGETHERSERVER_DATA_DIR" => production,
            "TOGETHERSERVER_STAGING_DATA_DIR" => stagingMarked,
            _ => null
        }, local, Path.Combine(local, "TogetherServer.exe"));
        regular.PrepareDataRoot();
        RequireThrows<InvalidDataException>(() => Demo(stagingMarked).PrepareDataRoot(),
            "demo reused a marked existing development root");
    }

    private static void CheckNativeRequests()
    {
        var address = new Uri("http://127.0.0.1:5129/?demo=sharing");
        foreach (var path in new[] { "/?demo=sharing", "/index.html?demo=sharing", "/assets/app.js", "/assets/app.css", "/favicon.svg", "/api/local/instance", "/api/local/window" })
            Require(DesktopWindow.IsDemoResourceAllowed(address, new Uri(address, path).AbsoluteUri, "GET"),
                "demo blocked a required native-shell asset or metadata read");
        foreach (var target in new[] { "http://127.0.0.1:5127/", "http://localhost:5129/", "https://127.0.0.1:5129/", "http://user@127.0.0.1:5129/", "file:///C:/private/world.db", "steam://install/892970", "https://example.invalid/", "http://127.0.0.1:5129/api/local/snapshot", "http://127.0.0.1:5129/api/local/quit" })
            Require(!DesktopWindow.IsDemoResourceAllowed(address, target, "GET"), "demo allowed a real native/network escape");
        Require(!DesktopWindow.IsDemoResourceAllowed(address, "http://127.0.0.1:5129/api/local/quit", "POST") &&
            DesktopWindow.IsDemoDocumentAllowed(address, address.AbsoluteUri) &&
            !DesktopWindow.IsDemoDocumentAllowed(address, "http://127.0.0.1:5129/api/local/instance") &&
            !DesktopWindow.IsDemoDocumentAllowed(address, "http://127.0.0.1:5129/assets/app.js"),
            "demo navigation or browser writes escaped the native-shell policy");
    }

    private static async Task CheckApiBoundaryAsync(string local)
    {
        var demo = AppInstance.Resolve(["--demo"], _ => null, local, Path.Combine(local, "TogetherServer.exe"));
        foreach (var request in new[] { ("GET", "/api/local/instance"), ("GET", "/api/local/window"), ("POST", "/api/local/show"), ("POST", "/api/local/quit") })
        {
            var context = Context(request.Item1, request.Item2);
            var reached = false;
            await DemoApplication.HandleRequestAsync(context, demo, 5129, () => { reached = true; return Task.CompletedTask; });
            Require(reached && context.Response.StatusCode == 200, "demo prevented safe native show/quit/metadata plumbing");
        }
        foreach (var request in new[]
        {
            ("GET", "/api/local/snapshot"), ("GET", "/api/local/public-ip"), ("GET", "/api/local/minecraft/discover"),
            ("POST", "/api/local/profiles/00000000-0000-0000-0000-000000000001/start"),
            ("POST", "/api/local/friend/pair"), ("POST", "/api/local/update/install"),
            ("PUT", "/api/local/settings"), ("POST", "/api/companion/heartbeat"),
            ("GET", "/api/local/instance/extra"), ("DELETE", "/api/local/quit")
        })
            await RequireDeniedAsync(Context(request.Item1, request.Item2));
        foreach (Action<DefaultHttpContext> change in new Action<DefaultHttpContext>[]
        {
            context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.123"),
            context => context.Request.Host = new HostString("127.0.0.1", 5127),
            context => context.Request.Host = new HostString("localhost", 5129),
            context => context.Request.Headers.Remove("Origin"),
            context => context.Request.Headers.Origin = "http://127.0.0.1:5127",
            context => context.Request.Headers.Remove("X-TogetherServer-Local"),
            context => context.Request.QueryString = new QueryString("?path=C%3A%5Cprivate"),
            context => context.Request.ContentLength = 1,
            context => context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature())
        })
        {
            var context = Context("POST", "/api/local/quit");
            change(context);
            await RequireDeniedAsync(context);
        }

        async Task RequireDeniedAsync(DefaultHttpContext context)
        {
            var reached = false;
            await DemoApplication.HandleRequestAsync(context, demo, 5129, () => { reached = true; return Task.CompletedTask; });
            Require(!reached && context.Response.StatusCode == 403 && context.Request.Body.Position == 0,
                "a real API or forged native request reached a handler or consumed its body in demo");
        }
    }

    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5129;
        context.Request.Host = new HostString("127.0.0.1", 5129);
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Origin = "http://127.0.0.1:5129";
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("untrusted request body remains unread"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
