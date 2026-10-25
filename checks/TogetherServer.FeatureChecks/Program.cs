using TogetherServer.FeatureChecks;

// Deliberately separate from TogetherServer.Checks.Program and its process journeys.
// Every registered group uses pure logic, synthetic files/protected state or fake adapters.
// No app entry point, process, listener, console attachment, browser or dialog is invoked.
var root = Path.Combine(Directory.GetCurrentDirectory(), "local-data", "feature-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var groups = new (string Name, Func<Task> Run)[]
{
    ("Windowless disposable pairing lab boundaries", () => LocalPairingLabChecks.RunAsync(root)),
    ("Isolated native development demo", () => DemoInstanceChecks.RunAsync(root)),
    ("Passive grouped server directory", () => ServerDirectoryChecks.RunAsync(root)),
    ("Calm completed player-count observations", () => { PlayerCountPresentationChecks.Run(); PlayerCountPresentationChecks.RunQuietCountdown(root); return Task.CompletedTask; }),
    ("Valheim player-count query recovery", () => { ValheimPlayerCountChecks.Run(root); return Task.CompletedTask; }),
    ("Versioned required feature setup", () => RequiredFeatureSetupChecks.RunAsync(root)),
    ("Installer handoff and optional first-use guide", () => GettingStartedChecks.RunAsync(root)),
    ("Separate owner-controlled game password access", () => GamePasswordPermissionChecks.RunAsync(root)),
    ("Ephemeral pinned Friend password reads", () => GamePasswordFriendChecks.RunAsync(root)),
    ("Protected person profiles and authenticated name exchange", () => UserProfileChecks.RunAsync(root)),
    ("Atomic world protection settings", () => ProtectionSettingsChecks.RunAsync(root)),
    ("One-action save hosting permissions", () => SharedHostingPermissionChecks.RunAsync(root)),
    ("Valheim shared setup comments", () => { SharedPublicationChecks.Run(root); return Task.CompletedTask; }),
    ("Completed shared backup retry", () => SharedHostingPermissionChecks.RetryPublicationAsync(root)),
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
    ("Native chrome styles and DPI glyph geometry", () => { DesktopChromeChecks.Run(); return Task.CompletedTask; }),
    ("Revoked PC deletion", () => { RevokedDeviceDeletionChecks.Run(root); return Task.CompletedTask; }),
    ("Server-scoped access removal and seamless world sessions", () => { ServerAccessRemovalChecks.Run(root); return Task.CompletedTask; }),
    ("Remembered Friend revocation", () => FriendRevocationVisibilityChecks.RunAsync(root)),
    ("Enrollment nonce scope and single-use", () => EnrollmentNonceChecks.RunAsync()),
    ("Concurrent shared enrollment exchanges", () => SharedEnrollmentExchangeChecks.RunAsync(root)),
    ("Legacy roster bootstrap races and trust floors", () => SharedRosterBootstrapChecks.RunAsync(root)),
    ("Proof-backed legacy owner roster projections", () => SharedRosterLegacyProjectionChecks.RunAsync(root)),
    ("Concurrent signed roster imports", () => { SharedRosterConcurrentImportChecks.Run(root); return Task.CompletedTask; }),
    ("Revoked shared-world device rejoin", () => SharedRosterRejoinChecks.RunAsync(root)),
    ("Solo and descendant world recovery", () => SoloRecoveryChecks.RunAsync(root)),
    ("Local hosting permission before preparation", () => LocalHostingPermissionChecks.RunAsync(root)),
    ("Adopted successor hosting identity and consent", () => AdoptedLocalHostingChecks.RunAsync(root)),
    ("Guided local shared hosting preparation", () => LocalHostingChecks.RunAsync(root)),
    ("Fixed shared hosting Start and repair actions", SharedHostingActionChecks.RunAsync),
    ("Observed local hosting progress", LocalHostingProgressChecks.RunAsync),
    ("Original owner guarded remote Stop", () => OriginalOwnerRemoteStopChecks.RunAsync(root)),
    ("Paired-PC hosting requests and exact final move", () => PlannedHandoffRequestChecks.RunAsync(root)),
    ("Two-PC planned move route proof", () => PlannedHandoffRouteChecks.RunAsync()),
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
if (args.Any(name => !groups.Any(group => group.Name.Equals(name, StringComparison.Ordinal))))
    throw new ArgumentException("Choose an exact registered source-check group name, or omit names to run every group.");
var selectedGroups = args.Length == 0 ? groups : groups.Where(group => args.Contains(group.Name, StringComparer.Ordinal)).ToArray();
Console.WriteLine($"Running {selectedGroups.Length} of {groups.Length} source/synthetic groups.");
foreach (var group in selectedGroups)
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
