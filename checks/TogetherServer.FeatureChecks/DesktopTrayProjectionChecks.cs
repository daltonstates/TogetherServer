using System.Reflection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class DesktopTrayProjectionChecks
{
    internal static void Run(string root)
    {
        CheckCiWebViewDebugArguments();
        // Construct the data-only adapter and call its projection setter. Start
        // is never called, so no form, tray icon, thread or native call exists.
        var desktop = new DesktopWindow(new Uri("http://127.0.0.1:5127/"), root,
            () => throw new Exception("The projection must not quit an app."), false, false);
        var field = typeof(DesktopWindow).GetField("traySummary", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("The tray projection could not be inspected.");
        DesktopTraySummary Current() => (DesktopTraySummary)field.GetValue(desktop)!;
        foreach (var state in new[] { "Not connected", "Update required" })
        {
            desktop.SetTraySummary(new(1, 1, state));
            desktop.SetTraySummary(new(2, 0, state));
            var current = Current();
            if (current != new DesktopTraySummary(2, 0, state) ||
                !DesktopWindow.TraySummaryText("Synthetic app", current).Contains("2 running", StringComparison.Ordinal))
                throw new Exception($"Canonical {state} prevented current Host counts from reaching the tray projection.");
        }
        var accepted = Current();
        desktop.SetTraySummary(new(-1, 0, "Connected"));
        desktop.SetTraySummary(new(0, 0, "Unreviewed state"));
        if (Current() != accepted)
            throw new Exception("Invalid metadata replaced the last reviewed tray projection.");
    }

    private static void CheckCiWebViewDebugArguments()
    {
        foreach (var enteredPort in new string?[] { null, "", "49152", "51234", "65535", "51234 --no-sandbox" })
            if (DesktopWindow.CiWebViewDebugArguments(false, enteredPort) is not null)
                throw new Exception("A production instance accepted the staging WebView debug opt-in.");
        foreach (var enteredPort in new string?[]
        {
            null, "", " ", "0", "9222", "49151", "65536", "99999", "2147483647", "049152",
            "+49152", "-49152", " 49152", "49152 ", "49152\n", "49_152", "49152.0", "4.9152e4",
            "４９１５２", "٤٩١٥٢", "49152\0", "49152 --no-sandbox", "49152;--remote-debugging-address=0.0.0.0"
        })
            if (DesktopWindow.CiWebViewDebugArguments(true, enteredPort) is not null)
                throw new Exception("A malformed or non-high port enabled WebView debugging.");
        foreach (var port in new[] { 49152, 51234, 65535 })
        {
            var enteredPort = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var expected = $"--remote-debugging-port={enteredPort} --remote-debugging-address=127.0.0.1";
            if (DesktopWindow.CiWebViewDebugArguments(true, enteredPort) != expected)
                throw new Exception("A staging WebView debug port did not produce the fixed loopback-only arguments.");
        }
    }
}
