namespace TogetherServer;

internal static class InstallerIntegration
{
    internal const string ConfigureCommand = "--installer-configure";
    internal const string RemoveStartupCommand = "--installer-remove-startup";

    internal static bool IsCommand(string[] arguments) =>
        arguments.Length > 0 && arguments[0] is ConfigureCommand or RemoveStartupCommand;

    internal static int Run(string[] arguments, string executablePath,
        Func<string, string?>? environment = null, string? localApplicationData = null,
        string? startupKeyPath = null)
    {
        if (arguments.Length == 1 && arguments[0] == RemoveStartupCommand)
        {
            try
            {
                var startup = Startup(executablePath, startupKeyPath);
                if (startup.IsEnabled()) startup.SetEnabled(false);
                return 0;
            }
            catch (Exception ex) when (Expected(ex))
            {
                Console.Error.WriteLine("TogetherServer installer cleanup could not remove its startup entry: " + ex.Message);
                return 1;
            }
        }

        if (!TryParse(arguments, out var mode, out var closeToTray, out var launchAtLogin)) return 2;
        try
        {
            var instance = AppInstance.Resolve([], environment, localApplicationData, executablePath);
            if (instance.IsStaging)
                throw new InvalidOperationException("Installer setup is available only for the production app.");
            instance.PrepareDataRoot();
            using var data = new LocalData(instance.DataRoot, instance.DefaultCompanionPort);
            if (mode is not null) data.SavePreferredMode(mode);
            if (closeToTray)
            {
                var preferences = data.LoadDesktopPreferences();
                preferences.CloseToTray = true;
                data.SaveDesktopPreferences(preferences);
            }
            if (launchAtLogin) Startup(executablePath, startupKeyPath).SetEnabled(true);
            return 0;
        }
        catch (Exception ex) when (Expected(ex))
        {
            Console.Error.WriteLine("TogetherServer optional installer setup could not be applied: " + ex.Message);
            return 1;
        }
    }

    private static bool TryParse(string[] arguments, out string? mode, out bool closeToTray,
        out bool launchAtLogin)
    {
        mode = null;
        closeToTray = false;
        launchAtLogin = false;
        if (arguments.Length < 1 || arguments[0] != ConfigureCommand) return false;
        var sawMode = false;
        for (var index = 1; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--mode" when !sawMode && index + 1 < arguments.Length:
                    var value = arguments[++index];
                    if (value.Equals("host", StringComparison.OrdinalIgnoreCase)) mode = "Host";
                    else if (value.Equals("friend", StringComparison.OrdinalIgnoreCase)) mode = "Friend";
                    else return false;
                    sawMode = true;
                    break;
                case "--close-to-tray" when !closeToTray:
                    closeToTray = true;
                    break;
                case "--launch-at-login" when !launchAtLogin:
                    launchAtLogin = true;
                    break;
                default:
                    return false;
            }
        }
        return mode is not null || closeToTray || launchAtLogin;
    }

    private static WindowsStartup Startup(string executablePath, string? keyPath) =>
        keyPath is null ? new WindowsStartup(executablePath) : new WindowsStartup(executablePath, keyPath);

    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidDataException or InvalidOperationException or
        System.Security.SecurityException or System.Text.Json.JsonException;
}
