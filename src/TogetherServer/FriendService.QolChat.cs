namespace TogetherServer;

public sealed partial class FriendService
{
    private FriendLink? SelectedChatLink(Guid connectionId)
    {
        // Guid.Empty identifies the protected pre-index legacy link. It is a
        // valid scope only while that exact saved link is currently selected.
        lock (sync) return disposed || selectedId != connectionId
            ? null : links.FirstOrDefault(item => item.Id == connectionId).Link;
    }

    private static ChatRoomView ChangedChat(Guid profileId) => new(false, "ConnectionChanged",
        "Choose the current saved Host before using this chat room.", Guid.Empty, profileId, [], []);

    private async Task<ChatRoomView> WithSelectedChatAsync(Guid connectionId, Guid profileId,
        Func<FriendLink, Task<ChatRoomView>> action)
    {
        var link = SelectedChatLink(connectionId);
        if (link is null) return ChangedChat(profileId);
        var room = await action(link);
        return ReferenceEquals(link, SelectedChatLink(connectionId)) ? room : ChangedChat(profileId);
    }

    public Task<ChatRoomView> SyncChatAsync(Guid connectionId, Guid profileId, CancellationToken token) =>
        WithSelectedChatAsync(connectionId, profileId, link => link.SyncChatAsync(profileId, token));
    public Task<ChatRoomView> PostChatAsync(Guid connectionId, Guid profileId, string? text, CancellationToken token) =>
        WithSelectedChatAsync(connectionId, profileId, link => link.PostChatAsync(profileId, text, token));
    public Task<ChatRoomView> EditQueuedChatAsync(Guid connectionId, Guid profileId, Guid draftId,
        ChatQueueEditRequest request, CancellationToken token) =>
        WithSelectedChatAsync(connectionId, profileId, link => link.EditQueuedChatAsync(profileId, draftId, request, token));
    public Task<ChatRoomView> CancelQueuedChatAsync(Guid connectionId, Guid profileId, Guid draftId,
        ChatQueueCancelRequest request, CancellationToken token) =>
        WithSelectedChatAsync(connectionId, profileId, link => link.CancelQueuedChatAsync(profileId, draftId, request, token));
    public async Task<ChatRoomSummaryView> ChatSummaryAsync(Guid connectionId, Guid profileId, CancellationToken token)
    {
        var link = SelectedChatLink(connectionId);
        if (link is null) return ServerChat.Summarize(ChangedChat(profileId));
        var summary = await link.ChatSummaryAsync(profileId, token);
        return ReferenceEquals(link, SelectedChatLink(connectionId)) ? summary : ServerChat.Summarize(ChangedChat(profileId));
    }
}
