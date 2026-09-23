using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

var root = Path.GetFullPath("local-data/update-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fixture = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the fixture in Release first.", fixture);
var passed = 0;
var failed = 0;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
string Metadata(string tag, byte[] bytes, string? digest = null, string? url = null, bool prerelease = false) =>
    JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft = false,
        prerelease,
        assets = new[] { new { name = AppUpdater.AssetName, state = "uploaded", size = bytes.Length,
            digest = "sha256:" + (digest ?? Hash(bytes)), browser_download_url = url ??
                $"https://github.com/daltonstates/TogetherServer/releases/download/{tag}/{AppUpdater.AssetName}" } }
    });
AppUpdater Updater(string name, HttpMessageHandler handler)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, "TogetherServer.exe");
    File.WriteAllText(installed, "old executable");
    return new AppUpdater(new HttpClient(handler), directory, installed, new Version(0, 1, 0));
}

await Check("automatic checks use the startup loop's 30-minute cadence", () =>
{
    Require(AppUpdater.AutomaticCheckInterval == TimeSpan.FromMinutes(30),
        "automatic update checks are not scheduled every 30 minutes");
    return Task.CompletedTask;
});

await Check("no GitHub release is a normal state", async () =>
{
    var updater = Updater("no-release", new FakeHandler(_ => new(HttpStatusCode.NotFound)));
    Require((await updater.CheckAsync()).State == "NoRelease", "missing release was not distinguished from an update");
    Require(!(await updater.PrepareAsync()).Ok, "missing release was installable");
});

await Check("newer release downloads only exact asset and digest", async () =>
{
    var bytes = File.ReadAllBytes(fixture);
    var metadata = Metadata("v1.0.0", bytes);
    var handler = new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
        ? new(HttpStatusCode.OK) { Content = new StringContent(metadata) }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    var updater = Updater("valid", handler);
    Require((await updater.CheckAsync()).State == "Available", "new version was not offered");
    Require((await updater.PrepareAsync()).Ok, "valid update did not download");
    Require(handler.Requests.Count(url => url.Host == "github.com") == 1, "download did not use the release URL once");
    var downloaded = Directory.GetFiles(Path.Combine(root, "valid", "updates"), AppUpdater.AssetName, SearchOption.AllDirectories).Single();
    Require(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "downloaded bytes changed");
});

await Check("release tag must match the packaged EXE version", async () =>
{
    var bytes = File.ReadAllBytes(fixture);
    var metadata = Metadata("v2.0.0", bytes);
    var updater = Updater("wrong-version", new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
        ? new(HttpStatusCode.OK) { Content = new StringContent(metadata) }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
    Require((await updater.PrepareAsync()).Code == "InvalidDownload", "wrong EXE version was accepted");
});

await Check("wrong digest rejects download and removes staging", async () =>
{
    var bytes = Encoding.UTF8.GetBytes("MZ tampered update");
    var metadata = Metadata("v0.2.0", bytes, new string('A', 64));
    var updater = Updater("wrong-hash", new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
        ? new(HttpStatusCode.OK) { Content = new StringContent(metadata) }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
    Require((await updater.CheckAsync()).State == "Available", "test release was not found");
    Require((await updater.PrepareAsync()).Code == "InvalidDownload", "tampered update was accepted");
    Require(Directory.GetFiles(Path.Combine(root, "wrong-hash", "updates"), "*", SearchOption.AllDirectories).Length == 0,
        "failed update left a runnable staged EXE");
});

await Check("untrusted URL and prerelease never install", () =>
{
    var bytes = Encoding.UTF8.GetBytes("MZ test");
    Require(AppUpdater.ParseRelease(Metadata("v0.2.0", bytes, url: "https://example.com/update.exe")) is null,
        "foreign URL was accepted");
    Require(AppUpdater.ParseRelease(Metadata("v0.2.0", bytes, prerelease: true)) is null,
        "prerelease was accepted");
    Require(AppUpdater.ParseRelease(Metadata("v0.2.0-rc1", bytes)) is null,
        "nonstable tag was accepted");
    return Task.CompletedTask;
});

await Check("replacement preserves previous EXE and rejects changed payload", () =>
{
    var directory = Path.Combine(root, "replace");
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, "TogetherServer.exe");
    var payload = Path.Combine(directory, AppUpdater.AssetName);
    File.WriteAllText(installed, "previous version");
    File.WriteAllText(payload, "new version");
    var digest = Hash(File.ReadAllBytes(payload));
    UpdateInstaller.ReplaceVerified(installed, payload, digest);
    Require(File.ReadAllText(installed) == "new version", "new EXE was not installed");
    Require(File.ReadAllText(installed + ".previous") == "previous version", "previous EXE was not backed up");
    File.WriteAllText(payload, "changed after verification");
    try { UpdateInstaller.ReplaceVerified(installed, payload, digest); throw new Exception("changed payload installed"); }
    catch (CryptographicException) { }
    Require(File.ReadAllText(installed) == "new version", "failed update changed the installed EXE");
    File.WriteAllText(payload, "third version");
    UpdateInstaller.ReplaceVerified(installed, payload, Hash(File.ReadAllBytes(payload)));
    Require(File.ReadAllText(installed) == "third version", "second update did not replace the EXE");
    Require(File.ReadAllText(installed + ".previous") == "new version", "second update did not keep the immediate previous EXE");
    return Task.CompletedTask;
});

Console.WriteLine($"Update checks: {passed} passed, {failed} failed. Isolated data: {root}");
return failed == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(reply(request));
    }
}
