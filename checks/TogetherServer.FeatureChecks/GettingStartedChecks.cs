using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic local files and in-memory requests only. InstallerIntegration.Run
// is called without startup/tray flags, so no registry, process, app, listener,
// browser, console fixture, game, installer executable or native dialog runs.
internal static class GettingStartedChecks
{
    internal static async Task RunAsync(string root)
    {
        CheckFirstUseAndUpgrade(root);
        CheckInstallerHandoff(root);
        CheckDamagedStateAndWriteFailure(root);
        await CheckRoutesAsync(root);
    }

    private static void CheckFirstUseAndUpgrade(string root)
    {
        var path = CasePath(root, "guide-first-use");
        using (var data = new LocalData(path))
        {
            var guide = new GettingStartedService(data);
            Require(guide.View() == new GettingStartedView(true, "Choose", false), "fresh portable data did not offer the guide");
            Require(new RequiredFeatureSetupService(data).View().Features.Single().Completed == false,
                "the optional guide bypassed required profile setup");
            guide.Save("Host", true);
            Require(data.LoadSettings().Profiles.Count == 0 && !data.LoadSettings().CompanionListeningEnabled &&
                data.LoadRuns().Count == 0, "guide progress configured hosting or started a server");
        }
        using (var data = new LocalData(path))
        {
            var guide = new GettingStartedService(data);
            Require(guide.View() == new GettingStartedView(true, "Host", true), "guide deferral did not survive restart");
            Require(guide.Save("Join", false) == new GettingStartedView(true, "Join", false), "guide replay could not select another route");
            Require(guide.Save("Choose", true) == new GettingStartedView(true, "Choose", true), "the choose-later route could not be dismissed");
        }

        var legacyPath = CasePath(root, "guide-upgrade");
        using (var legacy = new LocalData(legacyPath)) legacy.SavePreferredMode("Friend");
        using (var legacy = new LocalData(legacyPath))
        {
            var guide = new GettingStartedService(legacy);
            Require(guide.View() == new GettingStartedView(false, "Choose", false),
                "an existing empty installation was treated as first use on upgrade");
            Require(legacy.LoadPreferredMode() == "Friend", "guide inspection changed the existing workspace");
            guide.Save("Join", true);
        }
        using (var legacy = new LocalData(legacyPath))
            Require(new GettingStartedService(legacy).View() == new GettingStartedView(true, "Join", true),
                "manual guide progress on an existing installation did not persist");
    }

    private static void CheckInstallerHandoff(string root)
    {
        foreach (var (mode, intent) in new[] { ("host", "Host"), ("friend", "Join"), ("", "Choose") })
        {
            var path = CasePath(root, "guide-installer-" + intent);
            var executable = Path.Combine(path, "installed", "TogetherServer.exe");
            string? EnvironmentForCase(string name) => name == "TOGETHERSERVER_DATA_DIR" ? path : null;
            var arguments = mode.Length == 0
                ? new[] { InstallerIntegration.ConfigureCommand, "--guided-setup" }
                : new[] { InstallerIntegration.ConfigureCommand, "--guided-setup", "--mode", mode };
            Require(InstallerIntegration.Run(arguments, executable, EnvironmentForCase, path) == 0,
                "installer guide handoff rejected a supported route");
            using (var data = new LocalData(path))
            {
                var guide = new GettingStartedService(data);
                Require(guide.View() == new GettingStartedView(true, intent, false), "installer guide route was not retained for a deferred app opening");
                Require(data.LoadPreferredMode() == (mode == "friend" ? "Friend" : "Host"), "installer lost the selected page preference");
                Require(!data.LoadDesktopPreferences().CloseToTray && data.LoadSettings().Profiles.Count == 0 &&
                    !data.LoadSettings().CompanionListeningEnabled && data.LoadRuns().Count == 0,
                    "installer guide implicitly configured another preference, listener or game");
                guide.Save(intent, true);
            }
            Require(InstallerIntegration.Run([InstallerIntegration.ConfigureCommand, "--guided-setup"], executable,
                EnvironmentForCase, path) == 0, "explicit installer request could not reopen a guide");
            using var reopened = new LocalData(path);
            Require(new GettingStartedService(reopened).View() == new GettingStartedView(true, "Choose", false),
                "explicit installer choose-later did not reset optional guide dismissal");
            Require(reopened.LoadPreferredMode() == (mode == "friend" ? "Friend" : "Host"),
                "choose-later replaced an existing preferred workspace");
        }

        var invalidPath = CasePath(root, "guide-installer-invalid");
        string? InvalidEnvironment(string name) => name == "TOGETHERSERVER_DATA_DIR" ? invalidPath : null;
        foreach (var arguments in new[]
        {
            new[] { InstallerIntegration.ConfigureCommand },
            new[] { InstallerIntegration.ConfigureCommand, "--guided-setup", "--guided-setup" },
            new[] { InstallerIntegration.ConfigureCommand, "--guided-setup", "--mode", "arbitrary" },
            new[] { InstallerIntegration.ConfigureCommand, "--guided-setup", "--command", "start" }
        })
            Require(InstallerIntegration.Run(arguments, Path.Combine(invalidPath, "TogetherServer.exe"), InvalidEnvironment, invalidPath) == 2 &&
                !Directory.Exists(invalidPath), "invalid installer arguments wrote guide or app state");
    }

