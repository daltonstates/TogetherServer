# Core hosting and joining UX

The owner's 2026-10-09 direction replaces the previous Overview hierarchy: people should first see how to create or join a server, start or stop it, and get the details needed to play. Diagnostics, history, compatibility inventories and maintenance support those tasks.

## Delivered behavior

- The selected Host server has a compact name/status header and one primary Start or Stop above its tabs. The controls remain available in the other tabs. Active, fresh empty-server countdowns stay visible. Recovery, maintenance and unsaved-edit blockers remain explicit; lifecycle endpoints and owner player-impact confirmations are unchanged.
- Overview leads with masked connection details for a running server and the local join address. An Offline server gives one short next step. Version/add-on inventory, status/network checks, restart/checkpoint options, health and access tools remain findable through named disclosures or their owning tabs. Routine Offline/status explanations no longer repeat above Start.
- All server tabs use the same content inset and gap. Tabs have icons and scroll on narrow screens. The Sessions filters, match count and list now have internal padding instead of touching their containing border.
- Players leads with the current trusted count and empty-server timer. Stale/unavailable/display-only warnings stay visible. Count source/freshness, detailed timer evidence, driver help and recent count changes start collapsed. Add-time tools follow the count and are disclosed separately. Unknown never becomes zero and an invalid/stale deadline never becomes a valid countdown.
- Friend Play leads with Start when Offline, or Open game and masked connection details when available. Permitted Stop stays in the main action area, with occupied/unknown/stale/paused/maintenance guards. Detailed join instructions and server tools are secondary; requirements remain available after connection details. Each pending action gets its own label and spinner.
- Create and Join have equal first-use buttons. Saved Host and Join pages offer direct navigation to the other core task. First-server draft recovery, saved-connection access and guarded navigation remain intact.
- Chat Sync now immediately shows a disabled Syncing button and spinner, then a short success/error result. It has a separate single-flight request and cancels late results after scope changes or closure. Background polls cannot complete that manual request; an offline local copy cannot claim a successful Host sync.
- The executable now embeds a multi-size production ICO. Native window/tray code loads durable embedded production or distinct development ICOs rather than a temporary single-size drawing. Both include 16 through 256-pixel 32-bit frames and transparency masks. A byte-only check validates the assets without constructing a Form, Icon or window.

## Ownership

The coordinator owns integration, the common action/header/tab layout, Friend/first-use flows, shared icons/spacing, documentation, validation and commit. Bounded workers used the configured `gpt-6.1-sol` with max reasoning: native icon resources and Chat sync through subagents; Players through Codex CLI. File ownership was disjoint. Builds, integration checks and commits are serialized.

## Validation

The authorized source and synthetic validation is complete. Only source compilation, JSDOM, synthetic/fake-adapter checks and `scripts/verify-code-only.ps1` ran on the owner's active desktop.

- PASS focused interactions: 136 tests across AppFlows, FriendPlayFlow, PlayersPanel, ServerChat and HostFirstServer.
- PASS complete JSDOM regression suite: 670 tests in 65 files, 35.11 seconds.
- PASS UI TypeScript, lint and production bundle. Vite retains the existing warning for a JavaScript chunk over 500 kB; the bundle is 902.50 kB before gzip.
- PASS final `scripts/verify-code-only.ps1`: pinned tools, locked installs/restores, TypeScript/lint, solution compilation with zero warnings/errors, all 20 source/synthetic groups including embedded icon bytes, whitespace and diagnostic isolation. Native-import source-link subcases remain SKIPPED because this account cannot create symbolic links.
- PASS candidate publish with the new bundled UI and icons, without launching it. A byte-only PE inspection confirms all ten native executable icon frames match the reviewed production ICO exactly.

The unsigned working-tree candidate is `local-data/core-ux-candidate-20261009/TogetherServer DEVELOPMENT.exe`, version 0.3.1, SHA-256 `DB70D821DB9F6A3A22E7FB2F9538CF18F1242DC857AE8D9127688385C7C843C3`. The DEVELOPMENT filename selects the existing isolated staging path when deliberately opened. It was not opened, installed or copied over production, and it is not a published release. Logs are retained under ignored `local-data/ux-core-workers/`.

The initial full JSDOM run passed 653 cases and failed 12. Failures exposed old first-use naming and pre-join ordering assertions, repeated hidden status/recovery content, an incorrect hidden-disclosure assertion, and an overlong combined tab-navigation case. The duplicate content was removed, expectations now describe the owner's new hierarchy, and navigation coverage is split into bounded cases. The focused and full final UI runs pass.

