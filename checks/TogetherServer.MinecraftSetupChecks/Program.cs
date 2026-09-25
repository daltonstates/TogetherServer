using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

var root = Path.GetFullPath("local-data/minecraft-setup-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

byte[] Archive(params (string Name, byte[] Content)[] files)
{
    using var memory = new MemoryStream();
    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var file in files)
        {
            using var output = zip.CreateEntry(file.Name).Open();
            output.Write(file.Content);
        }
    return memory.ToArray();
}

const string versionUrl = "https://piston-meta.mojang.com/v1/packages/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/test.1.json";
const string runtimeUrl = "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jre_x64_windows_hotspot_25.0.4.1_1.zip";
const string bedrockUrl = "https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.2.3.zip";
const string manifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
const string bedrockLinksUrl = "https://net-secondary.web.minecraft-services.net/api/v1.0/download/links";

HttpClient FakeClient(byte[] jar, byte[] bedrockZip, byte[] runtimeZip, bool badChecksum = false,
    bool badServerUrl = false, bool badRuntimeChecksum = false, Action? requestSeen = null)
{
    var jarHash = badChecksum ? new string('b', 40) : Convert.ToHexString(SHA1.HashData(jar)).ToLowerInvariant();
    var serverUrl = badServerUrl ? "https://example.com/server.jar" :
        $"https://piston-data.mojang.com/v1/objects/{jarHash}/server.jar";
    var responses = new Dictionary<string, byte[]>
    {
        [manifestUrl] = JsonSerializer.SerializeToUtf8Bytes(new { latest = new { release = "test.1" }, versions = new[] { new { id = "test.1", url = versionUrl } } }),
        [versionUrl] = JsonSerializer.SerializeToUtf8Bytes(new { downloads = new { server = new { sha1 = jarHash, size = jar.Length, url = serverUrl } }, javaVersion = new { majorVersion = 25 } }),
        [serverUrl] = jar,
        ["https://api.adoptium.net/v3/assets/latest/25/hotspot?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot&os=windows&vendor=eclipse"] =
            JsonSerializer.SerializeToUtf8Bytes(new[] { new { binary = new { package = new {
                link = runtimeUrl, checksum = badRuntimeChecksum ? new string('0', 64) :
                    Convert.ToHexString(SHA256.HashData(runtimeZip)).ToLowerInvariant(), size = runtimeZip.Length } } } }),
        [runtimeUrl] = runtimeZip,
        [bedrockLinksUrl] = JsonSerializer.SerializeToUtf8Bytes(new { result = new { links = new[] { new { downloadType = "serverBedrockWindows", downloadUrl = bedrockUrl } } } }),
        [bedrockUrl] = bedrockZip
    };
    return new HttpClient(new FakeHandler(request =>
    {
        requestSeen?.Invoke();
        var url = request.RequestUri!.AbsoluteUri;
        return responses.TryGetValue(url, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }));
}

await Check("bounded scan finds several Java JARs and Bedrock without guessing a JAR", () =>
{
    var scan = Path.Combine(root, "scan");
    var folder = Path.Combine(scan, "my-server");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "server.properties"), "level-name=My World\nserver-port=25570\n");
    var jar = Archive(("META-INF/MANIFEST.MF", Encoding.ASCII.GetBytes("Manifest-Version: 1.0\nMain-Class: net.minecraft.server.Main\n\n")),
        ("fixture/Server.class", new byte[100]));
    File.WriteAllBytes(Path.Combine(folder, "server.jar"), jar);
    File.WriteAllText(Path.Combine(folder, ".togetherserver-java.json"), JsonSerializer.Serialize(new
    {
        version = "fixture",
        sha1 = Convert.ToHexString(SHA1.HashData(jar))
    }));
    File.WriteAllBytes(Path.Combine(folder, "paper-1.21.jar"), jar);
    File.WriteAllBytes(Path.Combine(folder, "client.jar"), jar);
    File.WriteAllText(Path.Combine(folder, "server-broken.jar"), "not a JAR");
    File.WriteAllText(Path.Combine(folder, "bedrock_server.exe"), "fixture");
    var java = Path.Combine(root, "java.exe");
    File.WriteAllText(java, "fixture");
    var found = MinecraftSetup.ScanRoots([scan], java);
    Require(found.Installations.Count == 2, "scanner did not keep vanilla Java and Bedrock separate");
    Require(found.Installations.Count(item => item.Kind == GameKinds.MinecraftJava) == 1 &&
        found.Installations.Single(item => item.Kind == GameKinds.MinecraftJava).ArtifactPath.EndsWith("server.jar"),
        "scanner admitted a modded/client/broken Java file or missed vanilla");
    Require(found.Installations.All(item => item.WorldName == "My World" && item.GamePort == 25570),
        "server.properties values were not detected");
    return Task.CompletedTask;
});

