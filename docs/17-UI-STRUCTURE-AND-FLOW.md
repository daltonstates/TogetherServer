# UI structure and flow audit

Date: 2026-10-09. Baseline: `295a37e`. Scope: a bounded front-end refactor of routine hosting, Friend play, backup browsing and recovery. The existing React/TypeScript UI, controls, themes, APIs and lifecycle rules remain the foundation. No new dependency, backend contract, deployment or installed-app update is part of this delivery.

## People, tasks and evidence

The local owner configures and supervises servers and grants access. Friends connect from their own Windows PCs and see only assigned servers and permitted actions. An owner can also play locally or join another Host; choosing Host or Join changes the visible workspace without stopping either capability. These roles and boundaries come from docs 00–04, current snapshots, handlers and tests.

Five representative tasks guide validation:

1. Select a saved Host server, review blockers, Start, observe progress, then Invite friends.
2. Paste a server code or return to a saved Host, review game requirements, and join using the existing game/address actions.
3. Prepare maintenance, edit settings/files, preserve an unfinished edit and cancel or resume setup.
4. Find a completed backup, inspect evidence, verify/copy/rehearse it and review an Offline restore.
5. Manage access or recover from an expired connection, failed pane, unavailable service or uncertain server state.

Assumptions: returning users start/join more often than they rename a connection, compare every server, edit raw files or move hosting to another PC. There is no task-frequency telemetry or new user study. Backups, permissions, shared worlds, certification and diagnostics remain important capabilities even where their presentation is deferred.

The app was not launched on the owner's desktop. `AGENTS.md` requires a separately approved PC or unattended Windows session for app/native/browser/process acceptance. No connected approved visual session was available. The audit therefore distinguishes source-confirmed structure, rendered JSDOM/stylesheet results, and unobserved visual concerns. JSDOM provides interaction and computed visibility evidence, not pixels, layout measurements, real screen-reader acceptance or usability proof. No before/after screenshot is claimed.

## Screen inventory and regional dispositions

There is one bundled entry route, `/`, with workspace and selected-server state in [main.tsx](../ui/src/main.tsx). Existing commands, Attention destinations, native notification destinations and saved tab identifiers supply direct navigation; no URL route was removed.

| Screen / significant region | Purpose and disposition | Where capabilities remain |
| --- | --- | --- |
| Shell: Host, Join, Attention, Settings; commands; status strip | KEEP concurrent-workspace navigation and background status. MERGE command terminology with server-section labels. | [WorkspaceChrome.tsx](../ui/src/WorkspaceChrome.tsx); Alt+1–4 and Ctrl+K remain. Running/new-activity badges now describe their buttons accessibly. |
| Host: empty state and four-step setup | KEEP Game → World → Server app → Review, source-copy review, blockers and Finish later. MERGE first-server cancellation with the existing footer pattern. | [HostSetupDialog.tsx](../ui/src/features/setup/HostSetupDialog.tsx), [useHostSetup.ts](../ui/src/features/setup/useHostSetup.ts). Initial setup now has visible Cancel setup; existing servers retain Cancel. |
| Host: all-server overview and saved-server list | KEEP selection/search/pins/order and warning cues. DEFER full comparison rather than showing the same servers twice before the selected workspace. REMOVE duplicate Add server presentation. | Saved servers remains the normal selector. With multiple servers, Compare all N servers opens the unchanged comparison. Add server remains in the page header. |
| Host: Overview | KEEP status, freshness, preflight, safety blockers, Start/Stop/Invite, confirmation, result and masked connection fields. MERGE duplicate Start and state headings. | One lifecycle toolbar follows status/blockers. [HostLifecycleSummary.tsx](../ui/src/HostLifecycleSummary.tsx) retains qualified evidence and useful recovery/player/maintenance destinations. |
| Host: timer and protection tools | MOVE supporting workflows out of unrelated server views. KEEP failures visible before decisions. | Add shutdown time opens Players, with presets and custom minutes. Backups owns World Safety Center, shared saves, grants, move-kit preparation and game restore drills. Latest backup failure still warns on Overview. |
| Host: Chat, Players, Logs, Sessions | KEEP independently useful content and visible failures/retry. REMOVE arbitrary descendant CSS hiding. | Existing sections/identifiers stay; hidden wrappers own supporting content explicitly. Player counts and archived summaries retain their separate evidence meanings. |
| Host: Settings & files | KEEP common game settings, player lists, raw-file editor, add-ons and setup checkpoints. DEFER a deeper internal reorganization. | Existing `files` destination now has the clearer label Settings & files. Maintenance entry and preflight remain reachable. Both existing editing surfaces and their draft guards remain. |
| Host: Maintenance & setup | MOVE Custom control certification here and retain health/archive/setup/update guidance. | Existing `setup` destination; Overview links directly to Custom certification. More server actions exposes Edit setup, Connection help, Check server health and eligible record archival. |
| Join: saved Hosts and pairing | KEEP search/pins/selection, protected saved access, code/legacy form, Connect, failure help and Cancel. | [main.tsx](../ui/src/main.tsx). Add another Host stays visible; Cancel returns to the saved connection. |
| Join: assigned server Play | KEEP requirements before joining, Start, Open game/manual fallback, copy fields, permitted secondary controls, logs/chat/shared worlds and feedback. REMOVE exact duplicate permission/Start/address explanations. | [FriendPlayFlow.tsx](../ui/src/FriendPlayFlow.tsx); Your access remains the canonical permission disclosure. |
| Join: connection maintenance and Doctor | MOVE below assigned Play cards; KEEP access-ended/approval/revocation/connection notices visible above them. | Connection help & saved Host contains Connection identity and recovery, rename/address/forget, Connection Doctor and staging rehearsal. Open Connection Doctor expands and focuses the moved section. |
| Requirements: Host and Friend | MERGE repeated version prose into required/current version facts, labelled sources and one comparison line. KEEP Unknown/mismatch/freshness/error guidance. | [GameCompatibilityPanel.tsx](../ui/src/GameCompatibilityPanel.tsx). Add-on inventory and manual/owner version entry remain direct sibling disclosures. |
| Backups: browsing and actions | KEEP identity/date/kind/size/pin/compare plus visible failed/unknown integrity cues. DEFER dated evidence and tools; MOVE extra filters out of the default search area. | [BackupCatalog.tsx](../ui/src/BackupCatalog.tsx). Review backup exposes every prior tool; Name and retention remains separate. Filter backups exposes pin/kind/date; active count and Clear filters remain visible. |
| Backups: protection/retention explanation | MERGE routine summary; DEFER quota and supporting prose. KEEP incomplete evidence explicit. | Protection and retention details and the existing Retention preview. Review panels stay mounted, preserving drafts, pending actions and Restore acknowledgement. |
| Attention and Host/Friend Settings | KEEP current problems, dated activity, role/permission controls, expiry, updates and recovery. MOVE PC migration into its appropriate settings task. | Existing App, Friend access, Stop & timer, Connection help, Diagnostics and Advanced sections. Move hosting to another PC is under Settings → Advanced, with a direct first-use link. |

