# Application UI redesign coverage

Source review on 2026-10-10 maps the approved synthetic atlas to the integrated application. [COVERAGE.md](mockups/COVERAGE.md) contains **29 pages, 120 dialogs and 38 confirmations**, or **187 views**. [coverage.json](mockups/coverage.json) contains **310 historical mapping rows**: 29 page, 128 dialog, 115 flow and 38 confirmation rows, with 294 distinct source references and 181 targets. Eighteen rows deliberately merge into another destination; none are marked removed. Historical line numbers have drifted, so this map names current modules and destinations.

These are inventory and source-reachability counts, **not 310 behavior tests or 187 rendered application approvals**. Focused application checks and final build results belong in [the delivery record](21-UI-REDESIGN-DELIVERY.md). The atlas's synthetic shared-flow model is design evidence only; existing backend authorization and observations govern the application.

## Pages

Navigation and guards are in [main.tsx](../ui/src/main.tsx), with section definitions and legacy aliases in [workspaceSections.ts](../ui/src/workspaceSections.ts). “Incorporated” means the content has a named owning page or disclosure rather than its former standalone surface.

| Atlas page | Integrated destination |
| --- | --- |
| `app-appearance` | App Settings → Appearance; direct `AppSettings` section. Header gear and rail open this same page. |
| `app-notifications` | App Settings → Notifications; PC defaults, with server overrides kept contextual. |
| `app-windows` | App Settings → Windows behavior; sign-in, tray and Quit. |
| `app-updates` | App Settings → Updates; review, trust, reminders and preparation details. |
| `app-shortcuts` | App Settings → Keyboard shortcuts. |
| `attention-current` | Attention Center → current problems and their destination actions. |
| `attention-history` | Attention Center → Activity history disclosure; incorporated into the same page. |
| `host-first-use` | Empty Host workspace → `HostFirstServer`; create, resume, Join and move guidance. |
| `host-all-servers` | Host → Compare all servers → wide All servers dialog; available with multiple saved servers. |
| `host-overview` | Selected Host → Overview; persistent Start/Stop header, Invite and masked connection details. |
| `host-chat` | Selected Host → Chat; `ServerChat`. |
| `host-players` | Selected Host → Players; `PlayersPanel`, fresh occupancy and add-time disclosure. |
| `host-logs` | Selected Host → Logs; `ServerLogViewer`. |
| `host-sessions` | Selected Host → Sessions; recent sessions and seven-day summary. |
| `host-backups` | Selected Host → Backups; catalog, World Safety Center and Host shared saves. |
| `host-files` | Selected Host → Settings & files (`files`); settings, player lists, files, add-ons and checkpoints. |
| `host-maintenance` | Selected Host → Maintenance & setup (`setup`). |
| `host-pc-access` | This Host PC → Friend access; PC filters, assignments and Manage access. |
| `host-pc-stop` | This Host PC → Stop & timer; countdown, extensions, safety and keep-awake. |
| `host-pc-connection` | This Host PC → Connection help (`network`); Doctor, route and TCP checks. |
| `host-pc-diagnostics` | This Host PC → Diagnostics; `OwnerDiagnostics` and redacted support export. |
| `host-pc-advanced` | This Host PC → Advanced; limits, listener, identity, move kit, notifications and removed PCs. |
| `join-pairing` | Join → Add a Host (`pairing`); masked invite input and older-invite disclosure. |
| `join-play` | Join → Play; `FriendPlayFlow`, lifecycle/game actions, connection details and requirements. |
| `join-chat` | Join → Chat; selected room and disconnected cached-room fallback. |
| `join-logs` | Join → Logs; separately granted log access and unsupported/ended feedback. |
| `join-shared` | Join → Shared saves (`shared-saves`); private copy, fresh connected-Host Join, Host this save and Manage sharing. |
| `join-help` | Join → Connection help (`network`); Friend Doctor and staging rehearsal. |
| `join-settings` | Join → Connection settings (`connection`); saved name, identity, address recovery, Forget and secondary server tools. |

## Dialog families

The following families account for all **120 dialog views**. A family can share one modal with sections or be incorporated into a page. Its presence in this map does not imply every loading, error, permission or game variant was executed.

