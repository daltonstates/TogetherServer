using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record UpdateView(string State, string CurrentVersion, string? LatestVersion, string Message,
    string PublisherTrust = "Checking", string ReleaseNotes = "", string? ReleaseNotesUrl = null,
    DateTimeOffset? SnoozedUntilUtc = null, bool PromptSnoozed = false, bool VersionSkipped = false,
    UpdatePreparationView? Preparation = null);
public sealed record UpdateResult(bool Ok, string Code, string Message);
public sealed record UpdateRelease(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256,
    string ReleaseNotes = "", Uri? ReleaseNotesUrl = null);
public sealed record UpdatePreparationView(string Stage, string Message, long DownloadedBytes = 0,
    long? TotalBytes = null, string? Blocker = null);
public sealed record AuthenticodeVerification(bool Valid, string? PublisherKey, string Message, bool IsUnsigned = false);

public interface IAuthenticodeVerifier
{
    AuthenticodeVerification Verify(string path);
}

public sealed class AppUpdater(HttpClient client, string dataRoot, string executablePath, Version currentVersion,
    IAuthenticodeVerifier? authenticodeVerifier = null, bool enabled = true,
    string disabledMessage = "Automatic updates are disabled for this app instance.", UpdateUiPreferences? uiPreferences = null)
{
    public const string AssetName = "TogetherServer-win-x64.exe";
    public const long MaximumBytes = 200L * 1024 * 1024;
    public const int MaximumReleaseNotesCharacters = 4000;
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromMinutes(30);
    private const string LatestUrl = "https://api.github.com/repos/daltonstates/TogetherServer/releases/latest";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IAuthenticodeVerifier signatureVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier();
    private readonly UpdateUiPreferences preferences = uiPreferences ?? new UpdateUiPreferences();
    private volatile UpdateView view = enabled
        ? new("Checking", currentVersion.ToString(3), null, "Checking for updates.")
        : new("Unsupported", currentVersion.ToString(3), null, disabledMessage, "Development build");
    private UpdateRelease? available;
    private string? preparedPath;
    private string? publisherKey;
    private DateTimeOffset checkedUtc;
    private volatile UpdatePreparationView preparation = new("Idle", "An update starts only when you choose Update and restart.");

    public UpdateView View
    {
        get
        {
            var current = view;
            var notes = current.LatestVersion is { } version ? preferences.NotesFor(version) : null;
            var until = current.LatestVersion is { } target ? preferences.SnoozedUntil(target) : null;
            return current with
            {
                ReleaseNotes = notes?.Text ?? "",
                ReleaseNotesUrl = notes?.Url,
                SnoozedUntilUtc = until,
                PromptSnoozed = until is not null,
                VersionSkipped = current.LatestVersion is { } latest && preferences.IsVersionSkipped(latest),
                Preparation = preparation
            };
        }
    }
    public UpdatePreparationView Preparation => preparation;
    public void ReportPreparation(string stage, string message, string? blocker = null)
    {
        if (stage is not ("Idle" or "Checking" or "Checkpoint" or "Restarting" or "Blocked" or "Failed") ||
            message.Length > 600 || blocker?.Length > 600) throw new ArgumentException("Update preparation phase is invalid.");
        preparation = new(stage, message, preparation.DownloadedBytes, preparation.TotalBytes, blocker);
    }

    public async Task<UpdateResult> SnoozeAsync(UpdateSnoozeRequest request)
    {
        if (!enabled) return new(false, "UpdatesDisabled", disabledMessage);
        await gate.WaitAsync();
        try
        {
            if (available is null || request.Version != available.Version.ToString(3))
                return new(false, "VersionChanged", "Check the currently available version before changing its reminder.");
            var until = preferences.Snooze(request);
            return new(true, "ReminderSaved", request.Duration == "SkipVersion" ? "This version's reminder is skipped. A different release will be shown." :
                until is null ? "Update reminders are back on." :
                "Update reminders are snoozed for this version. You can still update at any time.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(false, "ReminderFailed", "Could not save the update reminder. Try again."); }
        finally { gate.Release(); }
    }

    private UpdateView SetView(UpdateView next) { view = next; return View; }
    public string? PreparedVersion => available is not null && preparedPath is not null && File.Exists(preparedPath)
        ? available.Version.ToString(3) : null;
    public bool IsStandalone => File.Exists(executablePath) &&
        Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.ChangeExtension(executablePath, ".deps.json"));

    public async Task<UpdateView> CheckAsync(bool force = false)
    {
        await gate.WaitAsync();
        try
        {
            if (!enabled) return View;
            if (!force && checkedUtc != default && DateTimeOffset.UtcNow - checkedUtc < AutomaticCheckInterval) return View;
            checkedUtc = DateTimeOffset.UtcNow;
            if (!IsStandalone)
                return SetView(new("Unsupported", currentVersion.ToString(3), null, "Updates apply to the published Windows EXE.",
                    "Development build"));
            var installedSignature = signatureVerifier.Verify(executablePath);
            var signed = installedSignature.Valid && !string.IsNullOrWhiteSpace(installedSignature.PublisherKey);
            if (!signed && !installedSignature.IsUnsigned)
            {
                available = null;
                preparedPath = null;
                publisherKey = null;
                return SetView(new("Unsupported", currentVersion.ToString(3), null,
                    "Automatic updates are disabled because Windows found an invalid or unverifiable signature on this EXE.",
                    "Signature rejected"));
            }
            var nextPublisherKey = signed
                ? installedSignature.PublisherKey
                : null;
            if (!string.Equals(publisherKey, nextPublisherKey, StringComparison.Ordinal)) preparedPath = null;
            publisherKey = nextPublisherKey;
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
                    return SetView(new("NoRelease", currentVersion.ToString(3), null, "No published TogetherServer release yet.",
                        signed ? "Verified publisher" : "GitHub digest only"));
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
                    return SetView(new("Unavailable", currentVersion.ToString(3), null,
                        "The latest release has no valid Windows EXE and SHA-256 digest.",
                        signed ? "Verified publisher" : "GitHub digest only"));
                preferences.RememberNotes(release);
                if (release.Version.CompareTo(currentVersion) <= 0)
                {
                    available = null;
                    preparedPath = null;
                    return SetView(new("Current", currentVersion.ToString(3), release.Version.ToString(3), "TogetherServer is up to date.",
                        signed ? "Verified publisher" : "GitHub digest only"));
                }
                if (available?.Tag != release.Tag || available.Sha256 != release.Sha256) preparedPath = null;
                available = release;
                return SetView(new("Available", currentVersion.ToString(3), release.Version.ToString(3),
                    $"TogetherServer {release.Version.ToString(3)} is available.",
                    signed ? "Verified publisher" : "GitHub digest only"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or
                                      InvalidDataException or UnauthorizedAccessException)
            {
                return SetView(new("Unavailable", currentVersion.ToString(3), available?.Version.ToString(3),
                    "Could not check GitHub Releases. Your current app keeps working.",
                    signed ? "Verified publisher" : "GitHub digest only"));
            }
        }
        finally { gate.Release(); }
    }

    public async Task<UpdateResult> PrepareAsync()
    {
        if (!enabled) return PreparationResult(false, "UpdatesDisabled", disabledMessage);
        preparation = new("Checking", "Checking the release and installed app trust before downloading.");
        await CheckAsync(true);
        await gate.WaitAsync();
        try
        {
            if (available is null || view.State != "Available")
                return PreparationResult(false, "NoUpdate", view.Message);
            if (preparedPath is not null && File.Exists(preparedPath) &&
                await HasHashAsync(preparedPath, available.Sha256) &&
                HasRequiredPublisher(preparedPath))
                return PreparationResult(true, "Ready", ReadyMessage());
            var directory = Path.Combine(dataRoot, "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, AssetName);
            var valid = false;
            try
            {
                preparation = new("Downloading", "Downloading the reviewed Windows update.", 0, available.Size);
                using var request = new HttpRequestMessage(HttpMethod.Get, available.DownloadUrl);
                request.Headers.UserAgent.ParseAdd("TogetherServer/" + currentVersion.ToString(3));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumBytes or < 1 ||
                    response.Content.Headers.ContentLength is { } length && length != available.Size)
                    return PreparationResult(false, "InvalidDownload", "Release size did not match GitHub's release record.");
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
                            return PreparationResult(false, "InvalidDownload", "Release download exceeded its declared size.");
                        await target.WriteAsync(buffer.AsMemory(0, read));
                        preparation = new("Downloading", "Downloading the reviewed Windows update.", total, available.Size);
                    }
                    await target.FlushAsync();
                }
                preparation = new("Verifying", "Verifying the download size, SHA-256, release version and required publisher.", total, available.Size);
                if (total != available.Size || !await HasHashAsync(path, available.Sha256))
                    return PreparationResult(false, "InvalidDownload", "Release download failed its size or SHA-256 check.");
                var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (!Version.TryParse(fileVersion, out var packagedVersion) ||
                    packagedVersion.Major != available.Version.Major ||
                    packagedVersion.Minor != available.Version.Minor ||
                    packagedVersion.Build != available.Version.Build)
                    return PreparationResult(false, "InvalidDownload", "The release EXE version does not match its tag.");
                if (!HasRequiredPublisher(path))
                    return PreparationResult(false, "InvalidSignature",
                        "The release EXE is not validly signed by the same publisher as this installed app.");
                preparedPath = path;
                valid = true;
                return PreparationResult(true, "Ready", ReadyMessage());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            { return PreparationResult(false, "DownloadFailed", "Could not download the update. Your current app keeps working."); }
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return PreparationResult(false, "PrepareFailed", "Could not prepare the update. Your current app keeps working."); }
        finally { gate.Release(); }
    }

    public UpdateResult StartReplacement() => StartReplacement(null);

    private UpdateResult PreparationResult(bool ok, string code, string message)
    {
        preparation = new(ok ? code == "Restarting" ? "Restarting" : "Ready" : "Failed", message,
            preparation.DownloadedBytes, preparation.TotalBytes, ok ? null : message);
        return new(ok, code, message);
    }

    internal UpdateResult StartReplacement(StateCheckpointReference? checkpoint)
    {
        if (!enabled) return PreparationResult(false, "UpdatesDisabled", disabledMessage);
        if (!IsStandalone || view.State != "Available" || available is null || preparedPath is null || !File.Exists(preparedPath))
            return PreparationResult(false, "NotReady", "No verified update is ready.");
        try
        {
            var verification = publisherKey ?? UpdateInstaller.HashOnlyVerification;
            var helper = Path.Combine(Path.GetDirectoryName(preparedPath)!, "TogetherServer-updater.exe");
            if (!HasHashAsync(preparedPath, available.Sha256).GetAwaiter().GetResult())
                return PreparationResult(false, "InvalidDownload", "The downloaded update changed before installation.");
            if (publisherKey is not null && (!HasMatchingPublisher(executablePath, publisherKey) ||
                !HasMatchingPublisher(preparedPath, publisherKey)))
                return PreparationResult(false, "InvalidSignature", "The installed app or downloaded update failed publisher verification.");
            var installedHash = HashAsync(executablePath).GetAwaiter().GetResult();
            if (checkpoint is not null && !StateCheckpointService.TryValidate(dataRoot,
                    checkpoint.Directory, checkpoint.ManifestSha256, out _, installedHash))
                return PreparationResult(false, "CheckpointInvalid",
                    "The verified local-state recovery checkpoint changed before update handoff.");
            File.Copy(preparedPath, helper, true);
            if (!HasHashAsync(helper, available.Sha256).GetAwaiter().GetResult())
                return PreparationResult(false, "InvalidDownload", "The updater copy failed its SHA-256 check.");
            if (publisherKey is not null && !HasMatchingPublisher(helper, publisherKey))
                return PreparationResult(false, "InvalidSignature", "The updater copy failed publisher verification.");
            var ready = Path.Combine(Path.GetDirectoryName(preparedPath)!, "ready.signal");
            if (File.Exists(ready)) File.Delete(ready);
            using var process = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--apply-update", process.Id.ToString(CultureInfo.InvariantCulture),
                         process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                         Path.GetFullPath(executablePath), Path.GetFullPath(preparedPath), available.Sha256,
                         Path.GetFullPath(dataRoot), ready, verification })
                start.ArgumentList.Add(argument);
            if (checkpoint is not null)
            {
                start.ArgumentList.Add(Path.GetFullPath(checkpoint.Directory));
                start.ArgumentList.Add(checkpoint.ManifestSha256);
            }
            using var launched = Process.Start(start);
            if (launched is null) return PreparationResult(false, "LaunchFailed", "Could not start the updater.");
            for (var attempt = 0; attempt < 100 && !File.Exists(ready) && !launched.HasExited; attempt++)
                Thread.Sleep(50);
            if (!File.Exists(ready) || launched.HasExited)
            {
                if (!launched.HasExited) launched.Kill();
                return PreparationResult(false, "LaunchFailed", "The update helper could not start safely. Your current app keeps running.");
            }
            return PreparationResult(true, "Restarting", "TogetherServer is closing to install the update.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return PreparationResult(false, "LaunchFailed", "Could not start the updater. Your current app keeps running."); }
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
            var notes = release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                ? NormalizeReleaseNotes(body.GetString() ?? "") : "";
            var notesUrl = release.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String &&
                ValidReleaseNotesLink(page.GetString(), tag) && Uri.TryCreate(page.GetString(), UriKind.Absolute, out var approvedPage)
                ? approvedPage : null;
            return new(version, tag, url, size, digest[7..].ToUpperInvariant(), notes, notesUrl);
        }
        return null;
    }

    internal static string NormalizeReleaseNotes(string value) => new(value
        .Where(character => character is '\n' or '\t' || !char.IsControl(character))
        .Take(MaximumReleaseNotesCharacters).ToArray());

    public static bool ValidReleaseNotesLink(string? value, string tag) => value is null ||
        (Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) &&
         string.Equals(value, $"https://github.com/daltonstates/TogetherServer/releases/tag/{tag}", StringComparison.Ordinal));

    public static async Task<bool> HasHashAsync(string path, string expected)
    {
        var actual = await HashAsync(path);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> HashAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    private bool HasRequiredPublisher(string path) => publisherKey is null || HasMatchingPublisher(path, publisherKey);

    private string ReadyMessage() => publisherKey is null
        ? "Update downloaded and verified against GitHub's SHA-256 digest."
        : "Update downloaded and verified against GitHub's SHA-256 digest and the installed publisher.";

    private bool HasMatchingPublisher(string path, string? expectedPublisherKey)
    {
        if (string.IsNullOrWhiteSpace(expectedPublisherKey)) return false;
        var signature = signatureVerifier.Verify(path);
        if (!signature.Valid || string.IsNullOrWhiteSpace(signature.PublisherKey)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(signature.PublisherKey), Convert.FromHexString(expectedPublisherKey));
        }
        catch (FormatException) { return false; }
    }
}

