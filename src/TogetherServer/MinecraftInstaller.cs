using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record MinecraftInstallRequest(string Kind, string WorldName, int GamePort, bool AcceptedTerms);
public sealed record MinecraftInstallResult(bool Ok, string Code, string Message, MinecraftInstallation? Installation = null);

public sealed class MinecraftInstaller(HttpClient client, LocalData data)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private const long JarLimit = 200L * 1024 * 1024;
    private const long ZipLimit = 300L * 1024 * 1024;

    public async Task<MinecraftInstallResult> InstallAsync(MinecraftInstallRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Kind is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock))
            return new(false, "InvalidEdition", "Choose Minecraft Java or Bedrock.");
        if (!request.AcceptedTerms)
            return new(false, "TermsRequired", "Review and accept the Minecraft EULA and Microsoft Privacy Statement before installing.");
        if (!Regex.IsMatch(request.WorldName ?? "", @"^[A-Za-z0-9][A-Za-z0-9 _-]{0,31}$") ||
            request.WorldName?.EndsWith(' ') == true)
            return new(false, "InvalidWorldName", "Use 1 to 32 letters, numbers, spaces, hyphens, or underscores for the world name.");
        if (request.GamePort is < 1024 or > 65535)
            return new(false, "InvalidPort", "Choose a game port from 1024 to 65535.");

        await gate.WaitAsync(cancellationToken);
        try { return await InstallCoreAsync(request, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or
            UnauthorizedAccessException or OperationCanceledException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return new(false, "InstallFailed", "Minecraft installation failed: " + ex.Message);
        }
        finally { gate.Release(); }
    }

    private async Task<MinecraftInstallResult> InstallCoreAsync(MinecraftInstallRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(data.MinecraftInstallRoot);
        var edition = request.Kind == GameKinds.MinecraftJava ? "java" : "bedrock";
        var stage = Path.Combine(data.MinecraftInstallRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string artifact;
            string executable;
            string version;
            if (request.Kind == GameKinds.MinecraftJava)
            {
                using var manifest = await ReadJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
                version = manifest.RootElement.GetProperty("latest").GetProperty("release").GetString() ?? "";
                if (!Regex.IsMatch(version, @"^[A-Za-z0-9._-]{1,32}$")) throw new InvalidDataException("Minecraft release version is invalid.");
                var versionUrl = manifest.RootElement.GetProperty("versions").EnumerateArray()
                    .First(item => item.GetProperty("id").GetString() == version).GetProperty("url").GetString() ?? "";
                RequireUrl(versionUrl, "piston-meta.mojang.com", @"^/v1/packages/[a-f0-9]{40}/[a-z0-9._-]+\.json$");
                using var metadata = await ReadJsonAsync(versionUrl, ct);
                var server = metadata.RootElement.GetProperty("downloads").GetProperty("server");
                var sha1 = server.GetProperty("sha1").GetString() ?? "";
                var serverUrl = server.GetProperty("url").GetString() ?? "";
                var size = server.GetProperty("size").GetInt64();
                if (!Regex.IsMatch(sha1, "^[a-f0-9]{40}$") || size is < 1 or > JarLimit)
                    throw new InvalidDataException("Official server JAR metadata is invalid.");
                RequireUrl(serverUrl, "piston-data.mojang.com", @"^/v1/objects/[a-f0-9]{40}/server\.jar$");
                if (!serverUrl.Contains(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Server JAR URL and checksum disagree.");
                artifact = Path.Combine(stage, "server.jar");
                await DownloadAsync(serverUrl, artifact, JarLimit, size, sha1, HashAlgorithmName.SHA1, ct);
                var javaMajor = metadata.RootElement.GetProperty("javaVersion").GetProperty("majorVersion").GetInt32();
                if (javaMajor is < 17 or > 40) throw new InvalidDataException("Unsupported Java runtime version in Minecraft metadata.");
                executable = await EnsureRuntimeAsync(javaMajor, ct);
                File.WriteAllText(Path.Combine(stage, "server.properties"),
                    $"level-name={request.WorldName}\nserver-port={request.GamePort}\n", new UTF8Encoding(false));
                // This file is written only after the owner explicitly accepts terms in the local app.
                File.WriteAllText(Path.Combine(stage, "eula.txt"), "eula=true\n", Encoding.ASCII);
            }
            else
            {
                using var links = await ReadJsonAsync("https://net-secondary.web.minecraft-services.net/api/v1.0/download/links", ct);
                var url = links.RootElement.GetProperty("result").GetProperty("links").EnumerateArray()
                    .First(item => item.GetProperty("downloadType").GetString() == "serverBedrockWindows")
                    .GetProperty("downloadUrl").GetString() ?? "";
                RequireUrl(url, "www.minecraft.net", @"^/bedrockdedicatedserver/bin-win/bedrock-server-[A-Za-z0-9._-]+\.zip$");
                version = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath)["bedrock-server-".Length..];
                var zip = Path.Combine(data.MinecraftInstallRoot, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    await DownloadAsync(url, zip, ZipLimit, null, null, null, ct);
                    ExtractZip(zip, stage);
                }
                finally { if (File.Exists(zip)) File.Delete(zip); }
                artifact = Path.Combine(stage, "bedrock_server.exe");
                if (!File.Exists(artifact)) throw new InvalidDataException("Official Bedrock archive has no bedrock_server.exe.");
                executable = artifact;
                UpdateProperties(Path.Combine(stage, "server.properties"), request.WorldName, request.GamePort);
            }
            var final = Path.Combine(data.MinecraftInstallRoot, $"{edition}-{version}-{Guid.NewGuid():N}");
            Directory.Move(stage, final);
            artifact = Path.Combine(final, Path.GetFileName(artifact));
            if (request.Kind == GameKinds.MinecraftBedrock) executable = artifact;
            var installed = new MinecraftInstallation(request.Kind, final, artifact, executable,
                request.WorldName, request.GamePort, "Installed in TogetherServer", "Ready to select");
            return new(true, "Installed", $"Minecraft {edition} {version} is installed. Save setup, then Start server.", installed);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private async Task<string> EnsureRuntimeAsync(int major, CancellationToken ct)
    {
        var final = Path.Combine(data.MinecraftRuntimeRoot, $"java-{major}");
        var existing = FindRuntime(final);
        if (existing is not null) return existing;
        Directory.CreateDirectory(data.MinecraftRuntimeRoot);
        using var metadata = await ReadJsonAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot&os=windows&vendor=eclipse", ct);
        var package = metadata.RootElement.EnumerateArray().First().GetProperty("binary").GetProperty("package");
        var url = package.GetProperty("link").GetString() ?? "";
        var checksum = package.GetProperty("checksum").GetString() ?? "";
        var size = package.GetProperty("size").GetInt64();
        RequireUrl(url, "github.com", $@"^/adoptium/temurin{major}-binaries/releases/download/[^/]+/OpenJDK{major}U-jre_x64_windows_hotspot_[^/]+\.zip$");
        if (!Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$") || size is < 1 or > ZipLimit)
            throw new InvalidDataException("Java runtime metadata is invalid.");
        var stage = Path.Combine(data.MinecraftRuntimeRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(data.MinecraftRuntimeRoot, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(stage);
        try
        {
            await DownloadAsync(url, zip, ZipLimit, size, checksum, HashAlgorithmName.SHA256, ct);
            ExtractZip(zip, stage);
            if (FindRuntime(stage) is null) throw new InvalidDataException("Java runtime archive has no java.exe.");
            Directory.Move(stage, final);
            return FindRuntime(final)!;
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static string? FindRuntime(string root)
    {
        if (!Directory.Exists(root)) return null;
        try { return Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories).FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void UpdateProperties(string path, string world, int port)
    {
        if (!File.Exists(path)) throw new InvalidDataException("Bedrock archive has no server.properties.");
        var lines = File.ReadAllLines(path).Where(line =>
            !IsSetting(line, "level-name") && !IsSetting(line, "server-port")).ToList();
        lines.Add("level-name=" + world);
        lines.Add("server-port=" + port);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));

        static bool IsSetting(string line, string key)
        {
            var separator = line.IndexOf('=');
            return separator > 0 && line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000) throw new InvalidDataException("Download metadata is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (memory.Length + count > 2_000_000) throw new InvalidDataException("Download metadata is too large.");
            memory.Write(buffer, 0, count);
        }
        memory.Position = 0;
        return await JsonDocument.ParseAsync(memory, cancellationToken: ct);
    }

    private async Task DownloadAsync(string url, string destination, long limit, long? expectedSize,
        string? expectedHash, HashAlgorithmName? algorithm, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > limit)
            throw new InvalidDataException("Download is larger than the allowed limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = algorithm is { } hashName ? IncrementalHash.CreateHash(hashName) : null;
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            total += count;
            if (total > limit) throw new InvalidDataException("Download is larger than the allowed limit.");
            hash?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (expectedSize is { } size && total != size) throw new InvalidDataException("Downloaded file size did not match official metadata.");
        if (expectedHash is not null && !Convert.ToHexString(hash!.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded file checksum did not match official metadata.");
    }

    private static void RequireUrl(string value, string host, string pathPattern)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != host || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 || !Regex.IsMatch(uri.AbsolutePath, pathPattern, RegexOptions.IgnoreCase))
            throw new InvalidDataException("Official download metadata gave an unexpected URL.");
    }

    private static void ExtractZip(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > 5000) throw new InvalidDataException("Archive has too many entries.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var parts = name.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
            if (name.Length == 0 || name.StartsWith(Path.DirectorySeparatorChar) || name.Contains(':') ||
                parts.Any(part => part is ".." or "." or ""))
                throw new InvalidDataException("Archive contains an unsafe path.");
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(target))
                throw new InvalidDataException("Archive contains a duplicate or unsafe path.");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("Archive contains a symbolic link.");
            expanded += entry.Length;
            if (expanded > 1_000_000_000L) throw new InvalidDataException("Archive expands beyond the allowed limit.");
            if (name.EndsWith(Path.DirectorySeparatorChar)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }
}
