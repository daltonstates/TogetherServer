# Paired-PC hosting moves and sharing spacing

Owner request, 2026-10-19: remove repeated connection entry from Move hosting,
ask the receiving PC whether it wants to host, use the usual game preparation
checks, and fix cramped text in the backups/sharing page and move dialog.

## Intended flow

- The current Host chooses an approved paired PC with permission to receive and
  host this world, then sends a hosting request. The receiving PC can accept or
  decline. Requesting or declining does not stop the game.
- The receiving PC uses the guided hosting preparation to check its installed
  game server, compatible settings and add-ons, available ports, verified save
  and configured incoming connection. Missing software, terms and connection
  choices remain explicit local actions.
- A first move works when no save has been shared yet. Preliminary preparation
  uses bounded authenticated game/setup metadata without copying a running
  world. That metadata is never save authority; the final completed copy still
  needs every hash, signed receipt and exact-version preparation check.
- The guided move continues only for the exact request the owner initiated.
  After initial preparation it stops safely, publishes the final completed
  save, and waits for the recipient's exact-copy receipt and preparation of
  that final version before completing the signed move.
- Closing or reopening the owner dialog requires an explicit Resume move.
  Read-only status polling does not authorize a new move. The receiving PC
  chooses Start separately after restore and the final setup/connection checks.
- The owner never enters the recipient's address. Signed, authenticated
  connection metadata supplies it. Pairing alone does not prove that the
  recipient has enabled an incoming connection or that its route is reachable;
  missing connection setup is shown on the receiving PC.

## Spacing

The sharing summary, heading/help text, PC permission cards and move fields own
their spacing through explicit grid gaps. The backups page's paragraph reset
has lower specificity so it cannot override component spacing. Permission
details and pending-enrollment help have space above and readable line heights.
The move dialog presents the current step and keeps game Start explicit.
Rendered enlarged-text review also found a shared-dialog paragraph rule adding
extra margins inside the move grid. The move grid now owns that spacing, and
rendered checks bound its heading/copy/field gaps in both directions.
The shared-workspace paragraph reset also has low specificity so expanded
permission help can retain its intended spacing; that gap is measured too.

## Safety and verification

Request and recipient decisions use bounded typed local/companion operations.
The implementation must bind every continuation to the current request,
selected device, current signed roster, unexpired canonical grants and exact
final save. Existing graceful Stop, immutable copy hashes, signed receipts,
source fencing, fresh destination storage and manual Start remain required.
No pairing credential, arbitrary command, world path or executable choice is
accepted from a remote requester. No network policy changes or game terms are
performed by the move.

Serialized validation on 2026-10-19:

| Check | Result |
| --- | --- |
| `scripts/verify-code-only.ps1` | PASS: pinned tools, locked dependencies/restore, UI typecheck/lint, complete solution compilation with zero warnings/errors, 56 source/synthetic groups, whitespace and desktop diagnostic isolation |
| Complete JSDOM inventory | 1,236 cases across 98 files covered by the full run and final focused rerun; see composition below |
| Final move, ordinary hosting, shared-save and shell regression files | PASS: 219 tests in six files |
| Pure hosted screenshot fixtures | PASS: 25 tests |
| Production React bundle | PASS: existing advisory for a JavaScript chunk over 500 kB |
| Hosted rendered review | PASS: all 35 cases, 134 screenshots including scroll coverage, zero failed or skipped cases and zero layout findings |
| Final EXE static package check | PASS: current assembly and all seven UI assets embedded byte-for-byte; app not loaded or launched |

The final broad UI run passed 1,233 cases; three new tests expected a pause
message that a later freshness notice replaced. Their assertions now check the
paused controls, released busy state and absence of mutations; all pass in the
219-case final focused rerun. The earlier default-concurrency run also hit a
navigation timeout; that existing test passes in the bounded full and focused
runs. This is composed evidence, not a claim that one broad invocation passed
every final case.

