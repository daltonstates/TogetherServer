# Settings and recovered-session corrections

Date: 2026-10-11. Scope: the owner's recovery notice and inconsistent Settings report. This is an unreleased fix on version 0.3.2.

The reported app was running the earlier `local-data/settings-flow-production-20261010/TogetherServer.exe`. The current source already contains the global App Settings and contextual Host/Join navigation from the UI redesign. A fresh production executable is required to deliver those changes together with this correction; the existing running app is left alone.

Recovered app session now has a labelled Dismiss button. Dismissal persists in this window's session storage through snapshot polling, display-text changes, item reordering, tab changes and UI remounts. A different interrupted session or changed recovery state can show a fresh notice. If browser storage is unavailable, dismissal still works in the current view. The notice is shown on Host, with an explicit review under Attention > Session recovery. It no longer appears above global App Settings. An empty recovery-item list renders no empty choices box, and routine Windows recovery information is disclosed under About app recovery.

Dismissing or reviewing the notice performs no backend mutation, acknowledges no data corruption and starts no game. Resume hosting remains an explicit confirmation through the existing fixed endpoint and normal Start checks. Unknown process identity stays blocked. The independent Data recovery panel and lifecycle restrictions are unchanged.

The main Settings tab, header gear and command entry open the same Appearance page in Host and Join. Both have Appearance, Notifications, Windows behavior, Updates and Keyboard shortcuts. Hosting limits, remote controls, Friend access, networking, idle timers and Keep Windows awake while hosting stay under Host > This Host PC. Selected-server configuration remains under that server's Settings & files page. Global App Settings no longer accepts server lists or hosting-power callbacks, and Host-PC navigation no longer has an application-settings section. Legacy native notification aliases retain their reviewed routing.

## Verification

- PASS focused JSDOM regression suite: 113 tests across five files. Includes dismissal/remount/polling, changed-state visibility, optional storage failure, explicit Attention review, busy Resume, clean exit, and a real Host > Settings > Join > Settings > Host > Settings shell sequence.
- PASS full JSDOM suite: **931 tests across 78 files**, 71.32 seconds. The JSON result is retained under ignored `local-data/settings-recovery-ui-tests-20261011.json`.
- PASS `scripts/verify-code-only.ps1` (exit 0): pinned tools, locked dependencies/restores, TypeScript, lint, .NET solution build with zero warnings/errors, all 20 source/synthetic groups, whitespace and desktop diagnostic isolation. Source-link subcases are explicitly skipped because this account cannot create symbolic links.
- PASS all 17 pure hosted-render fixture checks; no renderer or browser was started.
- PASS bundled production UI build, including final TypeScript. Vite retains its existing advisory for the JavaScript chunk above 500 kB.
- Remaining packaging check: fresh production publish with embedded-asset verification.
- Browser/viewport and native packaged-app checks for this correction are pending; no browser, app window, game, console fixture, installer or process-control runner is launched on the owner's active desktop.
- Real game, Friend-PC, network and save acceptance remain separate and unrun. No production settings, worlds, credentials, process or listener is changed. No public release or tag is created.

Shared builds, validation and commits are serialized. This bounded correction was implemented by the primary agent without workers.
