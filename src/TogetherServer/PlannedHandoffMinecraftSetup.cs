using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

// Reuses an owner-installed matching server. Only reviewed runtime files enter
// a fresh app-managed folder; source worlds, permissions and secrets stay put.
internal static class PlannedHandoffMinecraftSetup
{
    internal static MinecraftInstallation Prepare(LocalData data, SharedWorldVersion version, Guid requestId,
        string serverFile, int gamePort, IReadOnlyList<ServerProfile> profiles)
    {
        if (version.Game is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) ||
            !ValheimSetup.ValidWorldId(version.WorldId) || requestId == Guid.Empty)
            throw new InvalidDataException("Choose a reviewed Minecraft save.");
        var root = Path.Combine(data.MinecraftInstallRoot, version.ProfileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        var marker = Path.Combine(root, ".planned-host-setup");
        var markerValue = requestId.ToString("N");
        if (Directory.Exists(root))
        {
            if (!ManagedImportFiles.PlainFile(marker))
                throw new InvalidDataException("An existing Minecraft folder was kept. Review it before moving hosting.");
            if (File.ReadAllText(marker) != markerValue)
            {
                var previousBytes = data.LoadProtected($"planned-local-hosting-{version.ProfileId:N}.protected");
                var previous = previousBytes is { Length: > 0 and <= 262144 } ? JsonSerializer.Deserialize<PlannedLocalHostingState>(previousBytes) : null;
                var payload = version.Game == GameKinds.MinecraftJava ? Path.Combine(root, version.WorldId) : Path.Combine(root, "worlds");
                if (previous is not { Schema: 1 } || previous.ProfileId != version.ProfileId ||
                    previous.PreparedServerRoot != root || File.ReadAllText(marker) != previous.RequestId.ToString("N") ||
                    Directory.Exists(payload) || File.Exists(payload) || profiles.Any(profile => profile.Id == version.ProfileId) ||
                    previous.SetupIdentity != FriendLocalHostingService.PortableSetupIdentity(version.Game, version.WorldId, version.PortableSetup))
                    throw new InvalidDataException("An existing Minecraft folder or world was kept. Review it before moving hosting.");
                // Only a compatible, protected, runtime-only preparation from a
                // canceled request is rebound; no world or runtime bytes change.
                File.WriteAllText(marker, markerValue, Encoding.ASCII);
            }
        }
        else
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(marker, markerValue, Encoding.ASCII);
        }
        var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(serverFile))!;
        var runtime = version.Game == GameKinds.MinecraftJava ?
            MinecraftSetup.ScanManaged(data, profiles).JavaRuntimePath : Path.Combine(root, "bedrock_server.exe");
        if (version.Game == GameKinds.MinecraftJava)
        {
            if (!ManagedImportFiles.PlainFile(runtime) || !Path.GetFileName(runtime).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Install or select a supported local Java runtime in normal Host setup, then retry.");
            CopyFile(serverFile, Path.Combine(root, "server.jar"));
            var provenance = Path.Combine(sourceRoot, ".togetherserver-java.json");
            if (File.Exists(provenance)) CopyFile(provenance, Path.Combine(root, ".togetherserver-java.json"));
            // Prepare is reached only after explicit local AcceptedTerms.
            WriteNewOrSame(Path.Combine(root, "eula.txt"), "eula=true\n");
        }
        else
        {
            var files = new List<string>();
            foreach (var name in new[] { "bedrock_server.exe", "Chakra.dll", "libcrypto-3-x64.dll", "libssl-3-x64.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                if (File.Exists(Path.Combine(sourceRoot, name))) files.Add(Path.Combine(sourceRoot, name));
            foreach (var name in new[] { "definitions", "resource_packs", "behavior_packs", "structures", "bedrock_server_symbols.debug" })
            {
                var folder = Path.Combine(sourceRoot, name);
                if (!Directory.Exists(folder)) continue;
                var pending = new Stack<string>(); pending.Push(folder);
                var directories = 0;
                while (pending.TryPop(out var current))
                {
                    if (++directories > 4096 || Path.GetRelativePath(sourceRoot, current).Count(character => character is '/' or '\\') > 32)
                        throw new InvalidDataException("The Minecraft runtime folder inventory exceeds its preparation limit.");
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The Minecraft runtime contains a linked folder.");
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        files.Add(file);
                        if (files.Count > 16384) throw new InvalidDataException("The Minecraft runtime inventory exceeds its preparation limit.");
                    }
                    foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
                }
            }
            if (files.Sum(file => new FileInfo(file).Length) > 1024L * 1024 * 1024)
                throw new InvalidDataException("The installed Minecraft runtime exceeds its preparation limit.");
            foreach (var file in files)
                CopyFile(file, SharedWorldService.SafeChild(root, Path.GetRelativePath(sourceRoot, file).Replace('\\', '/')));
        }
        var signed = version.PortableSetup;
        var properties = new StringBuilder().Append("level-name=").Append(version.WorldId).Append('\n')
            .Append("server-port=").Append(gamePort).Append('\n');
        if (version.Game == GameKinds.MinecraftBedrock) properties.Append("server-portv6=").Append(gamePort == 65535 ? gamePort - 1 : gamePort + 1).Append('\n');
        if (signed.MaxPlayers is { } players) properties.Append("max-players=").Append(players).Append('\n');
        if (signed.GameMode is { } mode) properties.Append("gamemode=").Append(mode).Append('\n');
        if (signed.Difficulty is { } difficulty) properties.Append("difficulty=").Append(difficulty).Append('\n');
        if (signed.AllowlistEnabled is not { } allowlist) throw new InvalidDataException("Receive a Minecraft save with reviewed allowlist settings.");
        properties.Append(version.Game == GameKinds.MinecraftJava ? "white-list=" : "allow-list=").Append(allowlist ? "true\n" : "false\n");
        WriteNewOrSame(Path.Combine(root, "server.properties"), properties.ToString());
        var entries = (signed.Allowlist ?? []).Select(entry =>
        {
            var value = new Dictionary<string, string> { ["name"] = entry.Name };
            if (entry.Id is not null) value[version.Game == GameKinds.MinecraftJava ? "uuid" : "xuid"] = entry.Id;
            return value;
        }).ToArray();
        WriteNewOrSame(Path.Combine(root, version.Game == GameKinds.MinecraftJava ? "whitelist.json" : "allowlist.json"), JsonSerializer.Serialize(entries));
        return new(version.Game, root, version.Game == GameKinds.MinecraftJava ? Path.Combine(root, "server.jar") : runtime,
            runtime, version.WorldId, gamePort, "Existing local installation", "Prepared without starting a game");
    }

    private static void CopyFile(string source, string destination)
    {
        if (!ManagedImportFiles.PlainFile(source) || new FileInfo(source).Length is < 0 or > 512L * 1024 * 1024)
            throw new InvalidDataException("An installed runtime file is linked or too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        if (!ManagedImportFiles.PlainFile(destination)) throw new InvalidDataException("The prepared runtime file is linked.");
        using var original = File.OpenRead(source);
        using var copied = File.OpenRead(destination);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(copied)))
            throw new InvalidDataException("An existing runtime file differs. No file was replaced.");
    }

    private static void WriteNewOrSame(string path, string value)
    {
        if (File.Exists(path))
        {
            if (!ManagedImportFiles.PlainFile(path) || new FileInfo(path).Length > 32768 || File.ReadAllText(path) != value)
                throw new InvalidDataException("An existing prepared setup file differs. No file was replaced.");
            return;
        }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(new UTF8Encoding(false).GetBytes(value));
    }
}
