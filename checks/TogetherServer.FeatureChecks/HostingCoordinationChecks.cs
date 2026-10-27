using System.Text.Json;
using System.Text.Json.Nodes;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class HostingCoordinationChecks
{
    internal static Task RunAsync(string parent)
    {
        var now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var schedule = new PeerReplicationSchedule();
        Require(schedule.Due("route", 0, now), "new route was not due");
        schedule.Completed("route", 0, now, false, false, 1);
        Require(!schedule.Due("route", 0, now.AddSeconds(59)) && schedule.Due("route", 0, now.AddSeconds(60)),
            "unchanged discovery did not idle for one minute");
        Require(schedule.Due("route", 1, now), "publication/reconnection did not invalidate idle deadline");
        Require(schedule.NextDelay(now, 1) == TimeSpan.FromSeconds(1), "an unvisited route lost immediate publication wake behind its old idle deadline");
        for (var index = 0; index < 8; index++) schedule.Completed("route", 1, now, true, false, 1);
        Require(!schedule.Due("route", 1, now.AddSeconds(299)) && schedule.Due("route", 1, now.AddSeconds(300)),
            "offline retry did not back off within five minutes");
        schedule.Completed("route", 1, now, false, true, 1);
        Require(!schedule.Due("route", 1, now.AddSeconds(1)) && schedule.Due("route", 1, now.AddSeconds(2)),
            "resumable active work inherited offline backoff");
        schedule.Track("world:removed", 1, now);
        schedule.RetainRoutes("world", new HashSet<string>());
        schedule.RetainWorlds(new HashSet<string>());
        Require(schedule.NextDelay(now) == TimeSpan.FromSeconds(60), "removed routes left a permanently due scheduling entry");
        var time = new TestClock();
        var ownership = new PeerPullOwnership(time);
        ownership.Served("world", "save", "member");
        Require(ownership.Active("world", "save", "member") && !ownership.Active("world", "other", "member") &&
            !ownership.Active("world", "save", "other"), "pull scheduling crossed exact save/recipient identity");
        time.Advance(TimeSpan.FromSeconds(60));
        Require(!ownership.Active("world", "save", "member"), "abandoned pull permanently prevented offer retry");
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original");
        _ = fixture.TargetService.Views();
        var before = fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "SignedRegistry").Count;
        _ = fixture.TargetService.Views();
        Require(before > 0 && fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "SignedRegistry").Count == before,
            "unchanged exact protected bytes repeated registry signature verification");
        // Same length/mtime cannot authorize different signed registry content.
        var path = Path.Combine(fixture.Target.RootPath, "world-peer-memberships.protected");
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = fixture.Target.LoadProtected("world-peer-memberships.protected")!;
        using var document = JsonDocument.Parse(bytes);
        var signature = document.RootElement.GetProperty("groups")[0].GetProperty("roster").GetProperty("signature").GetString()!;
        var replacement = (signature[0] == 'A' ? "B" : "A") + signature[1..];
        var changed = JsonNode.Parse(bytes)!;
        changed["groups"]![0]!["roster"]!["signature"] = replacement;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(encoded.Length == bytes.Length && !encoded.SequenceEqual(bytes), "the synthetic signed registry mutation did not change equal-length bytes");
        fixture.Target.SaveProtected("world-peer-memberships.protected", encoded);
        File.SetLastWriteTimeUtc(path, stamp);
        var refused = false;
        try { _ = fixture.TargetService.Views(); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "changed signed registry reused verification based on timestamps");
        return Task.CompletedTask;
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
