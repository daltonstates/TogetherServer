using System.Reflection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class DesktopTrayProjectionChecks
{
    internal static void Run(string root)
    {
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
}
