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
var publisherKey = new string('A', 64);
var trustedSignature = new FakeAuthenticodeVerifier(_ => new(true, publisherKey, "test publisher"));

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
AppUpdater Updater(string name, HttpMessageHandler handler, IAuthenticodeVerifier? verifier = null,
    string installedName = "TogetherServer.exe")
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, installedName);
    File.WriteAllText(installed, "old executable");
    return new AppUpdater(new HttpClient(handler), directory, installed, new Version(0, 1, 0), verifier ?? trustedSignature);
}

await Check("automatic checks use the startup loop's 30-minute cadence", () =>
{
    Require(AppUpdater.AutomaticCheckInterval == TimeSpan.FromMinutes(30),
        "automatic update checks are not scheduled every 30 minutes");
    return Task.CompletedTask;
});

await Check("Windows verifier distinguishes an ordinary unsigned EXE", () =>
{
    var signature = new WindowsAuthenticodeVerifier().Verify(fixture);
    Require(!signature.Valid && signature.IsUnsigned,
        "ordinary unsigned EXE was not distinguished from an invalid signature: " + signature.Message);
    return Task.CompletedTask;
});

await Check("staging update mode is offline and cannot replace the app", async () =>
{
    var directory = Path.Combine(root, "staging-disabled");
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, "TogetherServer.exe");
    File.WriteAllText(installed, "staging executable");
    var handler = new FakeHandler(_ => throw new Exception("disabled staging updater contacted the release service"));
    var updater = new AppUpdater(new HttpClient(handler), directory, installed, new Version(0, 1, 0),
        trustedSignature, enabled: false, disabledMessage: "Updates are disabled in staging.");
    Require((await updater.CheckAsync(true)).State == "Unsupported", "staging update check was not disabled");
    Require((await updater.PrepareAsync()).Code == "UpdatesDisabled", "staging prepared an update");
    Require(updater.StartReplacement().Code == "UpdatesDisabled", "staging started update replacement");
    Require(handler.Requests.Count == 0, "staging updater made a network request");
});

await Check("no GitHub release is a normal state", async () =>
{
    var updater = Updater("no-release", new FakeHandler(_ => new(HttpStatusCode.NotFound)));
    Require((await updater.CheckAsync()).State == "NoRelease", "missing release was not distinguished from an update");
    Require(!(await updater.PrepareAsync()).Ok, "missing release was installable");
});

await Check("unsigned installed apps use fixed-release SHA-256 verification", async () =>
{
    var bytes = File.ReadAllBytes(fixture);
    var metadata = Metadata("v1.0.0", bytes);
    var handler = new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
        ? new(HttpStatusCode.OK) { Content = new StringContent(metadata) }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    var updater = Updater("unsigned-installed", handler,
        new FakeAuthenticodeVerifier(_ => new(false, null, "unsigned", IsUnsigned: true)),
        "TogetherServer-win-x64 (4).exe");
    var result = await updater.CheckAsync();
    Require(result.State == "Available", "unsigned installed app did not offer the newer release");
    var prepared = await updater.PrepareAsync();
    Require(prepared.Ok && prepared.Message.Contains("SHA-256", StringComparison.Ordinal),
        "unsigned update was not verified through the release digest");
    Require(handler.Requests.Count(url => url.Host == "api.github.com") == 2 &&
        handler.Requests.Count(url => url.Host == "github.com") == 1,
        "unsigned updater did not use the fixed metadata and asset endpoints");
});

