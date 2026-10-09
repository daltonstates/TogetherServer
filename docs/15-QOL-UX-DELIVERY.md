# QoL and UX delivery: 100 authorized improvements

The owner authorized all 100 numbered improvements on 2026-10-08, with concurrent Codex CLI workers and subagents. This record is the implementation ledger. A row counts as implemented only after its behavior is integrated into the main checkout and reviewed; preparation, delegation and isolated worker edits do not count. Validation and external acceptance are recorded separately.

Started: 2026-10-08 21:35 America/New_York. Initial estimate: 2-4 hours for implementation and focus-safe validation; this is provisional, not a guarantee. Initial completed implementation: 0/100.

## Ownership and integration

Nine isolated worker worktrees use disjoint files. Editors, Friend, chat/access, backups, shared saves, logs/summaries, desktop/preferences, source projections and operations are concurrent lanes. Main.tsx, contracts.ts, Program.cs, shared CSS, WorkspaceChrome.tsx, shared API/protocol integration, this ledger, validation and commits belong to the coordinator. CLI workers and implementation subagents use configured gpt-6.1-sol with max reasoning. Source builds, integration and commits are serialized.

Do not launch TogetherServer, game processes, fixture/process/console runners, browser automation, installers, listeners, staging packages or native dialogs on the active desktop. Only code compilation, JSDOM, pure/synthetic/fake-adapter checks and scripts/verify-code-only.ps1 are authorized here. Production data, credentials and worlds remain untouched. No release, tag, push or deployment is part of this work. Preserve exact process identity, one-world writer, pinned TLS, per-device permissions, fail-closed Unknown occupancy, guarded maintenance and local-only shared-save authority.

## Numbered implementation ledger

