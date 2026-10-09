using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure local input/metadata checks only. The paths below are never created,
// inspected or opened; no app, process, picker or listener is invoked.
internal static class QolLocalEndpointInputChecks
{
    internal static void Run()
    {
        CheckSharedServerSelection();
        CheckSelectedGameProbeScope();
        CheckDraftResourceScope();
        CheckRunIdentityMetadata();
    }

    private static void CheckSelectedGameProbeScope()
    {
        var profileId = Guid.NewGuid();
        var hostA = Guid.NewGuid();
        var hostB = Guid.NewGuid();
        var firstRun = Guid.NewGuid();
        var nextRun = Guid.NewGuid();
        Require(QolLocalEndpointInputs.TryGameProbe(
                Bytes($"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":\"{firstRun:D}\"}}"), out var currentRequest) &&
                currentRequest == new ScopedGameProbeRequest(hostA, firstRun),
            "fixed game probe input lost its selected Host or canonical run scope");
        Require(QolLocalEndpointInputs.TryGameProbe(
                Bytes($"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":null}}"), out var legacyRequest) &&
                legacyRequest == new ScopedGameProbeRequest(hostA, null),
            "older peer's unavailable run metadata was invented or rejected");
        Require(QolLocalEndpointInputs.TryGameProbe(
                Bytes("{\"connectionId\":\"00000000-0000-0000-0000-000000000000\",\"runOperationId\":null}"), out var migratedRequest) &&
                migratedRequest == new ScopedGameProbeRequest(Guid.Empty, null),
            "the saved pre-index legacy link lost its actual indexed connection identity");
        foreach (var payload in new[]
        {
            "{}", "null", "[]", "{",
            $"{{\"connectionId\":\"{hostA:D}\"}}",
            $"{{\"runOperationId\":\"{firstRun:D}\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"connectionId\":\"{hostB:D}\",\"runOperationId\":null}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":null,\"runOperationId\":\"{firstRun:D}\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":null,\"profileId\":\"{profileId:D}\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":null,\"address\":\"192.0.2.1:2456\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":null,\"path\":\"private.exe\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":null,\"command\":\"start\"}}",
            $"{{\"ConnectionId\":\"{hostA:D}\",\"runOperationId\":null}}",
            $"{{\"connectionId\":\"{hostA:N}\",\"runOperationId\":null}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":\"{firstRun:N}\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":\"00000000-0000-0000-0000-000000000000\"}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":0}}",
            $"{{\"connectionId\":\"{hostA:D}\",\"runOperationId\":{{}}}}",
            "{\"connectionId\":null,\"runOperationId\":null}",
            "{\"connectionId\":5,\"runOperationId\":null}"
        }) Require(!QolLocalEndpointInputs.TryGameProbe(Bytes(payload), out _),
            "game probe accepted an ambiguous identity or caller-controlled resource");
        Require(!QolLocalEndpointInputs.TryGameProbe(
                Bytes(new string(' ', QolLocalEndpointInputs.MaximumGameProbeBytes + 1)), out _),
            "game probe request body exceeded its bounded input cap");

        var measured = new PublicProfile(profileId, "Synthetic shared profile", "Ready", "192.0.2.1:2456",
            Kind: GameKinds.Valheim, GameKind: GameKinds.Valheim, RunOperationId: firstRun);
        var viewA = new FriendView("Friend", "Connected", "Synthetic", "https://192.0.2.1:5131", null,
            true, false, false, [measured], [], ConnectionId: hostA);
        var viewB = viewA with { ConnectionId = hostB, Endpoint = "https://192.0.2.2:5131",
            Profiles = [measured with { JoinAddress = "192.0.2.2:2456" }] };
        Require(QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA, profileId, firstRun) is null &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA, profileId, firstRun, measured) is null,
            "an unchanged selected Host/run was denied before or after its fixed probe");
        Require(QolLocalEndpointInputs.GameProbeScopeFailure(hostB, hostA, viewB, profileId, firstRun, measured) == "GameProbeScopeChanged" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewB, profileId, firstRun) == "GameProbeScopeChanged",
            "Hosts sharing a profile UUID could label one another's probe reply");
        Require(QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA with { Profiles = [] }, profileId, firstRun) == "UnknownProfile" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA with { Profiles = [] }, profileId, firstRun, measured) == "UnknownProfile" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA with { Profiles = [measured, measured] }, profileId, firstRun) == "UnknownProfile" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA, Guid.NewGuid(), firstRun) == "UnknownProfile",
            "a changed or ambiguous assignment retained a measured game reply");
        var restarted = viewA with { Profiles = [measured with { RunOperationId = nextRun }] };
        Require(QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, restarted, profileId, firstRun) == "GameProbeScopeChanged" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, restarted, profileId, firstRun, measured) == "GameProbeScopeChanged" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, restarted, profileId, nextRun, measured) == "GameProbeScopeChanged",
            "a changed canonical run reused or relabeled another run's reply");
        var legacy = viewA with { Profiles = [measured with { RunOperationId = null }] };
        Require(QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, legacy, profileId, null) is null &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, legacy, profileId, firstRun) == "GameProbeScopeChanged" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA, profileId, null) == "GameProbeScopeChanged" &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, legacy, profileId, null, measured) == "GameProbeScopeChanged",
            "unavailable legacy run metadata matched or overwrote a known canonical run");
        var legacyMeasured = measured with { RunOperationId = null };
        var migrated = legacy with { ConnectionId = Guid.Empty };
        Require(QolLocalEndpointInputs.GameProbeScopeFailure(Guid.Empty, Guid.Empty, migrated, profileId, null, legacyMeasured) is null &&
                QolLocalEndpointInputs.GameProbeScopeFailure(hostA, Guid.Empty, migrated, profileId, null) == "GameProbeScopeChanged",
            "the known selected migrated link became an unscoped connection default");
        foreach (var changed in new[]
        {
            measured with { JoinAddress = "192.0.2.2:2456" },
            measured with { Kind = GameKinds.Terraria },
            measured with { GameKind = GameKinds.Terraria }
        }) Require(QolLocalEndpointInputs.GameProbeScopeFailure(hostA, hostA, viewA with { Profiles = [changed] },
                profileId, firstRun, measured) == "GameProbeScopeChanged",
            "a changed game/address retained a reply measured from the prior route");
    }

    private static void CheckSharedServerSelection()
    {
        var profileId = Guid.NewGuid();
        var games = new (string Kind, string FileName)[]
        {
            (GameKinds.Valheim, "valheim_server.exe"),
            (GameKinds.MinecraftJava, "server.jar"),
            (GameKinds.MinecraftBedrock, "bedrock_server.exe"),
            (GameKinds.Factorio, "factorio.exe"),
            (GameKinds.Terraria, "TerrariaServer.exe")
        };
        foreach (var game in games)
        {
            var body = Bytes($"{{\"profileId\":\"{profileId:D}\",\"kind\":\"{game.Kind}\"}}");
            Require(QolLocalEndpointInputs.TrySharedServerBrowse(body, out var request) &&
                    request == new SharedServerBrowseRequest(profileId, game.Kind),
                "supported game selection lost its exact profile scope");
            var picker = QolLocalEndpointInputs.Picker(game.Kind);
            Require(picker is not null && picker.FileName == game.FileName &&
                    picker.Filter.Split('|') is [_, var pattern] && pattern == game.FileName,
                "game picker widened its reviewed file filter");
        }
        var id = profileId.ToString("D");
        foreach (var payload in new[]
        {
            "{}", "null", "[]", "{",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Valheim\",\"path\":\"private.exe\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Valheim\",\"command\":\"start\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Valheim\",\"args\":[]}}",
            $"{{\"profileId\":\"{id}\",\"profileId\":\"{id}\",\"kind\":\"Valheim\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Valheim\",\"kind\":\"Terraria\"}}",
            $"{{\"profileId\":\"{id}\",\"game\":\"Valheim\"}}",
            $"{{\"profileId\":\"{id}\",\"Kind\":\"Valheim\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":null}}",
            $"{{\"profileId\":\"{id}\",\"kind\":5}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"valheim\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Custom\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Fixture\"}}",
            $"{{\"profileId\":\"{id}\",\"kind\":\"Unknown\"}}",
            "{\"profileId\":\"00000000-0000-0000-0000-000000000000\",\"kind\":\"Valheim\"}",
            $"{{\"profileId\":\"{profileId:N}\",\"kind\":\"Valheim\"}}",
            "{\"profileId\":5,\"kind\":\"Valheim\"}",
            "{\"kind\":\"Valheim\"}"
        }) Require(!QolLocalEndpointInputs.TrySharedServerBrowse(Bytes(payload), out _),
            "ambiguous or caller-controlled server selection was accepted");
        Require(!QolLocalEndpointInputs.TrySharedServerBrowse(
            Bytes(new string(' ', QolLocalEndpointInputs.MaximumSharedBrowseBytes + 1)), out _),
            "server selection body cap was not enforced");

        var selected = new SharedServerBrowseRequest(profileId, GameKinds.Valheim);
        var canonical = new PublicProfile(profileId, "Synthetic server", "Offline", null,
            Kind: "Display label", GameKind: GameKinds.Valheim);
        Require(QolLocalEndpointInputs.ProfileGameMatches(canonical, selected) &&
                QolLocalEndpointInputs.ProfileGameMatches(canonical with { GameKind = null, Kind = GameKinds.Valheim }, selected) &&
                !QolLocalEndpointInputs.ProfileGameMatches(canonical with { Id = Guid.NewGuid() }, selected) &&
                !QolLocalEndpointInputs.ProfileGameMatches(canonical with { GameKind = GameKinds.Terraria }, selected) &&
                !QolLocalEndpointInputs.ProfileGameMatches(canonical with { GameKind = null }, selected),
            "assigned server game scope followed a display label or another profile");
    }

    private static void CheckDraftResourceScope()
    {
        var supported = new (string Kind, string[] Files, string[] Lists)[]
        {
            (GameKinds.MinecraftJava, ["server-properties", "operators", "allow-list", "player-bans", "ip-bans"], ["allow-list"]),
            (GameKinds.MinecraftBedrock, ["server-properties", "allow-list", "permissions"], ["allow-list"]),
            (GameKinds.Valheim, ["admin-list", "ban-list", "permit-list"], ["admin-list", "ban-list", "permit-list"]),
            (GameKinds.Factorio, ["factorio-settings"], []),
            (GameKinds.Terraria, ["terraria-config"], [])
        };
        foreach (var game in supported)
        {
            var profile = new ServerProfile
            {
                Id = Guid.NewGuid(), Kind = game.Kind, WorldId = "synthetic",
                WorldDirectory = Path.GetFullPath(Path.Combine("local-data", "metadata-only-never-created", game.Kind))
            };
            foreach (var key in game.Files)
            {
                var identity = new ProtectedUiDraftIdentity("file", profile.Id, null, "file:" + key);
                Require(QolLocalEndpointInputs.DraftKeyMatches(identity, profile),
                    "a reviewed file draft required file contents or was denied");
                Require(!QolLocalEndpointInputs.DraftKeyMatches(identity with { ProfileId = Guid.NewGuid() }, profile) &&
                        !QolLocalEndpointInputs.DraftKeyMatches(identity with { ConnectionId = Guid.NewGuid() }, profile) &&
                        !QolLocalEndpointInputs.DraftKeyMatches(identity with { Key = key }, profile),
                    "file draft crossed server/connection/editor scope");
            }
            foreach (var key in new[] { "allow-list", "admin-list", "ban-list", "permit-list", "operators", "permissions" })
            {
                var identity = new ProtectedUiDraftIdentity("list", profile.Id, null, "list:" + key);
                Require(QolLocalEndpointInputs.DraftKeyMatches(identity, profile) == game.Lists.Contains(key),
                    "guided player draft accepted a list from another game or editor");
            }
            var properties = new ProtectedUiDraftIdentity("settings", profile.Id, null, "settings:properties");
            Require(QolLocalEndpointInputs.DraftKeyMatches(properties, profile) ==
                    (game.Kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock),
                "guided settings draft was not bound to reviewed Minecraft properties");
            foreach (var identity in new[]
            {
                properties with { Key = "server-properties" },
                properties with { Key = "host-setup" },
                properties with { Purpose = "file", Key = "file:../private" },
                properties with { Purpose = "file", Key = "file:" + profile.WorldDirectory },
                properties with { Purpose = "file", Key = "file:unreviewed" },
                properties with { Purpose = "file", Key = "File:server-properties" },
                properties with { Purpose = "list", Key = "list:../private" },
                properties with { Purpose = "chat", Key = "message-history" },
                properties with { Purpose = "other", Key = "compose" }
            }) Require(!QolLocalEndpointInputs.DraftKeyMatches(identity, profile),
                "draft eligibility accepted a path, reserved identity or unreviewed resource");
            var compose = new ProtectedUiDraftIdentity("chat", profile.Id, null, "compose");
            Require(QolLocalEndpointInputs.DraftKeyMatches(compose, profile) &&
                    !QolLocalEndpointInputs.DraftKeyMatches(compose with { ProfileId = Guid.NewGuid() }, profile) &&
                    !QolLocalEndpointInputs.DraftKeyMatches(compose with { ConnectionId = Guid.NewGuid() }, profile),
                "Host chat draft lost its exact compose/server scope");
            profile.WorldDirectory = "relative-unreviewed";
            Require(!QolLocalEndpointInputs.DraftKeyMatches(properties, profile) &&
                    !QolLocalEndpointInputs.DraftKeyMatches(new("file", profile.Id, null, "file:" + game.Files[0]), profile),
                "relative profile paths created reviewed draft resource keys");
        }
    }

    private static void CheckRunIdentityMetadata()
    {
        var profileId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var run = new ManagedRun { ProfileId = profileId, OperationId = first, ProcessId = 123 };
        Require(CompanionProtocol.ProjectRunOperationId(run, "Matched") == first &&
                CompanionProtocol.ProjectRunOperationId(run, "Matched") == first,
            "a stable measured run did not keep its canonical operation identity");
        run.OperationId = second;
        Require(CompanionProtocol.ProjectRunOperationId(run, "Matched") == second,
            "a changed managed run did not invalidate its prior operation identity");
        foreach (var state in new[] { "Missing", "Unknown", "Offline", "matched", "" })
            Require(CompanionProtocol.ProjectRunOperationId(run, state) is null,
                "uncertain or absent run identity exposed an operation ID");
        run.OperationId = Guid.Empty;
        Require(CompanionProtocol.ProjectRunOperationId(run, "Matched") is null &&
                CompanionProtocol.ProjectRunOperationId(null, "Matched") is null,
            "an unavailable canonical operation ID was invented");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyPublic = JsonSerializer.Deserialize<PublicProfile>(
            $"{{\"id\":\"{profileId:D}\",\"name\":\"Synthetic\",\"state\":\"Offline\",\"joinAddress\":null}}", json);
        var legacyRun = JsonSerializer.Deserialize<RunView>(
            $"{{\"profileId\":\"{profileId:D}\",\"state\":\"Offline\",\"detail\":\"No managed process\",\"processId\":null}}", json);
        Require(legacyPublic is { RunOperationId: null } && legacyRun is { RunOperationId: null },
            "older status documents did not degrade to an unavailable run identity");
        var currentPublic = legacyPublic! with { RunOperationId = first };
        var currentRun = legacyRun! with { RunOperationId = first };
        using var publicDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(currentPublic, json));
        using var runDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(currentRun, json));
        Require(publicDocument.RootElement.GetProperty("runOperationId").GetGuid() == first &&
                runDocument.RootElement.GetProperty("runOperationId").GetGuid() == first &&
                JsonSerializer.Deserialize<PublicProfile>(publicDocument.RootElement, json)?.RunOperationId == first &&
                JsonSerializer.Deserialize<RunView>(runDocument.RootElement, json)?.RunOperationId == first,
            "additive Host/Friend status metadata lost the canonical run ID");
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
