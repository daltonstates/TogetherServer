using TogetherServer.FeatureChecks;

// Deliberately separate from TogetherServer.Checks.Program and its process journeys.
// Every registered group uses pure logic, synthetic files/protected state or fake adapters.
// No app entry point, process, listener, console attachment, browser or dialog is invoked.
var root = Path.Combine(Directory.GetCurrentDirectory(), "local-data", "feature-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var groups = new (string Name, Func<Task> Run)[]
{
    ("Isolated native development demo", () => DemoInstanceChecks.RunAsync(root)),
    ("Passive grouped server directory", () => ServerDirectoryChecks.RunAsync(root)),
    ("Calm completed player-count observations", () => { PlayerCountPresentationChecks.Run(); PlayerCountPresentationChecks.RunQuietCountdown(root); return Task.CompletedTask; }),
    ("Versioned required feature setup", () => RequiredFeatureSetupChecks.RunAsync(root)),
    ("Protected person profiles and authenticated name exchange", () => UserProfileChecks.RunAsync(root)),
    ("Atomic world protection settings", () => ProtectionSettingsChecks.RunAsync(root)),
    ("One-action save hosting permissions", () => SharedHostingPermissionChecks.RunAsync(root)),
    ("Compatibility requirements", () => { GameCompatibilityChecks.Run(root); return Task.CompletedTask; }),
    ("Fixed game launch and pinned Friend checks", () => GameClientFriendChecks.RunAsync(root)),
    ("Fixed local feature inputs", () => { FeatureInputChecks.Run(); return Task.CompletedTask; }),
    ("Backup names and pins", () => { BackupBookmarksChecks.Run(root); return Task.CompletedTask; }),
    ("Seven-day session projection", () => { WeeklySummaryChecks.Run(); return Task.CompletedTask; }),
    ("Guided game settings", () => GameSettingsChecks.RunAsync(root)),
    ("Durable signed notices", () => { PinnedNoticeChecks.Run(root); return Task.CompletedTask; }),
    ("Pinned Friend notice copies", () => PinnedNoticeFriendChecks.RunAsync(root)),
    ("Protected UI drafts", () => { DesktopQolChecks.RunDrafts(); return Task.CompletedTask; }),
    ("Desktop presentation preferences", () => { DesktopQolChecks.RunPreferences(); return Task.CompletedTask; }),
    ("Trusted update metadata and reminders", () => DesktopQolChecks.RunUpdateMetadataAsync(root)),
    ("Per-PC prerelease enrollment", () => { PrereleaseUpdateChecks.RunPreferences(root); return Task.CompletedTask; }),
    ("Stable and preview update selection", () => PrereleaseUpdateChecks.RunSelectionAsync(root)),
    ("Prerelease trust and prepared-update invalidation", () => PrereleaseUpdateChecks.RunTrustAsync(root)),
    ("Exact backup catalog evidence", () => { BackupCatalogChecks.Run(root); return Task.CompletedTask; }),
    ("Safe unsent chat queue changes", () => ChatQueueChecks.RunAsync(root)),
    ("Native import selection previews", () => { SetupImportPreviewChecks.Run(root); return Task.CompletedTask; }),
    ("Shared save display projections", () => { SharedWorldProjectionChecks.Run(root); return Task.CompletedTask; }),
    ("Local QoL endpoint scopes and run identity", () => { QolLocalEndpointInputChecks.Run(); return Task.CompletedTask; }),
    ("Canonical tray projection", () => { DesktopTrayProjectionChecks.Run(root); return Task.CompletedTask; }),
    ("Bundled desktop icon assets", () => { DesktopIconChecks.Run(); return Task.CompletedTask; }),
    ("Revoked PC deletion", () => { RevokedDeviceDeletionChecks.Run(root); return Task.CompletedTask; }),
    ("Remembered Friend revocation", () => FriendRevocationVisibilityChecks.RunAsync(root)),
    ("Enrollment nonce scope and single-use", () => EnrollmentNonceChecks.RunAsync()),
    ("Concurrent shared enrollment exchanges", () => SharedEnrollmentExchangeChecks.RunAsync(root)),
    ("Legacy roster bootstrap races and trust floors", () => SharedRosterBootstrapChecks.RunAsync(root)),
    ("Solo and descendant world recovery", () => SoloRecoveryChecks.RunAsync(root)),
    ("Guided local shared hosting preparation", () => LocalHostingChecks.RunAsync(root)),
    ("Read-only local hosting route checks", () => LocalHostingRouteChecks.RunAsync()),
    ("Signed peer world directory", () => { SharedWorldPeerChecks.Run(root); return Task.CompletedTask; }),
    ("Fixed signed peer protocol", () => { WorldPeerProtocolChecks.Run(); return Task.CompletedTask; }),
    ("Offline-owner peer catchup", () => WorldPeerTransportChecks.RunAsync(root)),
    ("Explicit concurrent world defaults", () => { WorldPeerDefaultChecks.Run(); return Task.CompletedTask; }),
    ("Freshest verified world copies", () => SharedWorldFreshnessChecks.RunAsync(root)),
    ("Installed live-save acceptance", () => LiveSaveAcceptanceChecks.RunAsync(root)),
    ("Resumable verified peer files", () => WorldPeerFilesChecks.RunAsync(root)),
    ("Owner recovery of peer histories", () => OwnerPeerRecoveryChecks.RunAsync(root)),
    ("Private owner receipt isolation", () => { OwnerTestReceiptChecks.Run(); return Task.CompletedTask; }),
    ("Private peer test completion", () => { WorldPeerLiveTestChecks.Run(root); return Task.CompletedTask; }),
    ("Peer enrollment credential boundaries", () => { WorldPeerEnrollmentChecks.Run(root); return Task.CompletedTask; }),
    ("Verified saved peer connections", () => WorldPeerSavedConnectionChecks.RunAsync(root)),
    ("Completed peer reads during capture", () => WorldPeerBusyReadChecks.RunAsync(root)),
    ("World receiving consent across Hosts", () => WorldPeerConsentChecks.RunAsync(root)),
    ("Recovery source live capture ownership", () => BranchLiveSourceChecks.RunAsync(root)),
    ("Verified recovery head selection", () => WorldPeerRecoverySelectionChecks.RunAsync(root)),
    ("Recovery branch live-save integration", () => BranchLiveIntegrationChecks.RunAsync(root)),
    ("Offline-owner save replication", () => WorldPeerReplicationChecks.RunAsync(root))
};
foreach (var group in groups)
{
    try { await group.Run(); Console.WriteLine($"PASS {group.Name}"); }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {group.Name}: {error.GetType().Name}: {error.Message}");
        Console.Error.WriteLine(error.StackTrace);
        Environment.ExitCode = 1;
    }
}
Console.WriteLine("Source/synthetic evidence only. Native game opening, installed games, Friend-PC joins and saves remain unverified.");