| Atlas dialog IDs | Actual destination and owning source |
| --- | --- |
| `notification-menu`, `command-palette`, `server-picker`, `server-actions`, `invite-code`, `server-notifications` | Header notification disclosure, command dialog, searchable server picker, Server tools dialog, inline Overview invite and contextual notification dialog; `main.tsx`. |
| `quick-settings` | Historical App menu/preferences content incorporated into direct App Settings pages. Header gear opens Appearance; Windows behavior contains sign-in/tray/Quit and Updates contains update controls. No second App menu remains. |
| `update-review`, `update-progress`, `quit-review` | Update review dialog and preparation details in review/Updates; `DesktopPreferencesPanel`. Quit uses shared close/restart review when needed and backend refusal feedback, rather than an unconditional separate modal. |
| `activity-clear`, `chat-draft`, `global-draft-recovery`, `global-data-recovery`, `global-session-recovery` | Attention/header clear actions; Chat composer recovery; reusable protected editor recovery; `DataRecoveryPanel`; interrupted-session panel. Incorporated actions and panels. |
| `native-startup-error`, `native-runtime-fallback`, `native-close-warning` | Native fallbacks in `DesktopLaunch.cs` and `DesktopWindow.cs`; source reviewed only. |
| `host-overview-details`, `host-backup-tools`, `host-file-folders`, `host-maintenance-context` | Overview checks in Server tools; backup protection/catalog details; named server-folder tools in Settings & files; maintenance/setup context. Incorporated content. |
| `setup-game`, `setup-world`, `setup-app`, `setup-review`, `setup-import`, `setup-resume` | One four-step `HostSetupDialog`, inline import review and protected setup recovery. |
| `tool-countdown`, `tool-extensions`, `tool-safety`, `tool-doctor`, `tool-route`, `tool-tcp`, `tool-limits`, `tool-listener`, `tool-identity`, `tool-move`, `tool-revoked`, `tool-rehearsal` | Named `HostSettingsTools` dialogs under This Host PC Stop/network/Advanced; rehearsal is staging-only. |
| `tool-add-time`, `tool-health`, `tool-friend-test`, `tool-acceptance`, `tool-crash-recovery`, `tool-storage`, `tool-custom-certification`, `tool-session`, `tool-first-session` | Players add-time disclosure; health feedback; Doctor coordinated test and Acceptance Recorder; Overview/setup crash recovery; Attention storage/backup capacity; Custom certification; session cards; first-session guide. |
| `tool-backup-create`, `tool-protection`, `tool-backup-name`, `tool-backup-actions`, `tool-backup-compare`, `tool-backup-retention`, `tool-backup-restore`, `tool-backup-vault`, `tool-backup-hash-test`, `tool-game-rehearsal` | Backups page, `BackupCatalog`, `BackupBookmarkEditor`, protection in setup Review and `WorldLoadRehearsal`; inline name/pin, review, comparison, retention and guarded Restore. |
| `tool-file-editor`, `tool-file-review`, `tool-file-draft`, `tool-file-undo`, `tool-setup-checkpoint`, `tool-game-settings`, `tool-settings-review`, `tool-player-lists`, `tool-list-bulk`, `tool-addons`, `tool-requirements`, `tool-notice`, `tool-chat-members` | `ServerFilesPanel`, `GameSettingsPanel` and `ServerAddOnsPanel` in Settings & files; requirements in Maintenance & setup; pinned notices/membership in `ServerChat`. Predominantly incorporated panels. |
| `join-host-picker`, `join-pair-details`, `join-requirements`, `join-game-guide`, `join-tools`, `join-permissions`, `join-notice`, `join-chat-queue`, `join-chat-draft`, `join-rename`, `join-recover`, `join-pairing-help`, `join-chat-tools` | Saved Hosts picker; older-invite details on pairing; requirements/game guide/access help in Play; tools/name/address recovery in Connection settings; notice/queue/protected drafts in Chat. |
| `access-permissions`, `access-servers`, `access-time`, `access-details`, `access-preset-review`, `access-per-server`, `access-assignment-review`, `access-deadline`, `access-deadline-expired`, `access-helper`, `access-approval` | One Manage access dialog with Permissions, Servers, Time and Details sections; inline preset/assignment comparisons, per-server grants, deadline/helper/approval controls. |
| `shared-host-guide`, `shared-manage`, `shared-host-grants`, `shared-signing`, `shared-source-review`, `shared-handoff`, `shared-live-copy`, `shared-live-save` | `SharedWorldControls`: Host this save, Manage sharing, Host governance/signing/source checks and Move hosting; `LiveSaveControls` diagnostics and guarded live capture. |
| `shared-consent`, `shared-transfer`, `shared-receipt`, `shared-history`, `shared-group-trust`, `shared-recovery`, `shared-vote`, `shared-conflicts`, `shared-friend-grants` | Consent/progress on Shared saves; receipts/history/trust/recovery/votes/conflicts/member grants in named Manage sharing sections. |
| `shared-separate`, `shared-successor`, `shared-restore`, `shared-route`, `shared-join-successor`, `shared-rehearsal`, `shared-remote-rehearsal` | Separate-copy panel; `SharedWorldReadinessPanel` setup/restore/route checklist; guarded successor enrollment; disposable-copy rehearsal; staging owned-PC rehearsal. Preparation is in Host this save/Manage sharing; different new-Host identity leads to pairing. |