| ID | Improvement | Owner | State | Integrated evidence |
| --- | --- | --- | --- | --- |
| 1 | Protect unsaved edits | editors | Implemented | editorProtectedDraft.tsx; main.tsx; ProtectedUiDraftStore.cs; DesktopDraftFlushGate.cs |
| 2 | Put feedback beside the action | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 3 | Show the obvious next action | integration | Implemented | HostLifecycleSummary.tsx; PlayersPanel.tsx; main.tsx |
| 4 | Name background work | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 5 | Remember each server's last tab | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 6 | Preserve reading position | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 7 | Search saved servers | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 8 | Pin favorite servers | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 9 | Expand the command palette | integration | Implemented | WorkspaceChrome.tsx; guarded main.tsx command destinations |
| 10 | Offer comfortable and compact layouts | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 11 | Show every setup blocker together | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 12 | Apply suggested ports directly | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 13 | Explain world locations visually | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 14 | Preview imports | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 15 | Reuse nonsecret setup | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 16 | Improve installation selection | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 17 | Explain game support early | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 18 | Review setup changes | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 19 | Resume unfinished setup at the right step | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 20 | Guide the first successful session | editors | Implemented | HostSetupDialog.tsx; useHostSetup.ts; setupImprovements.ts; SetupImportPreview.cs |
| 21 | Show player impact before Stop or Restart | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 22 | Put Safe restart beside Restart | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 23 | Show observed startup stages | integration | Implemented | HostLifecycleSummary.tsx; PlayersPanel.tsx; main.tsx |
| 24 | Add timer-extension presets | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 25 | Preview timer changes | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 26 | Open maintenance guide directly | integration | Implemented | main.tsx maintenance navigation and profile-scoped editor work; existing lifecycle serialization retained |
| 27 | Explain server states | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 28 | Give unavailable player counts a next action | integration | Implemented | HostLifecycleSummary.tsx; PlayersPanel.tsx; main.tsx |
| 29 | Keep unrelated panels usable during work | integration | Implemented | main.tsx maintenance navigation and profile-scoped editor work; existing lifecycle serialization retained |
| 30 | Explain unavailable controls inline | integration | Implemented | main.tsx; QolWidgets.tsx; PlayersPanel.tsx |
| 31 | Collapse healthy connection diagnostics | friend | Implemented | FriendConnectionDoctor.tsx; OwnerDiagnostics.tsx; bounded reviewed troubleshooting exports |
| 32 | Choose which server diagnostics test | friend | Implemented | FriendConnectionDoctor.tsx; OwnerDiagnostics.tsx; bounded reviewed troubleshooting exports |
| 33 | Show connection-check freshness | friend | Implemented | FriendConnectionDoctor.tsx; FriendConnections.cs; canonical runOperationId; fixed scoped probe inputs |
| 34 | Copy address and port separately | friend | Implemented | ConnectionDetails.tsx; scoped address and port clipboard actions |
| 35 | Create one clear play flow | friend | Implemented | FriendPlayFlow.tsx; GameCompatibilityPanel.tsx; OpenGameButton.tsx; main.tsx |
| 36 | Simplify compatibility results | friend | Implemented | FriendPlayFlow.tsx; GameCompatibilityPanel.tsx; OpenGameButton.tsx; main.tsx |
| 37 | Explain effective permissions on each card | integration | Implemented | FriendPlayFlow.tsx; GameCompatibilityPanel.tsx; OpenGameButton.tsx; main.tsx |
| 38 | Cancel adding another Host | integration | Implemented | main.tsx pairing cancel and protected scope navigation |
| 39 | Improve Saved Hosts | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 40 | Clarify paused connections | integration | Implemented | FriendPlayFlow.tsx; GameCompatibilityPanel.tsx; OpenGameButton.tsx; main.tsx |
| 41 | Summarize each PC's access | integration | Implemented | FriendAccessSummary.tsx; permission before/after review; FriendPcFilters; main.tsx |
| 42 | Preview permission changes | integration | Implemented | FriendAccessSummary.tsx; permission before/after review; FriendPcFilters; main.tsx |
| 43 | Search and filter Friend PCs | integration | Implemented | FriendAccessSummary.tsx; permission before/after review; FriendPcFilters; main.tsx |
| 44 | Enter access deadlines in local time | chat | Implemented | AccessExpiry.tsx; local-time input and exact UTC preview |
| 45 | Explain code replacement consequences | integration | Implemented | main.tsx server-code replacement consequence review |
| 46 | Preserve unfinished chat messages | chat | Implemented | ServerChat.tsx; protected scoped drafts; card summaries; ServerChat.DraftQueue.cs; FriendLink.ChatQueue.cs |
| 47 | Show unread room counts | chat | Implemented | ServerChat.tsx; protected scoped drafts; card summaries; ServerChat.DraftQueue.cs; FriendLink.ChatQueue.cs |
| 48 | Jump to latest in chat | chat | Implemented | ServerChat.tsx; protected scoped drafts; card summaries; ServerChat.DraftQueue.cs; FriendLink.ChatQueue.cs |
| 49 | Manage queued offline messages | chat | Implemented | ServerChat.tsx; protected scoped drafts; card summaries; ServerChat.DraftQueue.cs; FriendLink.ChatQueue.cs |
| 50 | Preview pinned notices on server cards | chat | Implemented | ServerChat.tsx; protected scoped drafts; card summaries; ServerChat.DraftQueue.cs; FriendLink.ChatQueue.cs |
| 51 | Merge backup lists | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 52 | Search and filter backups | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 53 | Keep verification results beside backups | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 54 | Improve Restore review | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 55 | Explain pin capacity | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 56 | Preview retention settings | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 57 | Suggest bookmark names | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 58 | Clarify rehearsal results | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 59 | Compare two backups | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 60 | One protection summary | backups | Implemented | BackupCatalog.tsx; backupCatalogModel.ts; BackupEvidence.cs; HostManager.BackupEvidence.cs |
| 61 | Preview raw configuration changes | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 62 | Guide preparation for editing | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 63 | Explain unavailable files/folders | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 64 | Improve settings explanations | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 65 | Search guided settings | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 66 | Improve player-list editing | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 67 | Explain checkpoint coverage | editors | Implemented | ServerFilesPanel.tsx; GameSettingsPanel.tsx; editorChanges.ts; protected draft and maintenance guards |
| 68 | Follow latest in Logs | logs | Implemented | ServerLogViewer.tsx; follow/pause state, fixed issue destinations and optional filter preferences |
| 69 | Actionable problem labels | logs | Implemented | ServerLogViewer.tsx; follow/pause state, fixed issue destinations and optional filter preferences |
| 70 | Remember log-view preferences | logs | Implemented | ServerLogViewer.tsx; follow/pause state, fixed issue destinations and optional filter preferences |
| 71 | Simplify receiving versus hosting | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 72 | Show shared-save age | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 73 | Improve transfer progress | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 74 | Copy recovery codes beside offers | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 75 | Paste recovery route details together | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 76 | Resumable planned-handoff checklist | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 77 | Game-specific future-host setup | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 78 | Explain hosting eligibility | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 79 | Compare competing saves | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 80 | Explain live-save availability | shared | Implemented | SharedWorldControls.tsx; sharedWorldUx.ts; route/server-file helpers; canonical received-save projections |
| 81 | Clickable weekly metrics | logs | Implemented | WeeklySummary.tsx; RecentSessions.tsx; WeeklyRuntimeProjection.cs; AcceptanceRecorder.tsx |
| 82 | Useful session views | logs | Implemented | WeeklySummary.tsx; RecentSessions.tsx; WeeklyRuntimeProjection.cs; AcceptanceRecorder.tsx |
| 83 | Recorded activity by day | logs | Implemented | WeeklySummary.tsx; RecentSessions.tsx; WeeklyRuntimeProjection.cs; AcceptanceRecorder.tsx |
| 84 | Explain stale owner confirmations | logs | Implemented | WeeklySummary.tsx; RecentSessions.tsx; WeeklyRuntimeProjection.cs; AcceptanceRecorder.tsx |
| 85 | Copy concise troubleshooting explanations | logs | Implemented | FriendConnectionDoctor.tsx; OwnerDiagnostics.tsx; bounded reviewed troubleshooting exports |
| 86 | Filter Attention Center | integration | Implemented | AttentionWorkspace.tsx; main.tsx scoped read/dismiss history and current snapshot problems |
| 87 | Separate current problems from history | integration | Implemented | AttentionWorkspace.tsx; main.tsx scoped read/dismiss history and current snapshot problems |
| 88 | Individual notice read/dismiss controls | integration | Implemented | AttentionWorkspace.tsx; main.tsx scoped read/dismiss history and current snapshot problems |
| 89 | Open notifications in context | integration | Implemented | desktopNotificationBridge.ts; DesktopNotificationPreferences.cs; current assignment-checked main.tsx destinations |
| 90 | Notification preferences and quiet mode | desktop | Implemented | DesktopPreferencesPanel.tsx; DesktopNotificationPreferences.cs; AppUpdater.cs; fixed local Program.cs routes |
| 91 | Persistent update snoozes | desktop | Implemented | DesktopPreferencesPanel.tsx; DesktopNotificationPreferences.cs; AppUpdater.cs; fixed local Program.cs routes |
| 92 | Release notes before updating | desktop | Implemented | DesktopPreferencesPanel.tsx; DesktopNotificationPreferences.cs; AppUpdater.cs; fixed local Program.cs routes |
| 93 | Visible update preparation | desktop | Implemented | DesktopPreferencesPanel.tsx; DesktopNotificationPreferences.cs; AppUpdater.cs; fixed local Program.cs routes |
| 94 | Remember window bounds | desktop | Implemented | QolLocalPreferences.cs; DesktopWindow.cs window placement and monitor math |
| 95 | Improve tray summary | desktop | Implemented | DesktopWindow.cs; AppBackgroundTasks.cs canonical tray summary |
| 96 | Larger text | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 97 | System/Light/Dark themes | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 98 | Stronger status distinctions | integration | Implemented | main.tsx; qolPreferences.ts; qol.css; scaled legacy stylesheet fonts |
| 99 | Polished keyboard navigation | integration | Implemented | WorkspaceChrome.tsx; guarded main.tsx command destinations |
| 100 | Accessible status announcements | integration | Implemented | WorkspaceChrome.tsx; QolWidgets.tsx; component polite statuses and stable keyboard focus |

