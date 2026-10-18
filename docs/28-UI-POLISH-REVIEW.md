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
existing bundle-size advisory. The locked renderer fixtures passed **19/19**.
Hosted screenshot execution and image inspection are pending. The inventory has
187 explicit scenarios and 586 base viewport captures, plus top/middle/bottom
captures for overflowing panes. Hosted source revision, final screenshot counts
and visual findings will be recorded here before delivery.

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
