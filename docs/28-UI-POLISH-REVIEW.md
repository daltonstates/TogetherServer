# UI spacing, navigation and screen review

The 2026-10-18 UI polish keeps Host/Join and Start/Stop prominent while reducing
stacked settings and repeated explanations. This work changes presentation and
navigation; existing lifecycle, permission, consent and world-safety gates remain
authoritative.

Final result: **267 recorded screen/state cases, 844/844 capture plans and 2,438
screenshots reviewed**, with no confirmed remaining UI defect in that inventory.
The final UI build, 1,074 UI tests, code-only verification and hosted rendered
gate pass. Gallery, capture accounting and acceptance limits follow below.

## Delivered surfaces

- Desktop Host, Join and App Settings have independently scrolling contextual
  navigation and content. Narrow windows retain a single page scroll. Sidebar
  focus, section scroll restoration and viewport-bounded server/Host pickers are
  retained.
- Settings cards use compact icon, title and current-state rows. The full action
  remains available to assistive technology and through a tooltip. Utility rows,
  button heights and responsive spacing are aligned.
- Settings & files separates Game settings from Server files. General settings
  and one selected player list no longer form a long stack. Files, add-ons and
  complete setup checkpoints have separate sections; folder paths and detailed
  instructions use disclosures.
- Editors remain mounted across local section changes. Unsaved changes continue
  to block Start and protected navigation. Existing exact-file review,
  maintenance, Offline, checkpoint, Undo and confirmation rules remain in place.
- Saved Host identity/address recovery is collapsed by default with short icon
  actions. It still requires the pinned Host identity and saved device access.
- Friend shared-save management separates Copy status, People, Recovery and
  Details. Transfer progress, storage warnings and conflicting histories stay
  visible across sections, and unfinished recovery fields retain their values.
- Join puts Play and connection actions before world discovery. Short icon
  disclosures hold instructions, requirements and tools. Narrow screens flatten
  the nested card padding; joining instructions have one disclosure.

## Review and validation

The code-only verifier passed with the pinned .NET 10.0.301 and Node 24.14.1:
locked installs/restores, TypeScript, lint, solution compilation with zero
warnings/errors, source/synthetic checks, whitespace and desktop diagnostic
isolation. The source symbolic-link subcase was skipped because this Windows
account cannot create that link; app/process/native/installer and real-game
journeys were deliberately skipped. Detailed logs stay under ignored
`local-data/ui-polish-review/`.

The initial full JSDOM suite passed 1,060 tests in 89 files. A subsequent full
suite passed 1,063 after the failed-draft recovery fixes. The first integrated suite
passed **1,067/1,067 in 89 files** after the inactive-editor guard correction.
Final lint, TypeScript and production UI build passed; the build retains the
existing bundle-size advisory. The initial locked renderer fixtures passed
**19/19**. Subsequent focused tests cover the findings below; final local results
follow.

### Initial rendered review

