using System.Text.Json;

namespace TogetherServer;

internal sealed record SharedServerBrowseRequest(Guid ProfileId, string Kind);
internal sealed record SharedServerPicker(string Title, string FileName, string Filter);
internal sealed record SharedServerBrowseResult(bool Ok, string Code, string Message, string? Path = null);
internal sealed record ScopedGameProbeRequest(Guid ConnectionId, Guid? RunOperationId);

// Fixed local GUI inputs and metadata checks only. No path, script, argument,
// network target or lifecycle authority can be supplied by these requests.
internal static class QolLocalEndpointInputs
{
    internal const int MaximumSharedBrowseBytes = 256;
    internal const int MaximumGameProbeBytes = 256;

    internal static bool TryGameProbe(ReadOnlyMemory<byte> bytes, out ScopedGameProbeRequest? request)
    {
        request = null;
        if (bytes.Length is < 2 or > MaximumGameProbeBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!GameSettingsRequestParser.ExactObject(root, "connectionId", "runOperationId") ||
                root.GetProperty("connectionId").ValueKind != JsonValueKind.String ||
                root.GetProperty("connectionId").GetString() is not { Length: 36 } connectionText ||
                !Guid.TryParseExact(connectionText, "D", out var connectionId)) return false;
            // Guid.Empty is the pre-index saved legacy connection's actual ID.
            // Scope validation still requires that exact selected saved link.
            Guid? runOperationId = null;
            var operation = root.GetProperty("runOperationId");
            if (operation.ValueKind != JsonValueKind.Null)
            {
                if (operation.ValueKind != JsonValueKind.String || operation.GetString() is not { Length: 36 } operationText ||
                    !Guid.TryParseExact(operationText, "D", out var parsed) || parsed == Guid.Empty) return false;
                runOperationId = parsed;
            }
            request = new(connectionId, runOperationId);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    // These are observations, not remote control authority. Missing legacy run
    // metadata matches only null; a known run must match its exact canonical ID.
    internal static string? GameProbeScopeFailure(Guid selectedConnectionId, Guid connectionId,
        FriendView view, Guid profileId, Guid? expectedRunOperationId, PublicProfile? measuredProfile = null)
    {
        if (selectedConnectionId != connectionId || view.ConnectionId != connectionId ||
            expectedRunOperationId == Guid.Empty)
            return "GameProbeScopeChanged";
        var assigned = view.Profiles.Where(profile => profile.Id == profileId).Take(2).ToArray();
        if (profileId == Guid.Empty || assigned.Length != 1) return "UnknownProfile";
        var current = assigned[0];
        if (current.RunOperationId != expectedRunOperationId) return "GameProbeScopeChanged";
        if (measuredProfile is not null && (measuredProfile.Id != profileId ||
            measuredProfile.RunOperationId != expectedRunOperationId || current.Kind != measuredProfile.Kind ||
            current.GameKind != measuredProfile.GameKind || current.JoinAddress != measuredProfile.JoinAddress))
            return "GameProbeScopeChanged";
        return null;
    }

    internal static bool TrySharedServerBrowse(ReadOnlyMemory<byte> bytes, out SharedServerBrowseRequest? request)
    {
        request = null;
        if (bytes.Length is < 2 or > MaximumSharedBrowseBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!GameSettingsRequestParser.ExactObject(root, "profileId", "kind") ||
                root.GetProperty("profileId").ValueKind != JsonValueKind.String ||
                root.GetProperty("profileId").GetString() is not { Length: 36 } id ||
                !Guid.TryParseExact(id, "D", out var profileId) || profileId == Guid.Empty ||
                root.GetProperty("kind").ValueKind != JsonValueKind.String ||
                root.GetProperty("kind").GetString() is not { } kind || Picker(kind) is null) return false;
            request = new(profileId, kind);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    internal static SharedServerPicker? Picker(string kind) => kind switch
    {
        GameKinds.Valheim => new("Choose installed Valheim Dedicated Server", "valheim_server.exe",
            "Valheim Dedicated Server (valheim_server.exe)|valheim_server.exe"),
        GameKinds.MinecraftJava => new("Choose a TogetherServer-installed Minecraft Java server", "server.jar",
            "Official Minecraft Java server (server.jar)|server.jar"),
        GameKinds.MinecraftBedrock => new("Choose installed Minecraft Bedrock Dedicated Server", "bedrock_server.exe",
            "Minecraft Bedrock Dedicated Server (bedrock_server.exe)|bedrock_server.exe"),
        GameKinds.Factorio => new("Choose owner-installed Factorio server", "factorio.exe",
            "Factorio server (factorio.exe)|factorio.exe"),
        GameKinds.Terraria => new("Choose owner-installed Terraria server", "TerrariaServer.exe",
            "Terraria server (TerrariaServer.exe)|TerrariaServer.exe"),
        _ => null
    };

    internal static bool DraftKeyMatches(ProtectedUiDraftIdentity identity, ServerProfile profile)
    {
        if (identity.ConnectionId is not null || identity.ProfileId != profile.Id) return false;
        if (identity.Purpose == "chat") return identity.Key == "compose";
        // Definitions derive fixed reviewed keys from saved profile metadata;
        // SnapshotPaths does not open or read any configuration file.
        var keys = ServerFiles.SnapshotPaths(profile);
        return identity.Purpose switch
        {
            "file" => identity.Key.StartsWith("file:", StringComparison.Ordinal) && keys.ContainsKey(identity.Key[5..]),
            "settings" => identity.Key == "settings:properties" && GameSettings.SupportsProperties(profile.Kind) &&
                keys.ContainsKey("server-properties"),
            "list" => identity.Key.StartsWith("list:", StringComparison.Ordinal) &&
                GameSettings.SupportsList(profile.Kind, identity.Key[5..]) && keys.ContainsKey(identity.Key[5..]),
            _ => false
        };
    }

    internal static bool ProfileGameMatches(PublicProfile profile, SharedServerBrowseRequest request) =>
        profile.Id == request.ProfileId && (profile.GameKind ?? profile.Kind) == request.Kind;
}
