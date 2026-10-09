using TogetherServer.FeatureChecks;

// Deliberately separate from TogetherServer.Checks.Program and its process journeys.
// Every registered group uses pure logic, synthetic files/protected state or fake adapters.
// No app entry point, process, listener, console attachment, browser or dialog is invoked.
var root = Path.Combine(Directory.GetCurrentDirectory(), "local-data", "feature-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var groups = new (string Name, Func<Task> Run)[]
{
    ("Compatibility requirements", () => { GameCompatibilityChecks.Run(root); return Task.CompletedTask; }),
    ("Fixed game launch and pinned Friend checks", () => GameClientFriendChecks.RunAsync(root)),
    ("Fixed local feature inputs", () => { FeatureInputChecks.Run(); return Task.CompletedTask; }),
    ("Backup names and pins", () => { BackupBookmarksChecks.Run(root); return Task.CompletedTask; }),
    ("Seven-day session projection", () => { WeeklySummaryChecks.Run(); return Task.CompletedTask; }),
    ("Guided game settings", () => GameSettingsChecks.RunAsync(root)),
    ("Durable signed notices", () => { PinnedNoticeChecks.Run(root); return Task.CompletedTask; }),
    ("Pinned Friend notice copies", () => PinnedNoticeFriendChecks.RunAsync(root))
};
foreach (var group in groups)
{
    try { await group.Run(); Console.WriteLine($"PASS {group.Name}"); }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {group.Name}: {error.GetType().Name}: {error.Message}");
        Console.Error.WriteLine(error.StackTrace);
        Environment.ExitCode = 1;
    }
}
Console.WriteLine("Source/synthetic evidence only. Native game opening, installed games, Friend-PC joins and saves remain unverified.");
