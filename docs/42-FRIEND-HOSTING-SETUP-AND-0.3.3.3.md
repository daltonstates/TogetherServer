# Friend hosting setup and 0.3.3.3 prerelease

Owner direction, 2026-10-22: correct the shared-hosting permission shown on a
Friend PC, retain the check list during retries, guide every setup problem,
show the checks without a disclosure, keep the Play action stable after closing
the dialog, and align the removed-access checkbox. Publish `v0.3.3.3` as a
GitHub prerelease after implementation and validation.

Published [v0.3.3.3](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.3)
on 2026-10-22. All required functional checks pass. Windows notification
acceptance remains blocked by the runner's QuietTime; physical Friend-PC,
WAN and real-game save/load acceptance remain separate. Stable/latest stays
`v0.3.3`. The publication record below identifies the unchanged release bytes.

## Delivered interface

- Host locally keeps all five checks visible from its first read. Each check
  has its own icon, a status icon and text, and status color. The description
  and results have more padding.
- A retry retains the last results and check time. The displayed age updates
  each second; an interrupted check labels the old results and blocks Start
  until a valid recheck. A late response cannot revive an expired operation.
- The dialog reads its full status while open; the lightweight permission poll
  runs while it is closed. An unavailable sharing lookup keeps the action
  available for recovery. Verified permission withdrawal still cancels pending
  work and blocks hosting.
- A five-step guide selects the first unresolved check. Every check opens its
  relevant guide step and brings its heading into view. Automatic checks do
  not move keyboard focus. The guide covers owner approval and sharing, installed
  server files and terms, missing or conflicting saves, verification failures,
  game ports and incoming connections. Actions use fixed existing callbacks.
  Installation, terms, network changes and Start remain deliberate user actions.
- Show removed access keeps its checkbox and wrapping text on one clickable
  row. Its component rule now takes precedence over the shared sidebar label
  grid.

## Sharing repair

The backend investigation identified two related problems: unavailable or
unverified cached membership was presented as an owner permission denial, and
legacy standalone sharing snapshots could diverge from the retained signed
history.

The original owner reconciles a newer signed standalone snapshot with a journal
that a peer started from an older cached snapshot. A strictly newer schema-2
snapshot may advance only a legacy journal head, with the same signed owner,
profile and group and both counters strictly increasing. Exact previously
trusted snapshots remain retained. Existing journal changes use exact-parent
schema-3 revisions; standalone replacements cannot advance a schema-3 head.
Legacy standalone publication remains supported until a journal exists, so an
explicitly reviewed owner source rotation retains its prior behavior.

The companion roster read can resume after a bounded exact-hash header rather
than treating one PC's journal length as the other PC's offset. Legacy peers
retain the offset fallback. Peer metadata uses protected revision-to-position
lookup so retained legacy revision gaps cannot skip a later entry. Bounded
catch-up verifies complete pages before appending; wrong roots, invalid
signatures, equal-counter forks and schema-3 parent gaps still fail closed.

A full hosting-status retry refreshes pinned signed permission when connected.
When disconnected, a verified signed grant remains available without network
access; a missing, damaged or denied cache can retry the saved pinned Host.
Lightweight access polling retains its signed offline-grant behavior. Known
revocation, owner expiry and approval remain explicit blockers. Missing or
unverified sharing records are Unknown or need review, rather than claiming
that the owner turned sharing off.

A genuine rollback, damaged signature, revocation or conflicting history must
remain blocked; no floor is lowered to make a check pass.

## Validation checkpoint

The first focused component run passed 130 tests in FriendLocalHosting,
LocalHostingGuide, ServerDirectory and HostingMove. The full UI suite at this
local checkpoint passes **1,410 tests in 108 files**. The first full UI run found
two demo expectations that required the selected file field to disappear; the
updated assertions require the chosen file to remain visible instead. A
guide-copy correction passed its focused rerun. A composed demo click now
waits for the actual enabled action after changing server selection.
The final explicit-guide focus polish passes **136 focused tests in four files**;
lint, TypeScript and the rebuilt production UI bundle also pass. That check
requires explicit row selection to focus and reveal its guide heading while
automatic selection and polling preserve focus.

