using System.Diagnostics;
using System.Text.Json;

internal static partial class CoreRemoteJourney
{
    // The child waits on stdin until it belongs to the disposable job. Browser
    // descendants inherit containment; the parent observes their windows too.
    private static async Task RunLocalPairingLabUxAsync(string root, LocalPairingLabJob job,
        int hostPort, int friendPort, int observerPort, Guid profileId, string fixturePath,
        string? lanAddress)
    {
        LabRequire(lanAddress is not null, "UxRequiresSelectedLocalLanAddress");
        var script = Path.GetFullPath(Path.Combine("scripts", "run-local-pairing-ux.mjs"));
        LabRequire(File.Exists(script), "UxScriptMissing");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--allow-windowless-ux");
        foreach (var name in new[] { "NODE_OPTIONS", "NODE_PATH", "PWDEBUG", "DEBUG" })
            start.Environment.Remove(name);
        using var runner = Process.Start(start) ?? throw new PairingLabFailure("UxRunnerNotStarted");
        job.Add(runner);
        LabRequire(job.ProcessIds().Contains(runner.Id), "UxRunnerNotContained");
        // Drain private browser diagnostics rather than writing codes or page
        // contents to a shared console. The runner saves a bounded report.
        runner.BeginOutputReadLine();
        runner.BeginErrorReadLine();
        await runner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            root, hostPort, friendPort, observerPort, profileId, fixturePath, lanAddress,
            runnerPid = runner.Id, parentPid = Environment.ProcessId,
            authorization = "owner-approved-local-windowless-ux"
        }, Json));
        runner.StandardInput.Close();
        var timer = Stopwatch.StartNew();
        while (!runner.HasExited && timer.Elapsed < TimeSpan.FromMinutes(7)) await Task.Delay(100);
        if (!runner.HasExited)
        {
            await job.StopAsync();
            throw new PairingLabFailure("UxRunnerTimedOut");
        }
        LabRequire(runner.ExitCode == 0, "TwoEndedUxFailed");
        var reportPath = Path.Combine(root, "ux", "report.json");
        LabRequire(File.Exists(reportPath) && new FileInfo(reportPath).Length <= 256 * 1024, "UxReportMissingOrOversized");
        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(reportPath));
        LabRequire(report.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
            report.RootElement.TryGetProperty("stages", out var stages) && stages.GetArrayLength() == 8 &&
            stages.EnumerateArray().All(stage => stage.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True) &&
            report.RootElement.TryGetProperty("guidedHosting", out var guided) &&
            guided.GetProperty("startRequests").GetInt32() == 1 && guided.GetProperty("successfulStartResponses").GetInt32() == 1 &&
            report.RootElement.TryGetProperty("signedDiscovery", out var discovery) &&
            discovery.GetProperty("exactCandidateSession").ValueKind == JsonValueKind.True &&
            discovery.GetProperty("exactAdvertisedEndpoint").ValueKind == JsonValueKind.True &&
            discovery.GetProperty("exactConnectSelection").ValueKind == JsonValueKind.True,
            "UxReportDidNotPass");
    }
}