await Check("invalid installed signature cannot downgrade to unsigned verification", async () =>
{
    var handler = new FakeHandler(_ => throw new Exception("invalidly signed app contacted the release service"));
    var updater = Updater("invalid-signature", handler,
        new FakeAuthenticodeVerifier(_ => new(false, null, "invalid signature")));
    var result = await updater.CheckAsync();
    Require(result.State == "Unsupported" && result.Message.Contains("invalid", StringComparison.Ordinal),
        "invalid installed signature did not fail closed");
    Require(handler.Requests.Count == 0, "invalidly signed app made a release request");
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

await Check("candidate must use the installed publisher key", async () =>
{
    var bytes = File.ReadAllBytes(fixture);
    var metadata = Metadata("v1.0.0", bytes);
    var verifier = new FakeAuthenticodeVerifier(path => Path.GetFileName(path) == AppUpdater.AssetName
        ? new(true, new string('B', 64), "different publisher")
        : new(true, publisherKey, "installed publisher"));
    var updater = Updater("wrong-publisher", new FakeHandler(request => request.RequestUri!.Host == "api.github.com"
        ? new(HttpStatusCode.OK) { Content = new StringContent(metadata) }
        : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }), verifier);
    Require((await updater.PrepareAsync()).Code == "InvalidSignature", "different publisher key was accepted");
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
    var installed = Path.Combine(directory, "TogetherServer-win-x64 (4).exe");
    var payload = Path.Combine(directory, AppUpdater.AssetName);
    File.WriteAllText(installed, "previous version");
    File.WriteAllText(payload, "new version");
    var digest = Hash(File.ReadAllBytes(payload));
    UpdateInstaller.ReplaceVerified(installed, payload, digest, publisherKey, trustedSignature);
    Require(File.ReadAllText(installed) == "new version", "new EXE was not installed");
    Require(File.ReadAllText(installed + ".previous") == "previous version", "previous EXE was not backed up");
    File.WriteAllText(payload, "changed after verification");
    try { UpdateInstaller.ReplaceVerified(installed, payload, digest, publisherKey, trustedSignature); throw new Exception("changed payload installed"); }
    catch (CryptographicException) { }
    Require(File.ReadAllText(installed) == "new version", "failed update changed the installed EXE");
    File.WriteAllText(payload, "third version");
    UpdateInstaller.ReplaceVerified(installed, payload, Hash(File.ReadAllBytes(payload)), publisherKey, trustedSignature);
    Require(File.ReadAllText(installed) == "third version", "second update did not replace the EXE");
    Require(File.ReadAllText(installed + ".previous") == "new version", "second update did not keep the immediate previous EXE");
    return Task.CompletedTask;
});

await Check("unsigned replacement uses SHA-256 and preserves the previous EXE", () =>
{
    var directory = Path.Combine(root, "replace-unsigned");
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, "TogetherServer.exe");
    var payload = Path.Combine(directory, AppUpdater.AssetName);
    File.WriteAllText(installed, "unsigned previous version");
    File.WriteAllText(payload, "unsigned new version");
    var digest = Hash(File.ReadAllBytes(payload));
    var unsigned = new FakeAuthenticodeVerifier(_ => new(false, null, "unsigned"));
    UpdateInstaller.ReplaceVerified(installed, payload, digest, UpdateInstaller.HashOnlyVerification, unsigned);
    Require(File.ReadAllText(installed) == "unsigned new version", "unsigned new EXE was not installed");
    Require(File.ReadAllText(installed + ".previous") == "unsigned previous version",
        "unsigned previous EXE was not backed up");
    File.WriteAllText(payload, "changed unsigned payload");
    try
    {
        UpdateInstaller.ReplaceVerified(installed, payload, digest, UpdateInstaller.HashOnlyVerification, unsigned);
        throw new Exception("changed unsigned payload installed");
    }
    catch (CryptographicException) { }
    Require(File.ReadAllText(installed) == "unsigned new version", "failed unsigned update changed the installed EXE");
    return Task.CompletedTask;
});

await Check("replacement rejects a hash-valid different publisher", () =>
{
    var directory = Path.Combine(root, "replace-publisher");
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, "TogetherServer.exe");
    var payload = Path.Combine(directory, AppUpdater.AssetName);
    File.WriteAllText(installed, "trusted old version");
    File.WriteAllText(payload, "different publisher version");
    var verifier = new FakeAuthenticodeVerifier(path => File.ReadAllText(path).Contains("different", StringComparison.Ordinal)
        ? new(true, new string('B', 64), "different publisher")
        : new(true, publisherKey, "installed publisher"));
    try
    {
        UpdateInstaller.ReplaceVerified(installed, payload, Hash(File.ReadAllBytes(payload)), publisherKey, verifier);
        throw new Exception("different publisher installed");
    }
    catch (CryptographicException) { }
    Require(File.ReadAllText(installed) == "trusted old version", "publisher rejection changed installed EXE");
    return Task.CompletedTask;
});

await Check("post-replace verification restores the verified previous EXE", () =>
{
    var directory = Path.Combine(root, "replace-rollback");
    Directory.CreateDirectory(directory);
    var installed = Path.Combine(directory, "TogetherServer.exe");
    var payload = Path.Combine(directory, AppUpdater.AssetName);
    File.WriteAllText(installed, "verified previous version");
    File.WriteAllText(payload, "candidate version");
    var installedVerifications = 0;
    var verifier = new FakeAuthenticodeVerifier(path =>
    {
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase) &&
            ++installedVerifications == 2)
            return new(false, null, "post-replace verification failed");
        return new(true, publisherKey, "test publisher");
    });
    try
    {
        UpdateInstaller.ReplaceVerified(installed, payload, Hash(File.ReadAllBytes(payload)), publisherKey, verifier);
        throw new Exception("post-replace signature failure was accepted");
    }
    catch (CryptographicException) { }
    Require(File.ReadAllText(installed) == "verified previous version", "verified previous EXE was not restored");
    Require(Directory.GetFiles(directory, "*.rejected-*").Length == 0, "rejected replacement was left executable");
    Require(installedVerifications >= 3, "restored EXE was not verified before returning");
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

sealed class FakeAuthenticodeVerifier(Func<string, AuthenticodeVerification> verify) : IAuthenticodeVerifier
{
    public AuthenticodeVerification Verify(string path) => verify(path);
}
