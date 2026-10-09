using System.Text.Json;

namespace TogetherServer;

// A native owner-requested Quit may await only a fixed acknowledgement from
// the rendered local page. No draft content or lifecycle command crosses here.
internal sealed class DesktopDraftFlushGate
{
    internal const int MaximumReplyCharacters = 256;
    private readonly Uri address;
    private readonly object sync = new();
    private Pending? pending;
    private sealed record Pending(Guid Id, TaskCompletionSource<bool> Completion);

    internal DesktopDraftFlushGate(Uri address) => this.address = address;

    internal bool TryBegin(out Guid requestId, out Task<bool> completion)
    {
        lock (sync)
        {
            if (pending is not null)
            {
                requestId = Guid.Empty;
                completion = Task.FromResult(false);
                return false;
            }
            requestId = Guid.NewGuid();
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = new(requestId, source);
            completion = source.Task;
            return true;
        }
    }

    internal static string RequestJson(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A draft flush request needs a nonempty ID.");
        return JsonSerializer.Serialize(new { type = "together-flush-drafts", requestId });
    }

    internal bool TryComplete(string source, string json)
    {
        if (!IsOwnerPage(source) || json.Length is < 2 or > MaximumReplyCharacters) return false;
        Guid requestId;
        bool ok;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var reply = document.RootElement;
            if (!GameSettingsRequestParser.ExactObject(reply, "type", "requestId", "ok") ||
                reply.GetProperty("type").ValueKind != JsonValueKind.String ||
                reply.GetProperty("type").GetString() != "together-drafts-flushed" ||
                reply.GetProperty("requestId").ValueKind != JsonValueKind.String ||
                reply.GetProperty("requestId").GetString() is not { Length: 36 } text ||
                !Guid.TryParseExact(text, "D", out requestId) || requestId == Guid.Empty ||
                reply.GetProperty("ok").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            ok = reply.GetProperty("ok").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
        lock (sync)
        {
            if (pending is not { } current || current.Id != requestId) return false;
            pending = null;
            current.Completion.TrySetResult(ok);
            return true;
        }
    }

    internal bool Cancel(Guid requestId)
    {
        lock (sync)
        {
            if (pending is not { } current || current.Id != requestId) return false;
            pending = null;
            current.Completion.TrySetResult(false);
            return true;
        }
    }

    internal void CancelAll()
    {
        lock (sync)
        {
            if (pending is not { } current) return;
            pending = null;
            current.Completion.TrySetResult(false);
        }
    }

    private bool IsOwnerPage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var origin) &&
        origin.Scheme == address.Scheme && origin.Host.Equals(address.Host, StringComparison.OrdinalIgnoreCase) &&
        origin.Port == address.Port && origin.UserInfo.Length == 0 && origin.AbsolutePath == address.AbsolutePath &&
        origin.Query == address.Query;
}
