using System.Net;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class LocalHostingRouteChecks
{
    internal static async Task RunAsync()
    {
        var clock = new RouteClock();
        var route = PublicRoute();
        var current = route;
        var calls = 0;
        var probeState = "Reachable";
        var probeEndpoint = route.Endpoint;
        var probePort = route.ControlPort;
        var probeCheckedUtc = clock.GetUtcNow();
        var checker = new LocalHostingRouteCheck(_ => Task.FromResult(current), (endpoint, port, _) =>
        {
            Require(endpoint == route.Endpoint && port == route.ControlPort,
                "the outside check accepted a target other than the saved literal route");
            calls++;
            return Task.FromResult(new ExternalPortProbeResult(probeState, "untrusted checker detail",
                probePort, probeCheckedUtc, probeEndpoint));
        }, clock);

        Require((await checker.CurrentAsync()).Code == "OutsideCheckNotRun" && calls == 0,
            "passive route status performed an outside check");
        var reachable = await checker.CheckAsync();
        Require(reachable.Code == "OutsideTcpReachable" && reachable.Step.State == "Ready" && calls == 1 &&
            reachable.Message.Contains("TCP reachability only", StringComparison.Ordinal) &&
            reachable.Message.Contains("game route", StringComparison.Ordinal) &&
            !reachable.Message.Contains("untrusted checker detail", StringComparison.Ordinal),
            "outside TCP evidence was overstated or reflected arbitrary checker text");
        Require((await checker.CurrentAsync()) == reachable && calls == 1,
            "a passive read repeated the outside probe");

        foreach (var changed in new[]
        {
            route with { Endpoint = "https://8.8.4.4:5131" }, route with { ControlPort = 5132 },
            route with { BindAddress = "192.168.1.3" }, route with { Enabled = false },
            route with { Active = false }, route with { ListenerState = CompanionListenerStates.Idle },
            route with { CertificateFingerprint = new string('B', 64) }, route with { ListenerOwned = false }
        })
        {
            current = changed;
            Require((await checker.CurrentAsync()).Step.State != "Ready" && calls == 1,
                "a route or listener identity change retained old outside TCP credit");
        }
        current = route;
        clock.Now += TimeSpan.FromMinutes(6);
        Require((await checker.CurrentAsync()).Code == "OutsideCheckNotRun" && calls == 1,
            "expired route evidence remained current or reran the probe");
        clock.Now -= TimeSpan.FromMinutes(6);

        foreach (var (state, code, expectedState) in new[]
        {
            ("Not reachable", "OutsideTcpNotReachable", "ActionRequired"),
            ("Unavailable", "OutsideAddressUnverified", "ActionRequired"),
            ("Inconclusive", "OutsideCheckInconclusive", "Unknown"),
            ("invented result", "OutsideCheckInconclusive", "Unknown")
        })
        {
            probeState = state;
            var result = await checker.CheckAsync();
            Require(result.Code == code && result.Step.State == expectedState,
                "a closed, mismatched-address or inconclusive route result was misclassified");
        }
        probeState = "Reachable";
        foreach (var wrong in new[] { "endpoint", "port", "old", "future" })
        {
            probeEndpoint = wrong == "endpoint" ? "https://8.8.4.4:5131" : route.Endpoint;
            probePort = wrong == "port" ? route.ControlPort + 1 : route.ControlPort;
            probeCheckedUtc = clock.GetUtcNow() + (wrong == "old" ? TimeSpan.FromMinutes(-2) :
                wrong == "future" ? TimeSpan.FromMinutes(2) : TimeSpan.Zero);
            Require((await checker.CheckAsync()).Code == "OutsideCheckInconclusive",
                "foreign or stale probe evidence became a route pass");
        }

        await CheckLocalOnlyRoutesAsync(clock);
        await CheckChangedRouteAsync(clock);
        await CheckFixedProbeTransportAsync();
    }

    private static async Task CheckLocalOnlyRoutesAsync(RouteClock clock)
    {
        var calls = 0;
        var route = PublicRoute();
        var checker = new LocalHostingRouteCheck(_ => Task.FromResult(route), (_, _, _) =>
        {
            calls++;
            throw new Exception("a local-only route must not call an outside service");
        }, clock);
        foreach (var (changed, code) in new[]
        {
            (PublicRoute() with { Endpoint = "" }, "LocalAddressRequired"),
            (PublicRoute() with { Endpoint = "https://unapproved.example:5131" }, "LocalAddressInvalid"),
            (PublicRoute() with { Endpoint = "http://8.8.8.8:5131" }, "LocalAddressInvalid"),
            (PublicRoute() with { Endpoint = "https://8.8.8.8:5132" }, "LocalAddressInvalid"),
            (PublicRoute() with { Enabled = false }, "LocalListenerOff"),
            (PublicRoute() with { BindAddress = "invalid" }, "LocalBindInvalid"),
            (PublicRoute() with { BindAddress = "127.0.0.1" }, "LocalConnectionOnly"),
            (PublicRoute() with { Endpoint = "https://127.0.0.1:5131" }, "LocalConnectionOnly"),
            (PublicRoute() with { Active = false }, "LocalListenerNotRunning"),
            (PublicRoute() with { ListenerOwned = false }, "LocalListenerUnverified"),
            (PublicRoute() with { CertificateFingerprint = null }, "LocalListenerUnverified"),
            (PublicRoute() with { Endpoint = "https://10.1.2.3:5131" }, "PrivateRouteOnly")
        })
        {
            route = changed;
            Require((await checker.CheckAsync()).Code == code && calls == 0,
                "local route status invented outside evidence or contacted the checker");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await checker.CheckAsync(cancelled.Token); throw new Exception("cancelled preparation ran a route check"); }
        catch (OperationCanceledException) { }
        Require(calls == 0, "cancellation dispatched an outside request");
    }

    private static async Task CheckChangedRouteAsync(RouteClock clock)
    {
        var route = PublicRoute();
        var current = route;
        var checker = new LocalHostingRouteCheck(_ => Task.FromResult(current), (_, _, _) =>
        {
            current = route with { CertificateFingerprint = new string('B', 64) };
            return Task.FromResult(new ExternalPortProbeResult("Reachable", "TCP only", route.ControlPort,
                clock.GetUtcNow(), route.Endpoint));
        }, clock);
        Require((await checker.CheckAsync()).Code == "LocalRouteChanged" &&
            (await checker.CurrentAsync()).Step.State != "Ready",
            "a listener changed during the check retained outside TCP credit");
        checker = new(_ => Task.FromResult(route), (_, _, _) => throw new HttpRequestException("private network detail"), clock);
        var failed = await checker.CheckAsync();
        Require(failed.Code == "OutsideCheckInconclusive" && !failed.Message.Contains("private network detail", StringComparison.Ordinal),
            "a failed checker became a closed-port claim or reflected a private exception");
    }

    private static async Task CheckFixedProbeTransportAsync()
    {
        var route = PublicRoute();
        var paths = new List<string>();
        var seenAddress = "8.8.8.8";
        using var client = new HttpClient(new FakeChecker(request =>
        {
            Require(request.RequestUri?.Host == "checker.invalid" && request.Method == HttpMethod.Get,
                "the fake outside transport used an unapproved service or method");
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path == "/api/me" ? seenAddress : path == "/api/me/5131" ? "True" :
                throw new Exception("the checker received an arbitrary route");
        }));
        var external = new ExternalPortProbe(client, new Uri("https://checker.invalid/"));
        var checker = new LocalHostingRouteCheck(_ => Task.FromResult(route),
            (endpoint, port, cancellationToken) => external.CheckAsync(endpoint, port).WaitAsync(cancellationToken));
        Require((await checker.CheckAsync()).Code == "OutsideTcpReachable" && paths.SequenceEqual(new[] { "/api/me", "/api/me/5131" }),
            "the existing probe's fixed public-address and TCP checks were not reused");
        paths.Clear();
        seenAddress = "8.8.4.4";
        Require((await checker.CheckAsync()).Code == "OutsideAddressUnverified" && paths.SequenceEqual(new[] { "/api/me" }),
            "a different caller public address proceeded to a misleading port check");
    }

    private static LocalHostingRouteSnapshot PublicRoute() => new("https://8.8.8.8:5131", 5131,
        "0.0.0.0", true, true, CompanionListenerStates.Listening, new string('A', 64), true);

    private sealed class FakeChecker(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.ASCII) });
    }

    private sealed class RouteClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