## Validation and acceptance

Implementation: **100/100 (100 percent)**, integrated and independently mapped to current source. Completed on 2026-10-08 with nine gpt-6.1-sol/max workers: six Codex CLI lanes and three implementation subagents. The coordinator integrated and committed the authorized scope. Review follow-ups stayed within explicit file ownership; builds and commits were serialized.

Passing focus-safe checks:

- `scripts/verify-code-only.ps1`: pinned .NET 10.0.301 / Node 24.14.1; locked UI install with scripts disabled; TypeScript; zero-warning lint; locked .NET restore; complete solution compilation with zero warnings/errors; all 16 pure/synthetic/fake-adapter groups; whitespace and desktop diagnostic isolation.
- Full UI JSDOM suite: **583/583 passed across 63 files**. Earlier failures were resolved; the final full run has no failed tests.
- `npm run build`: TypeScript and bundled production UI passed. Vite reported its bundle-size advisory for the 893.27 kB minified main JS chunk (243.19 kB gzip); the threshold was not suppressed. Native startup/performance was not measured here.
- `dotnet build src/TogetherServer/TogetherServer.csproj -c Release --no-restore`: application compilation with the fresh bundled UI passed, zero warnings/errors.

Skipped: synthetic symbolic-link cases because the current test account could not create those links. Native window placement, tray/notification/quiet-mode/focus, picker and Quit/update/restart journeys; real browser viewport/keyboard checks; real Friend-PC/WAN/game joins; owner-installed game acceptance; valued-world save/load/change/restart acceptance. Those require an explicitly approved separate test PC or unattended Windows session. No skipped check is counted as a pass.

