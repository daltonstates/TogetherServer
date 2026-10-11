# Shared Settings and dismissible alerts

Date: 2026-10-11. Scope: the owner's inconsistent Settings report and app-wide request for closable alert boxes. This is an unreleased correction on version 0.3.2.

The reported app was running `local-data/settings-flow-production-20261010/TogetherServer.exe`. The current source contains the global App Settings and contextual Host/Join navigation from the UI redesign. The fresh executable delivers that navigation together with the alert corrections; the running app is preserved.

## Result

The main Settings tab, header gear and command entry open the same Appearance page from Host and Join. Both have Appearance, Notifications, Windows behavior, Updates and Keyboard shortcuts. Hosting limits, remote controls, Friend access, networking, idle timers and Keep Windows awake while hosting live under Host > This Host PC. Selected-server configuration remains under Settings & files. Global App Settings accepts no server list or hosting-power callback, and Host-PC navigation has no application-settings section. Legacy native notification aliases retain reviewed routing.

Recovered app session has a labelled Dismiss button. Session storage retains dismissal through polling, display-text changes, item reordering, navigation and UI remounts. A different interrupted session or changed recovery state can show a fresh notice. Dismissal works in the current view if optional browser storage is unavailable. The notice appears on Host and remains available under Attention > Session recovery. An empty item list renders no blank choices box; routine Windows recovery information is under About app recovery.

The shared `DismissibleNotice` supplies an accessible X at 175 callout locations across 41 runtime components. The audit covers global update/result messages, connection and pairing warnings, access expiry, players/readiness, backup and file errors, game setup, add-ons, chat sync/draft errors, sharing/hosting warnings, reports, rehearsals and pane errors. New message text, saved-PC/server scope or result identity can show a fresh notice. Ongoing operation feedback and recorded TCP results can retain dismissal across display-only updates while a new terminal result or changed verdict remains distinct. Generic notices keep dismissal while mounted; recovered-session dismissal additionally survives remounts in the same window.

Local data review has Dismiss and Review local data controls. Closing its presentation does not acknowledge quarantine or re-enable lifecycle actions. Resume hosting still requires its existing confirmation and normal Start checks. Dismissing warnings performs no server, permission, signed-governance, credential, world or backend mutation. Save, consent, maintenance, Offline, exact-hash, occupancy and Unknown-identity restrictions remain independent of the message's visibility.

Error-boundary Retry and Reload controls remain available after their message closes. Editor dialogs focus their main controls before a leading alert's X; dismissing a focused notice returns focus to another available control. Close buttons stay inside their banner and retain reserved space through compact-density and Attention padding rules. Chat sync retains its inline layout. Existing Cancel/Keep/Continue paths close confirmations. Update review also protects Escape/Not now during update preparation or reminder saving, then permits closing when the request finishes.

The source inventory's remaining raw matches are retained backup facts, signed chat/pinned content, inner icon/text structure, recovery's dedicated close controls, and history/import/access confirmations with existing dismissal/cancel paths. These were individually reviewed. Ordinary counts, readiness facts, logs, transfer/vote progress and required field labels retain their existing presentation. Closing the staging information banner leaves the development app's branded/native identity intact.

## Verification

The original bounded correction passed 113 focused tests, the full 931-test suite, the code-only gate, 17 pure renderer checks and a production publish before the owner extended the audit. That intermediate executable is retained under ignored `local-data/settings-recovery-production-20261011-d416f62`; it is superseded by the final executable below.

The integrated alert pass initially passed 331/335 focused cases. Three old tests expected Retry inside the dismissible message; they now verify Retry remains available after dismissal. The recorded TCP result needed an explicit stable identity for display-only changes. The corrected two-file rerun passed all 89 cases. An initial full run passed 954/956: an obsolete assertion prohibited dismissing current Attention callouts, and a Connection Doctor focus assertion raced the passive effect. The updated assertions preserve the current problem state and wait for actual open/focus evidence. That failed JSON result remains at `local-data/alerts-final-ui-tests-20261011.json`.

The corrected Attention/Connection Doctor rerun passed all 102 cases. The final full UI suite passed **956/956 tests across 79 files**; its passing JSON is separate at `local-data/alerts-final-ui-tests-pass-20261011.json`. `scripts/verify-code-only.ps1` passed TypeScript, lint, locked restores, the .NET solution build with zero warnings/errors, all 20 source/synthetic groups, whitespace and desktop diagnostic isolation. Symlink subcases were skipped because this user cannot create symbolic links. The 17 pure renderer checks also passed. `npm run build` succeeded; Vite retained its existing advisory about a JavaScript chunk exceeding 500 kB. All checks preserved the owner's active desktop.

The inventory and CLI review are retained under ignored `local-data/alert-dismissal-inventory-20261011.json` and `local-data/alert-dismissal-review-20261011/`.

Two bounded `gpt-6.1-sol` workers owned disjoint Host and Friend component/test files. A `gpt-6.1-sol` Codex CLI worker performed a read-only full-UI review, and the Host worker reviewed shared CSS/shell integration. Reviews caught displaced banner controls, padding overlap, leading-X focus, changed-warning identity, busy update dismissal and a remaining suspended-recovery warning. The coordinator integrated corrections and serializes all builds, validation, packaging and commits.

## Executable handoff

The settings/recovery correction is committed as `d416f623a032670f7b5163a85ee015a27c2cfce1`. The complete alert audit is committed as `6951b60de9368b8b25a97a1d96e8fcade8042837`. The final production executable was published from that clean implementation commit after the passing checks and fresh UI build:

- Path: `local-data/alerts-settings-production-20261011-6951b60/TogetherServer.exe`.
- Size: 65,696,573 bytes.
- Numeric file version: `0.3.2.0`; product version: `0.3.2+6951b60de9368b8b25a97a1d96e8fcade8042837`.
- SHA-256: `16816006e5a43e60853960249f2289470b35c49843602870b8efc127131ceece`.
- Adjacent evidence: `TogetherServer.exe.sha256` and `ui-asset-evidence.json`. Static comparison confirms all four current UI assets are embedded byte-for-byte in the published assembly without loading the app.
- Authenticode status: `NotSigned`, consistent with the existing owner-distributed builds.

The production filename is preserved. The executable has not been launched or installed. PID 30860 still references the original `settings-flow-production-20261010/TogetherServer.exe`; no automatic replacement or restart was performed. The owner should exit that app before opening the fresh executable.

## Acceptance boundaries

JSDOM interaction and computed-style checks are source/synthetic evidence. Browser/viewport, native WebView/packaged-app, installer, real game, Friend-PC, network and save acceptance remain separate and unrun for this correction. No app window, browser, game, console fixture, installer or process-control runner is launched on the owner's active desktop. No production settings, worlds, credentials, process or listener is changed. No public release or tag is created. Backend source, contracts and dependency manifests/lockfiles are unchanged.
