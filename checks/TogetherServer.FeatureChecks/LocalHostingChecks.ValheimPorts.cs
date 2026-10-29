using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class LocalHostingChecks
{
    private static async Task RetainedValheimPortsWithoutIncomingListenerAsync(string root)
    {
        foreach (var (removePreviousApproval, interruptedAfterPortSave) in
            new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            using var fixture = new Fixture(root, granted: true, automaticReceiving: true,
                fileCount: 2, game: GameKinds.Valheim);
            var sourceFiles = Directory.EnumerateFiles(Path.Combine(fixture.Data.RootPath, "received-shared-worlds"),
                    "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".db" or ".fwl")
                .ToDictionary(path => path, File.ReadAllBytes);
            Require(sourceFiles.Count == 2, "the file-only Valheim source did not contain both signed world files");
            var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId,
                new(true, true, RequirePeerConnection: false));
            Require(prepared.Ok && prepared.ReadyForManualStart && prepared.LocalProfileId is not null &&
                prepared.BranchHash is not null && prepared.SelectedApprovedPortId is not null,
                $"the Valheim file-only baseline did not prepare: {prepared.Code}; {prepared.Message}");
            var localId = prepared.LocalProfileId.Value;
            var originalProfile = fixture.Data.LoadSettings().Profiles.Single(item => item.Id == localId);
            var originalState = SharedWorldBranchLiveSource.ReadState(fixture.Data, localId)!;
            var originalPassword = fixture.Data.LoadValheimPassword(localId);
            var localFiles = Directory.EnumerateFiles(originalProfile.WorldDirectory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            Require(originalState.Ready && !originalState.Started && originalState.SetupHash is not null &&
                localFiles.Count == 2 && localFiles.All(file =>
                    sourceFiles.Single(source => Path.GetFileName(source.Key) == Path.GetFileName(file.Key))
                        .Value.SequenceEqual(file.Value)),
                "the prepared Valheim copy did not match the complete signed source");

            var replacementPort = originalProfile.GamePort + 2;
            Require(fixture.Data.ApprovedGamePorts.Save(new(GameKinds.Valheim, replacementPort, null)).Ok,
                "the replacement Valheim port approval was not saved");
            var selected = fixture.Data.ApprovedGamePorts.Find(GameKinds.Valheim)!;
            if (removePreviousApproval)
                Require(fixture.Data.ApprovedGamePorts.Delete(prepared.SelectedApprovedPortId.Value).Ok,
                    "the previous Valheim port approval was not removed");
            var passive = await fixture.Service.StatusAsync(fixture.ProfileId);
            Require(passive.GamePort == originalProfile.GamePort &&
                fixture.Data.LoadSettings().Profiles.Single().GamePort == originalProfile.GamePort &&
                SharedWorldBranchLiveSource.ReadState(fixture.Data, localId) == originalState,
                "saving or reading a preferred Valheim approval changed the existing setup or preparation proof");

            if (interruptedAfterPortSave)
            {
                // Model interruption at the real mutation boundary: the port
                // is durable, but Finish has not replaced its old setup proof.
                var persisted = await fixture.Manager.ChangeLocalHostingPortsAsync(originalProfile, selected,
                    CancellationToken.None);
                var interrupted = await fixture.Service.StatusAsync(fixture.ProfileId);
                var interruptedSettings = fixture.Data.LoadSettings();
                Require(persisted.Ok && interrupted.GamePort == replacementPort &&
                    interrupted.LocalProfileId == localId && interrupted.BranchHash == prepared.BranchHash &&
                    interrupted.SelectedApprovedPortId == selected.Id && !interrupted.ReadyForManualStart &&
                    !interrupted.Running && interruptedSettings.Profiles.Single().GamePort == replacementPort &&
                    SharedWorldBranchLiveSource.ReadState(fixture.Data, localId) == (originalState with { Ready = false }) &&
                    !interruptedSettings.CompanionListeningEnabled && !interruptedSettings.RemoteControlsEnabled &&
                    interruptedSettings.CompanionBindAddress == "127.0.0.1" &&
                    interruptedSettings.CompanionEndpoint == $"https://127.0.0.1:{fixture.ControlPort}" &&
                    fixture.Data.LoadRuns().Count == 0 && fixture.Driver.ProcessAttempts == 0,
                    $"interrupted Valheim re-preparation did not retain its new port and invalidate readiness: {persisted.Code}; {persisted.Message}");
            }

            // The ordinary continuation's incoming route stays advisory. This
            // explicit request repeats the full service's world/setup checks,
            // including a retry whose selected port already matches settings.
            var changed = await fixture.Service.PrepareAsync(fixture.ProfileId,
                new(true, true, RequirePeerConnection: false, ApprovedPortId: selected.Id));
            var settings = fixture.Data.LoadSettings();
            var profile = settings.Profiles.Single();
            var state = SharedWorldBranchLiveSource.ReadState(fixture.Data, localId)!;
            var branch = new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Single();
            var setup = new TakeoverLocalSetup(profile.ExecutablePath, branch.Offer.Version.PortableSetup.GameVersion,
                branch.Offer.Version.PortableSetup.AddOns, true, fixture.ControlPort, replacementPort);
            Require(changed.Ok && changed.ReadyForManualStart && !changed.Running &&
                changed.LocalProfileId == localId && changed.BranchHash == prepared.BranchHash &&
                changed.GamePort == replacementPort && changed.SelectedApprovedPortId == selected.Id &&
                !changed.NeedsPortApproval && profile.Id == localId &&
                profile.GamePort == replacementPort && profile.WorldDirectory == originalProfile.WorldDirectory &&
                state.Ready && !state.Started && state.SetupHash != originalState.SetupHash &&
                state.SetupHash == SharedWorldBranchLiveSource.SetupHash(profile, setup) &&
                fixture.Data.LoadValheimPassword(localId) == originalPassword,
                $"the retained Valheim port change did not renew preparation for the same world " +
                $"(old approval removed={removePreviousApproval}, interrupted={interruptedAfterPortSave}): {changed.Code}; {changed.Message}");
            var observed = await fixture.Service.StatusAsync(fixture.ProfileId);
            Require(observed.ReadyForManualStart && observed.GamePort == replacementPort &&
                !settings.CompanionListeningEnabled && !settings.RemoteControlsEnabled &&
                settings.CompanionBindAddress == "127.0.0.1" &&
                settings.CompanionEndpoint == $"https://127.0.0.1:{fixture.ControlPort}" &&
                sourceFiles.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) &&
                localFiles.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) &&
                Directory.EnumerateFiles(profile.WorldDirectory, "*", SearchOption.AllDirectories).Count() == localFiles.Count &&
                fixture.Data.LoadRuns().Count == 0 && fixture.Driver.ProcessAttempts == 0,
                "changing retained Valheim ports changed a world, incoming controls, or runtime state");
        }
    }
}
