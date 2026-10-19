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
| Hosted rendered review | Pending: 35 planned captures |

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
the current Servers directory; the application UI and packaged bytes are
unchanged by this harness correction.
The second run, `39276176121`, captured all owner surfaces with no layout
findings, but failed 17 cases: the recipient section still used the old Join
sidebar label, and two short-window dialogs attempted to scroll the inert
background. The harness now selects the shared selected-server sidebar and
checks dialog scroll coverage separately from background pane independence.
These failed runs are retained as failed evidence.
It checks actual rendered gaps and rejects a reintroduced recipient-address
field. Browser execution remains restricted to the dedicated hosted Windows
runner by the existing harness guard.

Native Windows, separate-PC transfer, actual incoming routes, game joins and
recognizable save/load/Stop/restart acceptance remain separate gates. No app,
game, console fixture, browser or native dialog is launched on the owner's
active desktop; production data and worlds remain untouched.