await Check("install needs explicit terms and valid settings before a network request", async () =>
{
    using var data = new LocalData(Path.Combine(root, "consent"));
    var requests = 0;
    using var client = FakeClient([], [], [], requestSeen: () => requests++);
    var installer = new MinecraftInstaller(client, data);
    Require((await installer.InstallAsync(new(GameKinds.MinecraftJava, "world", 25565, false))).Code == "TermsRequired", "missing consent accepted");
    Require((await installer.InstallAsync(new(GameKinds.MinecraftBedrock, "../escape", 19132, true))).Code == "InvalidWorldName", "unsafe world accepted");
    Require((await installer.InstallAsync(new(GameKinds.MinecraftJava, "world", 0, true))).Code == "InvalidPort", "invalid port accepted");
    Require(requests == 0, "invalid request accessed the network");
});

var fixtureJar = Archive(("META-INF/MANIFEST.MF", Encoding.ASCII.GetBytes("Manifest-Version: 1.0\nMain-Class: net.minecraft.server.Main\n\n")),
    ("fixture/Server.class", new byte[100]));
var fixtureRuntime = Archive(("jre-25/bin/java.exe", Encoding.ASCII.GetBytes("synthetic runtime")));
var fixtureBedrock = Archive(("bedrock_server.exe", Encoding.ASCII.GetBytes("synthetic server")),
    ("server.properties", Encoding.ASCII.GetBytes("level-name = Old\nserver-port = 19132\nserver-portv6=19133\n")));

await Check("fresh Java and Bedrock installs configure separate folders and preserve existing files", async () =>
{
    using var data = new LocalData(Path.Combine(root, "installed"));
    var existing = Path.Combine(root, "existing-world");
    Directory.CreateDirectory(existing);
    File.WriteAllText(Path.Combine(existing, "world.db"), "keep this");
    using var client = FakeClient(fixtureJar, fixtureBedrock, fixtureRuntime);
    var installer = new MinecraftInstaller(client, data);
    var java = await installer.InstallAsync(new(GameKinds.MinecraftJava, "New World", 25570, true));
    Require(java.Ok && java.Installation is not null, "Java install failed: " + java.Message);
    var javaInstall = java.Installation!;
    Require(File.ReadAllText(Path.Combine(javaInstall.ServerDirectory, "eula.txt")).Contains("eula=true"), "consented EULA was not recorded");
    Require(File.Exists(javaInstall.ExecutablePath) && File.Exists(javaInstall.ArtifactPath), "Java server/runtime missing");
    Require(javaInstall.ServerDirectory.StartsWith(data.MinecraftInstallRoot), "Java install escaped managed root");
    var bedrock = await installer.InstallAsync(new(GameKinds.MinecraftBedrock, "Bedrock World", 19140, true));
    Require(bedrock.Ok && bedrock.Installation is not null, "Bedrock install failed: " + bedrock.Message);
    var bedrockInstall = bedrock.Installation!;
    var properties = File.ReadAllText(Path.Combine(bedrockInstall.ServerDirectory, "server.properties"));
    Require(properties.Contains("server-port=19140") && !properties.Contains("server-port = 19132") &&
        properties.Contains("level-name=Bedrock World") && !properties.Contains("level-name = Old"), "Bedrock world or port not configured exactly");
    Require(MinecraftSetup.ScanRoots([data.MinecraftInstallRoot], javaInstall.ExecutablePath).Installations.Count == 2,
        "managed installs were not rediscovered");
    File.AppendAllText(javaInstall.ArtifactPath, "tampered");
    Require(MinecraftSetup.ScanRoots([data.MinecraftInstallRoot], javaInstall.ExecutablePath).Installations
            .All(item => item.Kind != GameKinds.MinecraftJava),
        "a managed Java JAR that no longer matched its official checksum was rediscovered");
    Require(File.ReadAllText(Path.Combine(existing, "world.db")) == "keep this", "existing world changed");
});

