using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteRehearsalRequest(Guid RequestId, string NetworkContext = "Unspecified");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteRehearsalExchangeRequest(Guid RequestId, Guid ChatMessageId);
public sealed record RemoteRehearsalSetup(bool Ok, string Code, string Message, Guid? ProfileId = null);
public sealed record RemoteRehearsalExchange(bool Ok, string Code, Guid? ReplyId = null,
    string? VersionHash = null, bool ReceiptConfirmed = false, bool LocalListener = false);
public sealed record RehearsalStage(string Id, string State, string Detail);
public sealed record RemoteRehearsalReport(int Schema, DateTimeOffset GeneratedUtc,
    string NetworkContext, IReadOnlyList<RehearsalStage> Stages);

internal static class RemoteRehearsal
{
    internal const string MarkerName = "remote-rehearsal.protected";
    internal sealed record Marker(int Schema, Guid ProfileId, string WorldDirectory);
    internal static Marker? Read(LocalData data)
    {
        var bytes = data.LoadProtected(MarkerName);
        if (bytes is null) return null;
        var marker = JsonSerializer.Deserialize<Marker>(bytes);
        return marker is { Schema: 1, ProfileId: var id } && id != Guid.Empty &&
            marker.WorldDirectory == data.NewWorldDirectory(id) ? marker : null;
    }

    internal static bool Matches(LocalData data, ServerProfile profile) =>
        Read(data) is { } marker && marker.ProfileId == profile.Id &&
        profile.Kind == GameKinds.Fixture && profile.WorldId == "connection-rehearsal" &&
        profile.WorldDirectory == marker.WorldDirectory &&
        profile.ExecutablePath == Path.Combine(data.RootPath, "remote-rehearsal.no-game");

    internal static string Message(Guid requestId) => "Connection rehearsal " + requestId.ToString("N");
    internal static Guid ReplyId(Guid requestId, Guid profileId, Guid deviceId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"rehearsal:{requestId:N}:{profileId:N}:{deviceId:N}"))
            .AsSpan(0, 16));

    internal static string Context(string endpoint, string requested) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)
            ? "Loopback" : requested switch
            {
                "SameLan" => "OwnerReportedSameLan",
                "SeparateNetwork" => "OwnerReportedSeparateNetwork",
                _ => "Unspecified"
            };

    internal static bool ValidContext(string value) => value is "Unspecified" or "SameLan" or "SeparateNetwork";
}