Initial compilation caught two references to local authentication helpers and
obsolete check-call signatures; integration now compiles every check project.
The first synthetic move also caught an Offline checkpoint that the existing
transfer path correctly refused because it was not a rolling backup. The move
now creates the transferable completed backup under the existing Offline gate,
without claiming a process Stop occurred. The final source gate includes the
two-PC signed proof, first-save move, stale request/access, offline destination
and changed-certificate regressions. One symbolic-link source subcase is
SKIPPED because this account cannot create the link.

The focused hosted inventory covers seven sharing, owner-move and
receiver-consent/setup surfaces in five viewport/text variants each.
The first hosted run, `39275404604`, correctly failed before those screens:
the older harness lacked profile/required-setup responses and still selected
the old Host/Join navigation. Its 35 attempts are failed evidence. The harness
now supplies decoded completed synthetic setup and selects saved servers from
the current Servers directory. These fixture corrections leave production
behavior unchanged; synthetic demo fixtures are also bundled, so the final
distribution is rebuilt from the reviewed revision and matched by UI hash.
The second run, `39276176121`, captured all owner surfaces with no layout
findings, but failed 17 cases: the recipient section still used the old Join
sidebar label, and two short-window dialogs attempted to scroll the inert
background. The harness now selects the shared selected-server sidebar and
checks dialog scroll coverage separately from background pane independence.
These failed runs are retained as failed evidence.
It checks actual rendered gaps and rejects a reintroduced recipient-address
field. Browser execution remains restricted to the dedicated hosted Windows
runner by the existing harness guard.

Final hosted run
[`39277838390`](https://github.com/daltonstates/TogetherServer/actions/runs/39277838390)
passed at source `3a8953978ba0512288dcb86019a0b8b73f903bee`, job
`118146565697`, artifact `12197479546`. The ZIP SHA-256 is
`39724557d8d73f813eb0e8ddd6bbbf9b9fdbd960d7ab84bc1637355261486289`.
Its UI index hash matches the final distribution below. All seven surfaces
were visually inspected at desktop and narrow 150% text, with additional
short-window dialogs and scroll segments: 20 representative images, with no
remaining visual findings. The 15 intentional error-state cases contain the
synthetic unavailable game-compatibility inventory; they are expected fixture
states, not failed captures or real game compatibility evidence. The expanded
permission help gap measures 12 px at default text; the move grid gap measures
16 px at default text and 24 px at 150% text.

## Production build

The final source revision is `3a8953978ba0512288dcb86019a0b8b73f903bee`.
The unpublished production EXE is
`local-data/move-hosting-production-20261019-3a89539/TogetherServer.exe`:

- File version: `0.3.2.1`; product version includes the exact source revision.
- Size: 66,057,399 bytes.
- SHA-256: `a11e4ae0350021e4bb643f26e83908cf78e0deacf52dd59057d63f99de6bc7de`.
- Static package verification: PASS. Windows GUI subsystem, matching bundled
  assembly and all seven current UI assets verified without loading the app.
- UI index SHA-256:
  `60d21e98d8102ccfe36aa0e212e9f2c9be68e978b8830a88347b95a982f51b1c`.
- Authenticode: NotSigned. The EXE was not launched or installed; the running
  production app was not replaced. No release or tag was created.

The artifact directory contains `artifact.json` and `package-evidence.json`.
Validation logs and downloaded hosted evidence are under
`local-data/move-hosting-review/`. These disposable files remain out of Git.

## Remaining acceptance

Native Windows, separate-PC transfer, actual incoming routes, game joins and
recognizable save/load/Stop/restart acceptance remain separate gates. No app,
game, console fixture, browser or native dialog is launched on the owner's
active desktop; production data and worlds remain untouched.
Updated process and companion check sources compile; their execution remains
pending in an approved isolated Windows session.
In particular, record a real first-save move between two paired PCs, recipient
Accept/Decline, game-specific preparation, exact final receipt and source
fencing, explicit destination Start and recognizable world loading before
claiming end-to-end game acceptance. Minecraft acceptance remains fixture-only.
