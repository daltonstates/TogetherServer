namespace TogetherServer;

public sealed partial class HostManager
{
    public async Task<GameSettingsView> ReadGameSettingsAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            return profile is null ? new(false, "UnknownProfile", "Choose a saved Host server.",
                "Unknown", null, null, false, []) : GameSettings.Read(data, profile);
        }
        finally { gate.Release(); }
    }

    public async Task<GameSettingsPreview> PreviewGameSettingsAsync(Guid profileId, GameSettingsChangeRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            return profile is null ? UnknownGameSettingsPreview("server-properties") :
                GameSettings.ProposeSettings(profile, ServerFiles.Read(data, profile, "server-properties"), request).Preview;
        }
        finally { gate.Release(); }
    }

    public async Task<ServerFileChangeResult> SaveGameSettingsAsync(Guid profileId, GameSettingsChangeRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return UnknownGameSettingsChange("server-properties");
            if (ServerFileEditBlock(profile, "server-properties") is { } blocked) return blocked;
            var current = ServerFiles.Read(data, profile, "server-properties");
            var proposal = GameSettings.ProposeSettings(profile, current, request);
            return SaveGameSettingsFileUnderGate(profile, "server-properties", current, proposal);
        }
        finally { gate.Release(); }
    }

    // This delegation happens without holding the gate: the existing file Undo
    // reacquires it and repeats maintenance, exact-run and checkpoint guards.
    public Task<ServerFileChangeResult> UndoGameSettingsAsync(Guid profileId, ServerFileUndoRequest request) =>
        GameSettings.IsHash(request?.ExpectedSha256) ? UndoServerFileAsync(profileId, "server-properties", request!) :
        Task.FromResult(new ServerFileChangeResult(false, "InvalidUndo", "Reload settings before undoing.", "server-properties"));

    public async Task<GameAccessListView> ReadGameAccessListAsync(Guid profileId, string key)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            return profile is null ? new(false, "UnknownProfile", "Choose a saved Host server.",
                "Unknown", key, GameSettings.ListLabel(key), null, [], false) : GameSettings.ReadList(data, profile, key);
        }
        finally { gate.Release(); }
    }

    public async Task<GameSettingsPreview> PreviewGameAccessListAsync(Guid profileId, string key,
        GameAccessListChangeRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            return profile is null ? UnknownGameSettingsPreview(key) :
                GameSettings.ProposeList(profile, key, ServerFiles.Read(data, profile, key), request).Preview;
        }
        finally { gate.Release(); }
    }

    public async Task<ServerFileChangeResult> SaveGameAccessListAsync(Guid profileId, string key,
        GameAccessListChangeRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return UnknownGameSettingsChange(key);
            if (!GameSettings.SupportsList(profile.Kind, key)) return new(false, "AccessListUnsupported",
                "Choose one of this game's reviewed access lists.", key);
            if (ServerFileEditBlock(profile, key) is { } blocked) return blocked;
            var current = ServerFiles.Read(data, profile, key);
            var proposal = GameSettings.ProposeList(profile, key, current, request);
            return SaveGameSettingsFileUnderGate(profile, key, current, proposal);
        }
        finally { gate.Release(); }
    }

    public async Task<ServerFileChangeResult> UndoGameAccessListAsync(Guid profileId, string key,
        ServerFileUndoRequest request)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return UnknownGameSettingsChange(key);
            if (!GameSettings.SupportsList(profile.Kind, key)) return new(false, "AccessListUnsupported",
                "Choose one of this game's reviewed access lists.", key);
            if (!GameSettings.IsHash(request?.ExpectedSha256)) return new(false, "InvalidUndo",
                "Reload the list before undoing.", key);
            if (ServerFileEditBlock(profile, key) is { } blocked) return blocked;
            var current = ServerFiles.Read(data, profile, key);
            if (!current.Ok || !current.CanUndo || !string.Equals(current.Sha256, request!.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                return new(false, "UndoUnavailable", "Reload the list before restoring its previous version.", key);
            var checkpoint = CreateManualBackupUnderGate(profileId, requireCompleteSetup: true);
            if (!checkpoint.Ok) return new(false, "CheckpointFailed",
                "The offline checkpoint failed. The list was not changed. " + checkpoint.Message, key);
            var restored = ServerFiles.Undo(data, profile, key, request);
            if (restored.Ok) Activity("Configuration", "FileRestored",
                "The Host restored a reviewed access list after an offline checkpoint.", ActivitySeverity.Important, profileId);
            return restored;
        }
        finally { gate.Release(); }
    }

    private ServerFileChangeResult SaveGameSettingsFileUnderGate(ServerProfile profile, string key,
        ServerFileContentResult current, GameSettings.Proposal proposal)
    {
        if (!proposal.Preview.Ok || proposal.Content is null)
            return new(false, proposal.Preview.Code, proposal.Preview.Message, key);
        if (proposal.Preview.Changes.Count == 0)
            return new(true, "NoChanges", "There are no changes to save.", key, current.Sha256, current.CanUndo);
        var checkpoint = CreateManualBackupUnderGate(profile.Id, requireCompleteSetup: true);
        if (!checkpoint.Ok) return new(false, "CheckpointFailed",
            "The offline checkpoint failed. The file was not changed. " + checkpoint.Message, key);
        // ServerFiles repeats the byte hash before its atomic replace and keeps
        // the protected prior version used by both simple and raw file Undo.
        var result = ServerFiles.Save(data, profile, key, new(current.Sha256!, proposal.Content));
        if (result.Ok && result.Code == "FileSaved") Activity("Configuration", "FileSaved",
            "The Host changed reviewed game settings after an offline checkpoint.", ActivitySeverity.Important, profile.Id);
        return result;
    }

    private static GameSettingsPreview UnknownGameSettingsPreview(string key) =>
        new(false, "UnknownProfile", "Choose a saved Host server.", key, null, null, []);
    private static ServerFileChangeResult UnknownGameSettingsChange(string key) =>
        new(false, "UnknownProfile", "Choose a saved Host server.", key);
}
