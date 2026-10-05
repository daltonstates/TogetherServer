using System.Diagnostics;
using System.Net.Http.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class CoreRemoteJourney
{
    internal static async Task RunWorldLoadAsync(string appPath, string fixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "world-load-journey", Guid.NewGuid().ToString("N")));
        var dataRoot = Path.Combine(root, "host");
        var port = FreeTcpPort();
        Process? host = null;
        Guid? trialProfile = null;
        var original = new ServerProfile
        {
            WorldId = "synthetic",
            Name = "Disposable source",
            WorldSource = "New",
            ExecutablePath = fixturePath,
            GamePort = FreeUdpPair()
        };
        var sourceRoot = Path.Combine(dataRoot, "worlds", original.Id.ToString("N"));
        var sourceFile = Path.Combine(sourceRoot, "copy.bin");
        original.WorldDirectory = sourceRoot;
        try
        {
            host = StartApp(appPath, "--host", port, dataRoot, staging: true);
            await WaitLocalAsync(port);
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(sourceFile, "synthetic baseline");
            WindowsListenerOwners.RequireTogetherServerOwner(port, host);
            using var owner = LocalClient(port);
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings",
                new() { Profiles = [original], MaxConcurrentServers = 2 })).Ok, "disposable source setup rejected");
            Require((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{original.Id}/backups/manual", new { })).Ok,
                "fixture backup was not created");
            var backupList = await RehearsalOwnerGetAsync<WorldBackupList>(owner, $"/api/local/profiles/{original.Id}/backups");
            var backupId = backupList.Backups.Single().Id;
            var prepared = await PostAsync<WorldLoadPreparationRequest, WorldLoadRehearsalResult>(owner,
                $"/api/local/profiles/{original.Id}/world-load/prepare", new(backupId));
            Require(prepared.Ok && prepared.Rehearsal?.CanLaunch == true, "fixture copy preparation failed: " + prepared.Code);
            var trial = prepared.Rehearsal!;
            trialProfile = trial.RehearsalProfileId;
            Require(trial.WorldDirectory != sourceRoot && trial.GamePort != original.GamePort &&
                trial.LoadOutcome == "Unobserved" && trial.RestartOutcome == "Unobserved", "copy isolation or evidence wrong");
            // Original and trial use distinct roots and ports under the same canonical lifecycle gate.
            Require((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{original.Id}/start", new { })).Ok,
                "original synthetic run failed");
            Require((await PostAsync<object, WorldLoadRehearsalResult>(owner, $"/api/local/world-load/{trial.Id}/start", new { })).Ok,
                "isolated trial Start failed");
            Require(!(await PostAsync<WorldLoadCleanupRequest, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/cleanup", new(true))).Ok, "running trial cleanup accepted");
            Require((await PostAsync<WorldLoadConfirmationRequest, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/confirm", new("Load", true, "synthetic fixture"))).Ok, "owner-confirmed load step failed");
            File.WriteAllText(Path.Combine(trial.WorldDirectory, "copy.bin"), "recognizable synthetic change");
            Require((await PostAsync<WorldLoadConfirmationRequest, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/confirm", new("Change", true))).Ok, "owner-confirmed change step failed");
            Require((await PostAsync<object, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/stop", new { })).Ok, "exact trial Stop failed");
            StopApp(host); host = StartApp(appPath, "--host", port, dataRoot, staging: true); await WaitLocalAsync(port);
            Require((await PostAsync<object, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/start", new { })).Ok, "durable trial restart failed");
            var confirmed = await PostAsync<WorldLoadConfirmationRequest, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/confirm", new("Restart", true));
            Require(confirmed.Ok && confirmed.Rehearsal?.RestartOutcome == "OwnerConfirmed" &&
                File.ReadAllText(Path.Combine(trial.WorldDirectory, "copy.bin")) == "recognizable synthetic change", "restart result wrong");
            Require((await PostAsync<object, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/stop", new { })).Ok, "second exact trial Stop failed");
            Require((await PostAsync<WorldLoadCleanupRequest, WorldLoadRehearsalResult>(owner,
                $"/api/local/world-load/{trial.Id}/cleanup", new(true))).Ok && !Directory.Exists(trial.WorldDirectory), "cleanup failed");
            Require(File.ReadAllText(sourceFile) == "synthetic baseline" &&
                (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single(run => run.ProfileId == original.Id).State == "Process running",
                "original data/process was touched by trial cleanup");
            Console.WriteLine("PASS packaged world-load preparation, separate roots/ports, exact Start/Stop, durable restart, owner labels and cleanup");
            Console.WriteLine("World-load rehearsal: synthetic process/copy evidence only. Real game load and saved restart remain UNVERIFIED.");
        }
        finally
        {
            if (trialProfile is { } id) await TryStopManagedRunAsync(port, id, host);
            await TryStopManagedRunAsync(port, original.Id, host);
            StopApp(host);
        }
    }
}
