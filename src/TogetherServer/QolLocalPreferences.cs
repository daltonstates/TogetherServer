using System.Drawing;
using System.Text.Json;

namespace TogetherServer;

public sealed record DesktopWindowPlacement(int X, int Y, int Width, int Height, bool Maximized = false);

public sealed class QolLocalPreferences(LocalData data)
{
    private const string FileName = "qol-desktop.json";
    private readonly object sync = new();

    public DesktopWindowPlacement? LoadWindowPlacement()
    {
        lock (sync)
        {
            var placement = data.LoadState<DesktopWindowPlacement?>(FileName, null);
            return Valid(placement) ? placement : null;
        }
    }

    public void SaveWindowPlacement(DesktopWindowPlacement placement)
    {
        if (!Valid(placement)) return;
        lock (sync) data.SaveState(FileName, placement);
    }

    // Pure monitor math is independently checkable without querying or opening any desktop surface.
    public static Rectangle ClampWindowBounds(DesktopWindowPlacement placement, IEnumerable<Rectangle> workingAreas)
    {
        var areas = workingAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
        if (areas.Length == 0) return new Rectangle(0, 0, 1180, 820);
        if (!Valid(placement)) placement = new(areas[0].X, areas[0].Y, 1180, 820);
        var requested = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
        var selected = areas.OrderByDescending(area => IntersectionArea(requested, area))
            .ThenBy(area => DistanceSquared(requested, area)).First();
        var width = Math.Clamp(placement.Width, Math.Min(380, selected.Width), selected.Width);
        var height = Math.Clamp(placement.Height, Math.Min(560, selected.Height), selected.Height);
        return new Rectangle(Math.Clamp(placement.X, selected.Left, selected.Right - width),
            Math.Clamp(placement.Y, selected.Top, selected.Bottom - height), width, height);
    }

    internal static bool Valid(DesktopWindowPlacement? value) => value is not null &&
        value.X is >= -100_000 and <= 100_000 && value.Y is >= -100_000 and <= 100_000 &&
        value.Width is >= 240 and <= 16_384 && value.Height is >= 240 and <= 16_384;
    private static long IntersectionArea(Rectangle a, Rectangle b)
    {
        var intersect = Rectangle.Intersect(a, b);
        return (long)intersect.Width * intersect.Height;
    }
    private static double DistanceSquared(Rectangle a, Rectangle b)
    {
        var dx = (double)a.X + a.Width / 2d - (b.X + b.Width / 2d);
        var dy = (double)a.Y + a.Height / 2d - (b.Y + b.Height / 2d);
        return dx * dx + dy * dy;
    }
}

public sealed record UpdateSnoozeRequest(string Version, string Duration);
public sealed record RetainedUpdateNotes(string Version, string Text, string? Url);

public sealed class UpdateUiPreferences
{
    private const string FileName = "update-ui.json";
    private readonly object sync = new();
    private readonly Action<State>? save;
    private readonly Func<DateTimeOffset> clock;
    private State state;

    public UpdateUiPreferences(LocalData data, Func<DateTimeOffset>? clock = null)
        : this(data.LoadState(FileName, new State()), value => data.SaveState(FileName, value), clock) { }

