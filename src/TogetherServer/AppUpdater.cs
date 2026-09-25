using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record UpdateView(string State, string CurrentVersion, string? LatestVersion, string Message);
public sealed record UpdateResult(bool Ok, string Code, string Message);
public sealed record UpdateRelease(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256);
public sealed record AuthenticodeVerification(bool Valid, string? PublisherKey, string Message);

public interface IAuthenticodeVerifier
{
    AuthenticodeVerification Verify(string path);
}

public sealed class AppUpdater(HttpClient client, string dataRoot, string executablePath, Version currentVersion,
    IAuthenticodeVerifier? authenticodeVerifier = null, bool enabled = true,
    string disabledMessage = "Automatic updates are disabled for this app instance.")
{
    public const string AssetName = "TogetherServer-win-x64.exe";
    public const long MaximumBytes = 200L * 1024 * 1024;
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromMinutes(30);
    private const string LatestUrl = "https://api.github.com/repos/daltonstates/TogetherServer/releases/latest";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IAuthenticodeVerifier signatureVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier();
    private UpdateView view = enabled
        ? new("Checking", currentVersion.ToString(3), null, "Checking for updates.")
        : new("Unsupported", currentVersion.ToString(3), null, disabledMessage);
    private UpdateRelease? available;
    private string? preparedPath;
    private string? publisherKey;
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
            if (!enabled) return view;
            if (!force && checkedUtc != default && DateTimeOffset.UtcNow - checkedUtc < AutomaticCheckInterval) return view;
            checkedUtc = DateTimeOffset.UtcNow;
            if (!IsStandalone)
                return view = new("Unsupported", currentVersion.ToString(3), null, "Updates apply to the published Windows EXE.");
            var installedSignature = signatureVerifier.Verify(executablePath);
            if (!installedSignature.Valid || string.IsNullOrWhiteSpace(installedSignature.PublisherKey))
            {
                available = null;
                preparedPath = null;
                publisherKey = null;
                return view = new("Unsupported", currentVersion.ToString(3), null,
                    "Automatic updates are disabled because this installed EXE does not have a valid Authenticode signature.");
            }
            publisherKey = installedSignature.PublisherKey;
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
        if (!enabled) return new(false, "UpdatesDisabled", disabledMessage);
        await CheckAsync(true);
        await gate.WaitAsync();
        try
        {
            if (available is null || view.State != "Available")
                return new(false, "NoUpdate", view.Message);
            if (preparedPath is not null && File.Exists(preparedPath) &&
                await HasHashAsync(preparedPath, available.Sha256) &&
                HasMatchingPublisher(preparedPath, publisherKey))
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
                if (!HasMatchingPublisher(path, publisherKey))
                    return new(false, "InvalidSignature",
                        "The release EXE is not validly signed by the same publisher as this installed app.");
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
        if (!enabled) return new(false, "UpdatesDisabled", disabledMessage);
        if (!IsStandalone || view.State != "Available" || available is null || preparedPath is null || !File.Exists(preparedPath))
            return new(false, "NotReady", "No verified update is ready.");
        try
        {
            var expectedPublisherKey = publisherKey!;
            var helper = Path.Combine(Path.GetDirectoryName(preparedPath)!, "TogetherServer-updater.exe");
            if (!HasHashAsync(preparedPath, available.Sha256).GetAwaiter().GetResult())
                return new(false, "InvalidDownload", "The downloaded update changed before installation.");
            if (!HasMatchingPublisher(executablePath, expectedPublisherKey) ||
                !HasMatchingPublisher(preparedPath, expectedPublisherKey))
                return new(false, "InvalidSignature", "The installed app or downloaded update failed publisher verification.");
            File.Copy(preparedPath, helper, true);
            if (!HasHashAsync(helper, available.Sha256).GetAwaiter().GetResult())
                return new(false, "InvalidDownload", "The updater copy failed its SHA-256 check.");
            if (!HasMatchingPublisher(helper, expectedPublisherKey))
                return new(false, "InvalidSignature", "The updater copy failed publisher verification.");
            var ready = Path.Combine(Path.GetDirectoryName(preparedPath)!, "ready.signal");
            if (File.Exists(ready)) File.Delete(ready);
            using var process = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--apply-update", process.Id.ToString(CultureInfo.InvariantCulture),
                         process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                         Path.GetFullPath(executablePath), Path.GetFullPath(preparedPath), available.Sha256,
                         Path.GetFullPath(dataRoot), ready, expectedPublisherKey })
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
                return new(false, null, $"Windows rejected the Authenticode signature (0x{trustStatus:X8}).");
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
