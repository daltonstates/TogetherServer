using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Protected disposable settings and fake port tables only. No process, app,
// listener, game, console attachment, browser or network policy is invoked.
internal static class ApprovedGamePortsChecks
{
    internal static async Task RunAsync(string root)
    {
        Store(root);
        await RoutesAsync(root);
        await SelectionAsync(root);
        await OwnerSetupAsync(root);
    }

    private static void Store(string root)
    {
        using var data = Data(root, "approved-store");
        var store = data.ApprovedGamePorts;
        Require(store.View().ApprovedPorts.Count == 0, "old settings invented a game-port approval");
        Require(store.Save(new(GameKinds.Valheim, 2456, null)).Ok, "valid Valheim ports failed");
        var first = store.Find(GameKinds.Valheim)!;
        Require(store.Save(new(GameKinds.Valheim, 2456, null)).Ok && store.View().ApprovedPorts.Count == 1 &&
            store.Find(GameKinds.Valheim)!.Id == first.Id, "duplicate approval created another identity");
        Require(store.Save(new(GameKinds.Valheim, 2460, null)).Ok && store.Find(GameKinds.Valheim)!.GamePort == 2460,
            "latest local choice was not preferred");
        Require(store.Save(new(GameKinds.Valheim, 2464, null), first.Id).Ok &&
            store.Find(GameKinds.Valheim)!.Id == first.Id && store.Find(GameKinds.Valheim)!.GamePort == 2464,
            "editing an approval lost its identity or preference");
        Require(!store.Save(new(GameKinds.Valheim, 65535, null)).Ok && !store.Save(new(GameKinds.Custom, 2456, null)).Ok &&
            !store.Save(new(GameKinds.Factorio, 34197, 34197)).Ok && !store.Save(new(GameKinds.Factorio, 34197, null)).Ok &&
            !store.Save(new(GameKinds.MinecraftJava, 25565, 27015)).Ok && !store.Save(new(GameKinds.Terraria, 80, null)).Ok,
            "invalid, unsupported or ambiguous ports were approved");
        Require(store.Save(new(GameKinds.Factorio, 34197, 27015)).Ok &&
            new ApprovedGamePortsStore(data).Find(GameKinds.Factorio)?.FactorioRconPort == 27015,
            "restart lost the private Factorio RCON choice");
        Require(store.Delete(first.Id).Ok && store.Find(GameKinds.Valheim, first.Id) is null &&
            !store.Delete(first.Id).Ok && new ApprovedGamePortsStore(data).Find(GameKinds.Valheim, first.Id) is null,
            "deleting an approval failed across restart");
        using var corrupt = Data(root, "approved-corrupt");
        corrupt.SaveProtected(ApprovedGamePortsStore.FileName, JsonSerializer.SerializeToUtf8Bytes(
            new ApprovedGamePortsState(1, [new(Guid.NewGuid(), GameKinds.Valheim, 80)])));
        Require(new ApprovedGamePortsStore(corrupt).View().ApprovedPorts.Count == 0 &&
            !corrupt.Recovery.LifecycleBlocked && corrupt.Recovery.Notices.Any(notice => notice.StateFile == ApprovedGamePortsStore.FileName),
            "damaged approval silently survived or blocked unrelated lifecycle");
        using var failed = Data(root, "approved-write-failure");
        var unchanged = failed.ApprovedGamePorts;
        File.Delete(Path.Combine(failed.RootPath, ApprovedGamePortsStore.FileName));
        Directory.CreateDirectory(Path.Combine(failed.RootPath, ApprovedGamePortsStore.FileName));
        try { unchanged.Save(new(GameKinds.Terraria, 7777, null)); throw new InvalidOperationException("write failure was accepted"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Require(unchanged.View().ApprovedPorts.Count == 0, "failed persistence published a reusable approval");
        using var legacy = Data(root, "approved-legacy");
        var owner = new ServerProfile { Kind = GameKinds.Valheim, GamePort = 2458 };
        var copied = new ServerProfile { Kind = GameKinds.Valheim, GamePort = 2468, SeparateCopySourceProfileId = Guid.NewGuid() };
        var successor = new ServerProfile { Kind = GameKinds.MinecraftJava, GamePort = 25565 };
        legacy.SaveSettings(new HostSettings { Profiles = [owner, copied, successor, new() { Kind = GameKinds.Fixture, GamePort = 2500 }] });
        legacy.SaveProtected($"successor-restore-{successor.Id:N}.protected", Encoding.UTF8.GetBytes("synthetic marker"));
        var migrated = legacy.ApprovedGamePorts;
        Require(migrated.View().ApprovedPorts.Count == 1 && migrated.Find(GameKinds.Valheim)?.GamePort == 2458,
            "migration missed an existing owner port choice or silently approved a received/fixture profile");
        migrated.Delete(migrated.Find(GameKinds.Valheim)!.Id);
        Require(new ApprovedGamePortsStore(legacy).View().ApprovedPorts.Count == 0,
            "restart migration restored a deliberately deleted approval");
    }

    private static async Task RoutesAsync(string root)
    {
        using var data = Data(root, "approved-routes");
        using var gate = new SemaphoreSlim(1);
        const string valid = "{\"game\":\"Valheim\",\"gamePort\":2456}";
        foreach (var input in new[] { "{}", "null", "[]", "{", "{\"game\":\"Valheim\",\"gamePort\":\"2456\"}",
            "{\"game\":\"Valheim\",\"gamePort\":2456,\"gamePort\":2458}",
            "{\"game\":\"Valheim\",\"gamePort\":2456,\"path\":\"anything\"}",
            "{\"game\":\"Factorio\",\"gamePort\":34197,\"factorioRconPort\":34197}", new string(' ', 513) })
            Reject(() => ApprovedGamePortsLocalRoutes.Decode(Encoding.UTF8.GetBytes(input)), "strict port input was bypassed");
        Require(ApprovedGamePortsLocalRoutes.Read(Context("GET"), data.ApprovedGamePorts) is JsonHttpResult<ApprovedGamePortsView>,
            "fixed local approved-port read failed");
        foreach (Action<DefaultHttpContext> mutate in new Action<DefaultHttpContext>[]
        {
            context => context.Request.Headers.Remove("X-TogetherServer-Local"),
            context => context.Request.Headers["X-TogetherServer-Local"] = new Microsoft.Extensions.Primitives.StringValues(["1", "1"]),
            context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.8"),
            context => context.Request.Headers.Origin = "https://foreign.example",
            context => context.Request.QueryString = new("?command=anything"),
            context => context.Request.Host = new("127.0.0.1", 5131)
        })
        {
            var request = Context("POST", valid); mutate(request);
            Require(Status(await ApprovedGamePortsLocalRoutes.MutateAsync(request, data.ApprovedGamePorts, null, gate, () => true)) == 403,
                "foreign, ambiguous or query-bearing mutation was accepted");
        }
        Require(Status(await ApprovedGamePortsLocalRoutes.MutateAsync(Context("POST", valid), data.ApprovedGamePorts, null, gate, () => false)) == 409 &&
            data.ApprovedGamePorts.View().ApprovedPorts.Count == 0, "operation gate did not block port changes");
        await ApprovedGamePortsLocalRoutes.MutateAsync(Context("POST", valid), data.ApprovedGamePorts, null, gate, () => true);
        var id = data.ApprovedGamePorts.Find(GameKinds.Valheim)!.Id;
        Require(Status(await ApprovedGamePortsLocalRoutes.MutateAsync(Context("DELETE", "{}"), data.ApprovedGamePorts, id, gate, () => true)) == 400,
            "Delete accepted a body");
        await ApprovedGamePortsLocalRoutes.MutateAsync(Context("DELETE"), data.ApprovedGamePorts, id, gate, () => true);
        Require(data.ApprovedGamePorts.View().ApprovedPorts.Count == 0, "fixed local Delete did not remove approval");
        foreach (var input in new[] { "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"gamePort\":2456}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"approvePorts\":true,\"gamePort\":80}",
            $"{{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"approvedPortId\":\"{Guid.NewGuid()}\",\"approvePorts\":true}}" })
            Reject(() => FriendLocalHostingService.ParseRequest(Encoding.UTF8.GetBytes(input)), "preparation accepted ambiguous local port consent");
    }

    private static async Task SelectionAsync(string root)
    {
        using var data = Data(root, "approved-selection");
        var available = true;
        var registry = new GameServerRegistry(data, false, PortProbeMode.ObserveOnly, _ => available);
        var manager = new HostManager(data, registry);
        var version = new SharedWorldVersion(4, Guid.NewGuid(), 1, null, Guid.NewGuid(), GameKinds.Valheim,
            "Synthetic", DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
            new(2456, false, "Unknown", [], []), [], "", "", "");
        var initial = await manager.LocalHostingPortsAsync(version, null, CancellationToken.None);
        Require(initial.GamePort == 2456 && initial.ApprovedPortId is null && !data.LoadSettings().CompanionListeningEnabled,
            "source metadata silently approved local ports or enabled a listener");
        data.ApprovedGamePorts.Save(new(GameKinds.Valheim, 2460, null));
        var remembered = await manager.LocalHostingPortsAsync(version, null, CancellationToken.None);
        Require(remembered.GamePort == 2460 && remembered.ControlPort == 5131 && remembered.ApprovedPortId is not null,
            "source port replaced this PC's approved preference or changed the companion port");
        available = false;
        var busy = await manager.LocalHostingPortsAsync(version, null, CancellationToken.None);
        Require(!busy.Available && busy.GamePort == 2460 && busy.ApprovedPortId == remembered.ApprovedPortId,
            "a busy approved port silently searched for an unapproved port");
        available = true;
        var existing = new ServerProfile { Kind = GameKinds.Valheim, GamePort = 2470 };
        var legacy = await manager.LocalHostingPortsAsync(version, existing, CancellationToken.None);
        Require(legacy.GamePort == 2470 && legacy.ApprovedPortId is null, "legacy prepared setup acquired approval from another port plan");
        Require(data.LoadRuns().Count == 0 && !data.LoadSettings().CompanionListeningEnabled, "port checks changed runtime state");
    }

    private static async Task OwnerSetupAsync(string root)
    {
        using var data = Data(root, "approved-owner");
        var driver = new LocalHostingChecks.FilesOnlyDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var profile = new ServerProfile { Kind = GameKinds.Fixture, Name = "Synthetic", WorldId = "Synthetic",
            WorldDirectory = Path.Combine(data.RootPath, "world"), ExecutablePath = Path.Combine(data.RootPath, "never-run.synthetic"), GamePort = 2500 };
        var saved = await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] });
        Require(saved.Ok && data.ApprovedGamePorts.Find(GameKinds.Fixture)?.GamePort == 2500 && driver.ProcessAttempts == 0,
            "explicit own-server setup did not record the selected port or invoked a process");
        var id = data.ApprovedGamePorts.Find(GameKinds.Fixture)!.Id;
        data.ApprovedGamePorts.Delete(id);
        Require((await manager.UpdateSettingsAsync(data.LoadSettings())).Ok && data.ApprovedGamePorts.Find(GameKinds.Fixture) is null,
            "an unrelated settings save silently restored deliberately removed port approval");
    }

    private static DefaultHttpContext Context(string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5130;
        context.Request.Host = new("127.0.0.1", 5130);
        context.Request.Method = method;
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.Headers.Origin = "http://127.0.0.1:5130";
        if (body is not null) { var bytes = Encoding.UTF8.GetBytes(body); context.Request.ContentType = "application/json"; context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length; }
        return context;
    }
    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private static LocalData Data(string root, string name) => new(Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")));
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { return; } throw new InvalidOperationException(message); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