## Prioritized audit

Severity: High affects recovery or directs users to the wrong task; Medium makes routine decisions/navigation harder; Low is a secondary consistency issue. These are product and implementation assessments, not results of usability testing.

| Screen / task | Observed issue | Evidence | Severity | Proposed change, delivered |
| --- | --- | --- | --- | --- |
| `/` → Host → Logs/Sessions/Files: recover from failure | CSS direct-child allowlists excluded `.pane-error`, hiding its alert and retry. Files also lost its rendered preflight; results could disappear. | [companion.css](../ui/src/companion.css), [PaneErrorBoundary.tsx](../ui/src/PaneErrorBoundary.tsx). Same synthetic DOM against baseline/current CSS: `none` → `grid` for all three errors. | High | Remove allowlists; let React/hidden wrappers own each pane. Injected failures verify visible retry and no server action. |
| `/` → Host → Overview: resolve recovery | Maintenance plus blocked recovery redirected Review recovery to Setup, despite the summary choosing Diagnostics. Suspended/Failed details were also hidden by presentation rules. | [main.tsx](../ui/src/main.tsx), [HostLifecycleSummary.tsx](../ui/src/HostLifecycleSummary.tsx). Source-confirmed; combined-state interaction covered. | High | Route the summary's explicit destination unchanged; show relevant recovery evidence/actions. |
| `/` → Host → Overview: start/invite | Requirements/chat/network regions preceded controls; Offline rendered two Start buttons. All-server rows duplicated the selector above the workspace. | [main.tsx](../ui/src/main.tsx), [AllServerOverview.tsx](../ui/src/AllServerOverview.tsx). Source order/count and rendered ordering assertions; scrolling impact unmeasured. | Medium | Put one control group after decision-critical status/preflight, then feedback/connection and supporting checks. Defer full multi-server comparison. |
| `/` → Join: play | Connection maintenance and detailed Doctor content preceded assigned server Play; permission/Start/address copy repeated. | [main.tsx](../ui/src/main.tsx), [FriendPlayFlow.tsx](../ui/src/FriendPlayFlow.tsx). Source-confirmed; rendered ordering/reachability tests. | Medium | Play first after compact connection status; move maintenance/Doctor to a labelled footer and retain direct focused navigation. |
| `/` → Host → Backups: choose a copy | Five initial copies could expose twenty dated facts, twenty-five catalog tool buttons and five Host move-kit buttons before a user chose a backup. Five filter controls preceded the rows. | [BackupCatalog.tsx](../ui/src/BackupCatalog.tsx). Source counts under all-tool callbacks; visual crowding unobserved. | Medium | Compact rows retain decision cues. Review backup opens tools/results; optional filters keep active state visible. |
| `/` → Host → Setup/Chat/Players: certify Custom control | Certification was hidden on Overview/Setup but leaked into unrelated Chat/Players. | Baseline [main.tsx](../ui/src/main.tsx)/[companion.css](../ui/src/companion.css). Source selector/condition mismatch. | Medium | Explicit Maintenance & setup ownership and direct Overview entry; existing certification guards unchanged. |
| `/` → commands/Attention/native destinations: return to a section | Explicit server commands/notifications could select a section without recording the same tab preference; Chat/Players commands were absent. Multiword search required a contiguous phrase. | [main.tsx](../ui/src/main.tsx), [WorkspaceChrome.tsx](../ui/src/WorkspaceChrome.tsx). Navigation/search regression tests. | Medium | Keep identifiers and guarded scope changes; persist server/tab consistently; expose all eight sections; search all entered words. |
| `/` → first-server setup: cancel | Initial setup had Escape and Finish later but no visible Cancel. | [HostSetupDialog.tsx](../ui/src/features/setup/HostSetupDialog.tsx). Source and callback/hook interactions. | Medium | Visible Cancel setup calls the existing guarded cancellation; Finish later still waits for protected draft persistence. |
| `/` → Host/Attention navigation: hear status | Explicit button names overrode descendant badge text. | [WorkspaceChrome.tsx](../ui/src/WorkspaceChrome.tsx). Accessible-description assertions. | Low | Associate running-count/new-activity descriptions without changing stable button names. |

