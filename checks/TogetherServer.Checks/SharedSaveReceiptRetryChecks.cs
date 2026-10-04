using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using TogetherServer;

internal static class SharedSaveReceiptRetryChecks
{
    internal static async Task RunAsync(string root, string fixture)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var host = new LocalData(Path.Combine(root, "host"));
        using var receiver = new LocalData(Path.Combine(root, "receiver"));
        var world = Path.Combine(host.RootPath, "world");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "world.dat"), "disposable retained-copy world");
        var profile = new ServerProfile
        {
            Name = "Retained retry",
            Kind = "Fixture",
            WorldId = "retained-retry",
            WorldDirectory = world,
            ExecutablePath = fixture,
            GamePort = 51385,
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 },
            SharedSavesEnabled = true
        };
        const string endpoint = "https://127.0.0.1:51388";
        var settings = new HostSettings
        {
            Profiles = [profile],
            CompanionEndpoint = endpoint,
            CompanionPort = 51388,
            CompanionBindAddress = "127.0.0.1",
            CompanionListeningEnabled = true
        };
        host.SaveSettings(settings);
        using var certificate = new HostIdentity(host).Ensure(endpoint);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid();
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        host.SavePairingState(new PairingPersistentState
        {
            Devices = [new PairedDevice
            {
                Id = deviceId, AssignedProfileIds = [profile.Id],
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1), SharedWorldPublicKey = publicKey,
                SharedWorldGrants = new() { [profile.Id] = new(Receive: true) }
            }]
        });
        var games = new GameServerRegistry(host, true, PortProbeMode.ObserveOnly);
        var pairing = new PairingService(host);
        var manager = new HostManager(host, games);
        var roster = await manager.PublishSharedWorldRosterAsync(profile.Id,
            [new(deviceId, publicKey, new(Receive: true), false)]);
        pairing.ConfirmSharedRosterPublished(profile.Id, roster);
        var backups = new WorldBackupService(host, TimeProvider.System);
        var backup = backups.Create(profile, BackupKinds.Rolling);
        Require(backup.Ok && backup.Backup is not null, "disposable backup failed");
        var version = new SharedWorldService(host, backups).PublishAfterStop(profile, backup.Backup!.Id).Version;
        Require(version is not null, "disposable shared copy was not published");
        receiver.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", key.ExportPkcs8PrivateKey());
        receiver.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            Endpoint = endpoint,
            DeviceId = deviceId,
            Credential = credential,
            Fingerprint = HostIdentity.Fingerprint(certificate),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            ConsentedSharedWorldProfiles = [profile.Id],
            SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey },
            ApprovedSharedWorldGroups = new() { [profile.Id] = roster.GroupId },
            SharedRosterFloors = new() { [profile.Id] = new(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
        }, json));
        using var companionGate = new SemaphoreSlim(1, 1);
        var companion = new CompanionServer(host, manager, pairing, games,
            new ServerLogService(host, manager), companionGate, 51390,
            inMemoryTransport: builder => builder.UseTestServer());
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseTestServer();
        await using var app = companion.BuildInMemoryApp(builder, settings, new Uri(endpoint));
        await app.StartAsync();
        var receipts = 0;
        var manifestReads = 0;
        Func<Task>? afterLatestRead = null;
        using var friend = new FriendLink(receiver, "friend.protected", (_, _) =>
            new HttpClient(new ReceiptHandler(app.GetTestServer().CreateHandler(), () => receipts++, async () =>
            {
                if (afterLatestRead is null || ++manifestReads != 2) return;
                var review = afterLatestRead;
                afterLatestRead = null;
                await review();
            }))
            { BaseAddress = new Uri(endpoint + "/") });
        Require((await friend.PollAsync()).Profiles.Any(item => item.Id == profile.Id), "Friend did not authenticate");
        var first = await friend.PullSharedWorldAsync(profile.Id);
        Require(first.Ok && receipts == 1, $"initial receive failed: {first.Code} {first.Message}");
        var vault = Path.Combine(receiver.RootPath, "received-shared-worlds", deviceId.ToString("N"), profile.Id.ToString("N"));
        var payload = Path.Combine(vault, version!.VersionHash);
        var pointer = Path.Combine(vault, "latest.json");
        Require(Directory.Exists(payload) && File.Exists(pointer), "initial receipt was not durable");
        // Model a crash after the atomic payload move and before writing the pointer.
        File.Delete(pointer);
        receipts = 0;
        var retry = await friend.PullSharedWorldAsync(profile.Id);
        Require(retry.Ok && FriendLink.ReadReceivedLatest(vault)?.VersionHash == version.VersionHash,
            $"retained-copy retry did not repair the receipt: {retry.Code} {retry.Message}");
        Require(receipts == 1 && new SharedWorldService(host, backups).Status(profile).ConfirmedCopies == 1,
            "retained-copy retry omitted its exact-copy Host confirmation");
        Require(!Directory.Exists(Path.Combine(vault, ".partial-" + version.VersionHash)),
            "retained-copy retry kept a duplicate partial payload");
        // The latest HTTP response can be in flight when a separate Check
        // observes a newer signed save. A retained payload must not bypass
        // the final gate and install that now-stale response as current.
        File.Delete(pointer);
        receipts = 0;
        SharedWorldVersion? newer = null;
        afterLatestRead = async () =>
        {
            File.WriteAllText(Path.Combine(world, "world.dat"), "newer disposable world");
            var nextBackup = backups.Create(profile, BackupKinds.Rolling);
            Require(nextBackup.Ok && nextBackup.Backup is not null, "newer backup failed");
            newer = new SharedWorldService(host, backups).PublishAfterStop(profile, nextBackup.Backup!.Id).Version;
            Require(newer is not null && newer.Number == version.Number + 1, "newer copy was not published");
            var checkedHead = await friend.CheckSharedWorldAsync(profile.Id);
            Require(checkedHead.Ok && checkedHead.Status?.HostVersion == newer!.Number,
                "concurrent Check did not observe the newer signed head");
        };
        var raced = await friend.PullSharedWorldAsync(profile.Id);
        Require(!raced.Ok && raced.Code == "NewerVersionAvailable" &&
            !File.Exists(pointer) && receipts == 0 && Directory.Exists(payload),
            "a retained-copy retry promoted a stale head after a concurrent Check");
        var latest = await friend.PullSharedWorldAsync(profile.Id);
        Require(latest.Ok && newer is not null && FriendLink.ReadReceivedLatest(vault)?.VersionHash == newer.VersionHash &&
            receipts == 1 && Directory.Exists(payload),
            "receiving the observed newer head failed or discarded the earlier verified copy");
        await app.StopAsync();
    }

    private sealed class ReceiptHandler(HttpMessageHandler inner, Action received, Func<Task> manifestRead) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/receipts", StringComparison.Ordinal) == true)
                received();
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/shared-world", StringComparison.Ordinal) == true)
                await manifestRead();
            return response;
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
