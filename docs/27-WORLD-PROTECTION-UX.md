# World protection and sharing UX

The owner requested this redesign on 2026-10-17 after reviewing the production
Manage sharing dialog. The previous screen mixed save controls, repeated technical
warnings and every PC's permissions into one scrolling form. Hosting permissions
were also difficult to find inside the Backups page.

## User flow

The selected Host server's Backups page leads with completed-copy, automatic-backup
and drive-space summaries. Backup library, Sharing & hosting and Protection settings
are direct navigation choices. The backup library retains search, filters, exact
copy comparisons and guarded review actions. Secondary checks remain available
without dominating the main view.

Protection settings has a focused Save/Cancel editor for automatic backups,
retention, free-space reserve and supported crash recovery. It preserves unrelated
server/settings fields, refuses a changed policy baseline and keeps rejected edits
available. Offline, app-busy and data-recovery checks remain in force.

The editor uses fixed local `PUT /api/local/profiles/{id}/protection` with only
expected/next backup and recovery policy values. The Host compares and patches
current settings within the lifecycle gate, rather than trusting the UI's stale
full-settings snapshot. Strict bounded JSON and the existing loopback, owner,
Origin and mode checks protect this route. Disposable rehearsals are read-only;
recovery branches cannot opt into automatic crash restart.

Manage sharing organizes people, save sharing and history/recovery separately.
Each PC has a distinct permission card. Receiving saves and hosting on that PC
lead; forwarding, recovery voting and managing sharing have explicit advanced
controls. A saved grant awaiting signing enrollment is not presented as active
access. Local receiving consent remains independent of owner permission.

Live-save presentation separates app-observed checks from owner-reported game
checks. Readiness must follow the actual backend contract; synthetic evidence
cannot accept an installed game. Resume, withdrawal and interruption remain
reachable when attention is required. No backend permission or save guarantees
are weakened by the presentation.

## Delivery and verification

- Coordinator: Backups workspace, focused protection editor, integration,
  contract checks, serialized validation and commits.
- `gpt-6.1-sol`, max reasoning subagent: Host sharing dialog and PC cards.
- `gpt-6.1-sol`, max reasoning subagent: live-save and private-test presentation.
- `gpt-6.1-sol`, max reasoning Codex CLI worker: backup library presentation.
- `gpt-6.1-sol`, max reasoning subagent: read-only integration review.

| Check | Result |
| --- | --- |
| Complete component suite | PASS: 1,054 tests across 88 files, JSDOM and mocked APIs |
| Code-only gate | PASS: 42 source/synthetic groups, pinned tools, locked restore, TypeScript, lint and isolation checks |
| Solution compilation | PASS: zero warnings and errors |
| Production UI bundle | PASS: current assets generated |
| Native/rendered viewport and actual game/network journeys | Not run on the owner's active desktop |

The atomic protection group covers concurrent writers, changed baselines,
newer unrelated settings and added/sibling profiles, malformed/oversized input,
fixed owner route/mode boundaries, trials, recovery branches, previews, unknown
run identity, pending restores and data-recovery blocks. Component checks retain
permission, consent, exact-copy/restore acknowledgement, private-test ordering,
failed-edit, stale-response, focus and cancellation expectations.

The live-save decoder now accepts the backend's installed `LiveSaveReady` /
`LiveSaveBlocked` contract and terminal `Unchanged` result. Real readiness needs
all six passed stages with their exact app/receipt/owner-report sources and valid
observation timestamps. Fixture evidence remains distinct. Backend installation,
process, grant and payload checks remain authoritative on submission.

Intermediate validation caught two invalid test-only TypeScript query options,
a disclosure assertion that confused hidden with removed controls, and a
navigation assertion reading local preferences before their asynchronous write.
Those harness assumptions were corrected while retaining the original safety
assertions; the final full suite passed. The CLI source worker completed its
handoff but returned exit 1; coordinator compilation and checks are the passing
evidence for its files.

One existing source-symbolic-link subcase skipped because this Windows account
cannot create symbolic links. Vite retains its chunk-size advisory (main minified
JavaScript about 1,046 kB / 284 kB gzip). No runtime performance or rendered-layout
claim follows from these source/component results.

The owner's active desktop is protected. Workers do not build or run checks.
Coordinator validation uses TypeScript, lint, JSDOM components, production bundle,
static file inspection and the code-only gate. No application, browser automation,
native dialog, game, fixture, installer or listener is launched locally. Rendered
viewport/native interaction and real-game/Friend-PC/network acceptance require
an approved separate test PC or unattended session; component checks do not prove
those outcomes. Production data, credentials, logs, worlds and running processes
remain outside this work.
