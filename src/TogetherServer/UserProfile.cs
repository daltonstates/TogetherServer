using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record UserProfileView(string DisplayName);
public sealed record UserProfileResult(bool Ok, string Code, string Message, string DisplayName);

// Names describe people. Credentials and pinned keys remain the identity and
// authorization boundary; neither PC labels nor saved Host aliases are changed.
internal static class UserProfileName
{
    internal const int MaximumLength = 80;

    internal static bool TryNormalize(string? value, out string? name)
    {
        name = null;
        if (value is null) return true; // An older peer did not send this field.
        if (value.Any(char.IsControl) || value.EnumerateRunes().Any(rune =>
            Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) ||
            !ValidUtf16(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length > MaximumLength) return false;
        name = trimmed.Length == 0 ? null : trimmed;
        return true;
    }

    private static bool ValidUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length ||
                !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }
}

internal sealed class UserProfileStore
{
    private const string FileName = "user-profile.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LocalData data;
    private readonly object sync = new();
    private UserProfileView profile;

    internal UserProfileStore(LocalData data)
    {
        this.data = data;
        var saved = data.LoadProtectedJson<UserProfileView>(FileName);
        if (saved is not null && (saved.DisplayName is null ||
            !UserProfileName.TryNormalize(saved.DisplayName, out _)))
        {
            data.QuarantineState(FileName, "The saved display name could not be read. Set your name again in Profile.", false);
            saved = null;
        }
        UserProfileName.TryNormalize(saved?.DisplayName, out var name);
        profile = new(name ?? "");
    }

    internal UserProfileView View() { lock (sync) return profile; }
    internal string? SharedName { get { lock (sync) return profile.DisplayName.Length == 0 ? null : profile.DisplayName; } }

    internal UserProfileResult Save(string? displayName)
    {
        lock (sync)
        {
            if (displayName is null || !UserProfileName.TryNormalize(displayName, out var name))
                return new(false, "InvalidProfileName", "Use up to 80 characters without line breaks or hidden formatting.", profile.DisplayName);
            var next = new UserProfileView(name ?? "");
            // Publish only after the protected atomic write succeeds.
            data.SaveProtected(FileName, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            profile = next;
            return new(true, "ProfileSaved", "Your name was saved. Paired PCs receive changes when they reconnect.", next.DisplayName);
        }
    }
}
