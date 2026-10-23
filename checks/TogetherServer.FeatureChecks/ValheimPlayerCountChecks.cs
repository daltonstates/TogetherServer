using System.Buffers.Binary;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Packet exchanges and disposable text only. No socket, process, console,
// installed game, app entry point, or real save is opened.
internal static class ValheimPlayerCountChecks
{
    internal static void Run(string root)
    {
        var zero = Info(0, 10);
        var positive = Info(2, 10);
        var challenge = Challenge(unchecked((int)0x87654321));

        var immediate = Query(true, zero);
        Require(immediate.Result.Players?.Online == 0 && !immediate.Result.NoReply && immediate.Requests.Count == 1,
            "a complete zero-count reply needed another query");
        var recovered = Query(true, null, zero);
        Require(recovered.Result.Players?.Online == 0 && recovered.Requests.Count == 2 &&
            recovered.Requests[0].SequenceEqual(recovered.Requests[1]),
            "one silent local exchange did not recover through the same request");
        var recoveredPositive = Query(true, null, positive);
        Require(recoveredPositive.Result.Players?.Online == 2 && recoveredPositive.Requests.Count == 2,
            "a positive count after transient silence was lost");
        var occupied = Query(true, positive, zero);
        Require(occupied.Result.Players?.Online == 2 && occupied.Requests.Count == 1,
            "a positive count was retried into zero");

        var challengedRetry = Query(true, challenge, null, zero);
        Require(challengedRetry.Result.Players?.Online == 0 && challengedRetry.Requests.Count == 3 &&
            challengedRetry.Requests[1].Length == challengedRetry.Requests[0].Length + sizeof(int) &&
            challengedRetry.Requests[1].SequenceEqual(challengedRetry.Requests[2]) &&
            BinaryPrimitives.ReadInt32LittleEndian(challengedRetry.Requests[1].AsSpan(
                challengedRetry.Requests[0].Length)) == unchecked((int)0x87654321),
            "retry discarded or changed the server's four-byte challenge");
        var challengeAfterSilence = Query(true, null, challenge, positive);
        Require(challengeAfterSilence.Result.Players?.Online == 2 && challengeAfterSilence.Requests.Count == 3,
            "a challenge received after the silent retry could not complete");
        var silent = Query(true, null, null, zero);
        Require(silent.Result.Players is null && silent.Result.NoReply && silent.Requests.Count == 2,
            "repeated silence became zero or exceeded its one retry");
        var externalSilent = Query(false, null, zero);
        Require(externalSilent.Result.Players is null && externalSilent.Result.NoReply && externalSilent.Requests.Count == 1,
            "an external endpoint probe gained an unrequested silence retry");
        var externalChallenge = Query(false, challenge, zero);
        Require(externalChallenge.Result.Players?.Online == 0 && externalChallenge.Requests.Count == 2,
            "an external probe stopped supporting the existing challenge handshake");
        var repeatedChallenge = Query(true, challenge, challenge, zero);
        Require(repeatedChallenge.Result.Players is null && !repeatedChallenge.Result.NoReply &&
            repeatedChallenge.Requests.Count == 2,
            "repeated challenges escaped the bounded handshake or enabled log fallback");
        var silentAfterChallenge = Query(true, challenge, null, null, zero);
        Require(silentAfterChallenge.Result.Players is null && silentAfterChallenge.Result.NoReply &&
            silentAfterChallenge.Requests.Count == 3,
            "a silent challenged query exceeded one retry or became zero");

        foreach (var invalid in new[]
        {
            Array.Empty<byte>(),
            new byte[] { 0xfe, 0xff, 0xff, 0xff, 0x49 }, // Unsupported split reply.
            challenge[..^1],
            Info(0, 0),
            Info(11, 10)
        })
        {
            var result = Query(true, invalid, zero);
            Require(result.Result.Players is null && !result.Result.NoReply && result.Requests.Count == 1,
                "a malformed or unsupported response was retried into zero or treated as silence");
        }
        // The fixture emits the complete mandatory A2S_INFO body without EDF.
        // Every cut before its final version terminator must remain Unknown.
        for (var length = 0; length < zero.Length; length++)
        {
            var partial = Query(true, zero[..length], zero);
            Require(partial.Result.Players is null && !partial.Result.NoReply && partial.Requests.Count == 1,
                "a partial A2S_INFO body authorized an empty-server count");
        }
        byte[] withEdf = [.. zero, 0];
        Require(Query(true, withEdf).Result.Players?.Online == 0,
            "a complete reply with an optional empty EDF stopped working");

        CheckFreshFallback(root);
        CheckLogSnapshots(root);
    }