The first source gate compiled with zero warnings/errors and passed 60 of 63
groups. Its three failures were retained: two fixtures still targeted old
journal/storage assumptions, and the new fresh-status Retry check required
the disconnected refresh path. After correction, an incremental full solution
build has zero warnings/errors and all five focused groups pass: server hosting
policy, legacy bootstrap/floors, permission before preparation, fixed hosting
Start/repair, and offline-owner peer catch-up. The next integrated run exposed
two offline regressions: a first incoming listener and a completed hosting move
attempted unnecessary network refresh. The final change preserves an already
verified offline grant. All three focused permission, preparation and exact
hosting-move groups pass without weakening their existing assertions. The final
integrated source run passes **63 of 63 groups**, with one synthetic symbolic-link
subcase skipped because this Windows account cannot create symbolic links.

The focused hosted renderer contains 20 cases across desktop, narrow and narrow
150% text: 60 plans. Its 34 pure fixture tests pass. New cases cover permission
unknown versus denied, all guide checks, a reopened action, preserved failed
retry results, successful recheck, and checkbox/text bounds and click behavior.

The immutable packaged candidate, hosted rendering, installer checks and
publication are pending at this checkpoint.
The production UI bundle and TypeScript check pass; Vite retains its existing
large-chunk advisory. Whitespace and desktop diagnostic isolation pass.
The normal .NET app build embeds that UI with zero warnings/errors.
No app, game, console fixture, native dialog, installer or browser automation
has been launched on the owner's active desktop.

The native window smoke's initial wait was shorter than the product's supported
startup deadline: approximately 10 seconds from process launch versus 15 seconds
after Window.Shown. The test now uses a 25-second initial wall-clock deadline
and retains the last window lifecycle fields. A terminal load failure still
fails immediately. Later chrome, taskbar, tray and geometry assertions retain
their existing scope and waits. Runtime verification is hosted-only.

## Acceptance boundaries

Source/JSDOM checks, synthetic hosted processes and rendered fixtures are
separate evidence. They do not establish a physical Friend-PC connection, WAN
reachability, a real game join, recognizable saved progress or game restart
integrity. Those remain external acceptance. Minecraft remains fixture-tested.
Release assets remain unsigned unless a validated signature is explicitly
recorded. Keep source, workflow/run/artifact identity and exact asset hashes
with the final publication record; never rebuild frozen release bytes to
complete a later check.

## Superseded first hosted candidate

