namespace TogetherServer;

// Packaged journey hook. A disposable receiver may report less space than the
// drive has; this can never relax the real 1 GiB reserve.
internal static class SharedWorldFixtureSpace
{
    internal const string CeilingEnvironmentVariable = "TOGETHERSERVER_FIXTURE_FREE_BYTES_CEILING";

    internal static long AvailableBytes(string vaultRoot)
    {
        var fullVault = Path.GetFullPath(vaultRoot);
        var actual = new DriveInfo(Path.GetPathRoot(fullVault)!).AvailableFreeSpace;
        if (Environment.GetEnvironmentVariable(GameServerRegistry.FixtureOptInEnvironmentVariable) != "1" ||
            !long.TryParse(Environment.GetEnvironmentVariable(CeilingEnvironmentVariable), out var ceiling) ||
            ceiling < 0)
            return actual;

        var fixtureRootValue = Environment.GetEnvironmentVariable("TOGETHERSERVER_FIXTURE_ROOT");
        var dataRootValue = Environment.GetEnvironmentVariable("TOGETHERSERVER_DATA_DIR");
        if (string.IsNullOrWhiteSpace(fixtureRootValue) || string.IsNullOrWhiteSpace(dataRootValue))
            return actual;
        string fixtureRoot;
        try
        {
            fixtureRoot = Path.GetFullPath(fixtureRootValue);
            if (!string.Equals(fixtureRoot, Path.GetFullPath(dataRootValue),
                    StringComparison.OrdinalIgnoreCase)) return actual;
        }
        catch (ArgumentException) { return actual; }
        var fixture = new DirectoryInfo(fixtureRoot);
        if (!fixture.Name.StartsWith("friend-", StringComparison.OrdinalIgnoreCase) ||
            fixture.Parent is null || !Guid.TryParseExact(fixture.Parent.Name, "N", out _) ||
            !string.Equals(fixture.Parent.Parent?.Name, "shared-world-journey",
                StringComparison.OrdinalIgnoreCase)) return actual;

        var relative = Path.GetRelativePath(fixtureRoot, fullVault)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length != 3 || relative[0] != "received-shared-worlds" ||
            !Guid.TryParseExact(relative[1], "N", out _) ||
            !Guid.TryParseExact(relative[2], "N", out _)) return actual;

        return Math.Min(actual, ceiling);
    }
}
