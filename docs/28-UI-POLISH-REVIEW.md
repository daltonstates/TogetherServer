# UI spacing, navigation and screen review

The 2026-10-18 UI polish keeps Host/Join and Start/Stop prominent while reducing
stacked settings and repeated explanations. This work changes presentation and
navigation; existing lifecycle, permission, consent and world-safety gates remain
authoritative.

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

## Review and validation

The code-only verifier passed with the pinned .NET 10.0.301 and Node 24.14.1:
locked installs/restores, TypeScript, lint, solution compilation with zero
warnings/errors, source/synthetic checks, whitespace and desktop diagnostic
isolation. The source symbolic-link subcase was skipped because this Windows
account cannot create that link; app/process/native/installer and real-game
journeys were deliberately skipped. Detailed logs stay under ignored
`local-data/ui-polish-review/`.

The initial full JSDOM suite passed 1,060 tests in 89 files. A subsequent full
suite passed 1,063 after the failed-draft recovery fixes. The final full suite
passed **1,067/1,067 in 89 files** after the inactive-editor guard correction.
Final lint, TypeScript and production UI build passed; the build retains the
existing bundle-size advisory. The initial locked renderer fixtures passed
**19/19**. Subsequent focused tests cover the findings below; final local results
follow. The expanded hosted capture remains pending.

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