    private static void CheckFreshFallback(string root)
    {
        var directory = Path.Combine(root, "valheim-fresh-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.log");
        const string ready = "Game server connected\n";
        var reads = 0;
        ValheimLogStatus ReadLog()
        {
            reads++;
            return ValheimServerLog.Read(path);
        }

        File.WriteAllText(path, ready);
        var pendingJoin = ValheimServerDriver.Observe(ReadLog, () =>
        {
            File.AppendAllText(path, "New connection\n");
            return new(null, true);
        }, () => true);
        Require(pendingJoin.OnlinePlayers is null && !pendingJoin.PlayerCountTrusted && reads == 2,
            "a join arriving during a silent query reused the pre-query empty log");

        File.WriteAllText(path, ready);
        reads = 0;
        var joined = ValheimServerDriver.Observe(ReadLog, () =>
        {
            File.AppendAllText(path, "New connection\nGot connection SteamID 111111\n");
            return new(null, true);
        }, () => true);
        Require(joined.OnlinePlayers == 1 && joined.PlayerCountTrusted && reads == 2,
            "a completed join during a silent query was reported as empty");

        File.WriteAllText(path, ready);
        reads = 0;
        var empty = ValheimServerDriver.Observe(ReadLog, () => new(null, true), () => true);
        Require(empty.OnlinePlayers == 0 && empty.PlayerCountTrusted && reads == 2,
            "a fresh complete non-Crossplay zero log lost its fallback");

        reads = 0;
        var invalidQuery = ValheimServerDriver.Observe(ReadLog, () => new(null, false), () => true);
        Require(invalidQuery.OnlinePlayers is null && !invalidQuery.PlayerCountTrusted && reads == 1,
            "an invalid query used the complete zero log as fallback");

        reads = 0;
        var crossplay = ValheimServerDriver.Observe(ReadLog, () => new(null, true), () => false);
        Require(crossplay.OnlinePlayers is null && !crossplay.PlayerCountTrusted && reads == 1,
            "Crossplay query silence used the unvalidated log fallback");

        reads = 0;
        var queryCount = ValheimServerDriver.Observe(ReadLog,
            () => new(new GamePlayerCount(2, 10), false), () => true);
        Require(queryCount.OnlinePlayers == 2 && queryCount.PlayerCountTrusted && reads == 1,
            "the owned-log zero overrode a valid positive query");

        reads = 0;
        var missingLog = ValheimServerDriver.Observe(ReadLog, () =>
        {
            File.WriteAllText(path, "");
            return new(null, true);
        }, () => true);
        Require(missingLog.OnlinePlayers is null && !missingLog.PlayerCountTrusted && reads == 2,
            "an unavailable post-query log retained the pre-query zero");
    }

    private static void CheckLogSnapshots(string root)
    {
        var directory = Path.Combine(root, "valheim-player-count", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.log");
        const string ready = "10/23/2026 12:00:00: Game server connected\n";
        var startupJoin = ValheimServerLog.Parse(new StringReader("New connection\n" + ready +
            "Got connection SteamID 111111\n"));
        var startupRoundTrip = ValheimServerLog.Parse(new StringReader("New connection\n" +
            "Got connection SteamID 222222\n" + ready + "RPC_Disconnect\nClosing socket 222222\n"));
        Require(startupJoin.Players?.Online == 1 && startupRoundTrip.Players?.Online == 0,
            "a complete startup connection sequence was lost across the ready marker");
        File.WriteAllText(path, ready);
        Require(ValheimServerLog.Read(path).Players?.Online == 0,
            "a complete owned startup history no longer establishes its zero baseline");

        File.AppendAllText(path, "10/23/2026 12:00:01: New conn");
        Require(ValheimServerLog.Read(path) is { Ready: true, Players: null },
            "an unfinished connection line left a trusted zero");
        File.AppendAllText(path, "ection\n10/23/2026 12:00:02: Got connection SteamID 111111\n");
        Require(ValheimServerLog.Read(path).Players?.Online == 1,
            "a completed connection could not recover after a partial write");
        File.AppendAllText(path, "10/23/2026 12:01:00: RPC_Disconnect\n10/23/2026 12:01:01: Closing socket 111111");
        Require(ValheimServerLog.Read(path) is { Ready: true, Players: null },
            "an unfinished closing-socket line supplied a zero count");
        File.AppendAllText(path, "\n");
        Require(ValheimServerLog.Read(path).Players?.Online == 0,
            "a completed disconnect did not restore the zero count");

        File.WriteAllText(path, ready + "10/23/2026 12:02:00: Connections 0 ZDOS:123");
        Require(ValheimServerLog.Read(path) is { Ready: true, Players: null },
            "an unterminated zero checkpoint authorized an empty server");
        File.AppendAllText(path, " sent:0 recv:0\r\n");
        Require(ValheimServerLog.Read(path).Players?.Online == 0,
            "a complete CRLF checkpoint did not recover");

        // Readiness may recur after the original count baseline is outside the
        // bounded tail. It cannot invent zero without a retained checkpoint.
        File.WriteAllText(path, ready + "Connections 2 ZDOS:123 sent:0 recv:0\n" +
            new string('x', 4 * 1024 * 1024) + "\n" + ready);
        Require(ValheimServerLog.Read(path) is { Ready: true, Players: null },
            "a truncated history combined with repeated readiness invented zero");
        File.AppendAllText(path, "Connections 2 ZDOS:123 sent:0 recv:0\n");
        Require(ValheimServerLog.Read(path).Players?.Online == 2,
            "a complete retained checkpoint could not reestablish the bounded history");
    }

    private static (ValveServerQueryResult Result, List<byte[]> Requests) Query(bool retrySilence,
        params byte[]?[] responses)
    {
        var remaining = new Queue<byte[]?>(responses);
        var requests = new List<byte[]>();
        var result = ValveServerQuery.Query(request =>
        {
            requests.Add(request.ToArray());
            Require(remaining.Count > 0, "the query exceeded its supplied bounded exchange");
            return remaining.Dequeue();
        }, retrySilence);
        return (result, requests);
    }

    private static byte[] Challenge(int value)
    {
        var reply = new byte[] { 0xff, 0xff, 0xff, 0xff, 0x41, 0, 0, 0, 0 };
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(5), value);
        return reply;
    }

    private static byte[] Info(byte online, byte capacity) =>
    [
        0xff, 0xff, 0xff, 0xff, 0x49, 17,
        .. Encoding.ASCII.GetBytes("Synthetic\0fixture-world\0valheim\0Valheim\0"),
        0, 0, online, capacity, 0, (byte)'d', (byte)'w', 1, 0,
        .. Encoding.ASCII.GetBytes("fixture\0")
    ];

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