public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChain = 0x40;

    public AuthenticodeVerification Verify(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, null, "Authenticode verification requires Windows.");
        if (!File.Exists(path)) return new(false, null, "The executable does not exist.");
        try
        {
            var trustStatus = VerifyTrust(path);
            if (trustStatus != 0)
                return trustStatus == TrustENoSignature
                    ? new(false, null, "The executable is not Authenticode-signed.", IsUnsigned: true)
                    : new(false, null, $"Windows rejected the Authenticode signature (0x{trustStatus:X8}).");
#pragma warning disable SYSLIB0057 // The BCL has no loader replacement for extracting an Authenticode signer from a PE file.
            using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signedCertificate.GetRawCertData());
            var publisherKey = Convert.ToHexString(SHA256.HashData(certificate.GetPublicKey()));
            return new(true, publisherKey, "The Authenticode signature is valid.");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new(false, null, "Could not verify the Authenticode signature: " + ex.Message);
        }
    }

    private static int VerifyTrust(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        var data = new WinTrustData();
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeWholeChain,
                UnionChoice = ChoiceFile,
                File = fileInfoPointer,
                StateAction = StateActionVerify,
                ProviderFlags = RevocationCheckChain
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            if (data.StateData != IntPtr.Zero)
            {
                data.StateAction = StateActionClose;
                var action = GenericVerifyV2;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
