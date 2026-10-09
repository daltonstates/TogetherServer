using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

// Callable from a source-only runner. No process, listener, console control,
// desktop automation, native dialog, network request, or real game is used.
internal static class GameSettingsChecks
{
    internal static async Task RunAsync(string root)
    {
        var directory = Path.Combine(Path.GetFullPath(root), "game-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RequestBounds();
        PropertyEdits(directory);
        AccessListEdits(directory);
        await GuardedSaveAndUndoAsync(directory);
    }

    private static void RequestBounds()
    {
        var hash = new string('A', 64);
        var values = new GameSettingsValues("normal", 20, "survival", false, false);
        var valid = Serialize(new GameSettingsChangeRequest(hash, values));
        Require(GameSettingsRequestParser.TryParseSettings(valid, out var parsed) && parsed?.Settings == values,
            "valid typed settings request was rejected");
        foreach (var malformed in new[]
        {
            Encoding.UTF8.GetString(valid).Replace("\"settings\":", "\"worldPath\":\"arbitrary\",\"settings\":"),
            Encoding.UTF8.GetString(valid).Replace("\"difficulty\":\"normal\"", "\"difficulty\":\"normal\",\"difficulty\":\"hard\""),
            Encoding.UTF8.GetString(valid).Replace("\"difficulty\":\"normal\"", "\"difficulty\":\"normal\",\"onlineMode\":false"),
            Encoding.UTF8.GetString(valid).Replace("\"maximumPlayers\":20", "\"maximumPlayers\":201"),
            Encoding.UTF8.GetString(valid).Replace("\"maximumPlayers\":20", "\"maximumPlayers\":1.5"),
            Encoding.UTF8.GetString(valid).Replace("\"maximumPlayers\":20", "\"maximumPlayers\":\"20\""),
            Encoding.UTF8.GetString(valid).Replace("\"allowListEnabled\":false", "\"allowListEnabled\":null"),
            Encoding.UTF8.GetString(valid).Replace("\"difficulty\":\"normal\"", "\"difficulty\":\"nightmare\""),
            Encoding.UTF8.GetString(valid).Replace("\"forceGameMode\":false", "\"ForceGameMode\":false"),
            "{\"expectedSha256\":\"" + hash + "\",\"settings\":null}",
            "{\"expectedSha256\":\"" + hash + "\",\"expectedSha256\":\"" + hash + "\",\"settings\":{}}"
        }) Require(!GameSettingsRequestParser.TryParseSettings(Encoding.UTF8.GetBytes(malformed), out _),
            "ambiguous or unsupported settings request was accepted");
        Require(!GameSettingsRequestParser.TryParseSettings(new byte[GameSettingsRequestParser.MaximumSettingsRequestBytes + 1], out _),
            "oversized settings body was accepted");
        var entry = new GameAccessEntry("Steam_111", null, null);
        var list = Serialize(new GameAccessListChangeRequest(hash, [entry]));
        Require(GameSettingsRequestParser.TryParseList(list, out var parsedList) && parsedList!.Entries.Single() == entry,
            "valid typed list request was rejected");
        Require(!GameSettingsRequestParser.TryParseList(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(list)
                .Replace("\"name\":null", "\"name\":null,\"command\":\"ban\"")), out _) &&
            !GameSettingsRequestParser.TryParseList(Serialize(new GameAccessListChangeRequest(hash,
                Enumerable.Repeat(entry, 129).ToList())), out _) &&
            !GameSettingsRequestParser.TryParseList(new byte[GameSettingsRequestParser.MaximumListRequestBytes + 1], out _),
            "unsupported or oversized list request was accepted");
        Require(GameSettingsRequestParser.TryParseUndo(Serialize(new ServerFileUndoRequest(hash)), out _) &&
            !GameSettingsRequestParser.TryParseUndo(Encoding.UTF8.GetBytes("{\"expectedSha256\":\"" + hash + "\",\"path\":\"arbitrary\"}"), out _) &&
            !GameSettingsRequestParser.TryParseUndo(Serialize(new ServerFileUndoRequest("bad")), out _),
            "Undo request bounds changed");
    }

