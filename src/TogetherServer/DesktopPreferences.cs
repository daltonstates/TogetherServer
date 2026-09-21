using Microsoft.Win32;

namespace TogetherServer;

public sealed class DesktopPreferences
{
    public bool CloseToTray { get; set; }
}

internal sealed record DesktopPreferenceChange(bool? LaunchAtLogin, bool? CloseToTray);

internal sealed class WindowsStartup
{
    private const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TogetherServer";
    private readonly string executablePath;
    private readonly string keyPath;

    internal WindowsStartup(string executablePath, string keyPath = DefaultKeyPath)
    {
        this.executablePath = executablePath;
        this.keyPath = keyPath;
    }

    internal string Command => $"\"{executablePath}\" --startup";

    internal bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    internal void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!OperatingSystem.IsWindows() ||
                !File.Exists(executablePath) ||
                !Path.GetFileName(executablePath).Equals("TogetherServer.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Open the installed TogetherServer.exe before enabling Windows startup.");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException("Windows startup settings are unavailable.");
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