Hosted Windows run [39138401661](https://github.com/daltonstates/TogetherServer/actions/runs/39138401661)
rendered source `d2d7048f9f5ef56b3b84006839727b38406d55e5`. It was canceled
after the inventory audit found missing backup, live-copy, recovery and access
dialogs. This is incomplete evidence, not a passing run. The interrupted
manifest was recovered from 553 complete per-case records: 974 screenshots,
43 failed records (including cancellation-tail failures) and one reported
horizontal layout finding. The artifact ZIP SHA-256 is
`E2BA4D367C126D61400712BBB92BE26BBE9EF14A530817E85E6FE9FC9FD254BD`.

All 154 contact sheets were visually inspected: Host 71, sharing 42, Settings 21,
Join/global 20. Suspicious frames were inspected at original size. Confirmed
findings and fixes include:

- Narrow update action overflow and dismissal overlap: a wrapping-safe grid
  reserves space for the close control.
- Excess vertical space in update/Quit rows and an empty update-details bar:
  correct narrow flex sizing and omit idle details.
- Cramped PC cards and support export text: reflow by content width; put export
  actions below a concise lead and disclose privacy details.
- Mobile Host navigation: combine selector/actions and place the two section
  selectors beside each other. Long selected names truncate within their control.
- Join primary actions: show the selected Host/connection form before world-group
  discovery. Within groups, sessions/actions precede disclosed sync permissions.
- Narrow log pages: collapse filters by available pane width while retaining
  unfinished searches. Pinned-notice actions wrap with explicit gutters.
- Oversized Game settings tabs and displaced Refresh: use compact content tabs
  and a dedicated heading row. Remove the duplicate file-safety chevron.
- Setup details: stack selected Minecraft identity/path, clear step labels from
  progress connectors, expand the sole world-location cell, give custom script
  editors usable height and align blocker links with their list markers.
- Shared-save transfer copy: keep one stage heading, byte progress and explicit
  phase checks instead of repeating the stage across three paragraphs.

The expanded inventory adds missing dialogs and stateful screens, explicit
expanded disclosures, visible nested-pane captures, independent sidebar wheel
and keyboard checks, and isolated parallel contexts. The final source revision,
counts, image inspection and remaining findings will be recorded before delivery.
It defines **265 cases** (164 page/state, 101 dialog/tooltip) and **836 planned
viewport captures**, including 37 narrow 150% and four short desktop cases,
before scroll companions. Pure fixture and inventory checks pass **23/23**.
The expanded full suite initially passed 1,069/1,070; one receiver-poll test still
expected the removed duplicate stage paragraph. It now checks the single live
stage heading before confirming the automatic transition to an up-to-date copy.
The corrected full suite passes **1,070/1,070 in 89 files**. The final code-only
gate also passes, with zero build warnings/errors and the same explicit skips.

### Expanded rendered review

Hosted Windows run [39141354749](https://github.com/daltonstates/TogetherServer/actions/runs/39141354749)
completed all **836/836** captures at
`80e9d2c540a8d4763b83802bace794fc648e3a65`, producing **2,072 screenshots**.
There were no missing images, skipped cases or fatal infrastructure errors.
The run failed its gate with 15 harness failures and 34 findings; it is not a
passing visual result. Artifact ZIP SHA-256:
`903711FD676753E4F86E99D223DE17DB5E47D39E7CB68BF64109924D8CB19086`.
All **314 contact sheets** were inspected: Host 164, sharing 89, Settings 27,
Join/global 34. Original PNGs were opened for suspected defects. Failed
screenshots remain failure evidence, even when their surrounding layout is
readable.

The stale permission/status selectors, removed transfer paragraph, notification
menu tooltip dismissal and underlying-picker hit tests were corrected in the
harness. Actual defects were an uncontained rehearsal hash, a file-diff minimum
width that inflated the editor, and keyboard focus lost after successful access
saves. Hashes and paths now wrap in their pane; exact diffs have a labeled,
keyboard-accessible internal horizontal reader; the topmost settings dialog
recovers focus when its active control becomes disabled, hidden or removed.

Additional observed polish aligns the removed-Host checkbox with its label,
shortens Play disclosures, gives very narrow privacy fields their own action
row and reduces narrow SettingsCard gutters without reducing text scaling.
Restore review now precedes its inventory; optional game-check guidance and
recovery setup explanations are disclosed while required inputs, blockers,
exact-copy evidence and consent stay visible.
The final Host review also found setup CSS overridden by later shared rules,
an indented unwrapped destination path, an oversized narrow timer label and two
nested rehearsal disclosures with the same title. Scoped selector precedence,
full path wrapping, compact timer/card gutters and one rehearsal disclosure
correct these findings. Visible programmatic focus targets retain focus during
dialog updates; disabled, hidden or removed targets recover within the top modal.

The initial three-position scroll sampling left gaps on long panes. Continuous
overlapping captures now record actual viewport/ancestor intersections and fail
on any unpictured interval beyond one-pixel rounding. The finite-case inventory,
shared UI tests and final hosted rerun will be recorded after integration.
Final inventory: **267 cases**, **844 viewport plans**, **39 narrow 150%** and
four short desktop cases, plus continuous scroll companions. Pure renderer and
fixture checks pass **24/24**. Two explicit expanded Play guidance cases cover
the shortened summaries and their full instructions.
After integrating these findings, the full UI suite passes **1,074/1,074 in 89
files**. TypeScript, lint and the production UI build pass, retaining only
the existing large-bundle advisory.
The code-only verifier was rerun against `c5cfaa7` and passes with zero build
warnings/errors; the source-link privilege and interactive acceptance skips
remain explicit. No production app or process fixture was launched locally.

### Complete capture and visual findings

Hosted Windows run [39143788396](https://github.com/daltonstates/TogetherServer/actions/runs/39143788396)
at `c5cfaa731114926d7d14daf6dc13d4e1a0c37b17` passed **844/844** planned
captures and produced **2,422 PNGs**, with no failed cases, layout findings,
missing screenshots, skips or fatal errors. The 194 intentional error-state
cases are expected synthetic UI states. ZIP SHA-256:
`385F4539E403BC919DED9BD022F76AC76D356D2D1D4B4B8D955FC471BFAC971C`.
The hosted bundle index hash matches the locally built index exactly.

All **542 non-null vertical pane records** report complete continuous coverage;
their **1,578 companion PNGs** cover the panes across 473 cases. The three
short-desktop scroll probes confirm separate sidebar/content movement and full
keyboard visibility of the last sidebar row. Advanced content fits without
scrolling; Files and Backups overflow and scroll independently. The fourth short
case tests the picker: all seven visible buttons pass hit testing and the
popover fits the viewport.

Exact SHA-256 comparison reuses **1,229 previously inspected PNGs** only from
captured cases with no reported finding. All **175 fresh contact sheets** were
visually inspected, covering the other **1,193 PNGs**: Host 87 sheets / 598 PNGs,
sharing 57 / 389, Settings 8 / 50, and Join/global 23 / 156. Original images were
opened when a sheet suggested a problem.
The Join/global review also opened its narrow
150% instruction original. That inspection found excess nested card padding
and a second instruction disclosure that reduced readable line width. The
mobile Join cards now share one inset, and instructions expand directly.
The Host review also found that vertical screenshots omit the far side of the
intentional file-diff horizontal reader. Horizontal companions now capture its
left, interior and right columns at each visible row band, restoring its original
scroll position afterward. Coverage fails unless every counted row band shows
all columns and the bands cover every row. A complete hosted rerun remains.

After the Join correction, the full UI suite again passes **1,074/1,074 in 89
files**, plus TypeScript, lint and the production UI build. The existing bundle
size advisory remains. Renderer/fixture checks pass **24/24** after updating the
obsolete two-disclosure expectation. Horizontal capture calculations also pass
the pure checks, including clipped readers and missing-corner counterexamples.
Final hosted capture results follow.
The code-only verifier also passes at final implementation commit `c11b249`,
with zero solution build warnings/errors and the same explicit source-link and
interactive skips. This final pass launched no app or fixture locally.

### Final capture and sign-off

Hosted Windows run [39145708393](https://github.com/daltonstates/TogetherServer/actions/runs/39145708393)
passes at final UI revision `c11b249fd67008011c6593ebf988b106d166e7d4`:
**267 screen/state cases, 844/844 viewport plans and 2,438 PNGs**. There are zero
failed cases, layout findings, fatal errors or missing image files. The 39 narrow
150% plans and four short-desktop plans remain included. The ZIP SHA-256 is
`0BD18EC693ED0A7258FC8881626CFB16E4D6573BA53E7584D049CE447376CD91`.

The final hosted index SHA-256
`E59AB0344C9A8D8A92F2BB3A3A5BEAEB47261229E2901D14664FD80737CFA69C`
matches the local production UI build. All **542 vertical pane** range unions
are complete. Both overflowing file-diff readers have complete row-and-column
coverage across **28 horizontal companion PNGs**. Five independent wheel probes
leave the other pane unchanged, three keyboard probes reveal the final sidebar
item and all seven visible short-desktop picker buttons pass hit testing.

The immutable artifact has a bookkeeping defect: a trailing variant spread
overwrites inventory IDs, falsely populating `skippedCases` with 844 records even
though `summary.skipped` is zero. The renderer now retains full case/variant IDs
and derives skipped totals from those identities. Pure checks cover complete,
partial and duplicate-attempt accounting. A separate read-only `--list` audit
matches **all 844 unique planned IDs to all 844 actual IDs**, including scenario,
workspace, theme, text scale and viewport, with no missing, unexpected, duplicate
or mismatched entry. **Actual skips are zero.** Original ZIP/manifest evidence is
preserved; the reconciliation is saved separately in
`local-data/ui-polish-review/signoff-39145708393-inventory-audit.json`.
This metadata-only correction does not change the reviewed application bundle.

The final comparison reuses **1,752 exact previously reviewed PNG hashes**. Its
fresh index contains **103 contact sheets / 686 changed or new PNGs**: Host
43 / 279, sharing 32 / 217, Settings 7 / 41 and Join/global 21 / 149. The Join
150% originals confirm normal instruction wrapping and short, aligned summary
rows; the repeated instruction disclosure is gone. **All 103 fresh sheets were
visually inspected**, plus all 28 horizontal originals, five sharing originals
and two Join originals. Every remaining PNG matches previously reviewed bytes.
No confirmed UI defects remain in the recorded inventory. The independent
inventory audit also records exact Host sheet and original-image lists.

The searchable gallery is
`local-data/ui-polish-review/signoff-39145708393/index.html`; its raw screenshots,
per-case JSON, range evidence and exact-hash review comparison remain beside it.
The downloaded ZIP is `local-data/ui-polish-review/signoff-39145708393.zip`.
Final application validation passes **1,074 UI tests in 89 files**, TypeScript,
lint, production UI build and the code-only verifier. Final renderer metadata
checks pass **24/24**; no further UI or backend change follows the captured build.
The final documentation/inventory-only commit skips another hosted capture;
its metadata/guard/accounting regressions pass locally, and the recorded UI
build and screenshot evidence remain unchanged.

The previous screenshot fixture inventory had drifted from the recent
Backups tabs and peer-world UI; this review updates it against the actual bundled
application rather than the historical mockups.

Workers use the configured `gpt-6.1-sol` workhorse. Component edits have explicit
file ownership, and shared builds, integration and commits are serialized. A
separate Codex CLI worker performs a read-only review of scroll ownership,
picker positioning, keyboard access and draft-preserving navigation.

That review found two P2 issues: the polled instance object could trigger scroll
restoration every three seconds, and a failed hidden draft could leave its
recovery controls on another section. Restoration now depends on stable
readiness. Failed draft saves reveal the exact outer and inner editor, retaining
contents and current review; the warning clears with that exact draft. The first
focused shell rerun had one asynchronous assertion that observed flush dispatch
before the section update; the regression now waits for the visible result.

Integration also found that an unopened editor's null identity retained its
initial Loading status and could block Start without edits. Each leaf now marks
only an actual loaded identity as pending. Genuine hidden drafts remain guarded.
Real-leaf regressions cover unopened file tools, unvisited player lists and
unsupported settings; hosted cases also assert Start remains available before
any file edit.

The active desktop remains untouched. The Browser skill found no connected
browser, so screenshot execution uses the explicitly isolated hosted Windows
workflow. Local checks use JSDOM, pure fixtures and the code-only verifier only.
No app/game/native dialog/process fixture/installer is launched locally.

## Acceptance boundary

Screenshots use the production React bundle with synthetic allowlisted API
responses. They establish rendered UI behavior for the recorded cases and
viewports, not native WebView or Windows picker/installer behavior. Real games,
worlds, separate Friend PCs, public routes, save integrity and restart acceptance
remain separate and are not certified by this UI review. No production data,
credentials, installed app, public listener, router or firewall setting is
changed. No release or tag is created.