    private static void PropertyEdits(string root)
    {
        var java = Profile(root, GameKinds.MinecraftJava);
        var original = $"# Keep this comment\r\nlevel-name={java.WorldId}\r\nserver-port={java.GamePort}\r\n" +
            "  difficulty = normal\r\nmax-players=20\r\ngamemode=survival\r\nwhite-list=false\r\nforce-gamemode=false\r\n" +
            "motd=Keep \\u00a7a and spaces\r\nonline-mode=true\r\n";
        var proposed = GameSettings.ProposeSettings(java, Content("server-properties", original),
            new(Hash(original), new("hard", 12, "spectator", true, true)));
        Require(proposed.Preview.Ok && proposed.Preview.Changes.Count == 5 &&
            proposed.Content == original.Replace("  difficulty = normal", "  difficulty = hard")
                .Replace("max-players=20", "max-players=12").Replace("gamemode=survival", "gamemode=spectator")
                .Replace("white-list=false", "white-list=true").Replace("force-gamemode=false", "force-gamemode=true") &&
            proposed.Preview.Changes.Single(change => change.Key == "difficulty").Before == "  difficulty = normal",
            "property editing changed an unrelated line, comment, world/port or line ending");
        var request = new GameSettingsChangeRequest(Hash(original), new("hard", 20, "survival", false, false));
        Require(GameSettings.ProposeSettings(java, Content("server-properties", original),
            request with { ExpectedSha256 = new string('0', 64) }).Preview.Code == "FileChanged", "stale properties hash was accepted");
        foreach (var malformed in new[]
        {
            original + "difficulty=easy\r\n", original + "\\u0064ifficulty=easy\r\n",
            original + "difficulty:easy\r\n", original.Replace("difficulty = normal", "difficulty = nor\\\r\nmal"),
            original.Replace("difficulty = normal", "\\u0064ifficulty = normal"),
            original.Replace(java.WorldId, "wrong-world"), original.Replace($"server-port={java.GamePort}", "server-port=12345")
        }) Require(!GameSettings.ProposeSettings(java, Content("server-properties", malformed),
            request with { ExpectedSha256 = Hash(malformed) }).Preview.Ok,
            "duplicate, escaped, continued or mismatched authoritative property was accepted");
        var legacy = original.Replace("difficulty = normal", "difficulty = 2").Replace("gamemode=survival", "gamemode=0");
        var legacyEdit = GameSettings.ProposeSettings(java, Content("server-properties", legacy),
            new(Hash(legacy), new("normal", 21, "survival", false, false)));
        Require(legacyEdit.Preview.Ok && legacyEdit.Preview.Changes.Count == 1 && legacyEdit.Content!.Contains("difficulty = 2") &&
            legacyEdit.Content.Contains("gamemode=0"), "an unchanged Java numeric alias was rewritten");
        var canonical = $"level-name={java.WorldId}\nserver-port={java.GamePort}\ndifficulty=easy\n";
        foreach (var whitespace in new[] { '\u00A0', '\u2003', '\u202F' })
        {
            foreach (var ambiguous in new[]
            {
                canonical.Replace("difficulty=easy", whitespace + "difficulty=easy"),
                canonical.Replace("difficulty=easy", "difficulty" + whitespace + "=easy"),
                canonical.Replace("difficulty=easy", "difficulty" + whitespace + " easy"),
                canonical.Replace("difficulty=easy", "difficulty=" + whitespace + "easy"),
                canonical.Replace("difficulty=easy", "difficulty=easy" + whitespace),
                canonical + whitespace + "difficulty=normal\n",
                canonical + "difficulty" + whitespace + "=normal\n",
                canonical + "force-gamemode=true" + whitespace + "\n"
            })
            {
                var refused = GameSettings.ProposeSettings(java, Content("server-properties", ambiguous),
                    new(Hash(ambiguous), new("hard", 20, "survival", false, false)));
                Require(!refused.Preview.Ok && refused.Content is null && refused.Preview.Changes.Count == 0,
                    "Java Unicode whitespace was treated as a reviewed key/value or hidden duplicate");
            }
            // No ASCII delimiter means this is a different Java key. An added
            // canonical difficulty must be a genuine key, with the old line kept.
            var unrelated = canonical.Replace("difficulty=easy", "difficulty" + whitespace + "easy");
            var added = GameSettings.ProposeSettings(java, Content("server-properties", unrelated),
                new(Hash(unrelated), new("hard", 20, "survival", false, false)));
            Require(added.Preview.Ok && added.Content == unrelated + "difficulty=hard\n" &&
                added.Preview.Changes.Single().Before is null && added.Preview.Changes.Single().After == "difficulty=hard",
                "a Unicode separator edited an ignored key instead of adding the genuine Java setting");
        }
        foreach (var trailing in new[] { " ", "\t", "\f" })
        {
            var withSuffix = canonical.Replace("difficulty=easy", "difficulty=easy" + trailing);
            Require(!GameSettings.ProposeSettings(java, Content("server-properties", withSuffix),
                new(Hash(withSuffix), new("hard", 20, "survival", false, false))).Preview.Ok,
                "Java's significant trailing whitespace was normalized in the typed projection");
        }
        var asciiPrefix = canonical.Replace("difficulty=easy", " \tdifficulty\t= \teasy") + "motd=\u00A0Unchanged\u00A0\n";
        var asciiEdit = GameSettings.ProposeSettings(java, Content("server-properties", asciiPrefix),
            new(Hash(asciiPrefix), new("hard", 20, "survival", false, false)));
        Require(asciiEdit.Preview.Ok && asciiEdit.Content == asciiPrefix.Replace(" \tdifficulty\t= \teasy", " \tdifficulty\t= \thard") &&
            asciiEdit.Preview.Changes.Single().Before == " \tdifficulty\t= \teasy" &&
            asciiEdit.Preview.Changes.Single().After == " \tdifficulty\t= \thard",
            "valid Java ASCII whitespace or unrelated Unicode content was not preserved exactly");
        var asciiDuplicate = canonical + " \tdifficulty\t= \tnormal\n";
        Require(!GameSettings.ProposeSettings(java, Content("server-properties", asciiDuplicate),
            new(Hash(asciiDuplicate), new("hard", 20, "survival", false, false))).Preview.Ok,
            "Java ASCII-prefix duplicate setting escaped duplicate checks");
        var bedrock = Profile(root, GameKinds.MinecraftBedrock);
        var bedrockText = $"level-name={bedrock.WorldId}\nserver-port={bedrock.GamePort}\nserver-portv6=19133\n" +
            "enable-lan-visibility=false\ndifficulty=easy\nmax-players=10\ngamemode=survival\nallow-list=false\n";
        var bedrockEdit = GameSettings.ProposeSettings(bedrock, Content("server-properties", bedrockText),
            new(Hash(bedrockText), new("easy", 10, "creative", true, true)));
        Require(bedrockEdit.Preview.Ok && bedrockEdit.Content!.Contains("allow-list=true") &&
            !bedrockEdit.Content.Contains("white-list") && bedrockEdit.Content.Contains("server-portv6=19133") &&
            bedrockEdit.Content.Contains("force-gamemode=true") &&
            !GameSettings.ProposeSettings(bedrock, Content("server-properties", bedrockText),
                new(Hash(bedrockText), new("easy", 10, "spectator", false, false))).Preview.Ok,
            "Bedrock edition semantics or IPv6/LAN property preservation changed");
    }

