using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record UpdateView(string State, string CurrentVersion, string? LatestVersion, string Message);
public sealed record UpdateResult(bool Ok, string Code, string Message);
public sealed record UpdateRelease(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256);

public sealed class AppUpdater(HttpClient client, string dataRoot, string executablePath, Version currentVersion)
{
    public const string AssetName = "TogetherServer-win-x64.exe";
    public const long MaximumBytes = 200L * 1024 * 1024;
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromMinutes(30);
    private const string LatestUrl = "https://api.github.com/repos/daltonstates/TogetherServer/releases/latest";
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateView view = new("Checking", currentVersion.ToString(3), null, "Checking for updates.");
    private UpdateRelease? available;
    private string? preparedPath;
    private DateTimeOffset checkedUtc;

    public UpdateView View => view;
    public bool IsStandalone => File.Exists(executablePath) &&
        Path.GetFileName(executablePath).Equals("TogetherServer.exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.ChangeExtension(executablePath, ".deps.json"));

    public async Task<UpdateView> CheckAsync(bool force = false)
    {
        await gate.WaitAsync();
        try
        {
            if (!force && checkedUtc != default && DateTimeOffset.UtcNow - checkedUtc < AutomaticCheckInterval) return view;
            checkedUtc = DateTimeOffset.UtcNow;
            if (!IsStandalone)
                return view = new("Unsupported", currentVersion.ToString(3), null, "Updates apply to the published Windows EXE.");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
                request.Headers.UserAgent.ParseAdd("TogetherServer/" + currentVersion.ToString(3));
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    available = null;
                    preparedPath = null;
                    return view = new("NoRelease", currentVersion.ToString(3), null, "No published TogetherServer release yet.");
                }
                response.EnsureSuccessStatusCode();
                await using var releaseStream = await response.Content.ReadAsStreamAsync();
                using var releaseBytes = new MemoryStream();
                var chunk = new byte[32 * 1024];
                int length;
                while ((length = await releaseStream.ReadAsync(chunk)) != 0)
                {
                    if (releaseBytes.Length + length > 1024 * 1024) throw new InvalidDataException("Release response was too large.");
                    releaseBytes.Write(chunk, 0, length);
                }
                var json = Encoding.UTF8.GetString(releaseBytes.ToArray());
                var release = ParseRelease(json);
                if (release is null)
                    return view = new("Unavailable", currentVersion.ToString(3), null,
                        "The latest release has no valid Windows EXE and SHA-256 digest.");
                if (release.Version.CompareTo(currentVersion) <= 0)
                {
                    available = null;
                    preparedPath = null;
                    return view = new("Current", currentVersion.ToString(3), release.Version.ToString(3), "TogetherServer is up to date.");
                }
                if (available?.Tag != release.Tag || available.Sha256 != release.Sha256) preparedPath = null;
                available = release;
                return view = new("Available", currentVersion.ToString(3), release.Version.ToString(3),
                    $"TogetherServer {release.Version.ToString(3)} is available.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                return view = new("Unavailable", currentVersion.ToString(3), null,
                    "Could not check GitHub Releases. Your current app keeps working.");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<UpdateResult> PrepareAsync()
    {
        await CheckAsync(true);
        await gate.WaitAsync();
        try
        {
            if (available is null || view.State != "Available")
                return new(false, "NoUpdate", view.Message);
            if (preparedPath is not null && File.Exists(preparedPath) &&
                await HasHashAsync(preparedPath, available.Sha256))
                return new(true, "Ready", "Update downloaded and verified.");
            var directory = Path.Combine(dataRoot, "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, AssetName);
            var valid = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, available.DownloadUrl);
                request.Headers.UserAgent.ParseAdd("TogetherServer/" + currentVersion.ToString(3));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumBytes or < 1 ||
                    response.Content.Headers.ContentLength is { } length && length != available.Size)
                    return new(false, "InvalidDownload", "Release size did not match GitHub's release record.");
                long total = 0;
                await using (var source = await response.Content.ReadAsStreamAsync())
                await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await source.ReadAsync(buffer)) != 0)
                    {
                        total += read;
                        if (total > MaximumBytes || total > available.Size)
                            return new(false, "InvalidDownload", "Release download exceeded its declared size.");
                        await target.WriteAsync(buffer.AsMemory(0, read));
                    }
                    await target.FlushAsync();
                }
                if (total != available.Size || !await HasHashAsync(path, available.Sha256))
                    return new(false, "InvalidDownload", "Release download failed its size or SHA-256 check.");
                var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (!Version.TryParse(fileVersion, out var packagedVersion) ||
                    packagedVersion.Major != available.Version.Major ||
                    packagedVersion.Minor != available.Version.Minor ||
                    packagedVersion.Build != available.Version.Build)
                    return new(false, "InvalidDownload", "The release EXE version does not match its tag.");
                preparedPath = path;
                valid = true;
                return new(true, "Ready", "Update downloaded and verified.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            { return new(false, "DownloadFailed", "Could not download the update. Your current app keeps working."); }
            finally
            {
                if (!valid)
                {
                    try { Directory.Delete(directory, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { /* A failed temporary file can be removed later. */ }
                }
            }
        }
        finally { gate.Release(); }
    }

    public UpdateResult StartReplacement()
    {
        if (!IsStandalone || view.State != "Available" || available is null || preparedPath is null || !File.Exists(preparedPath))
            return new(false, "NotReady", "No verified update is ready.");
        try
        {
            var helper = Path.Combine(Path.GetDirectoryName(preparedPath)!, "TogetherServer-updater.exe");
            if (!HasHashAsync(preparedPath, available.Sha256).GetAwaiter().GetResult())
                return new(false, "InvalidDownload", "The downloaded update changed before installation.");
            File.Copy(preparedPath, helper, true);
            if (!HasHashAsync(helper, available.Sha256).GetAwaiter().GetResult())
                return new(false, "InvalidDownload", "The updater copy failed its SHA-256 check.");
            var ready = Path.Combine(Path.GetDirectoryName(preparedPath)!, "ready.signal");
            if (File.Exists(ready)) File.Delete(ready);
            using var process = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--apply-update", process.Id.ToString(CultureInfo.InvariantCulture),
                         process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                         Path.GetFullPath(executablePath), Path.GetFullPath(preparedPath), available.Sha256,
                         Path.GetFullPath(dataRoot), ready })
                start.ArgumentList.Add(argument);
            using var launched = Process.Start(start);
            if (launched is null) return new(false, "LaunchFailed", "Could not start the updater.");
            for (var attempt = 0; attempt < 100 && !File.Exists(ready) && !launched.HasExited; attempt++)
                Thread.Sleep(50);
            if (!File.Exists(ready) || launched.HasExited)
            {
                if (!launched.HasExited) launched.Kill();
                return new(false, "LaunchFailed", "The update helper could not start safely. Your current app keeps running.");
            }
            return new(true, "Restarting", "TogetherServer is closing to install the update.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(false, "LaunchFailed", "Could not start the updater: " + ex.Message); }
    }

    public static UpdateRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var tag = tagValue.GetString()!;
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag[1..], out var version)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != AssetName ||
                !asset.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded" ||
                !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetInt64(out var size) ||
                size is < 1 or > MaximumBytes ||
                !asset.TryGetProperty("digest", out var digestValue) || digestValue.ValueKind != JsonValueKind.String ||
                !asset.TryGetProperty("browser_download_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String) continue;
            var digest = digestValue.GetString()!;
            var expectedUrl = $"https://github.com/daltonstates/TogetherServer/releases/download/{tag}/{AssetName}";
            if (!Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
                !string.Equals(urlValue.GetString(), expectedUrl, StringComparison.Ordinal) ||
                !Uri.TryCreate(expectedUrl, UriKind.Absolute, out var url)) continue;
            return new(version, tag, url, size, digest[7..].ToUpperInvariant());
        }
        return null;
    }

    public static async Task<bool> HasHashAsync(string path, string expected)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
