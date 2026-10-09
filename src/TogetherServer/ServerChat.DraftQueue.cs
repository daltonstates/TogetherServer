using System.Text.Json;

namespace TogetherServer;

public sealed record ChatQueueEditRequest(string Text, string ExpectedText);
public sealed record ChatQueueCancelRequest(string ExpectedText);
public sealed record ChatQueueMutationResult(bool Ok, string Code, string Message);
public sealed record ChatRoomSummaryView(bool Ok, string Code, string Message,
    Guid HostId, Guid ProfileId, IReadOnlyList<Guid> MessageIds,
    PinnedServerNotice? Notice = null, bool NoticeCached = false, bool NoticeSupported = false);

public sealed partial class ServerChat
{
    public const int MaximumQueueMutationBytes = 8 * 1024;
    public const int MaximumPostBytes = 4 * 1024;

    // Sent entries may already have been accepted when an HTTP reply is lost.
    // Persist this before dispatch so restart cannot offer to mutate that ID.
    public IReadOnlyList<ChatDraft> MarkPendingSubmitted(Guid hostId, Guid profileId, Guid deviceId)
    {
        lock (sync)
        {
            var drafts = Pending(hostId, profileId, deviceId).ToList();
            if (drafts.Any(draft => draft.Submitted != true))
            {
                drafts = drafts.Select(draft => draft with { Submitted = true }).ToList();
                SavePending(hostId, profileId, deviceId, drafts);
            }
            return drafts;
        }
    }

    public ChatQueueMutationResult EditQueued(Guid hostId, Guid profileId, Guid deviceId,
        Guid draftId, ChatQueueEditRequest request) => ChangeQueued(hostId, profileId, deviceId,
            draftId, request.ExpectedText, request.Text);

    public ChatQueueMutationResult CancelQueued(Guid hostId, Guid profileId, Guid deviceId,
        Guid draftId, ChatQueueCancelRequest request) => ChangeQueued(hostId, profileId, deviceId,
            draftId, request.ExpectedText, null);

    private ChatQueueMutationResult ChangeQueued(Guid hostId, Guid profileId, Guid deviceId,
        Guid draftId, string expectedText, string? nextText)
    {
        if (hostId == Guid.Empty || profileId == Guid.Empty || deviceId == Guid.Empty ||
            draftId == Guid.Empty || !ValidText(expectedText) || nextText is not null && !ValidText(nextText))
            return new(false, "InvalidChatDraft", "Use a queued message of at most 500 characters.");
        lock (sync)
        {
            // Never alter an accepted entry, including a local copy recovered before Confirm.
            if (Read(hostId, profileId).Any(entry => entry.Id == draftId))
                return new(false, "ChatDraftAccepted", "The Host already accepted this message. It cannot be edited or canceled.");
            var drafts = Pending(hostId, profileId, deviceId).ToList();
            var index = drafts.FindIndex(draft => draft.Id == draftId);
            if (index < 0)
                return new(false, "ChatDraftMissing", "This message is no longer in the queue. Sync the room.");
            if (drafts[index].Submitted != false)
                return new(false, "ChatDraftSubmitted", "This message was sent and is awaiting confirmation. Sync the room before making another change.");
            if (!string.Equals(drafts[index].Text, expectedText, StringComparison.Ordinal))
                return new(false, "ChatDraftChanged", "This queued message changed. Review its current text before editing or canceling.");
            if (nextText is null) drafts.RemoveAt(index);
            else drafts[index] = drafts[index] with { Text = nextText };
            SavePending(hostId, profileId, deviceId, drafts);
            return nextText is null
                ? new(true, "ChatDraftCanceled", "The unsent message was removed from this PC's queue.")
                : new(true, "ChatDraftEdited", "The unsent message was updated on this PC.");
        }
    }

    private void SavePending(Guid hostId, Guid profileId, Guid deviceId, IReadOnlyList<ChatDraft> drafts) =>
        data.SaveProtected(PendingFile(hostId, profileId, deviceId), JsonSerializer.SerializeToUtf8Bytes(drafts, Json));

    public static ChatRoomSummaryView Summarize(ChatRoomView room) => new(room.Ok, room.Code,
        room.Message, room.HostId, room.ProfileId,
        room.Ok ? room.Entries.Select(entry => entry.Id).ToArray() : [],
        room.Ok ? room.Notice : null, room.NoticeCached, room.NoticeSupported);

    public static bool TryQueueEdit(ReadOnlyMemory<byte> bytes, out ChatQueueEditRequest? request)
    {
        request = null;
        if (!TryQueueObject(bytes, ["text", "expectedText"], out var document)) return false;
        using (document)
        {
            var text = document!.RootElement.GetProperty("text");
            var expected = document.RootElement.GetProperty("expectedText");
            if (text.ValueKind != JsonValueKind.String || expected.ValueKind != JsonValueKind.String ||
                !ValidText(text.GetString()) || !ValidText(expected.GetString())) return false;
            request = new(text.GetString()!, expected.GetString()!);
            return true;
        }
    }

    public static bool TryChatPost(ReadOnlyMemory<byte> bytes, out ChatPostRequest? request)
    {
        request = null;
        if (bytes.Length > MaximumPostBytes || !TryQueueObject(bytes, ["text"], out var document)) return false;
        using (document)
        {
            var text = document!.RootElement.GetProperty("text");
            if (text.ValueKind != JsonValueKind.String || !ValidText(text.GetString())) return false;
            request = new(text.GetString());
            return true;
        }
    }

    public static bool TryQueueCancel(ReadOnlyMemory<byte> bytes, out ChatQueueCancelRequest? request)
    {
        request = null;
        if (!TryQueueObject(bytes, ["expectedText"], out var document)) return false;
        using (document)
        {
            var expected = document!.RootElement.GetProperty("expectedText");
            if (expected.ValueKind != JsonValueKind.String || !ValidText(expected.GetString())) return false;
            request = new(expected.GetString()!);
            return true;
        }
    }

    private static bool TryQueueObject(ReadOnlyMemory<byte> bytes, string[] keys, out JsonDocument? document)
    {
        document = null;
        if (bytes.Length is < 2 or > MaximumQueueMutationBytes) return false;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Length == keys.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
                    names.All(name => keys.Contains(name, StringComparer.Ordinal))) return true;
            }
            document.Dispose(); document = null; return false;
        }
        catch (JsonException) { document?.Dispose(); document = null; return false; }
    }
}
