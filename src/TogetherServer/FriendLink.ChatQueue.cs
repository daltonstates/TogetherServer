using System.Security.Cryptography;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    public Task<ChatRoomView> EditQueuedChatAsync(Guid profileId, Guid draftId,
        ChatQueueEditRequest request, CancellationToken cancellationToken = default) =>
        MutateQueuedChatAsync(profileId, draftId, request, null, cancellationToken);

    public Task<ChatRoomView> CancelQueuedChatAsync(Guid profileId, Guid draftId,
        ChatQueueCancelRequest request, CancellationToken cancellationToken = default) =>
        MutateQueuedChatAsync(profileId, draftId, null, request, cancellationToken);

    private async Task<ChatRoomView> MutateQueuedChatAsync(Guid profileId, Guid draftId,
        ChatQueueEditRequest? edit, ChatQueueCancelRequest? cancel, CancellationToken cancellationToken)
    {
        if (!TryRetain()) return ClosedChat(profileId);
        var entered = false;
        try
        {
            // PollAsync, SyncChatAsync and PostChatAsync use this same gate.
            await gate.WaitAsync(cancellationToken);
            entered = true;
            var local = LocalChat(profileId);
            if (!local.Ok || config is null) return local;
            var result = edit is not null
                ? chat.EditQueued(config.HostId, profileId, config.DeviceId, draftId, edit)
                : chat.CancelQueued(config.HostId, profileId, config.DeviceId, draftId, cancel!);
            // Queue operations are local-only; no Host request or lifecycle side effect.
            return LocalChat(profileId, result.Code, result.Message) with { Ok = result.Ok };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or
                                   UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "ChatQueueUnavailable", "The protected queue could not be changed. Sync the room and try again.",
                config?.HostId ?? Guid.Empty, profileId, [], []);
        }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    public async Task<ChatRoomSummaryView> ChatSummaryAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return ServerChat.Summarize(ClosedChat(profileId));
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            entered = true;
            // Read only the currently authorized local copy. Background sync stays
            // responsible for the pinned Host exchange; closed cards never send drafts.
            return ServerChat.Summarize(LocalChat(profileId));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or
                                   UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "ChatReviewRequired", "This room's protected copy needs review.",
                config?.HostId ?? Guid.Empty, profileId, []);
        }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    private static ChatRoomView ClosedChat(Guid profileId) =>
        new(false, "ConnectionClosed", "This saved connection is closing.", Guid.Empty, profileId, [], []);
}
