using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TogetherServer;

public static class UpdateInstaller
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8 || args[0] != "--apply-update" ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId < 1 ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStart) ||
            !Regex.IsMatch(args[5], "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)) return 2;
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
                !File.Exists(target) || !File.Exists(payload) || !await AppUpdater.HasHashAsync(payload, args[5])) return 2;

            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentStart) return 2;
            File.WriteAllText(ready, "ready");
            try
            {
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally { File.Delete(ready); }
            ReplaceVerified(target, payload, args[5]);
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
            if (target is not null && File.Exists(target))
            {
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                catch (Exception) { /* The error is already visible. */ }
            }
            return 1;
        }
    }

    public static void ReplaceVerified(string target, string payload, string sha256)
    {
        if (!Path.GetFileName(target).Equals("TogetherServer.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(target) || !File.Exists(payload) ||
            !Regex.IsMatch(sha256, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The update files are missing or invalid.");
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, "TogetherServer-" + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            File.Copy(payload, temporary);
            if (!AppUpdater.HasHashAsync(temporary, sha256).GetAwaiter().GetResult())
                throw new CryptographicException("The staged update changed before installation.");
            File.Replace(temporary, target, target + ".previous", true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
