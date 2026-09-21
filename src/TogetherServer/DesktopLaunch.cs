using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TogetherServer;

internal static class DesktopLaunch
{
    public static void EnsureConsoleForGameStop(bool hideWindow)
    {
        if (!OperatingSystem.IsWindows()) return;
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            if (!AllocConsole())
            {
                // A headless or pseudoconsole launch can already be attached
                // without exposing a console window to this process.
                var error = Marshal.GetLastWin32Error();
                if (error == 5) return;
                throw new Win32Exception(error, "Could not prepare graceful game stop.");
            }
            window = GetConsoleWindow();
            hideWindow = true;
        }
        if (hideWindow && window != IntPtr.Zero) ShowWindow(window, 0);
    }

    public static async Task<bool> TryShowExistingAsync(int port, bool showWindow = true)
    {
        var address = $"http://127.0.0.1:{port}/";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(address + "api/local/snapshot");
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (json.RootElement.TryGetProperty("mode", out var mode) &&
                        mode.GetString() is "Host" or "Friend")
                    {
                        if (!showWindow) return true;
                        using var show = new HttpRequestMessage(HttpMethod.Post, address + "api/local/show");
                        show.Headers.TryAddWithoutValidation("Origin", address.TrimEnd('/'));
                        show.Headers.Add("X-TogetherServer-Local", "1");
                        using var shown = await client.SendAsync(show);
                        if (shown.IsSuccessStatusCode) return true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            if (attempt < 19) await Task.Delay(200);
        }
        return false;
    }

    public static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "TogetherServer", 0x00000010);
        else Console.Error.WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
