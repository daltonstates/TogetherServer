using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic local files and in-memory requests only. Never opens an app,
// listener, process, game, browser, console fixture or native dialog.
internal static class RequiredFeatureSetupChecks
{
    internal static async Task RunAsync(string root)
    {
        CheckProfileRequirement(root);
        CheckVersionedRequirements(root);
        CheckCorruptionAndWriteFailure(root);
        await CheckRoutesAsync(root);
    }

    private static void CheckProfileRequirement(string root)
    {
        using var data = Data(root, "setup-profile");
        var setup = new RequiredFeatureSetupService(data);
        var first = setup.View().Features.Single(item => item.Id == "profile-name");
        Require(first.Revision == 1 && !first.Completed, "a first install skipped required profile setup");
        Require(!setup.Complete(first.Id, first.Revision).Ok && !setup.View().Features.Single().Completed,
            "an acknowledgement bypassed missing profile data");
        Require(!data.UserProfile.Save("").Ok && !data.UserProfile.Save("   ").Ok,
            "the required profile accepted a blank local name");
        Require(data.UserProfile.Save("Legacy person").Ok && new RequiredFeatureSetupService(data).View().Features.Single().Completed,
            "a valid existing profile unnecessarily reopened setup after upgrade");
        Require(setup.Complete("profile-name", 1).Ok, "configured profile could not complete setup");
        Require(!setup.Complete("unknown", 1).Ok && !setup.Complete("profile-name", 0).Ok && !setup.Complete("profile-name", -1).Ok && !setup.Complete("profile-name", 2).Ok,
            "unknown or mismatched setup acknowledgement was accepted");
        using var oldData = Data(root, "setup-old-version");
        oldData.SaveSettings(new HostSettings());
        Require(!new RequiredFeatureSetupService(oldData).View().Features.Single().Completed,
            "an old-version data directory without a name skipped setup");
    }

    private static void CheckVersionedRequirements(string root)
    {
        using var data = Data(root, "setup-versions");
        var configured = false;
        RequiredFeatureDefinition Feature(string id, int revision, bool ack = true) =>
            new(id, revision, "Synthetic " + id, "Required synthetic configuration", () => configured, ack);
        var setup = new RequiredFeatureSetupService(data, [Feature("first", 1)]);
        Require(!setup.View().Features.Single().Completed && !setup.Complete("first", 1).Ok,
            "an acknowledgement completed unconfigured data");
        configured = true;
        Require(!setup.View().Features.Single().Completed, "configuration bypassed explicit acknowledgement");
        Require(setup.Complete("first", 1).Ok && setup.View().Features.Single().Completed,
            "configured acknowledgement failed");
        Require(File.Exists(Path.Combine(data.RootPath, "required-setup.json")), "setup ledger was not saved");
        Require(new RequiredFeatureSetupService(data, [Feature("first", 1)]).View().Features.Single().Completed,
            "restart lost a completed revision");
        var expanded = new RequiredFeatureSetupService(data, [Feature("first", 1), Feature("second", 1)]);
        Require(expanded.View().Features.Single(item => item.Id == "first").Completed &&
            !expanded.View().Features.Single(item => item.Id == "second").Completed,
            "a new requirement reset completed work or silently skipped itself");
        Require(expanded.Complete("second", 1).Ok, "new configured requirement could not complete");
        var upgraded = new RequiredFeatureSetupService(data, [Feature("first", 2), Feature("second", 1)]);
        Require(!upgraded.View().Features.Single(item => item.Id == "first").Completed &&
            upgraded.View().Features.Single(item => item.Id == "second").Completed,
            "revision upgrade reopened the wrong requirement");
        Require(!upgraded.Complete("first", 1).Ok && !upgraded.View().Features.Single(item => item.Id == "first").Completed,
            "a stale client acknowledged a later requirement revision");
        Require(upgraded.Complete("first", 2).Ok, "later requirement could not complete");
        configured = false;
        Require(upgraded.View().Features.All(item => !item.Completed) &&
            new RequiredFeatureSetupService(data, [Feature("first", 2), Feature("second", 1)]).View().Features.All(item => !item.Completed),
            "saved ledger bypassed now-invalid configuration");
        configured = true;
        var inferred = new RequiredFeatureSetupService(data, [Feature("inferred", 9, false)]);
        Require(inferred.View().Features.Single().Completed, "configured data-only requirement needed an acknowledgement");
    }

