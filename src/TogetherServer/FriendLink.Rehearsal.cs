using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    private readonly SemaphoreSlim rehearsalGate = new(1, 1);
    internal static RemoteRehearsalReport EmptyRehearsal(string context) => new(1,
        DateTimeOffset.UtcNow, context,
        [
            new("listener", "Unverified", "The staging Host listener has not been checked."),
            new("outsideTcp", "Unverified", "No independent outside TCP check is part of this run."),
            new("connection", "Unverified", "Pinned HTTPS and device access have not been checked."),
            new("chat", "Unverified", "A two-way signed room exchange has not been checked."),
            new("transfer", "Unverified", "Synthetic bytes, hashes and an exact-copy receipt have not been checked."),
            new("gameEndpoint", "Unverified", "No supported running game has been queried."),
            new("humanJoinLoad", "Unverified", "A human game join, load and saved restart remain separate checks.")
        ]);

    public async Task<RemoteRehearsalReport> RunRemoteRehearsalAsync(Guid profileId,
        RemoteRehearsalRequest request, CancellationToken cancellationToken = default)
    {
        var initial = EmptyRehearsal(RemoteRehearsal.Context(config?.Endpoint ?? "", request.NetworkContext));
        var stages = initial.Stages.ToArray();
        RemoteRehearsalReport Report() => initial with { GeneratedUtc = DateTimeOffset.UtcNow, Stages = stages };
        void Stage(int index, string state, string detail) => stages[index] = stages[index] with { State = state, Detail = detail };
        if (request.RequestId == Guid.Empty || !RemoteRehearsal.ValidContext(request.NetworkContext) || !TryRetain())
            return Report();
        var entered = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            entered = await rehearsalGate.WaitAsync(0, timeout.Token);
            if (!entered) { Stage(2, "Failed", "A rehearsal is already running on this connection."); return Report(); }
            var fresh = await PollAsync();
            if (fresh.State is not ("Connected" or "Disabled") ||
                fresh.LastConnectedUtc is null || !fresh.Profiles.Any(profile => profile.Id == profileId))
            { Stage(2, "Failed", "The saved connection or server assignment was not authenticated."); return Report(); }
            Stage(2, "Passed", "A fresh status reply matched pinned HTTPS and authenticated saved access.");
            if (!fresh.HostCapabilities.Contains(CompanionProtocol.RemoteRehearsalCapability))
            { Stage(3, "Unavailable", "Update the Host app before running a rehearsal."); return Report(); }
            if (!LocalSharedWorldStatus(profileId).Consented)
            { Stage(4, "Unavailable", "Allow saves on this PC and obtain the Host's Receive grant first."); return Report(); }
            // Consent alone does not enroll a signing identity. Reuse the normal
            // challenge/roster/history checks before asking for a test exchange.
            var checkedCopy = await CheckSharedWorldAsync(profileId, timeout.Token);
            if (!checkedCopy.Ok)
            { Stage(4, "Unavailable", "The current Receive grant, signing enrollment or shared history needs review."); return Report(); }
            var connection = config;
            if (connection is null) return Report();
            async Task<RemoteRehearsalExchange?> Exchange(Guid messageId)
            {
                using var response = await HostClient().PostAsJsonAsync(
                    $"api/companion/servers/{profileId}/rehearsal/exchange",
                    new RemoteRehearsalExchangeRequest(request.RequestId, messageId), Json, timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                var bytes = await ReadBoundedSharedAsync(response.Content, 4096, timeout.Token);
                return bytes is null ? null : JsonSerializer.Deserialize<RemoteRehearsalExchange>(bytes, Json);
            }
            var ready = await Exchange(Guid.Empty);
            if (ready is not { Ok: true, VersionHash.Length: 64 })
            { Stage(3, "Unavailable", "Prepare the disposable staging server and review chat and Receive access."); return Report(); }
            Stage(0, ready.LocalListener ? "Passed" : "Unverified", "The authenticated staging Host reported its local listener state.");
            ChatRoomView sent;
            await gate.WaitAsync(timeout.Token);
            try
            {
                if (!ReferenceEquals(connection, config)) return Report();
                var local = LocalChat(profileId);
                if (!local.Ok) { Stage(3, "Failed", "Room access is unavailable."); return Report(); }
                if (!local.Entries.Any(entry => entry.Id == request.RequestId))
                    chat.Queue(connection.HostId, profileId, connection.DeviceId,
                        RemoteRehearsal.Message(request.RequestId), request.RequestId);
                sent = await SyncChatCoreAsync(profileId);
            }
            finally { gate.Release(); }
            if (!sent.Ok || !sent.Entries.Any(entry => entry.Id == request.RequestId &&
                entry.AuthorId == connection.DeviceId && entry.Text == RemoteRehearsal.Message(request.RequestId)))
            { Stage(3, "Failed", "The fixed rehearsal message was not accepted in the disposable room."); return Report(); }
            var exchange = await Exchange(request.RequestId);
            var reply = await SyncChatAsync(profileId);
            if (exchange is not { Ok: true, ReplyId: not null } ||
                exchange.VersionHash != ready.VersionHash || !reply.Ok ||
                !reply.Entries.Any(entry => entry.Id == exchange.ReplyId && entry.AuthorId == Guid.Empty))
            { Stage(3, "Failed", "The signed Host reply or exact rehearsal copy did not match."); return Report(); }
            Stage(3, "Passed", "The test PC and Host exchanged signed messages in the disposable server room.");
            var transfer = await PullSharedWorldAsync(profileId, timeout.Token);
            if (!transfer.Ok)
            { Stage(4, "Failed", "Synthetic transfer did not finish verification. Retry preserves resumable data."); return Report(); }
            var confirmed = await Exchange(request.RequestId);
            if (confirmed is not { Ok: true, ReceiptConfirmed: true } ||
                confirmed.VersionHash != ready.VersionHash || !ReferenceEquals(config, connection))
            { Stage(4, "Failed", "The Host has not confirmed a signed receipt for this exact synthetic copy."); return Report(); }
            Stage(4, "Passed", "Synthetic payload hashes passed and the Host verified this PC's exact-copy signed receipt. Game load is unverified.");
            var game = fresh.Profiles.FirstOrDefault(profile => profile.State == "Ready" &&
                (profile.Kind is GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) &&
                profile.JoinAddress is not null);
            if (game is not null)
            {
                var probe = GameEndpointProbe.Check(game);
                Stage(5, probe.Answered ? "Passed" : "Failed",
                    probe.Answered ? "A fixed query to an assigned running game answered from this PC. No join is proved." :
                    "The fixed game query did not answer from this PC.");
            }
            return Report();
        }
        catch (OperationCanceledException)
        { Stage(stages[3].State == "Passed" ? 4 : 3, "Failed", "The rehearsal was interrupted or timed out. Retry uses verified copies."); return Report(); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Stage(stages[3].State == "Passed" ? 4 : 3, "Failed", "The rehearsal could not finish safely. Review staging access and retry."); return Report(); }
        finally { if (entered) rehearsalGate.Release(); ReleaseRetained(); }
    }
}