    internal UpdateUiPreferences(State? initial = null, Action<State>? save = null, Func<DateTimeOffset>? clock = null)
    {
        state = Valid(initial) ? initial! : new State();
        this.save = save;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DateTimeOffset? SnoozedUntil(string version)
    {
        lock (sync)
        {
            var snooze = state.Snoozes.SingleOrDefault(item => item.Version == version);
            return snooze?.UntilUtc > clock() ? snooze.UntilUtc : null;
        }
    }

    public bool IsVersionSkipped(string version)
    {
        lock (sync) return state.Snoozes.Any(item => item.Version == version && item.SkipVersion);
    }

    public DateTimeOffset? Snooze(UpdateSnoozeRequest request)
    {
        ValidateVersion(request.Version);
        var duration = request.Duration switch
        {
            "OneHour" => TimeSpan.FromHours(1),
            "Tomorrow" => TimeSpan.FromDays(1),
            "ThreeDays" => TimeSpan.FromDays(3),
            "SevenDays" => TimeSpan.FromDays(7),
            "SkipVersion" => TimeSpan.Zero,
            "Clear" => TimeSpan.Zero,
            _ => throw new InvalidDataException("Choose a fixed update reminder duration.")
        };
        lock (sync)
        {
            var next = Clone();
            next.Snoozes.RemoveAll(item => item.Version == request.Version || item.UntilUtc < clock() - TimeSpan.FromDays(30));
            DateTimeOffset? until = null;
            if (duration > TimeSpan.Zero || request.Duration == "SkipVersion")
            {
                until = request.Duration == "SkipVersion" ? DateTimeOffset.MaxValue : clock() + duration;
                next.Snoozes.Add(new(request.Version, until.Value, request.Duration == "SkipVersion"));
                next.Snoozes = next.Snoozes.OrderByDescending(item => item.UntilUtc).Take(16).ToList();
            }
            Save(next);
            return until;
        }
    }

    internal void RememberNotes(UpdateRelease release)
    {
        var notes = new RetainedUpdateNotes(release.Version.ToString(3), release.ReleaseNotes, release.ReleaseNotesUrl?.AbsoluteUri);
        lock (sync)
        {
            if (state.Notes == notes) return;
            var next = Clone();
            next.Notes = notes;
            Save(next);
        }
    }

    public RetainedUpdateNotes? NotesFor(string version)
    {
        lock (sync) return state.Notes?.Version == version ? state.Notes : null;
    }

    public static UpdateSnoozeRequest ParseSnooze(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Update reminder must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            if (property.Name is not ("version" or "duration") || !names.Add(property.Name))
                throw new InvalidDataException("Update reminder contains unknown or duplicate fields.");
        if (names.Count != 2 || input.GetProperty("version").ValueKind != JsonValueKind.String ||
            input.GetProperty("duration").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Choose a version and reminder duration.");
        var version = input.GetProperty("version").GetString()!;
        var duration = input.GetProperty("duration").GetString()!;
        ValidateVersion(version);
        if (duration is not ("OneHour" or "Tomorrow" or "ThreeDays" or "SevenDays" or "SkipVersion" or "Clear"))
            throw new InvalidDataException("Choose a fixed update reminder duration.");
        return new(version, duration);
    }

    private State Clone() => new() { Snoozes = state.Snoozes.ToList(), Notes = state.Notes };
    private void Save(State next)
    {
        save?.Invoke(next);
        state = next;
    }
    private static bool Valid(State? value) => value is not null && value.Snoozes is not null &&
        value.Snoozes.Count <= 16 && value.Snoozes.All(item => item is not null && ValidVersion(item.Version) &&
            item.UntilUtc.Offset == TimeSpan.Zero) && value.Snoozes.Select(item => item.Version).Distinct().Count() == value.Snoozes.Count &&
        (value.Notes is null || (ValidVersion(value.Notes.Version) && value.Notes.Text is not null &&
            value.Notes.Text.Length <= AppUpdater.MaximumReleaseNotesCharacters &&
            AppUpdater.ValidReleaseNotesLink(value.Notes.Url, "v" + value.Notes.Version)));
    private static bool ValidVersion(string? value) => value is not null && value.Length <= 32 &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d+\.\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
        Version.TryParse(value, out _);
    private static void ValidateVersion(string value)
    {
        if (!ValidVersion(value)) throw new InvalidDataException("Update version must be a stable release version.");
    }
    public sealed class State
    {
        public List<SnoozeEntry> Snoozes { get; set; } = [];
        public RetainedUpdateNotes? Notes { get; set; }
    }
    public sealed record SnoozeEntry(string Version, DateTimeOffset UntilUtc, bool SkipVersion = false);
}
