# UI review coverage

29 pages, 149 dialogs and confirmations, 310 source mappings. All examples are synthetic. Application code has not been changed.

## Review views

| ID | Kind | Context | Title | States and variants |
| --- | --- | --- | --- | --- |
| app-appearance | Page | app | Appearance | normal, loading, error, success |
| app-notifications | Page | app | Notifications | normal, loading, error, blocked, success |
| app-windows | Page | app | Windows behavior | normal, loading, error, blocked, success |
| app-updates | Page | app | App updates | normal, loading, error, blocked, success, available, staging, signature-rejected |
| app-shortcuts | Page | app | Keyboard shortcuts | normal |
| attention-current | Page | attention | Needs attention | normal, empty, loading, error, success |
| attention-history | Page | attention | Activity history | normal, empty, loading, error, success |
| host-first-use | Page | host | Host your first server | normal, empty, loading, error, blocked, recovery, success |
| host-all-servers | Page | host | All servers | normal, empty, loading, error, blocked, recovery, success |
| host-overview | Page | host | Overview | normal, empty, loading, error, blocked, recovery, success, terraria, unknown, occupied, offline, starting, stopping, identity, staging |
| host-chat | Page | host | Chat | normal, empty, loading, error, blocked, recovery, success, offline |
| host-players | Page | host | Players | normal, empty, loading, error, blocked, recovery, success, occupied, terraria, unknown |
| host-logs | Page | host | Logs | normal, empty, loading, error, blocked, recovery, success, paused |
| host-sessions | Page | host | Sessions | normal, empty, loading, error, blocked, recovery, success |
| host-backups | Page | host | Backups | normal, empty, loading, error, blocked, recovery, success |
| host-files | Page | host | Settings & files | normal, empty, loading, error, blocked, recovery, success |
| host-maintenance | Page | host | Maintenance & setup | normal, empty, loading, error, blocked, recovery, success, active |
| host-pc-access | Page | host-pc | Friend access | normal, empty, loading, error, blocked, recovery, success |
| host-pc-stop | Page | host-pc | Stop & timer | normal, empty, loading, error, blocked, recovery, success |
| host-pc-connection | Page | host-pc | Connection help | normal, empty, loading, error, blocked, recovery, success |
| host-pc-diagnostics | Page | host-pc | Diagnostics | normal, empty, loading, error, blocked, recovery, success |
| host-pc-advanced | Page | host-pc | Advanced | normal, empty, loading, error, blocked, recovery, success |
| join-pairing | Page | join | Add a Host | normal, empty, loading, error, blocked, recovery, success, approval, expired, revoked, no-assignment, search-empty |
| join-play | Page | join | Play | normal, empty, loading, error, blocked, recovery, success, stale, expired, revoked, offline, starting, stopping, unknown, status-only, maintenance, port-conflict, listening, positive, update-required, staging |
| join-chat | Page | join | Server chat | normal, empty, loading, error, blocked, recovery, success, offline, expired, revoked, unsupported, cached-notice, cleared-notice, queue-full, draft-save-failed |
| join-logs | Page | join | Server logs | normal, empty, loading, error, blocked, recovery, success, paused, reading, ended, unsupported, expired, revoked, unassigned |
| join-shared | Page | join | Shared saves | normal, empty, loading, error, blocked, recovery, success, consent-off, untrusted, low-space, stalled, verifying, receipt-pending, expired, revoked, fenced, conflicts |
| join-help | Page | join | Connection help | normal, empty, loading, error, blocked, recovery, success, closed-port, timeout, network-offline, expired, revoked, game-query |
| join-settings | Page | join | Saved Host | normal, empty, loading, error, blocked, recovery, success, expired, revoked, credential-expired, pin-rotation, offline-forget |
| notification-menu | Dialog | app | Notifications | normal, empty, loading, error |
| quick-settings | Dialog | app | App preferences | normal |
| command-palette | Dialog | app | Commands | normal, empty, blocked |
| invite-code | Dialog | host | Invite friends to Alderheim | normal, loading, error, blocked, success |
| server-notifications | Dialog | host | Notifications for Alderheim | normal, loading, error, success |
| update-review | Dialog | app | Review app update | normal, loading, error, blocked, success |
| update-progress | Dialog | app | Preparing update | normal, loading, error, blocked, success |
| quit-review | Dialog | app | Quit TogetherServer? | normal, blocked, loading, error |
| activity-clear | Dialog | attention | Clear activity history? | normal, loading, error |
| chat-draft | Dialog | host | Review unsent message | normal, loading, error, recovery, success |
| global-draft-recovery | Dialog | app | Review protected draft | normal, loading, error, blocked, success |
| global-data-recovery | Dialog | app | Local data needs review | normal, loading, error, blocked, success |
| global-session-recovery | Dialog | host | Interrupted session | normal, loading, error, blocked, success |
| native-startup-error | Dialog | app | TogetherServer could not open | normal, window-failed, data-unavailable, data-rejected, local-gui-failed, link-failed |
| native-runtime-fallback | Dialog | app | The local interface is unavailable | normal, missing-runtime, navigation-failed, render-timeout |
| native-close-warning | Dialog | app | TogetherServer is still open | normal, draft-save-failed, managed-server-active, close-failed |
| server-picker | Dialog | host | Saved servers | normal, empty, loading, error |
| server-actions | Dialog | host | More server actions | normal, blocked, loading |
| tool-countdown | Dialog | host-pc | Empty-server countdown | normal, empty, loading, error, blocked, recovery, success |
| tool-extensions | Dialog | host-pc | Friend timer extensions | normal, empty, loading, error, blocked, recovery, success |
| tool-safety | Dialog | host-pc | Remote Stop safety | normal, empty, loading, error, blocked, recovery, success |
| tool-doctor | Dialog | host-pc | Connection Doctor | normal, empty, loading, error, blocked, recovery, success |
| tool-route | Dialog | host-pc | Friend connection route | normal, empty, loading, error, blocked, recovery, success, mesh, advanced |
| tool-tcp | Dialog | host-pc | Internet and local connection checks | normal, empty, loading, error, blocked, recovery, success, reachable, closed, stale |
| tool-limits | Dialog | host-pc | Hosting limits | normal, empty, loading, error, blocked, recovery, success |
| tool-listener | Dialog | host-pc | Advanced network settings | normal, empty, loading, error, blocked, recovery, success |
| tool-identity | Dialog | host-pc | Secure Host identity | normal, empty, loading, error, blocked, recovery, success |
| tool-move | Dialog | host-pc | Move hosting to another PC | normal, empty, loading, error, blocked, recovery, success |
| tool-revoked | Dialog | host-pc | Removed Friend PCs | normal, empty, loading, error, blocked, recovery, success |
| tool-rehearsal | Dialog | host-pc | Test with another owned PC | normal, empty, loading, error, blocked, recovery, success, production, report |
| tool-add-time | Dialog | host | Add shutdown time | normal, empty, loading, error, blocked, recovery, success, occupied |
| tool-health | Dialog | host | Server health | normal, empty, loading, error, blocked, recovery, success, java, bedrock, factorio, terraria, custom |
| tool-friend-test | Dialog | host-pc | Test with a Friend | normal, empty, loading, error, blocked, recovery, success, observed |
| tool-acceptance | Dialog | host-pc | Acceptance Recorder | normal, empty, loading, error, blocked, recovery, success, stale |
| tool-crash-recovery | Dialog | host | Crash recovery review | normal, empty, loading, error, blocked, recovery, success |
| tool-storage | Dialog | host-pc | Host storage & capacity | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-create | Dialog | host | Make an Offline checkpoint | normal, empty, loading, error, blocked, recovery, success |
| tool-protection | Dialog | host | World protection settings | normal, empty, loading, error, blocked, recovery, success, preview |
| tool-backup-name | Dialog | host | Backup name & retention pin | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-actions | Dialog | host | Inspect completed backup | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-compare | Dialog | host | Compare backup summaries | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-retention | Dialog | host | Retention preview | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-restore | Dialog | host | Review world Restore | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-vault | Dialog | host | Copy a verified backup to a vault | normal, empty, loading, error, blocked, recovery, success |
| tool-backup-hash-test | Dialog | host | Test restore hashes | normal, empty, loading, error, blocked, recovery, success |
| tool-game-rehearsal | Dialog | host | Test a copy in the game | normal, empty, loading, error, blocked, recovery, success, manual, failed |
| tool-file-editor | Dialog | host | Edit a reviewed server file | normal, empty, loading, error, blocked, recovery, success, java, bedrock, factorio, terraria |
| tool-file-review | Dialog | host | Review exact file changes | normal, empty, loading, error, blocked, recovery, success |
| tool-file-draft | Dialog | host | Protected file draft recovery | normal, empty, loading, error, blocked, recovery, success |
| tool-file-undo | Dialog | host | Review one-file Undo | normal, empty, loading, error, blocked, recovery, success |
| tool-setup-checkpoint | Dialog | host | Complete setup checkpoints | normal, empty, loading, error, blocked, recovery, success |
| tool-game-settings | Dialog | host | Reviewed game settings | normal, empty, loading, error, blocked, recovery, success, java, bedrock, valheim, custom |
| tool-settings-review | Dialog | host | Review game setting & list changes | normal, empty, loading, error, blocked, recovery, success, list |
| tool-player-lists | Dialog | host | Player administration | normal, empty, loading, error, blocked, recovery, success, java, bedrock |
| tool-list-bulk | Dialog | host | Review pasted players | normal, empty, loading, error, blocked, recovery, success, java, bedrock |
| tool-addons | Dialog | host | Add-ons & game version | normal, empty, loading, error, blocked, recovery, success, bedrock, vanilla |
| tool-requirements | Dialog | host | Before friends join | normal, empty, loading, error, blocked, recovery, success, factorio, bedrock |
| tool-notice | Dialog | host | Edit pinned room notice | normal, empty, loading, error, blocked, recovery, success |
| tool-chat-members | Dialog | host | People in this room | normal, empty, loading, error, blocked, recovery, success |
| tool-custom-certification | Dialog | host | Owner-certified Custom control | normal, empty, loading, error, blocked, recovery, success, inProgress, certified |
| tool-session | Dialog | host | Exact-run session details | normal, empty, loading, error, blocked, recovery, success, legacy |
| tool-first-session | Dialog | host | Try your first session | normal, empty, loading, error, blocked, recovery, success |
| setup-game | Dialog | host | Add a server · Game | normal, empty, loading, error, blocked, recovery, success, valheim, java, bedrock, factorio, terraria, custom, fixture |
| setup-world | Dialog | host | Add a server · World | normal, empty, loading, error, blocked, recovery, success, valheim, valheim-existing, java, bedrock, factorio, terraria, custom, fixture |
| setup-app | Dialog | host | Add a server · Server app | normal, empty, loading, error, blocked, recovery, success, valheim, java, bedrock, factorio, terraria, custom, fixture |
| setup-review | Dialog | host | Add a server · Review | normal, empty, loading, error, blocked, recovery, success, valheim, java, bedrock, factorio, terraria, custom, fixture, issues, editing |
| setup-import | Dialog | host | Review the world copy | normal, empty, loading, error, blocked, recovery, success, valheim, factorio, terraria, staging |
| setup-resume | Dialog | host | Resume protected setup | normal, empty, loading, error, blocked, recovery, success, custom, changed |
| join-pair-details | Dialog | join | Review an older invite | normal, empty, loading, error, blocked, recovery, success, expired, code-replaced |
| join-requirements | Dialog | join | Game requirements | normal, empty, loading, error, blocked, recovery, success, mismatch, add-on-mismatch, stale, old-host, expired, revoked |
| join-game-guide | Dialog | join | Join in the game | normal, empty, loading, error, blocked, recovery, success, java, bedrock, factorio, terraria, no-address |
| join-tools | Dialog | join | Players and server tools | normal, empty, loading, error, blocked, recovery, success, unknown, time-limit, replacement-blocked, controls-paused, maintenance |
| join-permissions | Dialog | join | Your access to this server | normal, empty, loading, error, blocked, recovery, success, expired, revoked, unassigned, temporary |
| join-notice | Dialog | join | Pinned server notice | normal, empty, loading, error, blocked, recovery, success, cleared, unsupported |
| join-chat-queue | Dialog | join | Queued chat messages | normal, empty, loading, error, blocked, recovery, success, full, expired, revoked |
| join-chat-draft | Dialog | join | Recover unfinished message | normal, empty, loading, error, blocked, recovery, success, save-uncertain |
| join-rename | Dialog | join | Rename saved Host | normal, empty, loading, error, blocked, recovery, success, expired |
| join-recover | Dialog | join | Connection identity and recovery | normal, empty, loading, error, blocked, recovery, success, expired, revoked, credential-expired, invalid-address, rotation |
| shared-host-grants | Dialog | host | Sharing permissions | normal, empty, loading, error, blocked, recovery, success, expired, revoked, owner-only, fenced |
| shared-friend-grants | Dialog | join | Manage sharing from Join | normal, empty, loading, error, blocked, recovery, success, expired, revoked, self |
| shared-signing | Dialog | host | Signing identity and grants | normal, empty, loading, error, blocked, recovery, success, expired |
| shared-source-review | Dialog | host | Review sharing source and signed roster | normal, empty, loading, error, blocked, recovery, success, friend-mode |
| shared-handoff | Dialog | shared | Move hosting to another PC | normal, empty, loading, error, blocked, recovery, success, recipient, receipt-pending, ready-to-complete, canceled, fenced, expired |
| shared-consent | Dialog | join | Allow saves on this PC | normal, empty, loading, error, blocked, recovery, success, expired, revoked, changed |
| shared-transfer | Dialog | join | Receive a verified save | normal, empty, loading, error, blocked, recovery, success, verifying, receipt, low-space, stalled, withdrawn, expired, revoked |
| shared-receipt | Dialog | shared | Exact save and signed receipt | normal, empty, loading, error, blocked, recovery, success, stale, host-view |
| shared-history | Dialog | shared | Signed hosting history | normal, empty, loading, error, blocked, recovery, success, fenced, conflicts, expired, revoked |
| shared-group-trust | Dialog | join | Confirm signed group and owner | normal, empty, loading, error, blocked, recovery, success, expired, revoked |
| shared-recovery | Dialog | join | Recover after Host loss | normal, empty, loading, error, blocked, recovery, success, host-current, historical, closed, observed-majority, conflicts, expired, revoked |
| shared-vote | Dialog | join | Review and cast a recovery vote | normal, empty, loading, error, blocked, recovery, success, majority, conflict-invitation, already-voted, expired, revoked |
| shared-separate | Dialog | join | Separate history and route | normal, empty, loading, error, blocked, recovery, success, route, ready-manual, running, expired, revoked |
| shared-conflicts | Dialog | shared | Resolve competing saved histories | normal, empty, loading, error, blocked, recovery, success, owner-override-off, branch-changed, old-host-running, expired, revoked |
| shared-successor | Dialog | join | Check this PC for future hosting | normal, empty, loading, error, blocked, recovery, success, route-needed, restore-done, unknown, java, bedrock, factorio, terraria, separate, expired, revoked, file-rehearsal, process-rehearsal |
| shared-restore | Dialog | join | Restore received copy for hosting | normal, empty, loading, error, blocked, recovery, success, java, bedrock, factorio, terraria, separate, changed-binary |
| shared-route | Dialog | shared | Check successor control route | normal, empty, loading, error, blocked, recovery, success, identity-mismatch, wrong-world, separate-proof, expired, current-host |
| shared-join-successor | Dialog | join | Join the signed new Host | normal, empty, loading, error, blocked, recovery, success, pin-mismatch, conflicts, expired, revoked |
| shared-rehearsal | Dialog | shared | Test a copy in the game | normal, empty, loading, error, blocked, recovery, success, prepared, load, change, restart, manual, cleaned, owner-failed, capacity, history |
| shared-remote-rehearsal | Dialog | shared | Staging test with another owned PC | normal, empty, loading, error, blocked, recovery, success, host-prepared, consent-needed, loopback, expired, revoked |
| shared-live-copy | Dialog | host | Interrupted live file copy | normal, empty, loading, error, blocked, recovery, success, full, friend-mode |
| shared-live-save | Dialog | host | Live save capture availability | normal, empty, loading, error, blocked, recovery, success, fixture, publishing, withdrawn, resume-refused, orphan |
| confirm-forget-session | Confirmation | host | Forget saved session | normal, loading, error, blocked, success |
| confirm-remove-profile | Confirmation | host | Remove server profile | normal, loading, error, blocked, success |
| confirm-discard-setup | Confirmation | host | Discard setup changes | normal, loading, error, blocked, success |
| confirm-finish-later | Confirmation | host | Finish setup later | normal, loading, error, blocked, success |
| confirm-copy-cached-world | Confirmation | host | Copy cached Valheim world | normal, loading, error, blocked, success |
| confirm-reload-game-settings | Confirmation | host | Reload settings and player lists | normal, loading, error, blocked, success |
| confirm-discard-game-settings | Confirmation | host | Discard unsaved settings | normal, loading, error, blocked, success |
| confirm-reload-player-list | Confirmation | host | Reload player list | normal, loading, error, blocked, success |
| confirm-discard-player-list | Confirmation | host | Discard player list draft | normal, loading, error, blocked, success |
| confirm-close-app | Confirmation | host-pc | Close or restart TogetherServer | normal, loading, error, blocked, success |
| confirm-stop | Confirmation | host-pc | Stop Alderheim | normal, loading, error, blocked, success |
| confirm-restart | Confirmation | host-pc | Restart Alderheim | normal, loading, error, blocked, success |
| confirm-replace-code | Confirmation | host-pc | Replace Alderheim code | normal, loading, error, blocked, success |
| confirm-forget-host | Confirmation | host-pc | Forget saved Host | normal, loading, error, blocked, success |
| confirm-change-pages | Confirmation | host-pc | Change pages with setup edits | normal, loading, error, blocked, success |
| confirm-safe-restart | Confirmation | host-pc | Safe restart with checkpoint | normal, loading, error, blocked, success |
| confirm-discard-advanced | Confirmation | host-pc | Discard advanced settings | normal, loading, error, blocked, success |
| confirm-resume-hosting | Confirmation | host-pc | Resume hosting | normal, loading, error, blocked, success |
| confirm-revoke-custom | Confirmation | host-pc | Revoke Custom certification | normal, loading, error, blocked, success |
| confirm-archive-session | Confirmation | host-pc | Archive exited session record | normal, loading, error, blocked, success |
| confirm-reload-file | Confirmation | host | Reload editor file | normal, loading, error, blocked, success |
| confirm-restore-file-version | Confirmation | host | Restore previous file version | normal, loading, error, blocked, success |
| confirm-create-configuration | Confirmation | host | Create game configuration | normal, loading, error, blocked, success |
| confirm-restore-setup | Confirmation | host | Restore complete setup checkpoint | normal, loading, error, blocked, success |
| confirm-maintenance | Confirmation | host | Prepare to change server | normal, loading, error, blocked, success |
| confirm-finish-maintenance | Confirmation | host | Finish maintenance | normal, loading, error, blocked, success |
| confirm-discard-file | Confirmation | host | Discard editor contents | normal, loading, error, blocked, success |
| confirm-cleanup-rehearsal | Confirmation | host | Clean up disposable rehearsal | normal, loading, error, blocked, success |
| confirm-addon-import-bedrock | Confirmation | host | Import Bedrock pack | normal, loading, error, blocked, success |
| confirm-addon-import-factorio | Confirmation | host | Import Factorio mod ZIP | normal, loading, error, blocked, success |
| confirm-addon-enable | Confirmation | host | Enable managed add-on | normal, loading, error, blocked, success |
| confirm-addon-disable | Confirmation | host | Disable managed add-on | normal, loading, error, blocked, success |
| confirm-addon-undo | Confirmation | host | Undo last add-on change | normal, loading, error, blocked, success |
| confirm-addon-version-review | Confirmation | host | Mark game version reviewed | normal, loading, error, blocked, success |
| confirm-discard-access | Confirmation | host-pc | Discard unsaved PC changes | normal, loading, error, blocked, success |
| confirm-remove-device | Confirmation | host-pc | Remove Friend PC A access | normal, loading, error, blocked, success |
| confirm-end-helper | Confirmation | host-pc | End temporary helper access | normal, loading, error, blocked, success |
| confirm-clear-deadline | Confirmation | host-pc | Clear owner access deadline | normal, loading, error, blocked, success |
| access-permissions | Dialog | host-pc | Permissions for Friend PC A | normal, loading, error, blocked, success |
| access-servers | Dialog | host-pc | Servers for Friend PC A | normal, loading, error, blocked, success |
| access-time | Dialog | host-pc | Access time for Friend PC A | normal, loading, error, blocked, success |
| access-details | Dialog | host-pc | PC details for Friend PC A | normal, loading, error, blocked, success |
| access-preset-review | Dialog | host-pc | Review permission preset | normal, loading, error, blocked, success |
| access-per-server | Dialog | host-pc | Per-server grants | normal, loading, error, blocked, success |
| access-assignment-review | Dialog | host-pc | Review server access changes | normal, loading, error, blocked, success |
| access-deadline | Dialog | host-pc | Choose owner access deadline | normal, loading, error, blocked, success |
| access-deadline-expired | Dialog | host-pc | Expired owner access | normal, loading, error, blocked, success |
| access-helper | Dialog | host-pc | Temporary helper access | normal, loading, error, blocked, success |
| access-approval | Dialog | host-pc | Pending approval for Friend PC B | normal, loading, error, blocked, success |