Six atlas dialogs have no direct historical `coverage.json` target: `shared-host-guide`, `shared-manage`, `host-overview-details`, `host-maintenance-context`, `join-pairing-help`, `join-chat-tools`. They are new compositions mapped above, rather than six omitted implementations.

## Decisions and aliases

The **28 original browser-decision sites** moved to labeled application confirmation dialogs using [Confirmation.tsx](../ui/src/Confirmation.tsx). Their **38 logical confirmation IDs** share handlers where appropriate; they are not 38 distinct modal call sites.

| Original sites | Logical atlas confirmation IDs and destination |
| --- | --- |
| Main application: 10 | `confirm-close-app`, `confirm-stop`, `confirm-restart`, `confirm-replace-code`, `confirm-forget-host`, `confirm-change-pages`, `confirm-safe-restart`, `confirm-discard-advanced`, `confirm-resume-hosting`, `confirm-revoke-custom`, `confirm-archive-session`; `main.tsx`, with Stop/Restart sharing review. |
| Setup: 4 | `confirm-remove-profile`, `confirm-discard-setup`, `confirm-finish-later`, `confirm-copy-cached-world`; `useHostSetup`, with confirmation bound to the current setup revision. |
| Game settings/lists: 4 | `confirm-reload-game-settings`, `confirm-discard-game-settings`, `confirm-reload-player-list`, `confirm-discard-player-list`; `GameSettingsPanel`. |
| Files/maintenance: 7 | `confirm-reload-file`, `confirm-restore-file-version`, `confirm-create-configuration`, `confirm-restore-setup`, `confirm-maintenance`, `confirm-finish-maintenance`, `confirm-discard-file`; `ServerFilesPanel`, with target checks around deferred review. |
| Add-ons: 1 handler | `confirm-addon-import-bedrock`, `confirm-addon-import-factorio`, `confirm-addon-enable`, `confirm-addon-disable`, `confirm-addon-undo`, `confirm-addon-version-review`; `ServerAddOnsPanel`. |
| Rehearsal cleanup: 1 | `confirm-cleanup-rehearsal`; `WorldLoadRehearsal`. |
| Recovery archive: 1 | `confirm-forget-session`; `DataRecoveryPanel`. |
| Additional logical atlas reviews | `confirm-discard-access`, `confirm-remove-device` are inline Manage access reviews; `confirm-end-helper` is explicit End now; `confirm-clear-deadline` is Clear selection followed by Save deadline. |

Additional application reviews include unsaved Host settings and unsaved notice/queued-message edits. A source search found no `window.confirm`, `window.prompt` or `window.alert` in application code; this is not native-runtime validation.

Legacy notification destinations remain supported: Settings `app` → App Appearance; legacy Settings access/stop/network/diagnostics/advanced → This Host PC; Join `overview` → Play. Atlas IDs map `join-shared` → `shared-saves`, `join-help` → `network`, `join-settings` → `connection`, `host-maintenance` → `setup`.

## Source review findings and corrections