The initial icon failure was a mistaken assertion that the 16-pixel development frame must contain an exactly zero-alpha pixel. Its antialiased corner samples have alpha 3 through 35 while its orange tile and dark D are opaque and visible. The corrected check requires low-alpha corners, an opaque correctly colored center, visible palette/glyph areas, valid frame directories and matching legacy masks; it does not weaken blank-icon detection. The icon assets are unchanged.

Read-only integration review identified and corrected an always-visible Start bypassing file-editor draft guards, countdowns being hidden with supporting tools, and Friend Stop losing its action-specific pending feedback. Focused regressions cover those behavioral boundaries.

## Acceptance boundaries

No TogetherServer app, browser, native window/dialog, fixture/process/console runner, installer, listener or game is launched by this work on the active desktop. Hidden/tray/headless labels and SkipDesktop do not establish focus safety. No AllowInteractiveTests switch is used. Production data, credentials, managed worlds, public routing and the running production app are untouched.

Supported viewport/layout review, enlarged text and keyboard interaction in the actual WebView, Windows taskbar/shortcut/DPI/tray behavior and installed package review require an explicitly approved separate test PC or dedicated unattended Windows session. These remain unrun, not passed. Source/icon bytes and JSDOM order/visibility do not certify actual pixel layout, taskbar cache behavior, network reachability, real game joins, saves or restart recovery.

## Follow-up: stale request results

The owner reproduced a Ready Valheim server retaining the original Start response, “process launched; waiting for its server-connected log signal.” `actionNotices` held an undated receipt indefinitely while the canonical snapshot independently advanced to Ready. The same receipt also fed Notifications/Attention. Other Host Start/Restart/Resume, player refresh, health and add-time responses used that shared path; Safe restart had its own equivalent writer. Friend cards independently kept completed remote operation records visible indefinitely.

Successful feedback is now a short receipt, not a status authority. It expires after eight seconds and cannot revive when tabs change. Server receipts also bind to the mode/connection, profile/run, phase, player evidence and countdown; a newer snapshot supersedes them immediately. A Start/Restart receipt is suppressed if the response already reports Ready. Friend cards retain pending/running work and failures, but hide completed startup messages once the server reaches Ready or leaves startup. A former successful Stop cannot claim the server is stopped after another Start. Chat manual-sync success also expires; pending sync and errors remain visible.

Failures are not timed out or discarded because a subsequent snapshot changes. Historical dated activity, backup/checkpoint evidence, version comparisons and draft-save errors remain intact. Existing outside-TCP endpoint/port/five-minute freshness and Friend game-check identity/run/45-second freshness guards were inspected and retained. No server authority, API, polling frequency, pairing, world or lifecycle behavior changed.

Focused regression validation passed 113 cases across action-feedback projection, real shell flows and chat. The shell case drives an initial Start result through the normal snapshot polling path to Ready and verifies that both the header and Notifications retire it. Final validation passed all 687 UI tests in 66 files, the complete code-only gate (including all 20 safe groups, TypeScript/lint and zero-warning/error .NET compilation), and the production UI bundle. The existing Vite chunk-size warning remains. Native-import symbolic-link subcases remain skipped as previously recorded. No automated app, game, browser, fixture/process runner or listener was launched.

## Follow-up: visible server startup (2026-10-21)

The selected Host server now shows an indeterminate progress bar beside its
controls from the initial Start request through the game's readiness checks.
Three short steps show Launch server, Check game, and Ready. The current
canonical driver detail appears below the heading; supported Host games have
an explicit View startup logs action. The panel remains visible across tabs.
Friend Play uses the same panel with current authenticated assigned-server
status and short fixed explanations, without exposing private Host details.

The launch phase covers the pending request. Starting and Process running
advance to Check game. An initial Unknown / ObservationPending may also show
that check only when an exact managed run ID is present. Other Unknown states,
stale observations, failures, Stop, and Listening are not relabeled as startup.
Ready removes the panel immediately; an old pending request cannot obscure
Ready or a failure. The bar never invents a percentage or a world-loading
milestone that the game has not reported. Reduced-motion settings disable
animation, and detail updates do not move keyboard focus.