## Current-to-proposed mapping

App preference dialogs explicitly merged into direct App Settings pages remain listed. Inline editors and reviews are included as flows. Native Windows file pickers retain system presentation.

| Current source | Current view or decision | Proposed ID | Treatment | Notes |
| --- | --- | --- | --- | --- |
| ui/src/AppSettings.tsx:46 | Appearance dialog | app-appearance | merged | Direct App Settings section replaces the summary-card dialog. |
| ui/src/AppSettings.tsx:50 | Notifications dialog | app-notifications | merged | PC defaults are global; server overrides stay contextual. |
| ui/src/AppSettings.tsx:54 | Windows behavior dialog | app-windows | merged | Hosting power moves to This Host PC Advanced. |
| ui/src/AppSettings.tsx:58 | Updates dialog | app-updates | merged | Same section regardless of the current Host or Join page. |
| ui/src/AppSettings.tsx:62 | Shortcuts dialog | app-shortcuts | merged | Direct section retains keyboard destinations. |
| ui/src/main.tsx:1801 | Friend-mode App Settings | app-appearance | merged | Both global Settings branches become one identical PC preference workspace. |
| ui/src/main.tsx:2272 | Host-mode App Settings | app-appearance | merged | Host-specific controls are removed from global App Settings. |
| ui/src/AttentionWorkspace.tsx:249 | Attention current problems | attention-current | reframed | Actionable issues lead; dated history has its own section. |
| ui/src/AttentionWorkspace.tsx:249 | Attention activity and filters | attention-history | reframed | Existing filters, repeat grouping and contextual destinations retained. |
| ui/src/main.tsx:1715 | Header notifications menu | notification-menu | reframed | Shared menu and destination styling. |
| ui/src/main.tsx:1728 | Quick app preference menu | quick-settings | reframed | PC-wide options only. |
| ui/src/WorkspaceChrome.tsx:143 | Command palette | command-palette | reframed | Existing commands retain their operational destinations. |
| ui/src/main.tsx:1742 | Update review | update-review | reframed | Trust, notes and reminder decisions remain visible. |
| ui/src/main.tsx:1745 | Update verification and handoff progress | update-progress | reframed | No mock action downloads or restarts an app. |
| ui/src/main.tsx:1464 | Server invitation | invite-code | reframed | Synthetic code only; separate copy and reveal. |
| ui/src/DesktopPreferencesPanel.tsx:46 | Server notification overrides | server-notifications | reframed | Local PC override retains server and connection scope. |
| ui/src/ServerChat.tsx:198 | Protected chat draft and queue editing | chat-draft | reframed | Queued text remains distinct from signed accepted messages. |
| ui/src/editorProtectedDraft.tsx:1 | Protected editor recovery | global-draft-recovery | reframed | Review changes before applying. |
| ui/src/DataRecoveryPanel.tsx:1 | Local data recovery | global-data-recovery | reframed | Lifecycle blockers remain explicit. |
| ui/src/main.tsx:1770 | Interrupted-session recovery | global-session-recovery | reframed | Reattachment, exited and unknown process identity remain separate. |
| ui/src/AppSettings.tsx:64 | Guarded Quit | quit-review | reframed | Running or unresolved managed servers block Quit. |
| ui/src/AttentionWorkspace.tsx:249 | Activity history clear | activity-clear | reframed | Confirmation only; server state is retained. |
| src/TogetherServer/DesktopLaunch.cs:61 | Native startup message fallback | native-startup-error | retained | Native fallback remains available when the WebView cannot show an app dialog. |
| src/TogetherServer/DesktopWindow.cs:473 | Window startup failed | native-startup-error | retained | Window-failed state preserves the outcome. |
| src/TogetherServer/Program.cs:56 | Local data unavailable at startup | native-startup-error | retained | Separate from invalid data; no destructive recovery. |
| src/TogetherServer/Program.cs:65 | Local data rejected at startup | native-startup-error | retained | No server action was started. |
| src/TogetherServer/Program.cs:2352 | Local GUI startup failed | native-startup-error | retained | The interface failure supplies no process authority. |
| src/TogetherServer/DesktopWindow.cs:910 | WebView2 download-page dispatch failed | native-startup-error | retained | Atlas does not dispatch a browser or install a runtime. |
| src/TogetherServer/DesktopWindow.cs:920 | Approved external page dispatch failed | native-startup-error | retained | Link-failed state retains the local app. |
| src/TogetherServer/DesktopWindow.cs:665 | Missing WebView2 runtime fallback | native-runtime-fallback | retained | Native fallback before the bundled interface is available. |
| src/TogetherServer/DesktopWindow.cs:714 | Local interface navigation failure | native-runtime-fallback | retained | Explicit navigation-failed state. |
| src/TogetherServer/DesktopWindow.cs:974 | Native Quit refused because draft protection failed | native-close-warning | retained | Draft-save-failed state keeps the app open. |
| src/TogetherServer/DesktopWindow.cs:992 | Native Quit refused by managed-server gate | native-close-warning | retained | Managed-server-active state keeps the app open. |
| src/TogetherServer/DesktopWindow.cs:1000 | Native Quit request failed | native-close-warning | retained | Close-failed state retains the running app. |
| ui/src/main.tsx:1980 | Saved server search, pins and ordering | server-picker | reframed | Searchable picker replaces the permanent saved-server column. |
| ui/src/main.tsx:2170 | More Host server actions | server-actions | reframed | Supporting actions use one compact secondary menu. |
| ui/src/main.tsx:1835 | Friend server tools | join-tools | reframed | Operational tools stay inside Join. |
| ui/src/HostFirstServer.tsx:16 | First Host / Join choice | host-first-use | reframed | Two compact paths preserve equal Host and Join prominence. |
| ui/src/HostFirstServer.tsx:19 | Unfinished first setup | setup-resume | retained | Non-secret continuation stays on this PC; password re-entry remains explicit. |
| ui/src/HostFirstServer.tsx:29 | Move from another PC | tool-move | retained | World kit inspection remains an owner-only path. |
| ui/src/AllServerOverview.tsx:89 | All-server comparison | host-all-servers | reframed | Attention-first compact cards preserve selection and separate current count from archived evidence. |
| ui/src/main.tsx:1981 | Saved-server search and ordering | host-all-servers | merged | Search, favorites, and manual order remain under named comparison controls. |
| ui/src/main.tsx:2001 | Selected server workspace | host-overview | reframed | Primary lifecycle toolbar is supplied by the atlas shell; body starts with connection details. |
| ui/src/main.tsx:2032 | Start / Stop / Unknown actions | host-overview | retained | Offline, Starting, Stopping, occupied, Unknown occupancy, and uncertain identity each have an explicit variant. |
| ui/src/main.tsx:2036 | Invite friends action | invite-code | retained | Coordinator-owned code dialog stays linked from Overview and access. |
| ui/src/main.tsx:2067 | Masked connection details | host-overview | retained | IP and password have independent reveal/copy controls and concealment explanation. |
| ui/src/ConnectionDetails.tsx:1 | Connection-value controls | host-overview | retained | Synthetic values only; icon actions carry names and tooltips. |
| ui/src/main.tsx:2077 | Host chat workspace | host-chat | reframed | Delivered messages, sync feedback, owner notice, member disclosure, and protected draft remain separate. |
| ui/src/main.tsx:2123 | Host Players workspace | host-players | reframed | Current trusted count and timer lead; source/help/count-only history are disclosed. |
| ui/src/PlayersPanel.tsx:115 | Current player evidence | host-players | retained | Unknown/occupied/Terraria variants keep unavailable occupancy distinct from empty. |
| ui/src/PlayersPanel.tsx:148 | Count source and timer reason | host-players | retained | Source, freshness, base window, saved added time, and final check remain visible in a named disclosure. |
| ui/src/PlayersPanel.tsx:161 | Game-specific occupancy help | tool-health | merged | Each reviewed driver retains its limitation in health variants. |
| ui/src/PlayersPanel.tsx:173 | Count-only transitions | host-players | retained | No names or IPs; historic count cannot authorize current Stop. |
| ui/src/main.tsx:2129 | Host timer additions | tool-add-time | reframed | Preset and custom additions stay beside the active timer and work while occupied with trusted count. |
| ui/src/main.tsx:2073 | Host Logs workspace | host-logs | reframed | Bounded exact-run tail with source and problem navigation. |
| ui/src/ServerLogViewer.tsx:352 | Pause / follow / refresh logs | host-logs | retained | Paused, error, stale-cursor recovery, and unsupported source states remain explicit. |
| ui/src/ServerLogViewer.tsx:364 | Severity / category / text filters | host-logs | retained | Filters, only-problems, clear, and jump-latest controls remain available. |
| ui/src/main.tsx:2094 | Archived Sessions workspace | host-sessions | reframed | Exact archived runs and the seven-day projection share one compact page. |
| ui/src/RecentSessions.tsx:186 | Session evidence/outcome/date filters | host-sessions | retained | Known timing and legacy gaps remain distinct; show-more is preserved. |
| ui/src/RecentSessions.tsx:256 | Archived run drilldown | tool-session | reframed | Typed lifecycle/count/backup summary with a legacy Unavailable variant. |
| ui/src/WeeklySummary.tsx:69 | Seven-day runtime summary | host-sessions | merged | Daily runtime links and retained-run exploration remain with coverage explanations. |
| ui/src/WeeklySummary.tsx:115 | Summary coverage and gaps | host-sessions | retained | Unfinished/legacy/clipped evidence is never upgraded into current authority. |
| ui/src/main.tsx:2098 | Host completed-copy catalog | host-backups | reframed | One searchable catalog leads the protection workflow. |
| ui/src/main.tsx:2166 | World Safety Center | host-backups | merged | Offline checkpoint, capacity, safe restart, settings, and distinct protection results remain on Backups. |
| ui/src/BackupCatalog.tsx:257 | Backup search and filters | host-backups | retained | Name, pins, kind, and local dates remain exposed before the list. |
| ui/src/BackupCatalog.tsx:61 | Review completed backup | tool-backup-actions | reframed | Integrity, vault, hash restore, game load, move kit, and world Restore each remain named. |
| ui/src/BackupBookmarkEditor.tsx:84 | Backup name and pin editor | tool-backup-name | reframed | Metadata stays separate from immutable payload/setup hashes and retention caps. |
| ui/src/BackupCatalog.tsx:84 | Compare two summaries | tool-backup-compare | reframed | Recorded metadata only; no fresh hash or file-content diff is implied. |
| ui/src/BackupCatalog.tsx:274 | Retention preview | tool-backup-retention | reframed | Pins use separate caps; projection and unpin delete nothing immediately. |
| ui/src/BackupCatalog.tsx:116 | Guarded world Restore review | tool-backup-restore | reframed | Current world, scope, re-verification, pre-restore checkpoint, and explicit acknowledgement remain required. |
| ui/src/main.tsx:2169 | Offline manual checkpoint | tool-backup-create | reframed | Definite Offline and reviewed save-only source are stated before the simulated action. |
| ui/src/main.tsx:2110 | Copy completed backup to vault | tool-backup-vault | reframed | Native owner selection, overlap/link rejection, destination hashes, and local preservation remain explicit. |
| ui/src/main.tsx:2111 | Hash-only restore rehearsal | tool-backup-hash-test | reframed | Only fresh owned scratch; no live world swap or game-load claim. |
| ui/src/WorldLoadRehearsal.tsx:111 | Test a copy in the game | tool-game-rehearsal | reframed | Exact source/copy, isolated Valheim trial, manual path, owner outcomes, and stopped cleanup stay distinct. |
| ui/src/main.tsx:2171 | Shared-save grants | shared-host-grants | retained | Join worker owns dialog; Host Backups retains discovery links. |
| ui/src/main.tsx:2171 | Planned signed handoff | shared-handoff | retained | Backups links to separately owned signed handoff; no automatic takeover. |
| ui/src/main.tsx:2171 | Signed history and conflicts | shared-history | retained | Host can reach the Shared World history and conflict-review dialogs. |
| ui/src/LiveSaveControls.tsx:1 | Real-game live capture acceptance | shared-live-copy | retained | Backups explicitly shows real-game live capture disabled until each game passes its own matrix. |
| ui/src/main.tsx:2084 | Settings & files workspace | host-files | reframed | Simple reviewed settings lead; folders, raw files, add-ons, checkpoint and Undo remain named disclosures. |
| ui/src/ServerFilesPanel.tsx:297 | Open actual saved folders | host-files | retained | Server, world, app run logs, backups, and reviewed add-on folders; missing locations stay unavailable. |
| ui/src/GameSettingsPanel.tsx:155 | Simple reviewed game controls | tool-game-settings | reframed | Java/Bedrock controls plus Valheim/Custom limitation variants; no arbitrary property or path in simple controls. |
| ui/src/GameSettingsPanel.tsx:405 | Exact typed before/after review | tool-settings-review | reframed | Current file hash, review invalidation, complete checkpoint, and saved prior version remain required. |
| ui/src/GameSettingsPanel.tsx:333 | Typed administration lists | tool-player-lists | reframed | Valheim administrator/ban/permit and Java/Bedrock allow-list variants preserve game-specific fields. |
| ui/src/GameSettingsPanel.tsx:370 | Paste several game-list entries | tool-list-bulk | reframed | Parsed preview first merges into an unsaved draft; it never looks up identities. |
| ui/src/ServerFilesPanel.tsx:345 | Raw reviewed file editor | tool-file-editor | reframed | Game-specific raw file variants, Notepad, reload, recovery, review, and Undo are kept. |
| ui/src/ServerFilesPanel.tsx:351 | Exact raw line comparison | tool-file-review | reframed | Line endings and before/after indices remain visible; changed bytes/draft invalidate review. |
| ui/src/ServerFilesPanel.tsx:348 | Protected raw draft recovery | tool-file-draft | reframed | Explicit current-vs-draft review without silent saved-file replacement. |
| ui/src/ServerFilesPanel.tsx:187 | One-file Undo | tool-file-undo | reframed | Tool reviews scope then links the separately owned native-confirm replacement. |
| ui/src/ServerFilesPanel.tsx:327 | Complete setup checkpoints | tool-setup-checkpoint | reframed | Complete vs world-only, included/excluded data, external packs, pre-restore snapshot, rollback, and recovery blocks remain clear. |
| ui/src/ServerAddOnsPanel.tsx:86 | Reviewed add-on inventory | tool-addons | reframed | Factorio and Bedrock variants retain enable/disable/import/Undo/version-review and unsupported vanilla paths. |
| ui/src/GameCompatibilityPanel.tsx:80 | Host requirements disclosure | tool-requirements | reframed | Observed vs owner-reported game versions, manual clear, enabled inventory, freshness, and Unknown remain distinct. |
| ui/src/main.tsx:2135 | Maintenance guide workspace | host-maintenance | reframed | Five-step local pause/zero-Stop/checkpoint/edit/validate workflow with active and recovery states. |
| ui/src/MaintenanceGuide.tsx:21 | Prepare / finish maintenance | host-maintenance | retained | Owner message, count gate, checkpoint, restart, Doctor, and actual confirmation links remain reachable. |
| ui/src/main.tsx:2195 | Provider game-update guide | host-maintenance | retained | Owner handles installation/terms; changed artifacts invalidate earlier acceptance. |
| ui/src/main.tsx:2151 | Custom live certification | tool-custom-certification | reframed | Contract v2, exact fingerprint, zero/positive/zero, final check, wrapper exit, saved change and restart remain required. |
| ui/src/main.tsx:2164 | Bounded crash recovery | tool-crash-recovery | reframed | Eligibility, 1/5/15-minute retries, suspension, Stop intent, and preview limitations are explicit. |
| ui/src/main.tsx:2180 | More server actions | host-overview | retained | Restart, safe checkpoint restart, edit, health, Friend access, and exited-run review remain findable. |
| ui/src/main.tsx:2184 | Owner health review | tool-health | reframed | Fixed reviewed health checks with game-specific sources and safety boundaries. |
| ui/src/main.tsx:2259 | Host-wide settings navigation | host-pc-access | reframed | This Host PC separates shared controls from selected-server tools. App preferences remain coordinator-owned. |
| ui/src/main.tsx:2280 | Friend access workspace | host-pc-access | reframed | Listener/controls switches, PC filters, approval, deadlines, and separate per-PC editor links remain. |
| ui/src/main.tsx:2320 | Per-PC permission review | access-permissions | retained | Dialog belongs to coordinator; Host cards link it without duplicating policy. |
| ui/src/main.tsx:2314 | Per-PC assigned servers | access-servers | retained | Independent assignments retain their own editor. |
| ui/src/AccessExpiry.tsx:1 | Owner-defined per-PC deadline | access-time | retained | Deadline is distinct from credential expiry and removal; card keeps expired status and editor access. |
| ui/src/FriendPcCard.tsx:1 | Per-PC identity/details | access-details | retained | Approval/paired/revoked state is reviewed separately from assignments or permission presets. |
| ui/src/HostSettingsTools.tsx:92 | Shared Stop & timer page | host-pc-stop | reframed | Automatic shutdown remains off by default and is separate from fixed Friend extensions. |
| ui/src/HostSettingsTools.tsx:97 | Idle countdown editor | tool-countdown | retained | 1–1440 minutes; fresh zero and final serialized probe are explicit. |
| ui/src/HostSettingsTools.tsx:99 | Friend extension editor | tool-extensions | retained | 1–120 increment and at least one increment up to 1440 maximum; per-PC grants remain independent. |
| ui/src/HostSettingsTools.tsx:101 | Remote Stop safety review | tool-safety | retained | Every saved server has a current reason, and Unknown never authorizes Stop. |
| ui/src/HostSettingsTools.tsx:106 | Host connection-help page | host-pc-connection | reframed | App route, game route, private networking, optional checker, and Doctor remain separate. |
| ui/src/HostSettingsTools.tsx:112 | Connection Doctor tool | tool-doctor | retained | Six non-transitive evidence stages and detailed next actions. |
| ui/src/ConnectionDoctor.tsx:104 | Coordinated Friend test | tool-friend-test | reframed | Post-start authenticated heartbeat/count transition plus explicit intended join/save confirmations. |
| ui/src/AcceptanceRecorder.tsx:134 | Owner acceptance recorder | tool-acceptance | reframed | Seven explicit fingerprint-bound owner checks; stale config/game identity and copy explanation retained. |
| ui/src/HostSettingsTools.tsx:114 | Direct/mesh/advanced route editor | tool-route | retained | Detected adapter selection and chosen IPv4 are read-only network-policy discovery. |
| ui/src/HostSettingsTools.tsx:116 | Internet/local checks tool | tool-tcp | retained | Optional checker purpose, address hint, stale/current TCP, router target, and inconclusive state remain. |
| ui/src/HostSettingsTools.tsx:120 | Owned-PC staging rehearsal | tool-rehearsal | retained | Staging-only synthetic preparation; pairing/grants/consent and honest LAN/outside/game boundaries remain required. |
| ui/src/HostSettingsTools.tsx:126 | Advanced Host settings page | host-pc-advanced | reframed | Limits, listener, identity, world move, and removed PCs remain under named cards. |
| ui/src/HostSettingsTools.tsx:130 | Managed-server limit editor | tool-limits | retained | 1–16 count never bypasses ports, one writer, or physical capacity. |
| ui/src/HostSettingsTools.tsx:132 | Explicit HTTPS listener configuration | tool-listener | retained | Owner GUI remains loopback; no arbitrary companion executable/path/command surface. |
| ui/src/HostSettingsTools.tsx:134 | Pinned Host identity and rotation | tool-identity | retained | Prepare, activate, bounded previous overlap, and retire remain deliberate local actions. |
| ui/src/HostSettingsTools.tsx:137 | World move-kit guide | tool-move | retained | Hash inspect and fresh local setup exclude binaries, credentials, TLS keys, and passwords. |
| ui/src/HostSettingsTools.tsx:140 | Revoked PC list | tool-revoked | retained | Removed access remains separate from expiration and cannot be silently revived. |
| ui/src/main.tsx:2301 | Owner diagnostic workspace | host-pc-diagnostics | reframed | Selected-server and Host-wide checks remain read-only, with recovery and redacted export. |
| ui/src/OwnerDiagnostics.tsx:195 | Preflight and fixed checks | host-pc-diagnostics | retained | Refresh and local pane retry do not mutate a server. |
| ui/src/OwnerDiagnostics.tsx:223 | Safe support export | host-pc-diagnostics | retained | Copy summary/report and fixed-name download retain allowlisted content and secret/path/address exclusions. |
| ui/src/main.tsx:2167 | Backup capacity and warnings | tool-storage | reframed | Bounded retained/free/reserve evidence remains informational and never proves integrity. |
| ui/src/ServerChat.tsx:490 | Pinned notice editor | tool-notice | reframed | 2000-character owner text, signed revision, explicit clear, and stale revision recovery remain. |
| ui/src/ServerChat.tsx:566 | Room members disclosure | tool-chat-members | reframed | Room removal is independent of lifecycle, logs, and shared saves; existing copies cannot be recalled. |
| ui/src/ServerChat.tsx:550 | Protected chat draft recovery | chat-draft | retained | Coordinator-owned protected-message flow stays linked from Host Chat. |
| ui/src/features/setup/HostSetupDialog.tsx:243 | Four-stage setup wizard | setup-game | reframed | All setup dialogs remain design-only, with game-variant navigation and non-secret finish-later. |
| ui/src/features/setup/HostSetupDialog.tsx:261 | Six game choices and fixture | setup-game | retained | Valheim, Java, Bedrock, Factorio, Terraria, Custom, and staging-only fixture each have selectable variants. |
| ui/src/features/setup/HostSetupDialog.tsx:255 | Reuse saved non-secret setup | setup-game | retained | Reusing setup does not duplicate worlds, passwords, scripts, credentials, or live writers. |
| ui/src/features/setup/HostSetupDialog.tsx:270 | World setup variants | setup-world | retained | New/copied Valheim, Minecraft naming/port/mode, preview imports, Custom directory, and fixture remain distinct. |
| ui/src/features/setup/HostSetupDialog.tsx:281 | Valheim older saves/custom source | setup-world | retained | Named root, .db/.fwl chooser, Steam-sync guidance, and review-copy action remain disclosed. |
| ui/src/features/setup/HostSetupDialog.tsx:311 | Server app selection variants | setup-app | retained | Installed game discovery/manual choice, explicit Minecraft consent, previews, Custom actions, and fixture are preserved. |
| ui/src/MinecraftSetup.tsx:98 | Owner-approved official install | setup-app | retained | Unselected acknowledgement with linked review controls; mockup downloads nothing or accepts no real terms. |
| ui/src/MinecraftSetup.tsx:125 | Prepared installation discovery | setup-app | retained | Search, selection, browse, and manual path retain strict official Java provenance. |
| ui/src/features/setup/HostSetupDialog.tsx:331 | Protected Custom script editor | setup-app | retained | Three owner-only actions, limits/environment/contract, wrapper lifetime, and staging exclusion remain explicit. |
| ui/src/features/setup/HostSetupDialog.tsx:345 | Review and Start variants | setup-review | retained | Every game’s technical defaults/ports/protection and data-recovery blockers are reviewed before final action. |
| ui/src/features/setup/HostSetupDialog.tsx:352 | Review editable game defaults | setup-review | retained | Valheim listing/path/world, preview executable/copy ports, and Custom extra ports retain named fields. |
| ui/src/features/setup/HostSetupDialog.tsx:33 | Advisory saved-port warning | setup-review | retained | Suggested plan changes remain advisory; ordinary authoritative Start conflict stops nothing. |
| ui/src/features/setup/HostSetupDialog.tsx:372 | Remove an existing saved profile | confirm-remove-profile | retained | Removal link is present only in the editing variant; world files remain on disk. |
| ui/src/features/setup/HostSetupDialog.tsx:419 | Source/destination import review | setup-import | retained | Valheim, Factorio ZIP, Terraria WLD, missing facts, expired reviews, and staging block have explicit variants. |
| ui/src/features/setup/HostSetupDialog.tsx:376 | Finish later and resume | setup-resume | reframed | Names/choices persist as bounded non-secret draft; passwords/scripts and unsaved advanced settings do not. |
| ui/src/setupFirstSessionGuide.tsx:11 | First session guide | tool-first-session | reframed | Ready, private invite, real saved change/restart, and backup trial stay as four direct next steps. |
| ui/src/features/setup/HostSetupDialog.tsx:361 | Per-server backup and crash protection preferences | tool-protection | reframed | Rolling backup, retention count, free-space reserve, off-by-default crash recovery and preview/Custom restrictions stay separate from verified game-save acceptance. |
| ui/src/main.tsx:1811 | Saved Hosts list, search, pin and selection | join-play | retained | Keeps independent saved connections and current selection. |
| ui/src/main.tsx:1820 | Add another Host without stopping current capabilities | join-pairing | reframed | Dedicated pairing page with return to saved Host. |
| ui/src/main.tsx:1836 | Password-style code entry and pinned Connect | join-pairing | retained | Uses invalid synthetic code and no network request. |
| ui/src/main.tsx:1841 | TS1 separate Host IP and older invite expiry | join-pair-details | reframed | Older-format review separated from normal TS3 pairing. |
| ui/src/main.tsx:1827 | Approval pending and saved connection retention | join-pairing | retained | Approval does not require a second code. |
| ui/src/main.tsx:1846 | Connected with zero assigned servers | join-play | retained | Owner assignment remains separate from authentication. |
| ui/src/main.tsx:1857 | Durable remote operation progress | join-play | retained | Pending, running, failed and interrupted states do not imply game results. |
| ui/src/main.tsx:1861 | Per-server maintenance and owner message | join-play | retained | Status remains visible while lifecycle requests are denied. |
| ui/src/main.tsx:1873 | Masked connection address and separate reveal/copy | join-play | retained | Address and password remain separate; only reserved demo values are copied. |
| ui/src/main.tsx:1892 | Player count, timer reason and shared deadline | join-tools | reframed | Server observations and Host-owned timer stay independent of Friend presence. |
| ui/src/main.tsx:1896 | Friend Restart requiring both grants and safe Stop | join-tools | reframed | Exact-zero and current authorization remain visible blockers. |
| ui/src/main.tsx:1897 | Fixed Friend added-time request and remaining allowance | join-tools | reframed | No arbitrary duration is accepted. |
| ui/src/main.tsx:1898 | Fixed game endpoint check from this Friend PC | join-help | merged | Independent game-query stage remains distinct from join evidence. |
| ui/src/main.tsx:1911 | Explicit replace empty port-conflicting server | join-tools | reframed | Ordinary Start never stops conflicts; no unassigned join details are disclosed. |
| ui/src/main.tsx:1915 | Unavailable Stop and Restart reasons | join-permissions | merged | Current grants, occupancy and maintenance blockers appear together. |
| ui/src/main.tsx:1924 | Open selected-server room and notice preview | join-chat | reframed | Chat receives its own page while retaining room scope. |
| ui/src/main.tsx:1939 | Offline saved rooms and local queue | join-chat | retained | Queued text remains undelivered until current Host confirmation. |
| ui/src/main.tsx:1954 | Saved Host identity, expiry and endpoint details | join-settings | reframed | Connection maintenance is a secondary named page. |
| ui/src/main.tsx:1956 | Rename local connection | join-rename | reframed | Local label changes no remote identity or grants. |
| ui/src/main.tsx:1960 | Changed endpoint with saved identity and credential | join-recover | reframed | No certificate bypass or arbitrary trust replacement. |
| ui/src/main.tsx:1081 | Forget saved Host confirmation | confirm-forget-host | retained | Shared confirmation is owned by the coordinator; reachable and offline consequences remain explicit. |
| ui/src/main.tsx:1968 | Staging Friend connection rehearsal | shared-remote-rehearsal | reframed | Uses normal pairing, Receive and local consent with separate seven-stage report. |
| ui/src/main.tsx:121 | Typed Friend connection troubleshooting by denial code | join-help | merged | Invalid invite, identity, network, closed port, timeout and permission remain distinct. |
| ui/src/FriendPlayFlow.tsx:18 | Owner access expiry distinct from revoked device | join-play | retained | AccessExpired retains connection and needs owner extension or Clear. |
| ui/src/FriendPlayFlow.tsx:26 | Approval, identity mismatch and renewable credential expiry | join-play | retained | Independent blocked states avoid treating every denial as disconnection. |
| ui/src/FriendPlayFlow.tsx:39 | Unavailable Host and stale reply | join-play | retained | Old Ready, count and grants cannot authorize current lifecycle actions. |
| ui/src/FriendPlayFlow.tsx:48 | App protocol mismatch versus paused controls | join-play | retained | A game-version match cannot repair companion protocol. |
| ui/src/FriendPlayFlow.tsx:60 | Offline, Starting, Ready, Listening, Stopping, Failed, Unknown | join-play | retained | Process presence and local listeners never prove game readiness or join. |
| ui/src/FriendPlayFlow.tsx:89 | Effective exact-server action and log permissions | join-permissions | reframed | Start, Stop, added time and logs stay independent. |
| ui/src/FriendPlayFlow.tsx:188 | Concrete game join instructions | join-game-guide | reframed | Named game guide remains near Play details. |
| ui/src/FriendPlayFlow.tsx:190 | Connection checks before Friend Start | join-help | merged | Read-only checks do not authorize or launch a game. |
| ui/src/ConnectionDetails.tsx:1 | Address reveal and copy presentation | join-play | retained | Atlas masks the reserved fake IP and copies only the synthetic value. |
| ui/src/GameCompatibilityPanel.tsx:79 | Required and installed game-version comparison | join-requirements | reframed | Observed, owner-reported and manual sources remain labeled. |
| ui/src/GameCompatibilityPanel.tsx:98 | Stale and unknown requirement check age | join-requirements | retained | Earlier comparisons retain age and do not become current matches. |
| ui/src/GameCompatibilityPanel.tsx:100 | Reviewed add-on inventory and Unknown/mismatch comparison | join-requirements | retained | Bedrock pack and custom client launch uncertainty remains explicit. |
| ui/src/GameCompatibilityPanel.tsx:112 | Manual installed version Save and Clear | join-requirements | retained | Manual client version never masquerades as observed server metadata. |
| ui/src/OpenGameButton.tsx:20 | Minecraft and unsupported manual launch fallback | join-game-guide | retained | No launcher picker, executable endpoint or raw arguments. |
| ui/src/OpenGameButton.tsx:32 | Explicit fixed Steam client open and failure states | join-game-guide | retained | Game open requested is separate from game started or joined. |
| ui/src/JoinGuide.tsx:3 | Valheim, Java, Bedrock, Factorio and Terraria join steps | join-game-guide | retained | Separate address/port entry for games that require it. |
| ui/src/ServerChat.tsx:473 | Visible sync action and feedback | join-chat | retained | Loading, failure and current acceptance remain scoped to this room. |
| ui/src/ServerChat.tsx:488 | Read-only Friend signed owner notice | join-notice | reframed | No Friend notice edit or lifecycle side effect. |
| ui/src/ServerChat.tsx:494 | Verified cached notice and signed clear | join-notice | retained | Current, cached, cleared and unsupported notice states stay distinct. |
| ui/src/ServerChat.tsx:517 | Accepted room message history and unread reading position | join-chat | retained | Accepted entries and local pending entries have separate delivery labels. |
| ui/src/ServerChat.tsx:525 | Unsent queue edit/cancel versus submitted immutable entry | join-chat-queue | reframed | Same message ID retries and 20-slot/500-character bounds remain visible. |
| ui/src/ServerChat.tsx:537 | Queued message form and concurrent submission race | join-chat-queue | retained | Entry that left editable queue cannot be changed. |
| ui/src/ServerChat.tsx:549 | Explicit recovered unfinished composer review | join-chat-draft | reframed | Exact Host/room scoped protected draft needs review before Send. |
| ui/src/ServerChat.tsx:554 | Composer persistence failure and Retry | join-chat-draft | retained | Failed local save keeps text and prevents uncertain Send. |
| ui/src/ServerChat.tsx:644 | Pinned notice preview and unread room summary | join-play | retained | Primary Play links to full room and notice. |
| ui/src/ServerLogViewer.tsx:348 | Permitted sanitized exact active-run viewer | join-logs | reframed | Friend never gains retained pre-assignment history. |
| ui/src/ServerLogViewer.tsx:353 | Pause/resume, follow latest and manual Refresh | join-logs | retained | Diagnostic reads stay independent of server actions. |
| ui/src/ServerLogViewer.tsx:364 | Severity, category, contains and likely-problem filters | join-logs | retained | Bounded filters, empty matches and clear remain available. |
| ui/src/ServerLogViewer.tsx:375 | Source loading/missing/unsupported/ended/error states | join-logs | retained | Source outcome cannot update readiness, count or occupancy. |
| ui/src/ServerLogViewer.tsx:376 | Preserved reading position and new-line cue | join-logs | retained | A user can jump to latest without losing named controls. |
| ui/src/ServerLogViewer.tsx:310 | Run change and stale cursor reset together | join-logs | retained | Recovery removes cursor and records together; same-run error preserves last good view. |
| ui/src/FriendConnectionDoctor.tsx:224 | Selected saved Host evidence ladder | join-help | reframed | TCP, pin, device access, protocol, assignment, controls and game query remain independent. |
| ui/src/FriendConnectionDoctor.tsx:229 | Choose assigned server for fixed game check | join-help | retained | No arbitrary target or network-policy changes. |
| ui/src/FriendConnectionDoctor.tsx:248 | Redacted report and concise troubleshooting copy | join-help | retained | Copies omit codes, pins, credentials, endpoints, names and identifiers. |
| ui/src/FriendConnectionDoctor.tsx:251 | Supported fixed game endpoint probe | join-help | retained | Answered query never upgrades real join or saved change. |
| ui/src/SharedWorldControls.tsx:548 | Host post-Stop sharing summary and copy age | shared-host-grants | reframed | Completed-copy possession and later changes remain explicit. |
| ui/src/SharedWorldControls.tsx:550 | Old Host fence and competing authority warning | shared-history | merged | Preserved copy is not current hosting authority. |
| ui/src/SharedWorldControls.tsx:563 | Live-save availability controls | shared-live-save | reframed | Real-game acceptance stays disabled independently from fixture action. |
| ui/src/SharedWorldControls.tsx:570 | Enable sharing only with rolling backup and authority | shared-host-grants | reframed | Separate prerequisite and blocked states. |
| ui/src/SharedWorldControls.tsx:574 | Share current direct address | shared-route | merged | Current-host address variant keeps identity-preserving recovery separate from successor signed route proof. |
| ui/src/SharedWorldControls.tsx:578 | Owner final say governance choice | shared-host-grants | retained | Only original owner may change the override. |
| ui/src/SharedWorldControls.tsx:585 | Signed roster active-PC and revision summary | shared-host-grants | retained | Expired and revoked members do not receive active grants. |
| ui/src/SharedWorldControls.tsx:588 | Receive/Eligible host/Recovery voter/Manage sharing grants | shared-host-grants | reframed | Sharing never implies lifecycle or log permissions. |
| ui/src/SharedWorldControls.tsx:592 | Reset enrolled signing identity and grants | shared-signing | reframed | Explicit owner consequence review; copies cannot be recalled. |
| ui/src/SharedWorldControls.tsx:597 | Host planned-handoff checklist and preparation | shared-handoff | reframed | Exact graceful exit and post-Stop copy precede successor receipt. |
| ui/src/SharedWorldControls.tsx:615 | Chosen successor direct HTTPS endpoint review | shared-handoff | retained | Only signed reviewed IP/port; no arbitrary commands or paths. |
| ui/src/SharedWorldControls.tsx:624 | Complete or cancel persisted pending handoff | shared-handoff | retained | Exact receipt is required; no automatic replay after restart. |
| ui/src/SharedWorldControls.tsx:631 | Owner competing-copy proposal decision | shared-conflicts | reframed | Complete branch set and enabled original-owner final say required. |
| ui/src/SharedWorldControls.tsx:645 | Interrupted live-copy exact-hash quarantine | shared-live-copy | reframed | Preserve signed orphan without publishing or promoting it. |
| ui/src/SharedWorldControls.tsx:657 | Retry signed permissions or review changed source | shared-source-review | reframed | Owner-only repair; Friends explicitly trust new group. |
| ui/src/SharedWorldControls.tsx:963 | Receive copies without becoming a Host | join-shared | reframed | Fresh copy age and no automatic takeover remain prominent. |
| ui/src/SharedWorldControls.tsx:970 | Explicit local save consent and withdrawal | shared-consent | reframed | Host Receive grant and local storage consent are separate. |
| ui/src/SharedWorldControls.tsx:972 | Check latest and pull exact save | shared-transfer | reframed | Resumable chunks, verification and receipt are distinct phases. |
| ui/src/SharedWorldControls.tsx:977 | Copy completion/received age and Host receipt | shared-receipt | reframed | Historical acknowledgement does not prove current availability. |
| ui/src/SharedWorldControls.tsx:988 | Receiving/verifying/receipt progress and storage capacity | shared-transfer | retained | Total bytes alone does not finish verification or receipt. |
| ui/src/SharedWorldControls.tsx:1005 | Signed hosting-history review for voters | shared-history | reframed | History trust is separate from save receiving. |
| ui/src/SharedWorldControls.tsx:1008 | Confirm signed group and owner key | shared-group-trust | reframed | No silent replacement of trusted owner/source identity. |
| ui/src/SharedWorldControls.tsx:1015 | Join a new Host after signed takeover | shared-join-successor | reframed | Check decision plus pinned direct route and preserve old connection/history. |
| ui/src/SharedWorldControls.tsx:1028 | Chosen Friend stages planned handoff exact copy | shared-handoff | retained | Recipient variant checks exact offer and receipt before local setup. |
| ui/src/SharedWorldControls.tsx:1035 | Guarded recovery after sustained Host loss | shared-recovery | reframed | At least two minutes failed Host checks; offline game is not Host loss. |
| ui/src/SharedWorldControls.tsx:1044 | Prepare/copy/show signed candidate offer | shared-recovery | retained | Offer names exact copy and direct route; does not Start. |
| ui/src/SharedWorldControls.tsx:1052 | Current/observed majority versus historical/closed offer | shared-recovery | retained | Only current signed decisions supply present authority credit. |
| ui/src/SharedWorldControls.tsx:1059 | Review candidate offer and cast separate voter grant | shared-vote | reframed | Current roster, exact proposal and duplicate-voter boundaries. |
| ui/src/SharedWorldControls.tsx:1067 | Warned separate-history acknowledgement | shared-separate | reframed | No automatic authoritative selection or Start. |
| ui/src/SharedWorldControls.tsx:1088 | Friend competing-branch table and resolution proposals | shared-conflicts | reframed | All branches compared and higher version is not a winner. |
| ui/src/SharedWorldControls.tsx:1103 | Signed voting invitation copy/review/approve | shared-vote | retained | Full current branch set is rechecked before exact-copy vote. |
| ui/src/SharedWorldControls.tsx:1172 | Delegated Manage sharing from Join | shared-friend-grants | reframed | Other members only; no self edit, owner override or Manage-sharing grant mutation. |
| ui/src/SharedWorldControls.tsx:1183 | Expired and revoked sharing member handling | shared-friend-grants | retained | Only owner can restore current access. |
| ui/src/SharedWorldReadinessPanel.tsx:215 | Future Host checklist and next missing prerequisite | shared-successor | reframed | Authority, setup, restore and route lead to deliberate manual Start only. |
| ui/src/SharedWorldReadinessPanel.tsx:228 | Installed server file, game version and ports | shared-successor | retained | Game-specific variants preserve local matching and no terms acceptance. |
| ui/src/SharedWorldReadinessPanel.tsx:235 | Required reviewed add-ons and fresh local access | shared-successor | retained | Copy hashes do not imply add-on load or password transfer. |
| ui/src/SharedWorldReadinessPanel.tsx:243 | Readiness check and disposable file restore rehearsal | shared-successor | retained | Synthetic process/file outcomes remain separate from real game load. |
| ui/src/SharedWorldReadinessPanel.tsx:248 | Another Friend PC signed control-route test | shared-route | reframed | Second-PC pinned HTTPS proof does not prove game traffic. |
| ui/src/SharedWorldReadinessPanel.tsx:270 | Fresh managed world restore from current signed decision | shared-restore | reframed | Received source and existing worlds stay intact. |
| ui/src/SharedWorldReadinessPanel.tsx:285 | Finish local setup and ready for manual Host Start | shared-successor | retained | Game join/load/change/restart remain separate external acceptance. |
| ui/src/SharedWorldRouteDetails.tsx:24 | Combined route details paste and scoped review | shared-route | reframed | Signed endpoint authoritative; pasted address only a hint. |
| ui/src/SharedWorldServerFile.tsx:33 | Future Host game-specific local file browse/manual path | shared-successor | merged | Visual picker simulation only; no native dialog or file access. |
| ui/src/SharedWorldSeparateRoutePanel.tsx:170 | Separate-copy proof and candidate control-route check | shared-separate | retained | Second approved PC proof does not settle authority split. |
| ui/src/SharedWorldSeparateRoutePanel.tsx:186 | Separate fresh restore and matching local setup | shared-restore | merged | Separate-history variant does not impersonate current shared authority. |
| ui/src/SharedWorldSeparateRoutePanel.tsx:215 | Warned explicit separate-copy Start and exact Host Stop | shared-separate | retained | Manual Start requires warning; review preserves every history. |
| ui/src/WorldLoadRehearsal.tsx:113 | Choose backup/received source and prepare fresh game copy | shared-rehearsal | reframed | Reverify source and keep original source intact. |
| ui/src/WorldLoadRehearsal.tsx:125 | Isolated fixed trial versus manual game path | shared-rehearsal | retained | Distinct ports/save storage; no automated game-load proof. |
| ui/src/WorldLoadRehearsal.tsx:134 | Owner-reported exact-copy load/change/restart confirmations | shared-rehearsal | retained | Confirmation ordering and optional observed version remain separate from process checks. |
| ui/src/WorldLoadRehearsal.tsx:103 | Disposable stopped-cleanup confirmation | confirm-cleanup-rehearsal | retained | Coordinator-owned confirmation removes working copy only and preserves result/source. |
| ui/src/WorldLoadRehearsal.tsx:141 | Exact copy/setup and retained rehearsal result | shared-rehearsal | retained | App/game preparation identity and owner reports stay independently labeled. |
| ui/src/RemoteRehearsal.tsx:54 | Host prepares disposable staging fixture | shared-remote-rehearsal | reframed | Does not pair or approve a PC or access production worlds. |
| ui/src/RemoteRehearsal.tsx:87 | Friend selects fixture and owner-reported network | shared-remote-rehearsal | retained | Loopback is not turned into external acceptance by a selector. |
| ui/src/RemoteRehearsal.tsx:95 | Bounded fixed rehearsal and seven-stage redacted report | shared-remote-rehearsal | retained | Outside TCP and human game stages never pass from synthetic results. |
| ui/src/LiveSaveControls.tsx:79 | Real-game disabled live action and fixture-only enabled action | shared-live-save | reframed | Each game retains 0 of 6 real capture stages accepted. |
| ui/src/LiveSaveControls.tsx:83 | Bedrock fixed save resume durable hold | shared-live-save | retained | Refused dispatch or no fresh acknowledgement preserves marker and blocks new capture. |
| ui/src/LiveSaveControls.tsx:85 | Exact bounded live attempt withdrawal and retry | shared-live-save | retained | Only matching request private work changes; signed orphan and other complete copies remain. |
| ui/src/LiveSaveControls.tsx:90 | Six independent live-save acceptance stages | shared-live-save | retained | Owner load reports do not enable real-game capture. |
| ui/src/DataRecoveryPanel.tsx:50 | Forget saved session | confirm-forget-session | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/features/setup/useHostSetup.ts:540 | Remove server profile | confirm-remove-profile | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/features/setup/useHostSetup.ts:565 | Discard setup changes | confirm-discard-setup | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/features/setup/useHostSetup.ts:581 | Finish setup later | confirm-finish-later | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/features/setup/useHostSetup.ts:617 | Copy cached Valheim world | confirm-copy-cached-world | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/GameSettingsPanel.tsx:95 | Reload settings and player lists | confirm-reload-game-settings | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/GameSettingsPanel.tsx:191 | Discard unsaved settings | confirm-discard-game-settings | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/GameSettingsPanel.tsx:271 | Reload player list | confirm-reload-player-list | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/GameSettingsPanel.tsx:387 | Discard player list draft | confirm-discard-player-list | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:498 | Close or restart TogetherServer | confirm-close-app | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:666 | Stop Alderheim | confirm-stop | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:666 | Restart Alderheim | confirm-restart | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:862 | Replace Alderheim code | confirm-replace-code | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:1081 | Forget saved Host | confirm-forget-host | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:1128 | Change pages with setup edits | confirm-change-pages | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:1232 | Safe restart with checkpoint | confirm-safe-restart | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:1457 | Discard advanced settings | confirm-discard-advanced | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:1776 | Resume hosting | confirm-resume-hosting | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:2160 | Revoke Custom certification | confirm-revoke-custom | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:2188 | Archive exited session record | confirm-archive-session | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:137 | Reload editor file | confirm-reload-file | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:187 | Restore previous file version | confirm-restore-file-version | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:222 | Create game configuration | confirm-create-configuration | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:249 | Restore complete setup checkpoint | confirm-restore-setup | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:265 | Prepare to change server | confirm-maintenance | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:276 | Finish maintenance | confirm-finish-maintenance | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerFilesPanel.tsx:361 | Discard editor contents | confirm-discard-file | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/WorldLoadRehearsal.tsx:103 | Clean up disposable rehearsal | confirm-cleanup-rehearsal | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerAddOnsPanel.tsx:74 | Import Bedrock pack | confirm-addon-import-bedrock | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerAddOnsPanel.tsx:74 | Import Factorio mod ZIP | confirm-addon-import-factorio | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerAddOnsPanel.tsx:74 | Enable managed add-on | confirm-addon-enable | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerAddOnsPanel.tsx:74 | Disable managed add-on | confirm-addon-disable | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerAddOnsPanel.tsx:74 | Undo last add-on change | confirm-addon-undo | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/ServerAddOnsPanel.tsx:74 | Mark game version reviewed | confirm-addon-version-review | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:2317 | Discard unsaved PC changes | confirm-discard-access | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:2379 | Remove Friend PC A access | confirm-remove-device | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/TemporaryHelperAccess.tsx:19 | End temporary helper access | confirm-end-helper | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/AccessExpiry.tsx:239 | Clear owner access deadline | confirm-clear-deadline | reframed | Preserves the source consequences with explicit safe cancel and action review. |
| ui/src/main.tsx:2318 | Permissions for Friend PC A | access-permissions | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2347 | Servers for Friend PC A | access-servers | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2365 | Access time for Friend PC A | access-time | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2371 | PC details for Friend PC A | access-details | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2330 | Review permission preset | access-preset-review | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2358 | Per-server grants | access-per-server | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2361 | Review server access changes | access-assignment-review | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/AccessExpiry.tsx:225 | Choose owner access deadline | access-deadline | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/AccessExpiry.tsx:223 | Expired owner access | access-deadline-expired | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/TemporaryHelperAccess.tsx:19 | Temporary helper access | access-helper | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:2377 | Pending approval for Friend PC B | access-approval | reframed | Separates one task from the PC access editor; synthetic values only. |
| ui/src/main.tsx:1740 | Development/staging identity banner | host-overview | retained | Staging state keeps unmistakable isolated-instance identity across the shared shell. |