Compiled source is `ed1040c4a3dacac83ca0ac6925aad63c10b2c10e`, version
`0.3.3.3`. [Windows CI 39816832047](https://github.com/daltonstates/TogetherServer/actions/runs/39816832047),
attempt 1, builds the immutable candidate once. Candidate job `119957404246`
and package job `119959000580` pass. Candidate artifact `12441999129` is
61,019,850 bytes, ZIP SHA-256
`a23d5eac62ccb554bf7bec64d4a5f69ffec863ed0a4978d98c934caa7cc04ce0`.
Package artifact `12443032428` is 120,264,933 bytes, ZIP SHA-256
`8513339255e3d8da4d7d52b68f6f9d1613a4f026ce42a1dd9f4d759bd6baaeb4`.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 66,143,111 | `e8417bffd36ac20f51daf2992f571f2462ff8167f44db471818c158e0ce57c3a` |
| `TogetherServer-Setup-win-x64.exe` | 60,605,349 | `d519c44f21357527fe77ecdd005d5be892103dfd8283d372da905ff4c0b910cb` |

Independent downloaded-byte inspection confirms the GUI subsystem, exact
bundled assembly hash, all seven embedded UI assets, local/hosted UI byte
equality, numeric/source versions, adjacent checksums and `NotSigned` status.
The installer passes six smoke cases and the actual Host, Friend and Decide
later wizard choices on hosted Windows. Six compatibility jobs for 0.3.0,
0.3.1, 0.3.2, 0.3.2.1, 0.3.2.2 and 0.3.3 pass their role-direction and
update-handoff checks. Browser and packaged rehearsal jobs pass.

Native Desktop job `119959000550` passes all 14 window-chrome checks, including
physical titlebar double-click, taskbar, caption, ShowAsync, single-instance
and tray restoration. Its independent QoL suite passes 12 required cases and
records one blocked case: Windows remains in `QuietTime`, so real notification
delivery, physical notification click and destination navigation are unproven.
The job remains failed; full native acceptance is not claimed. Development
staging startup is separately skipped by design; production startup belongs
to its existing disposable-candidate gate.

## First hosted review and correction

[UI review 39817041108](https://github.com/daltonstates/TogetherServer/actions/runs/39817041108),
render job `119958129093`, captures 54 of 60 plans and fails six plans across
two cases. It has no JavaScript errors, unhandled routes or layout findings
in those failures. The reopen case accidentally entered the passive-entry
assertion because both IDs end in `-entry`; the two visible Host locally
buttons then correctly trigger a strict-locator failure. The removed-access
assertion searched for an exact text node while its heading also contains the
nested access-status label. Its failure screenshot shows the checkbox checked
and the removed Host revealed on the correctly aligned row.

The corrected checks explicitly identify the three passive-entry cases and
use the removed Host's scoped accessible heading. They retain geometry,
full-label click, fresh reopen reads, blocked Start and zero hosting-write
assertions. All 34 pure fixture tests and syntax checks pass. The corrected
selectors will be exercised by the next candidate's normal hosted review.
The original failed review remains failed.

The first Existing job `119959000504` passes its first six stages, including
all 1,411 UI tests, then fails TogetherServer.Checks (109 passed, five failed).
The eager first-publication journal changed legacy fixture assumptions and
also blocked the supported owner-reviewed world-source rotation. This is a
product regression, not a test waiver. The two eager anchoring/bootstrap
additions are removed; strict signed legacy repair, hash-based catch-up,
revision-gap lookup, proof filtering and permission refresh remain. All five
original assertions in TogetherServer.Checks are unchanged. The interrupted
disable fixture now locks whichever authority destination actually exists.

The publication/journal mutation gate serializes the repaired race: a peer
either anchors first and publication follows the exact parent, or the owner
imports the strictly newer complete legacy snapshot before serving its current
roster. The explicit old-journal/new-standalone regression still exercises this
repair. A fresh candidate and hosted run are required after this product fix;
the first EXE and installer above will not be tagged or published.

The first automatic-sharing job passes both ordinary eight-stage journeys.
The local-pairing job passes its 18-stage LAN API journey, then its UI journey
passes seven stages through automatic receipt and original/received payload
hash checks before preparation fails. The masked report does not identify the
prepare response code. A bounded diagnostic now retains only a validated result
code, booleans and fixed step-key/state pairs before the unchanged readiness
assertions; messages, paths, IDs and connection material remain excluded.
The next hosted run must verify this stage and the two later UI stages, plus
the previously unattempted loopback journey.

The corrected source compiles with zero warnings/errors and passes all 63
source/synthetic groups. The same symbolic-link subcase remains an explicit
account-capability skip. No existing core regression assertion was changed.

## Corrected candidate and final UI capture

Corrected compiled source is `7d6ab87a0d92f3a1e4c6f403a37a898e4807fa1d`.
[Windows CI 39819723346](https://github.com/daltonstates/TogetherServer/actions/runs/39819723346)
and [UI review 39819723794](https://github.com/daltonstates/TogetherServer/actions/runs/39819723794)
use that source. The new UI review captures 59 of 60 plans. Its one remaining
failure is the 150% narrow removed-access screenshot: geometry, full-label
click and revealing the removed Host all pass, then the renderer tries to
capture Play after the sidebar click has scrolled that panel off screen.
The capture target now names Servers, the actual region under review.

A manual render-only option avoids repeating the independent packaged journeys
for this assertion-only correction. Application and UI component sources remain
unchanged. The capture-plan module is also an indirect build input for the
synthetic demo, so rebuilding after the plan correction changes bundle hashes.
The passing rebuilt 60-plan review is source-level fixture evidence; its index
does not match the frozen candidate and is not exact-package rendering proof.
The final review must receive the candidate's pinned artifact, verify every UI
asset and serve those retained files without a UI or app rebuild. The original
59-capture run remains failed.

## Retained corrected candidate evidence

Windows run `39819723346`, attempt 1, builds the corrected candidate in job
`119967477555`. Artifact `12443303157` is 61,020,009 bytes, ZIP SHA-256
`a55676eb4b7e17698770d15713670e332fb3ffeadc2e2c597b70b0bf46a96d3a`.
Package job `119968652505` passes. Its artifact `12443532218` is 120,263,916
bytes, ZIP SHA-256
`659da50d997ca2e94123ee454f1d4948de5f9abee52f44cbfce40b387ba90932`.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 66,143,076 | `05af882c8ce710bad2ff6990220bdd82019c291f102c9c0ea36643a4d277318b` |
| `TogetherServer-Setup-win-x64.exe` | 60,603,837 | `cac3ffd6bccd70acf48635d38c5ecca416a1e7a89cde7d70620de597783bbf04` |

Downloaded bytes independently pass static PE/bundle inspection, exact source
and numeric version, all seven embedded UI asset hashes, adjacent checksums
and unsigned-state verification. Installer smoke passes six cases; the actual
Host, Friend and Decide later choices pass. All six compatibility jobs,
Browser, QolApi, rehearsal and both automatic shared-hosting runs pass.

The corrected Existing job `119968652593` passes its first 12 stages,
including **1,411 UI tests**, **114 core checks**, Valheim 11, Factorio five,
Terraria two, Minecraft 12 and Minecraft setup five. The Custom runner passes
five cases and fails its immediate-Start race without retaining the Start
result code. Its port helper checked only UDP `p`, although the profile also
declares TCP `p+1`. The check-only correction observes both listener tables
and retains the fixed result code; it opens no listener and changes no Start,
Stop or timeout assertion. Custom plus the 14 unattempted Existing stages
require hosted continuation. The original failure remains failed.

The corrected local-pairing job `119968652866` passes all 18 LAN API stages
with zero observed windows and exact owned-process cleanup. The actual
two-ended UI passes seven stages and verifies the original and received
completed-save hashes. Preparation succeeds with `LocalHostingPrepared` and
all permission, server, save and port checks Ready. Its subsequent single
Start request fails before the required running state is observed. The dialog
is closed in the failure capture; a bounded diagnostic continuation must
identify whether a connection transition unmounted it or Start timed out.
It retains elapsed time, fixed status/code and capped state/element-presence
transitions only. It adds no poll, retry or time allowance. UX and the
unattempted loopback journey remain required before publication.

Corrected Desktop job `119968652604` passes all **14 native chrome checks**.
Its startup observation is 11,047 ms, within the corrected initial deadline.
The independent native QoL suite passes 12 required cases and remains failed
because Windows reports `QuietTime`. Notification delivery, physical click and
destination navigation are blocked. Full native acceptance remains false.

[Render-only review 39820425269](https://github.com/daltonstates/TogetherServer/actions/runs/39820425269)
passes all **60 plans**, with zero layout findings or skipped plans. Its
rebuilt index hash is
`1229ba9e472f98d9033a57b887949d9afc4b9562c3d3cc7f4ae8ea639f9778af`;
the frozen candidate's index is
`0093ec5af1464d59502ea0ced4cd208dd68919fcb9697ce5490de9f44436b48f`.
The difference is recorded rather than treating a rebuilt fixture as the
release package. An exact retained-byte replay is prepared.

## Exact retained UI replay

[UI review 39824097608](https://github.com/daltonstates/TogetherServer/actions/runs/39824097608),
render job `119982673185`, uses verification source
`d901b13307b27b6c711204b823fa3caf8e8625c8`. It receives the original pinned
candidate artifact, checks the app and all seven packaged UI assets and serves
those files without rebuilding the UI or launching the app. All **60 of 60
plans** capture successfully: zero failed or skipped plans and zero layout
findings. There are 328 screenshots; 51 intentional unavailable/error fixture
cases remain explicitly identified.

Artifact `12444684947` is 18,290,598 bytes, ZIP SHA-256
`31a9f4f8abb0b4880052073c62eb311f1edabafd682437b80c413440b4693202`.
The independently downloaded proof confirms the exact candidate hash and index
`0093ec5af1464d59502ea0ced4cd208dd68919fcb9697ce5490de9f44436b48f`,
all seven unchanged asset hashes, `rebuilt=false` and `appLaunched=false`.
Representative desktop and 150% narrow captures were visually inspected for
the permission guide, retained stale checks, port instructions and aligned
removed-access checkbox. These fixtures do not establish a real Friend-PC,
network, game, save or native acceptance result.

[Remaining-check continuation 39824093930](https://github.com/daltonstates/TogetherServer/actions/runs/39824093930)
uses the same verification source and immutable candidate. It retains the
original 12 Existing passes and 18 LAN stages, attempts the 15 remaining
Existing stages and runs UX plus loopback. Its outcomes are pending.

## Hosted interruption diagnosed and repaired

Continuation pairing job `119982660292` preserves the original UX failure and
passes all **11 loopback stages**, with zero observed windows and exact cleanup.
Its new UX attempt fails in preparation. Artifact `12444959826` is 2,287,752
bytes, ZIP SHA-256
`6f2362613aeeb79ae2ca11ddc652902dc7b40920e25556f3c0f2a925a99073b1`.
The sanitized trace identifies a product failure rather than a slow Start:
preparation is aborted after **4,373 ms**, before browser cleanup. At 59,713 ms
the heartbeat reports `HostBusy`, clears the assigned profiles and unmounts the
hosting article/dialog. At 68,754 ms the connection and profile recover, but
the dialog is closed and the deliberate hosting action was canceled.

The Friend now keeps its last known assigned rows for HTTP 429 and 5xx while
reporting Disconnected/Unknown and disabling remote Start/Stop/control flags.
An authenticated empty assignment still removes the row. Revocation, expiry,
approval and credential-denial branches retain their existing behavior.
Synthetic pinned-heartbeat checks exercise retained rows, disabled authority,
recovery and actual assignment withdrawal.

Enrollment GET/POST also previously classified any unsuccessful response as
owner approval or signing-key denial. Only 429 and 5xx now return
`RosterUnavailable` and retry guidance. Full-status tests cover both methods
under 429/503, exact unchanged saved roster/floor, successful explicit retry,
fresh signed hosting withdrawal, 401/403 and no-network known revocation.
Both focused groups pass without starting any app or listener locally.

Settled permission refresh previously made four companion calls, including an
empty history page after reading an already verified current roster. The
history request is skipped only when the fresh authenticated signed current
roster's hash already equals the verified single local head and the protected
floor is proven. The comparison occurs before importing any newer legacy
snapshot. Fresh roster reads, enrollment, root/signature/floor checks and
normal newer-history catch-up remain mandatory. Focused checks require a
fresh current read each time, no empty history request for an unchanged head,
and normal page catch-up and denial for a new signed hosting withdrawal.

Compiled source `7d6ab87a0d92f3a1e4c6f403a37a898e4807fa1d` and its
portable/installer bytes are therefore superseded and will not be published.
The UI source is unchanged by these backend repairs. A replacement immutable
candidate and hosted hosting acceptance are required before publication.

The complete 63-group source run for the transient-response and history-read
repairs passes, with the same single symbolic-link capability skip. Compilation
has zero warnings/errors and focused whitespace verification passes.

Existing continuation job `119982660990` passes **14 of its 15 stages**,
including the corrected Custom runner, SharedHistory, Update, Core remote,
world-load rehearsal, LiveSave action and transfer, all 46 Companion groups,
formatting, served/staging, hidden and visible desktop, and update handoff.
Together with the original 12 passes, 26 of the 27 Existing stages pass.
Artifact `12446966314` is 4,135 bytes, ZIP SHA-256
`825067ba8e9441cedb9f71b61e427dc18c91b3029433b9507d965dd33c02fd50`.
The sole failure is Shared Worlds after 10 passed groups: a recovery-voter-only
PC returns `HistoryReviewed` before its required first owner/group confirmation.
The assertion and fixture timing remain unchanged.

Background signed membership refresh was automatically approving every first
group, including a member without Receive access. Automatic first-group
following now requires an active matching signed Receive grant. A voter-only
member still confirms the exact owner and group through the existing review
action. New fake-HTTP checks perform repeated background-equivalent refreshes,
require the initial owner/group prompt, reject incorrect confirmations and
accept the exact one without granting Receive consent or hosting/forwarding
rights. Ordinary Receive and automatic-host members still follow their first
signed group. The new focused check exposed a second edge case: an absent
approved-group dictionary entry was read as a non-null empty ID and rejected
as a conflicting approval. The lookup now preserves absence as null; an actual
saved mismatch still returns `TrustRequired`.

The final source compiles with zero warnings/errors. The bootstrap/floor and
local-hosting permission groups pass after that correction; the final revocation,
offline-owner catch-up and saved-peer groups also pass. The complete 63-group
run immediately before the voter-review addition passes, with the same single
symbolic-link capability skip. The replacement candidate's normal hosted source
gate will run all 63 groups again. The production UI rebuild and TypeScript
compilation pass; its index is
`1229ba9e472f98d9033a57b887949d9afc4b9562c3d3cc7f4ae8ea639f9778af`.
The existing Vite large-chunk advisory remains. A fresh immutable candidate and
hosted acceptance are pending.

## Replacement immutable release candidate

Final compiled source is `f17b02a994fd88557151430d7ac78847a8a0d284`.
[Windows CI 39828066495](https://github.com/daltonstates/TogetherServer/actions/runs/39828066495),
attempt 1, candidate job `119996548499`, passes the build and static bundle
gate. Candidate artifact `12446564179` is 61,020,299 bytes, ZIP SHA-256
`3b1b1f3efc3c07edd2f921fe814ddfcb402f4d8ac54f1c3860147a03ddf62ac5`.
Independent downloaded-byte inspection verifies GUI subsystem 2, bundled
assembly SHA-256
`9afa2c87328b5e32d09718d32ee503f9f0e7d39f6fcb1ec4cd389459eaa20de4`
and all seven embedded UI files against the same-source local UI build.

Package job `119997976902` passes all six installer smoke cases and actual
Host, Friend and Decide later wizard choices. Release artifact `12447602159`
is 120,264,910 bytes, ZIP SHA-256
`1bb4af81d4c4a4479875ca1cfd985740946a1c45a104e95d7e55c72a9adcf354`.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 66,143,164 | `d963736148a6fce58c2f7c49a8e8b1498154db63ccf41cec058d773a326f3bec` |
| `TogetherServer-Setup-win-x64.exe` | 60,604,925 | `ebb50fdedcfa7ce4b235e368e8773efc8856255df16aac7b635169e8315fa632` |

Independent asset checks confirm numeric version `0.3.3.3`, portable source
version `0.3.3.3+f17b02a994fd88557151430d7ac78847a8a0d284`, sizes, both
adjacent checksum contents and `NotSigned` for both executables. The package
manifest SHA-256 is
`11c9560c81a6e0e9d494a49a1c6793475748b7d7031bac0cd5dc169b6036e9cd`.
It deliberately remains `fullyAccepted=false`, `reviewOnly=true` and does not
claim functional or native notification acceptance.

[UI review 39828076596](https://github.com/daltonstates/TogetherServer/actions/runs/39828076596),
render job `119996582501`, passes all **60 plans**, zero failed/skipped plans
and zero layout findings, with 328 captures. Its same-source rebuilt index
matches the frozen candidate and local build:
`1229ba9e472f98d9033a57b887949d9afc4b9562c3d3cc7f4ae8ea639f9778af`.
Representative final captures of the permission checks and aligned directory
were visually inspected. Artifact `12446639426` is 18,290,187 bytes, ZIP
SHA-256 `539c90dee803a04b37f32fadcd1ea5b6e55cc249ccab8f7e88ce9b7e97e9d304`.
This is a source-built fixture capture; the exact retained-byte replay above
belongs to the superseded candidate and is not relabeled as this candidate.

The replacement browser and packaged rehearsal jobs pass. Functional hosting,
source, Existing and compatibility outcomes are still being collected.
Replacement Desktop job `119997976757` again passes the native chrome checks
and 12 required QoL cases while notification acceptance stays blocked by
Windows `QuietTime`. That failed job remains failed; physical notification
delivery/click/navigation and full native acceptance remain unproven.

## Replacement functional results

All six compatibility jobs pass: 0.3.0, 0.3.1, 0.3.2, 0.3.2.1, 0.3.2.2 and
0.3.3, including both Host/Friend directions and update handoff. QolApi job
`119997976507` passes all **63 source groups with no skips** and the packaged
API journey on the exact candidate. Artifact `12447337952` is 690 bytes, ZIP
SHA-256 `ae827036c213e63e8ceda86de06ef57f5778592931a61d6c559d1dc25d134470`.
The source run covers the new temporary-error, retained-row, exact-history and
voter-only review regressions.

Local pairing job `119997977287` passes every planned stage, with no skips:
**18 LAN API stages**, the wrapper's three UX stages and all **10 actual
two-ended UI stages**, then **11 loopback stages**. Every mode observes zero
visible owned windows and confirms zero remaining owned processes. Candidate
hashes match before and after. Artifact `12447468171` is 1,268,653 bytes, ZIP
SHA-256 `f1283715e8f1756391ab46074774203a84c463a32157ef1c256f14757cb7a384`.

The UI performs exactly one preparation and one Start after one deliberate
network confirmation. Preparation completes in 4,049 ms with
`LocalHostingPrepared`; Start completes in 7,702 ms with `SeparateCopyStarted`.
Missing server files block Start, owner withdrawal cancels stale intent,
restoration does not replay it and cancellation performs no hosting mutation.
The synthetic completed-save hashes match the received and hosted copies;
the original remains unchanged after candidate Stop and destination storage
is separate. Signed discovery, Connect, permission isolation and cleanup also
pass. These are same-PC synthetic processes, not physical Friend-PC or game
acceptance.

Automatic shared-hosting job `119997976069` passes both ordinary eight-stage
handoff runs against the frozen candidate, with one Friend Start and one owner
Start per run, exact descendant saves and empty owned-process jobs at cleanup.
Artifact `12447398528` is 2,717 bytes, ZIP SHA-256
`bff5e6796050c9dfb2e73443d9cb0762d266f418b45c16361ea7ae64e7e9a1cf`.
The independent same-source packaged hosting review job `119996582988` also
passes. Its separately built app is not substituted for the release bytes.

The native chrome result contains 14 successful cases. The QoL native report
requires 13 cases: 12 pass and one is blocked by `QuietTime`; cleanup and all
other native cases complete. Retained Desktop artifact `12447327690` is
309,333 bytes, ZIP SHA-256
`296e9cfbcbef3f4b8e6292e4640b43833153c5a6c9873df6ba11e6668e38b8c1`.
Existing job `119997976449` passes all **27 serial stages** against the frozen
candidate, whose hash remains unchanged. This includes **1,411 UI tests in 108
files**, **114 core checks**, Custom six, Valheim 11, Factorio five, Terraria
two, Minecraft 12, Minecraft setup five, update 15 and Companion 46. Shared
history, core remote, world-load, served package, staging isolation, hidden and
visible desktop and update handoff also pass. The full Shared Worlds journey
passes all **11 groups**, including legacy voter-only review, owner override
Off, typed majority resolution and both timed host-loss recovery scenarios,
with **zero failed or skipped recovery scenarios**. LiveSave owner action and
transfer pass three and five groups respectively; real-game acceptance remains
false.

Existing artifact `12450150312` is 1,116 bytes, ZIP SHA-256
`a0a98938ef0f555b472c562ca485da3f620f15061e98a40618b6bd1cdcb03719`.
Its downloaded report binds source, version and candidate hash and has no
failed or skipped stage. Across the replacement Windows workflow, 14 jobs pass
and the one Desktop job remains failed solely for the recorded notification
blocker. All required functional hosting checks are complete. Publication uses
the retained package bytes above and keeps the notification limitation visible.

## Published prerelease and anonymous verification

Release `421068086` is published at
[v0.3.3.3](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.3)
at `2026-10-22T18:36:57Z`, with `draft=false`, `prerelease=true` and
`make_latest=false`. Annotated tag object
`fc67f82b395a0acb04a83b4eff6e32a21fd74036` points to compiled source
`f17b02a994fd88557151430d7ac78847a8a0d284`. This later evidence-only commit
does not move the tag or rebuild the release assets.

| Public asset | Asset ID | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 666726433 | `d963736148a6fce58c2f7c49a8e8b1498154db63ccf41cec058d773a326f3bec` |
| `TogetherServer-win-x64.exe.sha256` | 666726968 | `f3b30375e90feadfc97b7f64df1fcb368099b1cb74a0eb414887db4c7d20698e` |
| `TogetherServer-Setup-win-x64.exe` | 666727015 | `ebb50fdedcfa7ce4b235e368e8773efc8856255df16aac7b635169e8315fa632` |
| `TogetherServer-Setup-win-x64.exe.sha256` | 666727481 | `680d576ff4f25ec6cab422ef6d3cd9816299b8c7e46ffb9d288d1b98c3d4c2e0` |

Anonymous public verification completes at
`2026-10-22T18:37:09.7755743Z`. All four browser-download URLs return bytes
matching the uploaded asset IDs, API digests, sizes, frozen package manifest
and adjacent checksum files. Both executables have numeric version `0.3.3.3`
and remain `NotSigned`; the portable source version matches the tag. No asset
is launched during this check. The public tag and release classifications
match. Stable/latest remains release `418702775`, tag `v0.3.3`. The flags,
inventories and exact asset metadata of `v0.3.3.2`, `v0.3.3.1`, `v0.3.3` and
`v0.3.2.2` remain unchanged.

Release notes describe all six requested changes, ask users to update both
owner and Friend PCs, explain preview enrollment and unsigned Windows builds,
link the validation runs and retain the native notification and real-machine
acceptance limits. Local working files and downloaded binaries remain ignored
under `local-data/release-0.3.3.3/replacement/`. The owner's running app, game,
worlds, desktop, networking and settings were not changed.