    private static void CheckDamagedStateAndWriteFailure(string root)
    {
        using var corrupt = new LocalData(CasePath(root, "guide-corrupt"));
        File.WriteAllText(Path.Combine(corrupt.RootPath, "getting-started.json"),
            "{\"schemaVersion\":1,\"offered\":true,\"intent\":\"arbitrary\",\"dismissed\":false}");
        var recovered = new GettingStartedService(corrupt);
        Require(!recovered.View().Offered && !corrupt.Recovery.LifecycleBlocked &&
            corrupt.Recovery.Notices.Any(notice => notice.StateFile == "getting-started.json" && !notice.BlocksLifecycle),
            "damaged optional guide state reopened setup or blocked game lifecycle");

        using var blocked = new LocalData(CasePath(root, "guide-write-failure"));
        blocked.SaveSettings(new HostSettings { MaxConcurrentServers = 3 });
        blocked.SaveRuns([new ManagedRun { ProfileId = Guid.NewGuid(), OperationId = Guid.NewGuid() }]);
        var settingsBefore = File.ReadAllBytes(Path.Combine(blocked.RootPath, "host.json"));
        var runsBefore = File.ReadAllBytes(Path.Combine(blocked.RootPath, "runs.json"));
        var guide = new GettingStartedService(blocked);
        var previous = guide.View();
        File.Delete(Path.Combine(blocked.RootPath, "getting-started.json"));
        Directory.CreateDirectory(Path.Combine(blocked.RootPath, "getting-started.json"));
        var failed = false;
        try { guide.Save("Host", true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        Require(failed && guide.View() == previous, "failed guide save published progress in memory");
        Require(!blocked.Recovery.LifecycleBlocked &&
            File.ReadAllBytes(Path.Combine(blocked.RootPath, "host.json")).SequenceEqual(settingsBefore) &&
            File.ReadAllBytes(Path.Combine(blocked.RootPath, "runs.json")).SequenceEqual(runsBefore),
            "optional guide write failure changed lifecycle state or blocked its authority");

        using var initialWrite = new LocalData(CasePath(root, "guide-initial-write-failure"));
        Directory.CreateDirectory(Path.Combine(initialWrite.RootPath, "getting-started.json"));
        Require(new GettingStartedService(initialWrite).View().Offered && !initialWrite.Recovery.LifecycleBlocked,
            "failed initial optional guide persistence blocked startup or lifecycle");

        using var recoveryWrite = new LocalData(CasePath(root, "guide-recovery-write-failure"));
        File.WriteAllText(Path.Combine(recoveryWrite.RootPath, "getting-started.json"), "{ damaged guide");
        Directory.CreateDirectory(Path.Combine(recoveryWrite.RootPath, "data-recovery.json"));
        Require(!new GettingStartedService(recoveryWrite).View().Offered && !recoveryWrite.Recovery.LifecycleBlocked,
            "optional guide recovery-marker failure escaped into startup or lifecycle blocking");
    }

    private static async Task CheckRoutesAsync(string root)
    {
        using var data = new LocalData(CasePath(root, "guide-routes"));
        var guide = new GettingStartedService(data);
        using var gate = new SemaphoreSlim(1);
        var first = guide.View();
        var read = GettingStartedLocalRoutes.Read(Context("GET"), guide);
        Require(Status(read) == 200 && read is Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<GettingStartedView> json &&
            json.Value == first, "exact local guide read failed");
        var bodyGet = Context("GET");
        bodyGet.Request.ContentLength = 1;
        Require(Status(GettingStartedLocalRoutes.Read(bodyGet, guide)) == 400, "guide read accepted a body");
        foreach (var payload in new[]
        {
            "", "{}", "null", "[]", "{", "{\"Intent\":\"Host\",\"dismissed\":true}",
            "{\"intent\":\"Friend\",\"dismissed\":true}", "{\"intent\":\"Host\",\"dismissed\":\"true\"}",
            "{\"intent\":null,\"dismissed\":true}", "{\"intent\":\"Host\",\"intent\":\"Join\"}",
            "{\"intent\":\"Host\",\"dismissed\":true,\"command\":\"start\"}", new string(' ', 257)
        })
        {
            var rejected = false;
            try { GettingStartedLocalRoutes.Decode(Encoding.UTF8.GetBytes(payload)); }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { rejected = true; }
            Require(rejected, "guide decoding accepted ambiguous, unknown or oversized input");
        }
        Require(GettingStartedLocalRoutes.Decode(Encoding.UTF8.GetBytes("{\"dismissed\":false,\"intent\":\"Choose\"}")) ==
            new GettingStartedRequest("Choose", false), "exact guide replay input did not decode");
        foreach (Action<DefaultHttpContext> mutate in new Action<DefaultHttpContext>[]
        {
            context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1"),
            context => context.Request.Host = new HostString("localhost", 5130),
            context => context.Request.Host = new HostString("127.0.0.1", 5131),
            context => context.Request.QueryString = new QueryString("?intent=Host"),
            context => context.Request.Headers.Remove("X-TogetherServer-Local"),
            context => context.Request.Headers["X-TogetherServer-Local"] = new Microsoft.Extensions.Primitives.StringValues(["1", "1"]),
            context => context.Request.Headers.Origin = "https://example.invalid",
            context => context.Request.Method = "POST"
        })
        {
            var request = Context("PUT", "{\"intent\":\"Host\",\"dismissed\":true}");
            mutate(request);
            Require(Status(await GettingStartedLocalRoutes.SaveAsync(request, guide, gate, () => true)) == 403 && guide.View() == first,
                "foreign or ambiguous local guide request changed progress");
        }
        var wrongType = Context("PUT", "{\"intent\":\"Host\",\"dismissed\":true}");
        wrongType.Request.ContentType = "text/plain";
        Require(Status(await GettingStartedLocalRoutes.SaveAsync(wrongType, guide, gate, () => true)) == 400,
            "guide update accepted non-JSON content");
        var allowed = true;
        await gate.WaitAsync();
        var pending = GettingStartedLocalRoutes.SaveAsync(Context("PUT", "{\"intent\":\"Join\",\"dismissed\":true}"), guide, gate, () => allowed);
        Require(!pending.IsCompleted && guide.View() == first, "guide update bypassed the mode gate");
        allowed = false;
        gate.Release();
        Require(Status(await pending) == 409 && guide.View() == first, "guide operation restrictions were not rechecked under the gate");
        Require(Status(await GettingStartedLocalRoutes.SaveAsync(Context("PUT", "{\"intent\":\"Join\",\"dismissed\":true}"), guide, gate, () => true)) == 200 &&
            guide.View() == new GettingStartedView(true, "Join", true), "valid guide deferral failed");
    }

    private static DefaultHttpContext Context(string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5130;
        context.Request.Method = method;
        context.Request.Host = new HostString("127.0.0.1", 5130);
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.Headers.Origin = "http://127.0.0.1:5130";
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        return context;
    }
    private static string CasePath(string root, string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
    private static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