public sealed partial class HostManager
{
    // This fixed owner action creates only synthetic app-owned data. Pairing,
    // room membership, Receive grants and Friend consent remain separate actions.
    public async Task<RemoteRehearsalSetup> PrepareRemoteRehearsalAsync(bool staging)
    {
        if (!staging) return new(false, "StagingRequired", "Open the development Host to prepare a rehearsal.");
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return new(false, "DataRecoveryRequired", "Review local data recovery first.");
            var marker = RemoteRehearsal.Read(data);
            ServerProfile profile;
            if (marker is null)
            {
                if (data.HasProtected(RemoteRehearsal.MarkerName) || settings.Profiles.Any(item =>
                    item.Kind == GameKinds.Fixture && item.WorldId == "connection-rehearsal"))
                    return new(false, "RehearsalReviewRequired", "The disposable rehearsal needs owner review.");
                profile = new ServerProfile
                {
                    Name = "Connection rehearsal",
                    ServerName = "Connection rehearsal",
                    Kind = GameKinds.Fixture,
                    WorldId = "connection-rehearsal",
                    WorldSource = "New",
                    SharedSavesEnabled = true,
                    Backups = new() { Enabled = true, RetentionCount = 2 }
                };
                profile.ExecutablePath = Path.Combine(data.RootPath, "remote-rehearsal.no-game");
                profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, profile.WorldDirectory);
                if (Directory.Exists(profile.WorldDirectory))
                    return new(false, "RehearsalReviewRequired", "The disposable directory already exists.");
                Directory.CreateDirectory(profile.WorldDirectory);
                // Multiple chunks exercise transfer and receipt, without a real world.
                var bytes = new byte[SharedWorldService.ChunkBytes * 3 + 17];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
                File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "rehearsal.bin"), bytes);
                settings.Profiles.Add(profile);
                data.SaveSettings(settings);
                data.SaveProtected(RemoteRehearsal.MarkerName, JsonSerializer.SerializeToUtf8Bytes(
                    new RemoteRehearsal.Marker(1, profile.Id, profile.WorldDirectory)));
            }
            else
            {
                var saved = settings.Profiles.SingleOrDefault(item => item.Id == marker.ProfileId);
                if (saved is null || !RemoteRehearsal.Matches(data, saved) ||
                    runs.Any(run => run.ProfileId == saved.Id))
                    return new(false, "RehearsalReviewRequired", "The disposable rehearsal setup changed.");
                profile = saved;
            }
            if (!profile.SharedSavesEnabled || SharedAuthorityBlocked(profile.Id, out _))
                return new(false, "RehearsalReviewRequired", "Review this rehearsal's shared-save state.");
            if (sharedWorlds.ReadRoster(profile) is null)
                sharedWorlds.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
            if (sharedWorlds.Status(profile).Latest is null)
            {
                // The transfer adapter admits rolling checkpoints. This synthetic
                // fixture has no game process and carries no real save claim.
                var backup = backups.Create(profile, BackupKinds.Rolling, profile.WorldDirectory);
                if (!backup.Ok || backup.Backup is null)
                    return new(false, "RehearsalBackupFailed", "The synthetic checkpoint could not be completed.");
                var published = sharedWorlds.PublishAfterStop(profile, backup.Backup.Id);
                if (!published.Ok)
                    return new(false, "RehearsalPublicationFailed", "The synthetic copy could not be published.");
            }
            AdvanceReadModel();
            Volatile.Write(ref lastOwnerSnapshot, Snapshot());
            return new(true, "RehearsalPrepared",
                "Pair the test PC to Connection rehearsal, grant Receive, and allow saves on that PC. No game world is used.", profile.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            CryptographicException or JsonException or ArgumentException)
        { return new(false, "RehearsalReviewRequired", "The synthetic rehearsal could not be verified. Production data was not used."); }
        finally { gate.Release(); }
    }

    internal async Task<RemoteRehearsalExchange> RehearsalExchangeAsync(Guid profileId,
        Guid deviceId, Guid requestId, Guid messageId, ServerChat chat, Guid hostId, bool localListener)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null || !RemoteRehearsal.Matches(data, profile) ||
                !profile.SharedSavesEnabled || SharedAuthorityBlocked(profileId, out _))
                return new(false, "RehearsalNotPrepared");
            var version = sharedWorlds.Status(profile).Latest;
            var roster = sharedWorlds.ReadRoster(profile);
            var member = roster?.Members.SingleOrDefault(item => item.DeviceId == deviceId);
            if (version is null || roster is null || member is not { Revoked: false, Grants.Receive: true } ||
                member.AccessExpiresUtc is { } expiry && expiry <= clock.GetUtcNow())
                return new(false, "RehearsalReceiveRequired");
            if (messageId == Guid.Empty)
                return new(true, "RehearsalReady", VersionHash: version.VersionHash, LocalListener: localListener);
            if (!chat.Read(hostId, profileId).Any(entry => entry.Id == messageId &&
                entry.AuthorId == deviceId && entry.Text == RemoteRehearsal.Message(requestId)))
                return new(false, "RehearsalMessageMissing");
            var replyId = RemoteRehearsal.ReplyId(requestId, profileId, deviceId);
            chat.Post(hostId, profileId, Guid.Empty, "Host", "Rehearsal reply received", replyId);
            var receipt = sharedWorlds.VerifiedReceipt(profile, version, deviceId, roster, requireEligibleHost: false);
            return new(true, "RehearsalExchanged", replyId, version.VersionHash, receipt is not null, localListener);
        }
        finally { gate.Release(); }
    }
}