## Before and after core flows

**Start and invite.** Before: choose Host → all-server overview → saved-server selector → requirements/chat/readiness → lifecycle controls plus another Start summary → invite result. After: choose server → honest state/blockers and offline preflight → one Start → Starting/operation feedback → Ready connection fields and Invite in the same action area. Stop/Restart retain player-impact confirmation; Safe restart stays beside Restart. Start never infers Offline or readiness.

**Connect and play.** Before: saved Host → connection management/Doctor → assigned card with repeated permission copy → long requirements → game/copy. After: saved Host → compact connection/access notice → assigned Play card → compact required/current version and mismatch/Unknown guidance → existing game/manual/copy path. Connection repair remains one explicit Open Connection Doctor action away, opening and focusing the moved section. Paused controls still permit joining with current access; owner expiry denies it.

**Review a backup.** Before: quota prose and all filters → repeated result facts and tools on every row → reviewed Restore. After: summary/search → compact row with known failures/Unknown → Review backup → dated facts and tools → the same Restore review and acknowledgement. Closing a row never destroys a name draft, pending tool or restore acknowledgement. This introduces an intentional extra interaction before using a particular copy; it does not add an interaction to ordinary scanning/pinning/comparison.

Full multi-server comparison and optional backup filters now take one explicit interaction. This favors the assumed common selected-server/browse tasks; users who routinely compare everything may prefer the old default. No page-height, click-time or user-success improvement was measured.

## Reusable UI rules

1. State each screen's task before adding regions. Keep status, permissions, warnings and prerequisites before the action they support; place its result beside it.
2. Use one primary lifecycle action group. Avoid repeating the same Start/permission/state explanation in both a card and a summary. Keep common actions labelled and directly reachable.
3. A moved capability needs a named destination and, when relevant, a direct contextual entry. Keep stored identifiers and protected navigation guards stable.
4. Reveal optional evidence or advanced editing through one clearly labelled entry. Keep known failures, Unknown and active filter state visible outside it. Preserve mounted drafts when merely closing a supporting panel.
5. Let a pane own its visibility. Never use broad descendant CSS allowlists that can hide an error boundary, retry or feedback. A render failure must leave other tasks usable.
6. Compact repeated data through facts/lists, not smaller text or controls. Wrap section navigation/long content, use existing scaling/theme tokens and preserve at least 44 px narrow touch targets.
7. Keep local listener, outside TCP, authenticated access, readiness, occupancy, backup hashes and real game/save acceptance distinct. Cleaner presentation grants no authority.
8. Verify synthetic loading/empty/populated/error/long data, navigation and drafts before extending the pattern. Record visual/keyboard/platform/user acceptance separately.

## Delivery and validation

Pass 1 changed Host action order, duplicate presentation and pane ownership, then passed 26 focused shell/summary tests before expansion. Pass 2 changed Friend play/requirements, backup browsing, visible initial cancellation and guarded navigation. Its first complete focused run passed 105 tests across nine files. Independent gpt-6.1-sol subagent reviews found no concrete integration regression; a Codex CLI gpt-6.1-sol audit supplied the reproducible pane-visibility and routing findings. Builds, tests and integration were serialized; workers had disjoint file ownership.