    private static void CheckCorruptionAndWriteFailure(string root)
    {
        using var corrupt = Data(root, "setup-corrupt");
        File.WriteAllText(Path.Combine(corrupt.RootPath, "required-setup.json"), "{ broken ledger");
        var feature = new RequiredFeatureDefinition("ack", 1, "Acknowledge", "Synthetic", () => true, true);
        var recovered = new RequiredFeatureSetupService(corrupt, [feature]);
        Require(!recovered.View().Features.Single().Completed, "corrupt ledger silently acknowledged setup");
        Require(!corrupt.Recovery.LifecycleBlocked && corrupt.Recovery.Notices.Any(item => item.StateFile == "required-setup.json" && !item.BlocksLifecycle),
            "setup corruption blocked unrelated game lifecycle or lacked a recovery notice");
        Require(!File.Exists(Path.Combine(corrupt.RootPath, "required-setup.json")) &&
            Directory.EnumerateFiles(corrupt.RootPath, "*", SearchOption.AllDirectories).Any(path => Path.GetFileName(path).Contains("required-setup", StringComparison.Ordinal)),
            "corrupt setup state was not quarantined");
        using var forged = Data(root, "setup-forged-ledger");
        File.WriteAllText(Path.Combine(forged.RootPath, "required-setup.json"),
            "{\"schemaVersion\":1,\"completed\":{\"ack\":9999}}");
        var unconfigured = new RequiredFeatureDefinition("ack", 1, "Acknowledge", "Synthetic", () => false, true);
        var forgedSetup = new RequiredFeatureSetupService(forged, [unconfigured]);
        Require(!forgedSetup.View().Features.Single().Completed && !forgedSetup.Complete("ack", 1).Ok,
            "an invented ledger revision bypassed missing configuration");
        using var blocked = Data(root, "setup-write-failure");
        var setup = new RequiredFeatureSetupService(blocked, [feature]);
        Directory.CreateDirectory(Path.Combine(blocked.RootPath, "required-setup.json"));
        try { Require(!setup.Complete("ack", 1).Ok, "failed ledger write reported success"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Require(!setup.View().Features.Single().Completed, "failed ledger write published completion in memory");
    }

    private static async Task CheckRoutesAsync(string root)
    {
        using var data = Data(root, "setup-routes");
        var setup = new RequiredFeatureSetupService(data,
            [new RequiredFeatureDefinition("ack", 1, "Acknowledge", "Synthetic", () => true, true)]);
        using var gate = new SemaphoreSlim(1);
        var read = RequiredFeatureSetupLocalRoutes.Read(Context("GET"), setup);
        Require(Status(read) == 200 && read is Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<RequiredFeatureSetupView> json &&
            json.Value?.Features.Single().Id == "ack" && !json.Value.Features.Single().Completed,
            "exact local setup read failed");
        var bodyGet = Context("GET");
        bodyGet.Request.ContentLength = 1;
        Require(Status(RequiredFeatureSetupLocalRoutes.Read(bodyGet, setup)) == 400, "setup GET accepted a body");
        foreach (var payload in new[] { "", "{}", "null", "[]", "{", "{\"Revision\":1}", "{\"revision\":\"1\"}", "{\"revision\":null}", "{\"revision\":1.5}", "{\"revision\":1,\"revision\":2}", "{\"revision\":1,\"command\":\"start\"}", new string(' ', 257) })
        {
            var rejected = false;
            try { RequiredFeatureSetupLocalRoutes.Decode(Encoding.UTF8.GetBytes(payload)); }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or OverflowException) { rejected = true; }
            Require(rejected, "setup decoding accepted ambiguous or oversized input");
        }
        Require(RequiredFeatureSetupLocalRoutes.Decode(Encoding.UTF8.GetBytes("{\"revision\":1}")) == 1,
            "exact setup input failed decoding");
        foreach (Action<DefaultHttpContext> mutate in new Action<DefaultHttpContext>[]
        {
            c => c.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1"),
            c => c.Request.Host = new HostString("localhost", 5130),
            c => c.Request.Host = new HostString("127.0.0.1", 5131),
            c => c.Request.QueryString = new QueryString("?revision=1"),
            c => c.Request.Headers.Remove("X-TogetherServer-Local"),
            c => c.Request.Headers["X-TogetherServer-Local"] = new Microsoft.Extensions.Primitives.StringValues(["1", "1"]),
            c => c.Request.Headers.Origin = "https://evil.example",
            c => c.Request.Method = "PUT"
        })
        {
            var request = Context("POST", "{\"revision\":1}");
            mutate(request);
            Require(Status(await RequiredFeatureSetupLocalRoutes.CompleteAsync(request, "ack", setup, gate, () => true)) == 403 &&
                !setup.View().Features.Single().Completed, "foreign local setup request changed completion");
        }
        var wrongType = Context("POST", "{\"revision\":1}");
        wrongType.Request.ContentType = "text/plain";
        Require(Status(await RequiredFeatureSetupLocalRoutes.CompleteAsync(wrongType, "ack", setup, gate, () => true)) == 400,
            "setup completion accepted non-JSON input");
        Require(Status(await RequiredFeatureSetupLocalRoutes.CompleteAsync(Context("POST", "{\"revision\":1}"), "ack", setup, gate, () => false)) == 409 && !setup.View().Features.Single().Completed,
            "setup completion bypassed operation restrictions");
        var allowed = true;
        await gate.WaitAsync();
        var pending = RequiredFeatureSetupLocalRoutes.CompleteAsync(Context("POST", "{\"revision\":1}"), "ack", setup, gate, () => allowed);
        Require(!pending.IsCompleted && !setup.View().Features.Single().Completed, "setup completion bypassed the mode gate");
        allowed = false;
        gate.Release();
        Require(Status(await pending) == 409 && !setup.View().Features.Single().Completed,
            "setup operation restrictions were not rechecked inside the gate");
        Require(Status(await RequiredFeatureSetupLocalRoutes.CompleteAsync(Context("POST", "{\"revision\":1}"), "ack", setup, gate, () => true)) == 200 && setup.View().Features.Single().Completed,
            "valid setup completion failed");
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
    private static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
    private static LocalData Data(string root, string prefix) => new(Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N")));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
