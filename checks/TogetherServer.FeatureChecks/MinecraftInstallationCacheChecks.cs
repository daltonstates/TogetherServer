using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class MinecraftInstallationCacheChecks
{
    internal static void Run(string root)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var directory = Path.Combine(root, "java-installation-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jar = Path.Combine(directory, "server.jar");
        var provenance = Path.Combine(directory, ".togetherserver-java.json");
        var bytes = Archive('a');
        File.WriteAllBytes(jar, bytes);
        MinecraftSetup.WriteManagedJavaProvenance(directory, "1.21.1", Convert.ToHexString(SHA1.HashData(bytes)));
        var inspected = 0;
        var cache = new MinecraftJavaInstallationCheck(input =>
        {
            inspected++;
            return MinecraftJavaInstallationCheck.InspectArchive(input);
        });
        Require(cache.Check(jar, directory) && cache.Check(jar, directory) && inspected == 1,
            "unchanged current bytes repeated archive inspection");

        var jarTimestamp = File.GetLastWriteTimeUtc(jar);
        var changed = Archive('b');
        Require(bytes.Length == changed.Length, "equal-size content-change fixture changed length");
        File.WriteAllBytes(jar, changed);
        File.SetLastWriteTimeUtc(jar, jarTimestamp);
        Require(!cache.Check(jar, directory) && inspected == 1,
            "equal-size timestamp-preserving JAR tampering reused setup verification");
        MinecraftSetup.WriteManagedJavaProvenance(directory, "1.21.1", Convert.ToHexString(SHA1.HashData(changed)));
        Require(cache.Check(jar, directory) && inspected == 2,
            "changed official artifact did not invalidate archive inspection");

        var metadataTimestamp = File.GetLastWriteTimeUtc(provenance);
        var metadataLength = new FileInfo(provenance).Length;
        MinecraftSetup.WriteManagedJavaProvenance(directory, "1.21.2", Convert.ToHexString(SHA1.HashData(changed)));
        File.SetLastWriteTimeUtc(provenance, metadataTimestamp);
        Require(new FileInfo(provenance).Length == metadataLength && cache.Check(jar, directory) && inspected == 3,
            "equal-size timestamp-preserving provenance change retained its earlier result");
        File.Delete(provenance);
        Require(!cache.Check(jar, directory), "missing provenance reused an earlier result");
        File.WriteAllText(provenance, "{\"version\":\"1.21.2\",\"sha1\":\"invalid\"}");
        Require(!cache.Check(jar, directory), "invalid provenance reused an earlier result");
        MinecraftSetup.WriteManagedJavaProvenance(directory, "1.21.2", Convert.ToHexString(SHA1.HashData(changed)));

        // A second app-lifetime checker must inspect again. Nothing durable or
        // unrelated to this exact path and its current bytes grants a cache hit.
        var freshInspected = 0;
        var fresh = new MinecraftJavaInstallationCheck(input =>
        {
            freshInspected++;
            return MinecraftJavaInstallationCheck.InspectArchive(input);
        });
        Require(fresh.Check(jar, directory) && freshInspected == 1, "fresh checker inherited setup verification");
        for (var index = 0; index < MinecraftJavaInstallationCheck.MaximumEntries; index++)
        {
            var other = Path.Combine(directory, "other-" + index);
            Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "server.jar"), changed);
            MinecraftSetup.WriteManagedJavaProvenance(other, "1.21.2", Convert.ToHexString(SHA1.HashData(changed)));
            Require(fresh.Check(Path.Combine(other, "server.jar"), other), "bounded cache fixture was rejected");
        }
        var beforeEvicted = freshInspected;
        Require(fresh.Check(jar, directory) && freshInspected == beforeEvicted + 1,
            "bounded setup cache retained an evicted artifact");

        var unsafeJar = Archive('c', modded: true);
        File.WriteAllBytes(jar, unsafeJar);
        MinecraftSetup.WriteManagedJavaProvenance(directory, "1.21.2", Convert.ToHexString(SHA1.HashData(unsafeJar)));
        Require(!cache.Check(jar, directory) && !cache.Check(jar, directory) && inspected == 5,
            "modded archive was accepted or a failed inspection became reusable success");
        File.WriteAllBytes(jar, changed);
        MinecraftSetup.WriteManagedJavaProvenance(directory, "1.21.2", Convert.ToHexString(SHA1.HashData(changed)));

        if (OperatingSystem.IsWindows())
        {
            var writeBlocked = false;
            var leaseCheck = new MinecraftJavaInstallationCheck(input =>
            {
                try { using var writer = File.Open(jar, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { writeBlocked = true; }
                return MinecraftJavaInstallationCheck.InspectArchive(input);
            });
            Require(leaseCheck.Check(jar, directory) && writeBlocked,
                "archive inspection did not hold the stream whose bytes were hashed against writes");
        }

        using var data = new LocalData(Path.Combine(directory, "app-data"));
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.MinecraftJava,
            WorldId = "world",
            WorldDirectory = directory,
            ExecutablePath = Path.Combine(directory, "java.exe"),
            GamePort = 25565,
            Minecraft = new() { ServerJarPath = jar }
        };
        File.WriteAllText(profile.ExecutablePath, "synthetic runtime; never executed");
        var eula = Path.Combine(directory, "eula.txt");
        var properties = Path.Combine(directory, "server.properties");
        File.WriteAllText(eula, "eula=true\n");
        File.WriteAllText(properties, "level-name=world\nserver-port=25565\n");
        var driver = new MinecraftJavaServerDriver(data);
        Require(driver.ValidateForStart(profile) is null && driver.ValidateForStart(profile) is null,
            "valid repeated Start validation failed");
        File.WriteAllText(eula, "eula=false\n");
        Require(driver.ValidateForStart(profile)?.Code == "MinecraftEulaRequired",
            "reused installation verification skipped current owner terms choice");
        File.WriteAllText(eula, "eula=true\n");
        File.WriteAllText(properties, "level-name=other\nserver-port=25565\n");
        Require(driver.ValidateForStart(profile)?.Code == "MinecraftWorldMismatch",
            "reused installation verification skipped current world selection");
        File.WriteAllText(properties, "level-name=world\nserver-port=25566\n");
        Require(driver.ValidateForStart(profile)?.Code == "MinecraftPortMismatch",
            "reused installation verification skipped changed current port settings");
        File.WriteAllText(properties, "level-name=world\nserver-port=25565\n");
        File.WriteAllText(Path.Combine(directory, "ops.json"), "{\"invalid\":true}");
        Require(driver.ValidateForStart(profile)?.Code == "MinecraftConfigurationInvalid",
            "reused installation verification skipped current reviewed configuration");
    }

    private static byte[] Archive(char payload, bool modded = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nMain-Class: net.minecraft.server.Main\n\n");
            Add("fixture/Server.class", new string(payload, 200));
            if (modded) Add("fabric.mod.json", "{}");
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(Encoding.ASCII.GetBytes(text));
            }
        }
        return memory.ToArray();
    }
}