| Finding | Integrated correction and verification boundary |
| --- | --- |
| Two guarded navigations could cancel selecting the prepared shared profile before opening setup. | `openSharedHost` performs profile/tab/preferences/scope selection within one guarded navigation. It opens the existing profile and never dispatches Start. Source reviewed; coordinator owns App-flow checks. |
| Header App settings used a separate menu/native-title surface. | One Button opens Appearance, matching the rail; `Controls.Button` supplies the shared keyboard/focus tooltip. Windows/Updates preserve the former menu operations. |
| Notice and queued-message edits were outside the ordinary composer guard. | `ServerChat` registers combined dirty/saving state, persists only the composer, and requires explicit discard of the exact notice/queue revision. Deferred scope or edit changes cancel authorization; navigation never publishes/sends. Source reviewed; coordinator records Chat checks. |
| Same-saved-Host notification/Attention navigation could stay behind Add another Host. | Notification navigation and Attention Open server explicitly clear pairing within guarded scope selection. Both corrections are present in the reviewed source; coordinator records App-flow checks. |
| An open hosting guide could retain preparation for an obsolete signed head. | Head/revision changes immediately withdraw readiness. Existing handoff GET revalidates the exact stage/head, with periodic single-flight checks, pending/failure/mismatch Unknown, ignored deferred old replies and preserved setup fields. |
| Failed/stalled shared-save GET left current Receiving/Verifying/Receipt progress visible. | Failure or six-second observation expiry marks current status Unknown, hides current progress/Host version, and retains verified-copy metadata, consent and guarded local recovery. A valid read restores observation. |
| Shared landing lacked direct joining/manual Start review actions. | Fresh connected Ready offers Join connected Host → Play; a different verified successor offers Pair with the new Host → pairing. Exact fresh local preparation promotes Review Start server → Host controls. Every action navigates only. |

The Shared corrections passed **85 focused JSDOM tests across seven files**, scoped lint and diff checks. Regression cases cover deferred head/profile responses, all setup draft fields, failed and stalled reads, recovery consent/grants, slow single-flight metadata hashing, connected Ready freshness, new-identity pairing and manual Start navigation. This is synthetic component evidence; no app, browser, native dialog, server process or game was launched for this slice. The coordinator's subsequent whole-application validation is separate.

## Safety and remaining acceptance

Host Start/Stop and Friend Play actions remain primary. Supporting settings, history and evidence have named destinations. Connection values use independent reveal/copy controls and rehide on blur/timeout; compact shared identity contains neither raw addresses nor repeated world headings. A saved connection name is only a display hint. Icons have short labels or accessible names, and shared dialogs use the existing naming, cancellation, focus containment and restoration behavior. Tooltip code inspection and JSDOM geometry do not certify rendered pixels.

Planned transfer remains a multi-PC journey: original Host Stops, prepares the final backup, confirms the successor's signed receipt and completes the signed decision; successor consents, verifies/stages the exact copy, prepares/restores on definitely Offline local storage, checks the pinned control route and then manually reviews Start. Friends pair with the new identity before joining. Copy possession, a route check, a process/listener or Start response never proves game readiness, game joining or save integrity. Start/Stop backend gates, signed lineage, distinct receive/eligible-host/voter/manager grants, trust, local consent and owner override are unchanged.

**Existing GET limitation:** ordinary received-save status has no current signed head or Host device/address metadata. Recovery GET identifies verified quorum heads, but does not identify another PC's Planned successor. This PC's handoff/restore GET exposes a verified staged successor only when the unique signed decision, exact local copy and configured signed candidate route match; UI identity also requires its hash to match the observed authority head. The original Host has full successor authority metadata, and the matching local successor can show “This PC preparing hosting.” Other Friends cannot derive that planned successor's identity from current GET contracts. They show signed Host unchecked/Unknown and keep any fresh connected-server observation separate. Matching endpoint text alone does not bind a Friend run to signed Host authority. **No backend or API change was made to bridge this limitation.**

Reachability is intentionally conditional: Compare all servers requires multiple profiles; Friend Logs requires its separate grant and distinguishes unsupported older Hosts; revoked/expired access may remove current profiles; Custom profiles have no shared-save surface. Staging setup excludes existing-world import, Custom, Factorio and Terraria. Real-game live capture stays disabled where unsupported. Guarded recovery/separate-copy/rehearsal remain available from a verified local copy when current Host capability is unavailable; receiving and delegated management retain capability/grant checks. A stopped or unreachable Host does not establish safe takeover, and recovery still warns about split histories and requires signed votes.

Protected setup/files/settings/lists and Chat participate in navigation guards; failed persistence blocks leaving, secrets require deliberate discard, and notice/queue edits require their own exact-revision review. Fresh positive or Unknown occupancy cancels automatic Stop; the backend's serialized zero-player recheck remains authoritative. Unknown is never zero. Restore continues to require definite Offline and never starts automatically.

Native startup/runtime/close dialogs, file/folder pickers, native WebView focus, real Friend-PC TLS/permissions/reachability, external TCP/game traffic, real game load/join/save/restart and rendered responsive acceptance are separate gates. They were not executed by this source audit or its focused JSDOM checks. Hosted image evidence, if collected by the coordinator, must identify its actual source/run and review scope in the delivery record. No active-desktop automation or production data/listener/settings changes were used for this review.
