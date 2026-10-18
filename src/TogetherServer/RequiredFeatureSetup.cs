namespace TogetherServer;

public sealed record RequiredFeatureView(string Id, int Revision, string Title,
    string Description, bool Completed);
public sealed record RequiredFeatureSetupView(IReadOnlyList<RequiredFeatureView> Features);
public sealed record RequiredFeatureSetupResult(bool Ok, string Code, string Message,
    IReadOnlyList<RequiredFeatureView> Features);

// Add one fixed definition and its UI editor for each new required feature.
// Configuration can prove an older installation already completed a step;
// explicit reviews instead require acknowledgement of that step's revision.
internal sealed record RequiredFeatureDefinition(string Id, int Revision, string Title,
    string Description, Func<bool> IsConfigured, bool RequiresAcknowledgement = false);

internal sealed class RequiredFeatureSetupState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, int> Completed { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class RequiredFeatureSetupService
{
    private const string FileName = "required-setup.json";
    private readonly LocalData data;
    private readonly IReadOnlyList<RequiredFeatureDefinition> definitions;
    private readonly object sync = new();
    private RequiredFeatureSetupState state;

    internal RequiredFeatureSetupService(LocalData data,
        IReadOnlyList<RequiredFeatureDefinition>? definitions = null)
    {
        this.data = data;
        this.definitions = definitions?.ToArray() ??
        [
            new("profile-name", 1, "Your name",
                "Choose the name Hosts and Friends will see when you connect.",
                () => data.UserProfile.SharedName is not null)
        ];
        if (this.definitions.Count > 128 ||
            this.definitions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != this.definitions.Count ||
            this.definitions.Any(item => !ValidId(item.Id) || item.Revision is < 1 or > 10000 ||
                string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 80 ||
                string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 320))
            throw new ArgumentException("Required features must have unique fixed IDs and bounded revisions.");
        state = data.LoadState(FileName, new RequiredFeatureSetupState());
        if (state.SchemaVersion != 1 || state.Completed is null || state.Completed.Count > 128 ||
            state.Completed.Any(item => !ValidId(item.Key) || item.Value is < 1 or > 10000))
        {
            data.QuarantineState(FileName, "Required setup progress could not be read. Review any pending setup again.", false);
            state = new();
        }
    }

    private static bool ValidId(string value) => value is { Length: >= 1 and <= 64 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private RequiredFeatureView Project(RequiredFeatureDefinition definition) => new(definition.Id,
        definition.Revision, definition.Title, definition.Description,
        definition.IsConfigured() && (!definition.RequiresAcknowledgement ||
            state.Completed.GetValueOrDefault(definition.Id) >= definition.Revision));

    internal RequiredFeatureSetupView View()
    {
        lock (sync) return new(definitions.Select(Project).ToArray());
    }

    internal RequiredFeatureSetupResult Complete(string id, int revision)
    {
        lock (sync)
        {
            var definition = definitions.SingleOrDefault(item => item.Id == id);
            if (definition is null || revision != definition.Revision)
                return Result(false, "RequiredFeatureChanged", "Reload setup to review the current required step.");
            if (!definition.IsConfigured())
                return Result(false, "RequiredFeatureIncomplete", "Save the required information before continuing.");
            if (state.Completed.GetValueOrDefault(id) < revision)
            {
                var next = new RequiredFeatureSetupState { Completed = new(state.Completed, StringComparer.Ordinal) };
                if (next.Completed.Count >= 128 && !next.Completed.ContainsKey(id))
                    return Result(false, "RequiredSetupUnavailable", "Setup progress could not be saved. Retry setup.");
                next.Completed[id] = revision;
                data.SaveState(FileName, next);
                state = next;
            }
            return Result(true, "RequiredFeatureCompleted", "Setup step saved.");
        }
    }

    private RequiredFeatureSetupResult Result(bool ok, string code, string message) =>
        new(ok, code, message, definitions.Select(Project).ToArray());
}