    private static void AccessListEdits(string root)
    {
        var java = Profile(root, GameKinds.MinecraftJava);
        var first = new GameAccessEntry("11111111-1111-4111-8111-111111111111", "TestPlayer", null);
        var second = new GameAccessEntry("22222222-2222-4222-8222-222222222222", "NewPlayer", null);
        var content = "[{\"uuid\":\"" + first.Identity + "\",\"name\":\"TestPlayer\"}]";
        var proposal = GameSettings.ProposeList(java, "allow-list", Content("allow-list", content), new(Hash(content), [first, second]));
        Require(proposal.Preview.Ok && proposal.Preview.Changes.Count == 1 &&
            proposal.Content!.Contains("{\"uuid\":\"" + first.Identity + "\",\"name\":\"TestPlayer\"}"),
            "adding a Java allow-list entry rewrote an unchanged entry or failed");
        Require(!GameSettings.ProposeList(java, "allow-list", Content("allow-list", content),
                new(Hash(content), [first, second with { Identity = first.Identity }])).Preview.Ok &&
            !GameSettings.ValidEntries(GameKinds.MinecraftJava, [first with { Identity = Guid.Empty.ToString() }]) &&
            !GameSettings.ValidEntries(GameKinds.MinecraftJava, [first with { Name = "not a username" }]),
            "invalid or duplicate Java identity was accepted");
        var bedrock = Profile(root, GameKinds.MinecraftBedrock);
        var bedrockText = "[{\"name\":\"Test Gamer\",\"ignoresPlayerLimit\":false,\"xuid\":\"123456\"}]";
        var bedrockFirst = new GameAccessEntry("123456", "Test Gamer", false);
        var bedrockSecond = new GameAccessEntry(null, "New Gamer", false);
        Require(GameSettings.ProposeList(bedrock, "allow-list", Content("allow-list", bedrockText),
            new(Hash(bedrockText), [bedrockFirst, bedrockSecond])).Preview.Ok &&
            !GameSettings.ValidEntries(GameKinds.MinecraftBedrock, [bedrockFirst with { Identity = "abc" }]) &&
            !GameSettings.ValidEntries(GameKinds.MinecraftBedrock, [bedrockFirst, bedrockSecond with { Name = "test gamer" }]),
            "Bedrock optional XUID or name uniqueness rules changed");
        var valheim = Profile(root, GameKinds.Valheim);
        var valheimText = "// Game-generated header\r\n# Keep comment\r\nSteam_111\r\n\r\nXbox_222\r\n";
        var valheimEdit = GameSettings.ProposeList(valheim, "permit-list", Content("permit-list", valheimText),
            new(Hash(valheimText), [new("Xbox_222", null, null), new("Steam_333", null, null)]));
        Require(valheimEdit.Preview.Ok && valheimEdit.Content ==
            "// Game-generated header\r\n# Keep comment\r\n\r\nXbox_222\r\nSteam_333\r\n" &&
            !GameSettings.ValidEntries(GameKinds.Valheim, [new("Steam_111\nban", null, null)]) &&
            GameSettings.ValidEntries(GameKinds.Valheim, [new("Steam_111", null, null), new("steam_111", null, null)]),
            "Valheim comments, newline style or case-sensitive platform identities changed");
        Require(GameSettings.ProposeList(valheim, "permissions", Content("permissions", "[]"), new(Hash("[]"), [])).Preview.Code ==
            "AccessListUnsupported", "an arbitrary reviewed-file key entered the simple list mutation path");
    }

    private static async Task GuardedSaveAndUndoAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "guarded-data"));
        var profile = Profile(root, GameKinds.MinecraftJava);
        var world = Path.Combine(profile.WorldDirectory, profile.WorldId);
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), "synthetic world only");
        var file = Path.Combine(profile.WorldDirectory, "server.properties");
        var original = $"# Keep\r\nlevel-name={profile.WorldId}\r\nserver-port={profile.GamePort}\r\ndifficulty=normal\r\n";
        File.WriteAllText(file, original, new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "whitelist.json"), "[]");
        var registry = new GameServerRegistry([new SettingsOnlyDriver()], PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, registry);
        Require((await manager.UpdateSettingsAsync(new() { Profiles = [profile] })).Ok, "synthetic settings profile failed");
        var read = await manager.ReadGameSettingsAsync(profile.Id);
        Require(read.Ok && read.Settings is not null && read.Sha256 is not null, "typed properties read failed");
        var request = new GameSettingsChangeRequest(read.Sha256!, read.Settings! with { Difficulty = "hard" });
        var reviewed = await manager.PreviewGameSettingsAsync(profile.Id, request);
        Require(reviewed.Ok && data.LoadBackupCatalog().Records.Count == 0,
            "read-only preview made a checkpoint or required maintenance");
        Require((await manager.SaveGameSettingsAsync(profile.Id, request)).Code == "MaintenanceRequired" &&
            File.ReadAllText(file) == original, "settings mutated outside maintenance");
        profile.Maintenance.Enabled = true;
        Require((await manager.UpdateSettingsAsync(new() { Profiles = [profile] })).Ok, "maintenance profile failed");
        Require((await manager.SaveGameSettingsAsync(profile.Id, request with { ExpectedSha256 = new string('0', 64) })).Code ==
            "FileChanged" && data.LoadBackupCatalog().Records.Count == 0, "stale typed save made a checkpoint");
        var saved = await manager.SaveGameSettingsAsync(profile.Id, request);
        Require(saved.Ok && saved.CanUndo && saved.Sha256 == reviewed.ProposedSha256 &&
            File.ReadAllText(file) == original.Replace("difficulty=normal", "difficulty=hard") &&
            File.ReadAllBytes(file).AsSpan().StartsWith(Encoding.UTF8.Preamble) &&
            data.LoadBackupCatalog().Records.Count == 1 && data.LoadBackupCatalog().Records.Single().SetupIncluded,
            "typed save did not preserve BOM, checkpoint complete setup or retain Undo");
        var rawUndo = await manager.UndoServerFileAsync(profile.Id, "server-properties", new(saved.Sha256!));
        Require(rawUndo.Ok && File.ReadAllText(file) == original && data.LoadBackupCatalog().Records.Count == 2,
            "simple settings save could not be undone through the existing file editor");
        var current = await manager.ReadGameSettingsAsync(profile.Id);
        var rawSaved = await manager.SaveServerFileAsync(profile.Id, "server-properties",
            new(current.Sha256!, original.Replace("difficulty=normal", "difficulty=easy")));
        Require(rawSaved.Ok && (await manager.UndoGameSettingsAsync(profile.Id, new(rawSaved.Sha256!))).Ok &&
            File.ReadAllText(file) == original, "raw file save could not be undone through simple settings");
        var beforeList = await manager.ReadGameAccessListAsync(profile.Id, "allow-list");
        var listSaved = await manager.SaveGameAccessListAsync(profile.Id, "allow-list", new(beforeList.Sha256!,
            [new("11111111-1111-4111-8111-111111111111", "TestPlayer", null)]));
        Require(listSaved.Ok && listSaved.CanUndo &&
            (await manager.UndoGameAccessListAsync(profile.Id, "allow-list", new(listSaved.Sha256!))).Ok &&
            File.ReadAllText(Path.Combine(profile.WorldDirectory, "whitelist.json")) == "[]", "typed list save/Undo failed");
        var checkpointsBeforeFailure = data.LoadBackupCatalog().Records.Count;
        current = await manager.ReadGameSettingsAsync(profile.Id);
        using (var held = new FileStream(Path.Combine(world, "level.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await manager.SaveGameSettingsAsync(profile.Id,
                new(current.Sha256!, current.Settings! with { Difficulty = "hard" }));
            Require(failed.Code == "CheckpointFailed" && File.ReadAllText(file) == original &&
                data.LoadBackupCatalog().Records.Count == checkpointsBeforeFailure,
                "failed checkpoint changed settings or claimed a completed backup");
        }
        data.SaveProtected($"setup-restore-pending-{profile.Id:N}.protected", [1]);
        Require((await manager.SaveGameSettingsAsync(profile.Id,
            new(current.Sha256!, current.Settings! with { Difficulty = "hard" }))).Code == "SetupRestoreRecoveryRequired",
            "an interrupted complete-setup restore did not block simple edits");
        data.DeleteProtected($"setup-restore-pending-{profile.Id:N}.protected");
        data.SaveRuns([new() { ProfileId = profile.Id, OperationId = Guid.NewGuid(), Kind = profile.Kind,
            WorldId = profile.WorldId, WorldDirectory = profile.WorldDirectory, ExecutablePath = profile.ExecutablePath,
            GamePort = profile.GamePort, ProcessId = null, StartTimeUtcTicks = null }]);
        var unresolved = new HostManager(data, registry);
        Require(!(await unresolved.SaveGameSettingsAsync(profile.Id,
                new(current.Sha256!, current.Settings! with { Difficulty = "hard" }))).Ok &&
            File.ReadAllText(file) == original && data.LoadBackupCatalog().Records.Count == checkpointsBeforeFailure,
            "an unresolved saved run allowed a file write or checkpoint");
    }

    private static ServerProfile Profile(string root, string kind)
    {
        var directory = Path.Combine(root, "server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new() { Name = "Synthetic settings", ServerName = "Synthetic settings", Kind = kind,
            WorldId = "synthetic-world", WorldDirectory = directory, GamePort = 25565,
            ExecutablePath = Path.Combine(directory, "not-launched.exe"), Backups = new() { MinimumFreeSpaceMb = 0, RetentionCount = 20 } };
    }
    private static ServerFileContentResult Content(string key, string content) =>
        new(true, "ServerFileReady", "Synthetic source", key, content, Hash(content));
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class SettingsOnlyDriver : IGameServerDriver
    {
        public string Kind => GameKinds.MinecraftJava;
        public string DisplayName => "Source-only settings adapter";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => Path.Combine(profile.WorldDirectory, profile.WorldId);
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => throw new InvalidOperationException("No process test is permitted.");
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
        public GameHealthResult Health(ManagedRun run) => new(false, "SourceOnly", "Unknown", "No game evidence is tested.");
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw new InvalidOperationException("No process test is permitted.");
    }
}
