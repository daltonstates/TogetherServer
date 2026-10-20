using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic state and fake HTTP only. Payloads are inspected as bytes and are never executed.
internal static class PrereleaseUpdateChecks
{
    private const string LatestUrl = "https://api.github.com/repos/daltonstates/TogetherServer/releases/latest";
    private const string ReleasesUrl = "https://api.github.com/repos/daltonstates/TogetherServer/releases?per_page=100";

    internal static void RunPreferences(string root)
    {
        var directory = Path.Combine(root, "prerelease-preferences");
        using (var data = new LocalData(directory))
        {
            var preferences = new UpdateUiPreferences(data);
            Require(!preferences.IncludePrereleases, "New PCs must stay on stable releases by default");
            preferences.Snooze(new("0.4.0", "Tomorrow"));
            preferences.SetIncludePrereleases(true);
            preferences.RememberNotes(new(new Version(0, 4, 0), "v0.4.0", new Uri("https://example.test/unused"), 1,
                new string('A', 64), "Synthetic notes"));
        }
        using (var data = new LocalData(directory))
        {
            var restarted = new UpdateUiPreferences(data);
            Require(restarted.IncludePrereleases && restarted.SnoozedUntil("0.4.0") is not null &&
                restarted.NotesFor("0.4.0")?.Text == "Synthetic notes", "Enrollment, notes and reminders must survive restart together");
            restarted.Snooze(new("0.4.0", "Clear"));
            Require(new UpdateUiPreferences(data).IncludePrereleases, "Reminder changes must preserve enrollment");
            restarted.SetIncludePrereleases(false);
            Require(!new UpdateUiPreferences(data).IncludePrereleases && restarted.NotesFor("0.4.0") is not null,
                "Opting out must persist without losing release notes");
        }
        using (var otherData = new LocalData(Path.Combine(root, "other-prerelease-device")))
            Require(!new UpdateUiPreferences(otherData).IncludePrereleases, "One PC's enrollment must not enroll another PC");
        var legacy = JsonSerializer.Deserialize<UpdateUiPreferences.State>("{\"Snoozes\":[],\"Notes\":null}");
        Require(!new UpdateUiPreferences(legacy).IncludePrereleases, "Existing state without enrollment must remain stable");
        var failure = new UpdateUiPreferences(new UpdateUiPreferences.State { IncludePrereleases = true }, _ => throw new IOException("Synthetic save failure"));
        Reject<IOException>(() => failure.SetIncludePrereleases(false));
        Require(failure.IncludePrereleases, "A failed save must preserve the canonical choice");
        foreach (var choice in new[] { true, false })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { includePrereleases = choice }));
            Require(UpdateUiPreferences.ParseIncludePrereleases(json.RootElement) == choice, "The exact checkbox value must be accepted");
        }
        foreach (var body in new[] { "null", "[]", "{}", "{\"includePrereleases\":null}",
            "{\"includePrereleases\":\"true\"}", "{\"includePrereleases\":1}", "{\"IncludePrereleases\":true}",
            "{\"includePrereleases\":true,\"includePrereleases\":false}",
            "{\"includePrereleases\":true,\"url\":\"https://example.test/update\"}",
            "{\"includePrereleases\":true,\"install\":true}" })
        {
            using var json = JsonDocument.Parse(body);
            Reject<InvalidDataException>(() => UpdateUiPreferences.ParseIncludePrereleases(json.RootElement));
        }
        foreach (var version in new[] { "0.3.2.1", "0.3.2.0" })
        {
            var versionDirectory = Path.Combine(root, "numeric-preferences-" + version);
            using (var data = new LocalData(versionDirectory))
            {
                var preferences = new UpdateUiPreferences(data);
                preferences.SetIncludePrereleases(true);
                preferences.Snooze(new("0.3.2", "SkipVersion"));
                preferences.Snooze(new(version, "Tomorrow"));
                using var reminder = JsonDocument.Parse(JsonSerializer.Serialize(new { version, duration = "Tomorrow" }));
                Require(UpdateUiPreferences.ParseSnooze(reminder.RootElement).Version == version,
                    "A four-part reminder request must preserve its exact release identity");
                preferences.RememberNotes(AppUpdater.ParseRelease(Metadata(version, preview: true), true)!);
            }
            using (var data = new LocalData(versionDirectory))
            {
                var restarted = new UpdateUiPreferences(data);
                Require(restarted.IncludePrereleases && restarted.SnoozedUntil(version) is not null &&
                    restarted.NotesFor(version)?.Url == NotesUrl(version) && restarted.IsVersionSkipped("0.3.2") &&
                    !restarted.IsVersionSkipped(version),
                    "Four-part notes and reminders must persist independently from the preceding stable release");
            }
        }
        foreach (var version in new[] { "0.3", "0.3.2.1.1", "0.3.2-rc.1", "0.3.2.1+build", "0.3.2.1\n" })
            Reject<InvalidDataException>(() => new UpdateUiPreferences().Snooze(new(version, "Tomorrow")));
    }

    internal static async Task RunSelectionAsync(string root)
    {
        var stable = Metadata("0.4.0");
        var listing = "[" + string.Join(',',
            Metadata("9.0.0", preview: true, draft: true), Metadata("0.4.9", preview: true),
            Metadata("0.5.0"), Metadata("0.3.99", preview: true),
            Metadata("10.0.0", preview: true, url: "https://example.test/untrusted.exe")) + "]";
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            return Json(url == LatestUrl ? stable : url == ReleasesUrl ? listing : throw new Exception("Unexpected update URL"));
        }));
        var preferences = new UpdateUiPreferences();
        var updater = Updater(root, "selection", client, preferences);
        var normal = await updater.CheckAsync(true);
        Require(normal.LatestVersion == "0.4.0" && !normal.IncludePrereleases && !normal.LatestIsPrerelease &&
            requests.SequenceEqual([LatestUrl]), "Default checks must use only the stable latest feed");
        var enrolled = await SetAndCheckAsync(updater, true);
        Require(enrolled.LatestVersion == "0.5.0" && enrolled.IncludePrereleases && !enrolled.LatestIsPrerelease &&
            requests[^1] == ReleasesUrl, "Preview PCs must also receive a newer stable release, regardless of feed order");
        listing = "[" + Metadata("0.5.1", preview: true) + "," + stable + "]";
        var preview = await updater.CheckAsync(true);
        Require(preview.LatestVersion == "0.5.1" && preview.LatestIsPrerelease && preview.Message.Contains("prerelease"),
            "Newer eligible prereleases must be offered and visibly identified");
        stable = Metadata("0.5.1");
        listing = "[" + stable + "]";
        var promoted = await updater.CheckAsync(true);
        Require(promoted.LatestVersion == "0.5.1" && !promoted.LatestIsPrerelease,
            "Promotion must retain the same version and refresh its stable designation");
        var optedOut = await SetAndCheckAsync(updater, false);
        Require(!optedOut.IncludePrereleases && !optedOut.LatestIsPrerelease && requests[^1] == LatestUrl,
            "Opting out must immediately return to stable metadata");
        var newerInstallation = Updater(root, "no-downgrade", client, new(new UpdateUiPreferences.State { IncludePrereleases = true }), new Version(0, 6, 0));
        Require((await newerInstallation.CheckAsync(true)).State == "Current" && newerInstallation.PreparedVersion is null,
            "Neither feed may offer a downgrade to an installed preview");
        Require(!Directory.Exists(Path.Combine(root, "selection", "updates")), "Checking or enrolling must not download or install an update");

        Require(AppUpdater.FormatVersion(new Version(0, 3, 2, 0)) == "0.3.2" &&
            AppUpdater.FormatVersion(new Version(0, 3, 2, 1)) == "0.3.2.1",
            "Installed version display must retain a nonzero revision and preserve stable display identities");
        Require(CompanionProtocol.AppVersion == AppUpdater.FormatVersion(typeof(CompanionProtocol).Assembly.GetName().Version!) &&
            DesktopWindow.IsApprovedExternal(NotesUrl("0.3.2.1")) &&
            !DesktopWindow.IsApprovedExternal(NotesUrl("0.3.2.1") + "?command=start"),
            "Companion display and approved notes navigation must retain the numeric revision without widening trusted links");
        stable = Metadata("0.3.2");
        listing = "[" + string.Join(',', Metadata("0.3.2.1", preview: true), stable,
            Metadata("0.3.2.2", preview: true), Metadata("0.3.2.0")) + "]";
        var revisionPreferences = new UpdateUiPreferences(new UpdateUiPreferences.State { IncludePrereleases = true });
        var revisionUpdater = Updater(root, "numeric-selection", client, revisionPreferences, new Version(0, 3, 2, 1));
        var nextRevision = await revisionUpdater.CheckAsync(true);
        Require(nextRevision.State == "Available" && nextRevision.CurrentVersion == "0.3.2.1" &&
            nextRevision.LatestVersion == "0.3.2.2" && nextRevision.LatestIsPrerelease &&
            nextRevision.ReleaseNotesUrl == NotesUrl("0.3.2.2"),
            "The next numeric revision must retain its display, ordering, preview label and notes URL");
        Require((await revisionUpdater.SnoozeAsync(new("0.3.2", "SkipVersion"))).Code == "VersionChanged" &&
            (await revisionUpdater.SnoozeAsync(new("0.3.2.2", "SkipVersion"))).Ok &&
            revisionUpdater.View.VersionSkipped && !revisionPreferences.IsVersionSkipped("0.3.2.1"),
            "Only the exact available revision may change its reminder");
        listing = "[" + Metadata("0.3.2.1", preview: true) + "]";
        Require((await revisionUpdater.CheckAsync(true)).State == "Current",
            "An installed numeric preview must not offer itself again");
        var stableOnly = await SetAndCheckAsync(revisionUpdater, false);
        Require(stableOnly.State == "Current" && stableOnly.CurrentVersion == "0.3.2.1" &&
            stableOnly.LatestVersion == "0.3.2" && revisionUpdater.PreparedVersion is null,
            "Opting out of a four-part preview must not offer a stable downgrade");
        stable = Metadata("0.3.3");
        listing = "[" + Metadata("0.3.2.2", preview: true) + "," + stable + "]";
        foreach (var includePreviews in new[] { false, true })
        {
            var stableTransition = Updater(root, "stable-transition-" + includePreviews, client,
                new(new UpdateUiPreferences.State { IncludePrereleases = includePreviews }), new Version(0, 3, 2, 2));
            var available = await stableTransition.CheckAsync(true);
            Require(available.State == "Available" && available.CurrentVersion == "0.3.2.2" &&
                available.LatestVersion == "0.3.3" && !available.LatestIsPrerelease &&
                available.ReleaseNotesUrl == NotesUrl("0.3.3") && stableTransition.PreparedVersion is null,
                "Both update channels must offer stable 0.3.3 after preview 0.3.2.2 without preparing an unattended install");
        }
    }

    internal static async Task RunTrustAsync(string root)
    {
        var preview = Metadata("0.5.0", preview: true);
        Require(AppUpdater.ParseRelease(preview) is null && AppUpdater.ParseRelease(preview, true)?.IsPrerelease == true,
            "Prerelease parsing must remain an explicit opt-in");
        foreach (var metadata in new[] { Metadata("0.5.0", preview: true, draft: true),
            Metadata("0.5.0", preview: true, url: "https://example.test/untrusted.exe"),
            Metadata("0.5.0", preview: true, digest: "sha256:bad"),
            Metadata("0.5.0-rc.1", preview: true),
            Metadata("0.5.0.1.1", preview: true),
            Metadata("0.5.0.1-rc.1", preview: true),
            Metadata("0.5.0.1\n", preview: true),
            Metadata("0.5.0", preview: true).Replace("\"prerelease\":true", "\"prerelease\":null", StringComparison.Ordinal) })
            Require(AppUpdater.ParseRelease(metadata, true) is null, "Opt-in must retain the release trust and numeric tag constraints");
        Require(AppUpdater.ParseReleases("[]") is null && AppUpdater.ParseReleases("[null,{}," + preview + "]")?.IsPrerelease == true,
            "Empty or unusable entries must not become trusted releases");
        Reject<InvalidDataException>(() => AppUpdater.ParseReleases("{}"));
        Reject<InvalidDataException>(() => AppUpdater.ParseReleases("[" + string.Join(',', Enumerable.Repeat("{}", 101)) + "]"));

        // A built managed PE supplies version resources for download verification, without starting any executable.
        var payloadPath = typeof(AppUpdater).Assembly.Location;
        var payload = await File.ReadAllBytesAsync(payloadPath);
        var packagedVersion = Version.Parse(FileVersionInfo.GetVersionInfo(payloadPath).FileVersion!);
        var payloadVersion = AppUpdater.FormatVersion(packagedVersion);
        var valid = Metadata(payloadVersion, preview: true, payload: payload);
        var listing = "[" + valid + "]";
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? Json(request.RequestUri.AbsoluteUri == LatestUrl ? Metadata("0.0.0") : listing)
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var updater = Updater(root, "prepared-opt-out", client, new(new UpdateUiPreferences.State { IncludePrereleases = true }));
        Require((await updater.PrepareAsync()).Ok && updater.PreparedVersion == payloadVersion &&
            updater.View.LatestIsPrerelease, "A valid opted-in preview must pass normal download verification");
        var installed = Path.Combine(root, "prepared-opt-out", "TogetherServer.exe");
        var before = await File.ReadAllBytesAsync(installed);
        await SetAndCheckAsync(updater, false);
        var after = await File.ReadAllBytesAsync(installed);
        Require(updater.PreparedVersion is null && updater.Preparation.Stage == "Idle" && !updater.View.LatestIsPrerelease &&
            before.SequenceEqual(after), "Opting out must invalidate a prepared preview without replacing the app");

        listing = "[" + Metadata(payloadVersion, preview: true, payload: payload, digest: "sha256:" + new string('0', 64)) + "]";
        var tampered = Updater(root, "preview-tampered", client, new(new UpdateUiPreferences.State { IncludePrereleases = true }));
        Require((await tampered.PrepareAsync()).Code == "InvalidDownload" && tampered.PreparedVersion is null,
            "Opt-in must not weaken payload digest verification");
        var wrongRevision = new Version(packagedVersion.Major, packagedVersion.Minor, packagedVersion.Build,
            Math.Max(0, packagedVersion.Revision) + 1).ToString(4);
        listing = "[" + Metadata(wrongRevision, preview: true, payload: payload) + "]";
        var mismatchedRevision = Updater(root, "preview-wrong-revision", client,
            new(new UpdateUiPreferences.State { IncludePrereleases = true }));
        Require((await mismatchedRevision.PrepareAsync()).Code == "InvalidDownload" && mismatchedRevision.PreparedVersion is null,
            "Matching major/minor/patch and digest must not accept a different packaged revision");
        listing = "[" + Metadata(packagedVersion.ToString(4), preview: true, payload: payload) + "]";
        var exactRevision = Updater(root, "preview-exact-revision", client,
            new(new UpdateUiPreferences.State { IncludePrereleases = true }));
        Require((await exactRevision.PrepareAsync()).Ok && exactRevision.PreparedVersion == packagedVersion.ToString(4),
            "Download verification and prepared handoff must retain the exact four-part release identity");
        listing = "[" + valid + "]";
        var signed = Updater(root, "preview-publisher", client, new(new UpdateUiPreferences.State { IncludePrereleases = true }),
            verifier: new Verifier(path => Path.GetFileName(path) == AppUpdater.AssetName
                ? new(true, new string('B', 64), "Other synthetic publisher")
                : new(true, new string('A', 64), "Installed synthetic publisher")));
        Require((await signed.PrepareAsync()).Code == "InvalidSignature" && signed.PreparedVersion is null,
            "Opt-in must retain the installed publisher requirement");
        using var disabledClient = new HttpClient(new Handler(_ => throw new Exception("Disabled updater contacted GitHub")));
        var disabled = Updater(root, "preview-disabled", disabledClient, new(new UpdateUiPreferences.State { IncludePrereleases = true }), enabled: false);
        Require((await disabled.SetIncludePrereleasesAsync(false)).Code == "UpdatesDisabled" && disabled.View.State == "Unsupported" &&
            (await disabled.PrepareAsync()).Code == "UpdatesDisabled", "Staging must remain offline from release updates");
        using var failedClient = new HttpClient(new Handler(_ => throw new HttpRequestException("Synthetic metadata failure")));
        var failedPreferences = new UpdateUiPreferences();
        var failed = Updater(root, "preview-offline", failedClient, failedPreferences);
        Require((await SetAndCheckAsync(failed, true)).State == "Unavailable" && failed.View.IncludePrereleases &&
            failed.PreparedVersion is null, "A saved opt-in must survive a failed check without leaving an installable candidate");
        Require(await AppUpdater.HasHashAsync(Directory.GetFiles(Path.Combine(root, "prepared-opt-out", "updates"),
            AppUpdater.AssetName, SearchOption.AllDirectories).Single(), digest),
            "Opting out must not mutate the previously verified payload bytes");
        await CheckBusyAndFailedRefreshAsync(root);
        CheckNumericCheckpoints(root);
    }

    private static void CheckNumericCheckpoints(string root)
    {
        using var data = new LocalData(Path.Combine(root, "numeric-update-checkpoints"));
        var executable = Path.Combine(data.RootPath, "TogetherServer.exe");
        File.WriteAllText(executable, "Synthetic checkpoint source; never executed");
        var executableHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
        var service = new StateCheckpointService(data, TimeProvider.System);
        foreach (var (current, target) in new[] { ("0.3.2", "0.3.2.1"), ("0.3.2.1", "0.3.2.2"),
            ("0.3.2", "0.3.3"), ("0.3.2.2", "0.3.3") })
        {
            var result = service.Create(current, target, executable);
            Require(result.Ok && result.Checkpoint is not null, "A forward numeric revision must create a recovery checkpoint");
            var checkpoint = result.Checkpoint!;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(checkpoint.Directory, "checkpoint-manifest.json")));
            Require(manifest.RootElement.GetProperty("currentVersion").GetString() == current &&
                manifest.RootElement.GetProperty("targetVersion").GetString() == target &&
                StateCheckpointService.TryValidate(data.RootPath, checkpoint.Directory, checkpoint.ManifestSha256,
                    out _, executableHash, allowSupportedSourceSchema: true),
                "The helper must validate the full forward revision and exact previous-EXE checkpoint binding");
        }
        foreach (var target in new[] { "0.3.2", "0.3.2.0", "0.3.2.1" })
        {
            var result = service.Create("0.3.2.1", target, executable);
            Require(result.Ok && result.Checkpoint is not null &&
                !StateCheckpointService.TryValidate(data.RootPath, result.Checkpoint.Directory, result.Checkpoint.ManifestSha256,
                    out _, executableHash, allowSupportedSourceSchema: true),
                "A checkpoint must not let the helper repeat or downgrade an installed numeric revision");
        }
    }

    private static async Task CheckBusyAndFailedRefreshAsync(string root)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new HttpClient(new AsyncHandler(_ =>
        {
            if (++requests > 1) throw new HttpRequestException("Synthetic refresh failure");
            entered.TrySetResult();
            return response.Task;
        }));
        var preferences = new UpdateUiPreferences(new UpdateUiPreferences.State { IncludePrereleases = true });
        var updater = Updater(root, "preview-busy", client, preferences);
        var check = updater.CheckAsync(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await updater.SetIncludePrereleasesAsync(false).WaitAsync(TimeSpan.FromSeconds(2));
        Require(rejected.Code == "UpdateBusy" && preferences.IncludePrereleases,
            "A slow metadata request must not hold a preference change or mutate its saved value");
        response.SetResult(Json("[" + Metadata("0.5.0", preview: true) + "]"));
        Require((await check).LatestIsPrerelease, "The pending preview check must complete normally");
        Require((await updater.CheckAsync(true)).State == "Unavailable" && updater.View.LatestVersion == "0.5.0" &&
            updater.View.LatestIsPrerelease, "A failed refresh must preserve the last known release classification");
    }

    private static async Task<UpdateView> SetAndCheckAsync(AppUpdater updater, bool includePrereleases)
    {
        Require((await updater.SetIncludePrereleasesAsync(includePrereleases)).Ok, "The preference change must be accepted before checking");
        return await updater.CheckAsync(true);
    }

    private static AppUpdater Updater(string root, string name, HttpClient client, UpdateUiPreferences preferences,
        Version? currentVersion = null, IAuthenticodeVerifier? verifier = null, bool enabled = true)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "TogetherServer.exe");
        File.WriteAllText(executable, "Synthetic installed file; never executed");
        return new(client, directory, executable, currentVersion ?? new Version(0, 0, 0),
            verifier ?? new Verifier(_ => new(false, null, "Synthetic unsigned file", IsUnsigned: true)), enabled: enabled,
            uiPreferences: preferences);
    }

    private static string Metadata(string version, bool preview = false, bool draft = false, string? url = null,
        string? digest = null, byte[]? payload = null) => JsonSerializer.Serialize(new
        {
            tag_name = "v" + version,
            draft,
            prerelease = preview,
            body = "Synthetic release notes",
            html_url = NotesUrl(version),
            assets = new[] { new { name = AppUpdater.AssetName, state = "uploaded", size = payload?.Length ?? 4,
                digest = digest ?? "sha256:" + (payload is null ? new string('A', 64) : Convert.ToHexString(SHA256.HashData(payload))),
                browser_download_url = url ?? $"https://github.com/daltonstates/TogetherServer/releases/download/v{version}/{AppUpdater.AssetName}" } }
        });
    private static string NotesUrl(string version) => $"https://github.com/daltonstates/TogetherServer/releases/tag/v{version}";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    private sealed class Verifier(Func<string, AuthenticodeVerification> verify) : IAuthenticodeVerifier
    {
        public AuthenticodeVerification Verify(string path) => verify(path);
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("An unsafe or ambiguous update input was accepted");
    }
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
