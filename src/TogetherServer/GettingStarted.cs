namespace TogetherServer;

public sealed record GettingStartedView(bool Offered, string Intent, bool Dismissed);
internal sealed record GettingStartedRequest(string Intent, bool Dismissed);

internal sealed class GettingStartedState
{
    public int SchemaVersion { get; set; } = 1;
    public bool Offered { get; set; }
    public string Intent { get; set; } = "Choose";
    public bool Dismissed { get; set; }
}

// Optional guidance is independent of required profile setup and all runtime
// hosting/connection state. Nothing here configures, starts or stops a server.
internal sealed class GettingStartedService
{
    private const string FileName = "getting-started.json";
    private readonly LocalData data;
    private readonly object sync = new();
    private GettingStartedState state;

    internal GettingStartedService(LocalData data)
    {
        this.data = data;
        var existed = File.Exists(Path.Combine(data.RootPath, FileName));
        state = new();
        try
        {
            state = data.LoadState(FileName, new GettingStartedState());
            if (state.SchemaVersion != 1 || !ValidIntent(state.Intent) || (!state.Offered && state.Dismissed))
            {
                data.QuarantineState(FileName, "The getting started guide could not be read. You can open it again from the app.", false);
                state = new();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The optional display state cannot prevent supervision or safe
            // Stop when reading it or recording its recovery notice fails.
            state = new();
            DiagnosticOutput.WriteError("TogetherServer could not read the getting started guide state.");
            return;
        }
        if (!existed && data.IsNewDataDirectory)
        {
            state = new() { Offered = true };
            try { data.SaveState(FileName, state); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An optional guide must not prevent supervision or safe Stop
                // if its initial progress cannot be written.
                DiagnosticOutput.WriteError("TogetherServer could not save the initial getting started guide state.");
            }
        }
    }

    internal static bool ValidIntent(string? intent) => intent is "Choose" or "Host" or "Join";

    internal GettingStartedView View()
    {
        lock (sync) return new(state.Offered, state.Intent, state.Dismissed);
    }

    internal GettingStartedView Save(string intent, bool dismissed)
    {
        if (!ValidIntent(intent)) throw new ArgumentException("Choose a supported getting started guide.", nameof(intent));
        lock (sync)
        {
            var next = new GettingStartedState { Offered = true, Intent = intent, Dismissed = dismissed };
            data.SaveState(FileName, next);
            state = next;
            return new(state.Offered, state.Intent, state.Dismissed);
        }
    }
}
