using System.ComponentModel;
using System.Diagnostics;

namespace TogetherServer;

public static class ClientMonitor
{
    // Null means the configured executable cannot be verified, so idle and remote Stop must pause.
    public static bool? IsRunning(string? approvedPath)
    {
        if (string.IsNullOrWhiteSpace(approvedPath) || !Path.IsPathFullyQualified(approvedPath) || !File.Exists(approvedPath))
            return null;
        var expected = Path.GetFullPath(approvedPath);
        var name = Path.GetFileNameWithoutExtension(expected);
        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.HasExited) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) return null;
                    if (Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