Source checks used bounded synthetic files, fake HTTP/desktop adapters and pure projections. No app, game, console fixture, browser automation, installer, staging package or listener was launched. No production data, real world, real credential, public network setting, release or tag was changed. No push or deployment was performed.

On 2026-10-09 the owner authorized additional testing on a separate Windows CI runner while preserving the running version. Follow-up fixes, the isolated draft PR and exact runtime results are recorded in [the QoL validation ledger](16-QOL-UX-VALIDATION.md). The implementation results above remain the original code-only baseline; pending runtime checks are not counted as passes.

## Integration and recovery notes

- Protected drafts are per actual file/list/settings/profile or selected Friend connection/room, use Windows CurrentUser protection and bounded CAS revisions, and never store passwords/scripts in setup recovery or private fields in browser preferences. Failed/uncertain saves and newer edits during flush retain navigation. Native manual Quit requests a bounded local WebView acknowledgement before taking the API mode gate; failure keeps the app open. Close-to-tray retains its normal hide behavior.
- Command and notification destinations batch scope changes after draft flush. Native notifications validate current owned/assigned profiles and their saved connection; clicks never authorize a lifecycle operation. Attention read/dismiss IDs remain scoped to their owning activity source. Current problems use current snapshot rows, while dated activity remains history.
- The Friend game probe accepts only selected connection and canonical run IDs, captures the assigned game/address, and rechecks that exact scope before and after the fixed probe. It never falls back to a different saved Host. A network reply still does not prove readiness, a Friend game join or a saved world change.
- Backup catalogs retain separate dated local integrity, vault transfer, hash rehearsal and owner-reported game rehearsal outcomes for exact completed-backup identities. Anchored file leases prevent a completion/payload replacement during measured backup operations. Failed optional evidence persistence preserves the actual operation result and reports unavailable history.
- Unrelated server views and local drafts stay usable during profile-specific work. Host mutations retain the existing lifecycle/mode serialization and all process identity, maintenance, checkpoint, one-writer and exact-zero Stop gates.
- Shared saves remain local-only with explicit Receive consent, manual guarded hosting, signed lineage and explicit grants. Live-save eligibility, receipt metadata and displayed dates do not grant hosting or claim unverified deployment acceptance.

The nine worker prompt packets are retained under `agent-prompts/`. Isolated worktrees and detailed handoffs remain ignored under `local-data/qol-workers/`; those are not shipped or committed. The 100-row ledger is the durable implementation record for future work.