The local owner retains the existing confirmed graceful Stop during startup,
including the first pending game observation for an exact run. Friend Stop
continues to require Ready and the existing fresh exact-zero safety checks.
Stale connections, removed assignments and denied access hide Friend progress.
There is no new API, poller, timer, log parser, lifecycle authority or persisted
state. No game installation, world, credential or running production app changes.

Validation passed:

- All 1,354 UI tests in 106 files, including startup transitions, a pending
  initial observation, failed requests, owner Stop confirmation, stale Friend
  access, Ready cleanup, explicit log navigation and stable keyboard focus.
- `scripts/verify-code-only.ps1`: pinned tools, locked installs/restores,
  TypeScript, lint, solution compilation with zero warnings/errors, the safe
  source/synthetic groups, whitespace and desktop diagnostic isolation.
- `npm run build` and a final Release app compilation embedding the updated
  UI, with zero .NET warnings/errors. Vite retains its existing large-chunk
  warning.

Synthetic source-link subcases remain skipped because this account cannot
create symbolic links. Browser/WebView viewport and native desktop checks,
separate-PC/network checks and real-game startup/join/save/restart acceptance
remain unrun. Validation preserved the owner's active desktop; no app, browser,
listener or game was launched, and the running production app was left alone.

### Production build handoff (2026-10-21)

The production-named portable app is
`local-data/startup-progress-production-20261021-5419ead/TogetherServer.exe`,
built from clean source `5419ead66ca5075fb1c1be83fd9529c0786a594a` with the pinned
tools, a fresh production UI bundle, locked restore and self-contained win-x64
single-file Release publish. Version is `0.3.3.0`; size is 66,122,862 bytes;
Authenticode is `NotSigned`. SHA-256:
`5c6ae61aac3ac1fa566622370b8f05653f89bf2166b591aaf1959d709663b393`.

PASS static GUI executable, exact bundled/current assembly match, all seven
current UI assets embedded byte-for-byte, bundled .NET runtime core, version
and startup-progress content. The folder retains package evidence, build
metadata, checksum and logs. The first wrapper stopped on Vite's existing
size warning before publish; the final UI build and publish passed using
native exit-code checks. No app was launched, installed or replaced. Native,
real-game and Friend-network acceptance remain unrun; this is a local build,
with no tag or public release.

### Live startup console (2026-10-21)

The startup panel now includes the latest 60 console lines for Valheim,
Minecraft Java and Minecraft Bedrock, using the existing sanitized log API
and single-flight viewer. Pause/Resume, Follow latest and Refresh remain
available, and Open full logs navigates to the existing Logs tab. The preview
stops reading while that full viewer is open. A keyboard-scrollable, wrapping
console keeps long lines and bounded history within the startup panel. Output
does not announce every line or move focus. The full viewer retains filters;
startup always begins unfiltered and never overwrites those preferences.

Preview reads begin only after the canonical snapshot identifies the managed
run. The viewer normalizes the snapshot GUID to the log API's exact run ID
and refuses another run or an archived source. A new run remounts the reader,
aborts the old request and ignores late replies. Ready removes the startup
panel and its reader; old retained logs cannot appear during the launch request.
An isolated pane failure leaves progress and lifecycle controls usable.

Friend preview and full-log navigation require current authenticated access,
assignment, the exact View logs grant, an existing supported driver and the
Host capability. Removed, revoked, stale or unsupported access unmounts the
preview. Remote controls being paused does not remove an independent log grant.
No API, log permission, console attachment, command, readiness/count parser,
Stop policy or real game file changes. Logs remain display information only.

PASS all 1,361 UI tests in 106 files, including real viewer/proxy requests,
exact-run/archived-source rejection, late reply cancellation, unfiltered
bounded text rendering, Friend permission/capability/access changes and
navigation without duplicate readers. The first shell case used a legacy
fixture without a run ID; adding the exact run ID corrected that fixture,
and the focused retry and complete final suite passed.

PASS `scripts/verify-code-only.ps1`, including TypeScript, lint, locked
dependencies, solution compilation with zero warnings/errors, source/synthetic
checks, whitespace and diagnostic isolation. PASS the production UI bundle;
Vite retains its existing large-chunk warning. Synthetic source-link subcases
remain skipped because the account cannot create symbolic links.

Native/WebView viewport, real-game and separate-PC checks remain unrun.
Validation preserved the active desktop; no app, browser, listener or game
was launched and the running production app remains untouched.
