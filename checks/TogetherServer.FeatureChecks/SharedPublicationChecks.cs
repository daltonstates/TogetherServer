using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Direct synthetic setup snapshots only: no files, game, process or listener.
internal static class SharedPublicationChecks
{
    internal static void Run(string root)
    {
        RequirePlayers("// List of permitted players, one ID per line.\n");
        RequirePlayers("\uFEFF// List of permitted players.\r\n\r\n");
        RequirePlayers("\uFEFF \t// Default header\r\n\t# Existing comment syntax\r\n" +
            "  Steam_76561198000000000  \r\n\t// Another comment\r\n" +
            " PlayFab_0123456789ABCDEF \r\n",
            "Steam_76561198000000000", "PlayFab_0123456789ABCDEF");
        RequirePlayers("# Existing comment\nSteam_12345\n", "Steam_12345");
        RequirePlayers(string.Concat(Enumerable.Repeat(" // Comment\n", 140)) + "PlayFab_12345\n",
            "PlayFab_12345");

        var maximum = Enumerable.Range(0, 128).Select(number => "Steam_" + number).ToArray();
        RequirePlayers("// Header\n" + string.Join('\n', maximum), maximum);

        foreach (var invalid in new[]
        {
            "C:/private", "..\\private", "Steam/12345", "/Steam_12345",
            "Steam_12345 // Inline comment", "Steam_12345 # Inline comment",
            "https://example.invalid/player", "Steam_12\0bad", new string('A', 129),
            "Steam_12345\nSteam_12345", "Steam_12345\nsteam_12345",
            string.Join('\n', Enumerable.Range(0, 129).Select(number => "Steam_" + number))
        })
        {
            RequireRejected("// A header must not hide invalid actual entries.\n" + invalid);
        }
    }

    private static SharedWorldPortableSetup Capture(string content) =>
        SharedWorldPortableSetupReader.Capture(new ServerSetupSnapshot(3,
            Guid.Parse("299c0943-91c5-4dca-af20-cafc4a883a24"), GameKinds.Valheim,
            "synthetic-permitted-players", "Unknown", "synthetic-inventory", [],
            [new SetupFileSnapshot("permit-list", Encoding.UTF8.GetBytes(content))], 2456));

    private static void RequirePlayers(string content, params string[] expected)
    {
        var setup = Capture(content);
        Require(SharedWorldPortableSetupReader.Valid(GameKinds.Valheim, setup) &&
            setup.Allowlist is not null &&
            setup.Allowlist.Select(item => item.Name).SequenceEqual(expected, StringComparer.Ordinal) &&
            setup.Allowlist.All(item => item.Id is null),
            "Valheim comments changed the actual portable player entries.");
    }

    private static void RequireRejected(string content)
    {
        try { Capture(content); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A malformed, repeated or oversized Valheim player list was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
