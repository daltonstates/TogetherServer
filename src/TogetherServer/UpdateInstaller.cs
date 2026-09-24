using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TogetherServer;

public static class UpdateInstaller
{
    public static async Task<int> RunAsync(string[] args, IAuthenticodeVerifier? authenticodeVerifier = null)
    {
        if (args.Length != 9 || args[0] != "--apply-update" ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId < 1 ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStart) ||
            !Regex.IsMatch(args[5], "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(args[8], "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)) return 2;
        var signatureVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier();
        string? target = null;
        try
        {
            target = Path.GetFullPath(args[3]);
            var payload = Path.GetFullPath(args[4]);
            var root = Path.GetFullPath(args[6]);
            var ready = Path.GetFullPath(args[7]);
            var updates = Path.Combine(root, "updates") + Path.DirectorySeparatorChar;
            var helper = Path.GetFullPath(Environment.ProcessPath ?? "");
            if (!Path.GetFileName(target).Equals("TogetherServer.exe", StringComparison.OrdinalIgnoreCase) ||
                !payload.StartsWith(updates, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetDirectoryName(payload)!.Equals(Path.GetDirectoryName(helper), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetDirectoryName(ready)!.Equals(Path.GetDirectoryName(helper), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target) || !File.Exists(payload) || !await AppUpdater.HasHashAsync(payload, args[5]) ||
                !HasPublisher(target, args[8], signatureVerifier) ||
                !HasPublisher(payload, args[8], signatureVerifier) ||
                !HasPublisher(helper, args[8], signatureVerifier)) return 2;

            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentStart) return 2;
            File.WriteAllText(ready, "ready");
            try
            {
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally { File.Delete(ready); }
            ReplaceVerified(target, payload, args[5], args[8], signatureVerifier);
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target)!
            });
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                   InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or
                   CryptographicException)
        {
            DesktopLaunch.ShowError("TogetherServer could not finish the update. Check the app and its previous EXE beside it.\n\n" + ex.Message);
            if (target is not null && File.Exists(target) && HasPublisher(target, args[8], signatureVerifier))
            {
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                catch (Exception) { /* The error is already visible. */ }
            }
            return 1;
        }
    }

    public static void ReplaceVerified(string target, string payload, string sha256, string publisherKey,
        IAuthenticodeVerifier? authenticodeVerifier = null)
    {
        if (!Path.GetFileName(target).Equals("TogetherServer.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(target) || !File.Exists(payload) ||
            !Regex.IsMatch(sha256, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(publisherKey, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The update files are missing or invalid.");
        var signatureVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier();
        if (!HasPublisher(target, publisherKey, signatureVerifier) ||
            !HasPublisher(payload, publisherKey, signatureVerifier))
            throw new CryptographicException("The installed app or staged update failed publisher verification.");
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, "TogetherServer-" + Guid.NewGuid().ToString("N") + ".new");
        var previous = target + ".previous";
        try
        {
            File.Copy(payload, temporary);
            if (!AppUpdater.HasHashAsync(temporary, sha256).GetAwaiter().GetResult())
                throw new CryptographicException("The staged update changed before installation.");
            if (!HasPublisher(temporary, publisherKey, signatureVerifier))
                throw new CryptographicException("The copied update failed publisher verification.");
            File.Replace(temporary, target, previous, true);
            if (!HasPublisher(target, publisherKey, signatureVerifier))
            {
                if (!File.Exists(previous) || !HasPublisher(previous, publisherKey, signatureVerifier))
                    throw new CryptographicException(
                        "The installed update failed publisher verification and its previous EXE could not be verified.");
                var rejected = target + ".rejected-" + Guid.NewGuid().ToString("N");
                File.Replace(previous, target, rejected, true);
                if (!HasPublisher(target, publisherKey, signatureVerifier))
                    throw new CryptographicException(
                        "The installed update failed publisher verification and the restored EXE could not be verified.");
                File.Delete(rejected);
                throw new CryptographicException(
                    "The installed update failed publisher verification; the verified previous EXE was restored.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool HasPublisher(string path, string expectedPublisherKey, IAuthenticodeVerifier verifier)
    {
        var signature = verifier.Verify(path);
        if (!signature.Valid || string.IsNullOrWhiteSpace(signature.PublisherKey)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(signature.PublisherKey), Convert.FromHexString(expectedPublisherKey));
        }
        catch (FormatException) { return false; }
    }
}
