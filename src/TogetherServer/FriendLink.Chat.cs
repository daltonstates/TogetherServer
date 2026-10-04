using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    private ChatRoomView LocalChat(Guid profileId, string code = "ChatReady",
        string message = "Messages on this PC are shown below.")
    {
        var current = config;
        if (current is null || current.HostId == Guid.Empty)
            return new(false, "NotPaired", "Connect to a Host first.", Guid.Empty,
                profileId, [], []);
        if (view.State is "Revoked" or "Access expired" ||
            current.ChatDeniedProfiles?.Contains(profileId) == true)
            return new(false, "ChatAccessDenied", "The Host has removed this PC from this chat room.",
                current.HostId, profileId, [], []);
        var profiles = view.State is "Connected" or "Disabled" ? view.Profiles :
            current.CachedProfiles ?? [];
        if (!profiles.Any(profile => profile.Id == profileId))
            return new(false, "UnknownProfile", "This server is not available to this PC.",
                current.HostId, profileId, [], []);
        return new(true, code, message, current.HostId, profileId,
            chat.Read(current.HostId, profileId),
            chat.Pending(current.HostId, profileId, current.DeviceId));
    }

    public ChatRoomView ChatRoom(Guid profileId) => LocalChat(profileId);

    public async Task<ChatRoomView> PostChatAsync(Guid profileId, string? text)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This saved connection is closing.",
            Guid.Empty, profileId, [], []);
        await gate.WaitAsync();
        try
        {
            var local = LocalChat(profileId);
            if (!local.Ok) return local;
            if (!ServerChat.ValidText(text))
                return local with
                {
                    Ok = false,
                    Code = "InvalidChatText",
                    Message = "Write a message of at most 500 characters."
                };
            try { chat.Queue(config!.HostId, profileId, config.DeviceId, text!); }
            catch (InvalidOperationException)
            {
                return local with
                {
                    Ok = false,
                    Code = "ChatQueueFull",
                    Message = "Send queued messages before adding more."
                };
            }
            if (view.State is "Connected" or "Disabled") return await SyncChatCoreAsync(profileId);
            return LocalChat(profileId, "ChatQueued", "Saved on this PC. It will sync when the Host is reachable.");
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<ChatRoomView> SyncChatAsync(Guid profileId)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This saved connection is closing.",
            Guid.Empty, profileId, [], []);
        await gate.WaitAsync();
        try { return await SyncChatCoreAsync(profileId); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    private async Task<ChatRoomView> SyncChatCoreAsync(Guid profileId)
    {
        var local = LocalChat(profileId);
        if (!local.Ok && local.Code != "ChatAccessDenied") return local;
        if (view.State is not ("Connected" or "Disabled"))
            return local with
            {
                Code = "ChatOffline",
                Message = "Showing the copy on this PC. Messages will sync after reconnection."
            };
        if (config is null || view.HostCapabilities?.Contains(CompanionProtocol.ServerChatCapability) != true)
            return local with
            {
                Ok = false,
                Code = "ChatUpdateRequired",
                Message = "Update the Host app before using this server's chat room."
            };
        try
        {
            using var response = await HostClient().PostAsJsonAsync(
                $"api/companion/servers/{profileId}/chat/sync",
                new ChatSyncRequest(local.Entries, local.Pending), Json);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var denial = await ReadBoundedChatPayloadAsync(response.Content);
                if (denial is not null)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(denial);
                        if (document.RootElement.TryGetProperty("code", out var code) &&
                            code.GetString() == "ChatAccessDenied")
                        {
                            config.ChatDeniedProfiles.Add(profileId);
                            SaveConfig();
                            return LocalChat(profileId);
                        }
                    }
                    catch (JsonException) { /* A generic 403 is not a room removal. */ }
                }
                return local with
                {
                    Code = "ChatUnavailable",
                    Message = "The Host could not sync chat. Messages on this PC are kept."
                };
            }
            if (!response.IsSuccessStatusCode)
                return local with
                {
                    Code = "ChatUnavailable",
                    Message = "The Host could not sync chat. Messages on this PC are kept."
                };
            var bytes = await ReadBoundedChatPayloadAsync(response.Content);
            if (bytes is null)
                return local with
                {
                    Code = "ChatInvalidResponse",
                    Message = "The Host returned too much chat data."
                };
            ChatSyncResponse? remote;
            try { remote = JsonSerializer.Deserialize<ChatSyncResponse>(bytes, Json); }
            catch (JsonException) { remote = null; }
            if (remote is not { Ok: true, PublicKey: not null, Entries: not null } ||
                remote.HostId != config.HostId || remote.ProfileId != profileId ||
                remote.Entries.Count > ServerChat.MaximumEntries ||
                config.ChatOwnerKeys.TryGetValue(profileId, out var pinned) &&
                pinned != remote.PublicKey ||
                !chat.Merge(config.HostId, profileId, remote.Entries, remote.PublicKey))
                return local with
                {
                    Ok = false,
                    Code = "ChatCopyRejected",
                    Message = "The Host's chat copy or signing identity did not match this saved connection."
                };
            if (!config.ChatOwnerKeys.ContainsKey(profileId))
            {
                config.ChatOwnerKeys[profileId] = remote.PublicKey;
                SaveConfig();
            }
            config.ChatDeniedProfiles.Remove(profileId);
            chat.Confirm(config.HostId, profileId, config.DeviceId);
            return LocalChat(profileId, "ChatSynced", "Messages are up to date.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                                   InvalidDataException or UnauthorizedAccessException or CryptographicException or
                                   ArgumentException or InvalidOperationException)
        {
            return local with
            {
                Code = "ChatOffline",
                Message = "Showing the copy on this PC. Messages will sync after reconnection."
            };
        }
    }

    private static async Task<byte[]?> ReadBoundedChatPayloadAsync(HttpContent content)
    {
        if (content.Headers.ContentLength > ServerChat.MaximumWireBytes) return null;
        await using var source = await content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = ServerChat.MaximumWireBytes - (int)output.Length;
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)));
            if (read == 0) return output.ToArray();
            if (read > remaining) return null;
            output.Write(buffer, 0, read);
        }
    }
}
