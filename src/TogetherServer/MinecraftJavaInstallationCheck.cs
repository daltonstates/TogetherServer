using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// Reuse only the archive's reviewed format result. Every call still verifies
// the actual installed bytes against the owner's installation provenance.
// No timestamp, process, permission, world or readiness result enters this cache.
internal sealed class MinecraftJavaInstallationCheck(Func<Stream, bool>? inspectArchive = null)
{
    internal const int MaximumEntries = 32;
    private sealed record Provenance(string Version, string Sha1);
    private sealed record Artifact(string Jar, string Folder, string Sha256, string ProvenanceSha256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnsupportedNames =
        ["paper", "purpur", "spigot", "bukkit", "fabric", "forge", "neoforge", "quilt", "installer", "client"];
    private static readonly HashSet<string> MainClasses = new(StringComparer.Ordinal)
        { "net.minecraft.server.Main", "net.minecraft.bundler.Main" };
    private readonly Func<Stream, bool> archiveCheck = inspectArchive ?? InspectArchive;
    private readonly HashSet<Artifact> verified = [];
    private readonly Queue<Artifact> order = [];
    private readonly object gate = new();

    internal bool Check(string jar, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(jar);
        if (UnsupportedNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)) ||
            !Path.GetFileName(jar).Equals("server.jar", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var metadata = new FileStream(Path.Combine(folder, ".togetherserver-java.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            if (metadata.Length is < 1 or > 4096) return false;
            var metadataBytes = new byte[(int)metadata.Length];
            metadata.ReadExactly(metadataBytes);
            using var metadataReader = new StreamReader(new MemoryStream(metadataBytes, writable: false));
            var provenance = JsonSerializer.Deserialize<Provenance>(metadataReader.ReadToEnd(), Json);
            if (provenance is null || string.IsNullOrWhiteSpace(provenance.Version) ||
                provenance.Sha1 is not { Length: 40 } || provenance.Sha1.Any(character => !char.IsAsciiHexDigit(character)))
                return false;

            // Hash and inspect one read lease. Reopening the path between those
            // steps could certify a different archive than the bytes just hashed.
            using var input = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 100 or > 300_000_000) return false;
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                sha256.AppendData(buffer, 0, read);
            }
            if (!Convert.ToHexString(sha1.GetHashAndReset()).Equals(provenance.Sha1, StringComparison.OrdinalIgnoreCase))
                return false;
            var artifact = new Artifact(Path.GetFullPath(jar), Path.GetFullPath(folder),
                Convert.ToHexString(sha256.GetHashAndReset()), Convert.ToHexString(SHA256.HashData(metadataBytes)));
            lock (gate)
            {
                if (verified.Contains(artifact)) return true;
                input.Position = 0;
                if (!archiveCheck(input)) return false;
                if (verified.Count == MaximumEntries) verified.Remove(order.Dequeue());
                verified.Add(artifact);
                order.Enqueue(artifact);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return false; }
    }

    internal static bool InspectArchive(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
        if (manifest is null || manifest.Length > 64_000) return false;
        using var reader = new StreamReader(manifest.Open());
        var mainClass = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Main-Class", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim()).SingleOrDefault();
        return mainClass is not null && MainClasses.Contains(mainClass) &&
            !zip.Entries.Any(entry => entry.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("io/papermc/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("org/bukkit/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("net/fabricmc/", StringComparison.OrdinalIgnoreCase));
    }
}