Final serialized results:

| Check | Actual result |
| --- | --- |
| `scripts/verify-code-only.ps1` | PASS, exit 0: pinned .NET 10.0.301 / Node 24.14.1, locked dependencies, TypeScript, zero-warning lint, locked solution restore/build, 19 source/synthetic/fake-adapter groups, whitespace and desktop diagnostic isolation. Solution compilation: zero warnings/errors. |
| Complete UI `npm test -- --reporter=dot` | PASS, exit 0: **630/630 tests across 64 files**, including 23 App shell cases and the updated interaction suites. |
| Baseline/current pane CSS against identical JSDOM fixture | PASS, exit 0: Logs, Sessions and Files error panels were `display: none` before and `display: grid` afterward. This is computed visibility evidence, not a screenshot. |
| `npm run build` | PASS, exit 0: TypeScript and Vite production bundle. Existing >500 kB chunk advisory remains; current main JS is 896.74 kB minified / 243.95 kB gzip. Native performance was not measured. |
| `dotnet build src/TogetherServer/TogetherServer.csproj -c Release --no-restore` | PASS, exit 0: app compilation after the fresh bundle, zero warnings/errors. App was not launched. |
| Synthetic symbolic-link subcases | SKIP: this Windows test account cannot create those links. Other registered groups passed; the skipped cases are not acceptance evidence. |

Earlier harness fixture/expectation errors were corrected. The first full UI run had 629/630 passing because a new shell check proceeded before initial selection effects settled. The harness now settles those effects and waits explicitly for section navigation; the final complete run passed all 630. An initial shell wrapper also reported failure on line-ending warnings despite completed subchecks; changed files were normalized to the repository's LF convention and the code-only gate was rerun successfully with its actual exit status.

Detailed synthetic logs are ignored under `local-data/ui-refactor/`; they contain no production evidence. The retained regression suites are [AppFlows.test.tsx](../ui/src/AppFlows.test.tsx), the changed component suites and setup hook tests. Shell tests use the real App, navigation, handlers, guards and styles with synthetic transport and isolated leaf stubs; leaf interaction/contract behavior is covered by its existing dedicated tests. Checks cover scoped draft failure/retry, name drafts through disclosure, exact Restore acknowledgement/Offline gates, initial Cancel/Finish later, Start progress/invite/copy/confirmation, paused versus expired access, command persistence and visible failed-pane retry. Existing suites retain long/bounded input, partial data, permission/assignment and stale-evidence coverage.

Remaining limits: no app/browser/native launch or screenshots, no desktop/tablet/narrow pixel review, no real screen reader, measured contrast/zoom/touch validation, native focus/tray/installer checks, Friend-PC/WAN route, real game join or valued-world save/load/restart. Existing responsive/light/dark/high-contrast/text-scaling rules are retained and new facts collapse to one column; those rules still need an approved rendered viewport review at 1440×900, 768×1024 and 390×844 with long labels and 150% text. Do not count these skips as passes or claim user-tested usability. Settings/files internal page length remains a future candidate, with its workflow protections preserved.

## Screenshot-driven first Host correction

The owner supplied the initial Host view after trying the development package. The previous delivery had no visual review of that view; its successful code checks did not establish layout quality. The supplied screenshot and a read-only capture of the already-open development window show three concrete problems: the rehearsal box precedes the task, the welcome panel's 850 px width centers it against full-width title/recovery regions, and recovery describes a file editor rather than saved server setup.

This correction replaces the three-region stack with [HostFirstServer.tsx](../ui/src/HostFirstServer.tsx): one aligned setup area, compact Host/Join actions, contextual saved-setup recovery, guarded continuation, and the existing migration link. A saved setup keeps Review/Discard actions and its message; it is never applied automatically. File editors retain their original recovery wording. The setup dialog uses the same setup-specific language. Rehearsal remains available under Settings → Connection help, as documented in README.

Verification before the refreshed preview: **637/637 UI tests across 65 files**, lint, TypeScript and production bundle passed. Added cases cover explicit creation/Join/migration, pending controls, paused continuation without duplicate creation, recovery message/review/discard and exact saved-step restoration, unchanged file-editor language, and rehearsal relocation with no implicit preparation. Initial harness failures (missing JSDOM dialog methods, an incorrect step selector and a fixture string type) were corrected; final runs passed. Browser connection could not initialize (`failed to write kernel assets`), so no browser viewport pass is claimed.

The existing-window before capture was 1180×820 and reported an unchanged foreground handle. It launched no app and issued no input. It is ignored under `local-data/ui-refactor/visual/`, outside Git. An after-render review of the new package is pending owner-controlled reopening or explicit approval for that development-only relaunch. Production and game processes remain untouched. This correction is not visually accepted until that rendered result has been inspected.