await Check("staging Bedrock install isolates IPv4 IPv6 and LAN discovery ports", async () =>
{
    using var data = new LocalData(Path.Combine(root, "staging-bedrock"), 5132);
    using var client = FakeClient(fixtureJar, fixtureBedrock, fixtureRuntime);
    var installer = new MinecraftInstaller(client, data, isolatedNetworking: true);
    Require((await installer.InstallAsync(new(GameKinds.MinecraftBedrock, "Staging World", 65535, true))).Code == "InvalidPort",
        "staging accepted a Bedrock base port without room for its IPv6 port");
    var result = await installer.InstallAsync(new(GameKinds.MinecraftBedrock, "Staging World", 19134, true));
    Require(result.Ok && result.Installation is not null, "staging Bedrock install failed: " + result.Message);
    var properties = File.ReadAllText(Path.Combine(result.Installation!.ServerDirectory, "server.properties"));
    Require(properties.Contains("server-port=19134") && properties.Contains("server-portv6=19135") &&
        properties.Contains("enable-lan-visibility=false") && !properties.Contains("server-portv6=19133"),
        "staging Bedrock did not isolate its IPv4, IPv6, and LAN discovery ports");
});

await Check("checksum mismatch, foreign URL, and unsafe archive leave no server install", async () =>
{
    async Task Attempt(string name, HttpClient client, string kind)
    {
        using (client)
        using (var data = new LocalData(Path.Combine(root, name)))
        {
            var result = await new MinecraftInstaller(client, data).InstallAsync(new(kind, "world", 25565, true));
            Require(!result.Ok, name + " was accepted");
            Require(!Directory.Exists(data.MinecraftInstallRoot) ||
                !Directory.EnumerateDirectories(data.MinecraftInstallRoot).Any(), name + " left an install or staging folder");
        }
    }
    await Attempt("checksum", FakeClient(fixtureJar, fixtureBedrock, fixtureRuntime, badChecksum: true), GameKinds.MinecraftJava);
    await Attempt("runtime-checksum", FakeClient(fixtureJar, fixtureBedrock, fixtureRuntime, badRuntimeChecksum: true), GameKinds.MinecraftJava);
    await Attempt("foreign-url", FakeClient(fixtureJar, fixtureBedrock, fixtureRuntime, badServerUrl: true), GameKinds.MinecraftJava);
    var unsafeZip = Archive(("../escape.txt", Encoding.ASCII.GetBytes("bad")),
        ("bedrock_server.exe", Encoding.ASCII.GetBytes("synthetic")));
    await Attempt("zip-slip", FakeClient(fixtureJar, unsafeZip, fixtureRuntime), GameKinds.MinecraftBedrock);
    Require(!File.Exists(Path.Combine(root, "escape.txt")), "ZIP wrote outside staging");
});

Console.WriteLine($"Minecraft setup checks: {passed} passed, {failed} failed. Disposable data: {root}");
return failed == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(handle(request));
}
