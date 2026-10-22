# Shared-hosting history repair and action feedback

Owner report, 2026-10-22: two current apps can remotely Start/Stop across
Ethernet and Wi-Fi, but **Host locally** reports **The saved sharing history
changed unexpectedly**. The original Host has no confirmed save download.
The setup guide also gives too little feedback after its buttons are pressed.

This slice prepares an unreleased `0.3.3.5` test build. It does not publish a
GitHub release, change networking, install the app or replace the running copy.

## Cause and repair

`ReviewSharedHistoryAsync` could accept a valid schema-3 sharing list and save
its protected revision/signature floor without importing the signed parent
revisions into the local journal. The next permission or transfer check then
rejected the older journal as a rollback. The signed records can all be valid;
this does not require broken LAN connectivity or changed owner permissions.

History review now completes the existing bounded signed-roster catch-up before
advancing the floor. An already affected PC can import the missing exact signed
parents and resume from its retained journal. Successful repair must retain the
original exact floor in that journal. Neither cached metadata nor a newer
standalone snapshot can jump over it. Changed signatures at the same revision,
missing parents, wrong owners/groups, competing heads and damaged signatures
still fail closed. Explicitly confirmed same-owner legacy source-group changes
remain supported when no earlier journal or authority conflicts with them.

Scheduled receiving and local hosting use this same history check before
requesting the save manifest, payload or completion receipt. Its failure can
therefore leave the original Host with no confirmed download even when remote
Start/Stop works. Receiving still requires local consent, complete signed
ancestry and hashes, followed by a signed exact-copy receipt. A published source
or a partial download is not a confirmed received copy.

## Wizard behavior

The guide now shows the operation being performed and its result for rechecking,
preparation, server-file selection, hosting-move responses and Start. The guide's
server-file button opens the existing native picker when available; cancellation
and manual entry receive visible instructions. Picking a file never starts a
server. Move-response waiting has a bounded timeout and rejects late replies.

When current hosting permission/history is unavailable, the later checks show
**Not checked**, explain the prerequisite and link to its guide step. They no
longer present placeholder save/server/port/connection results as separate
failures. Once access is verified, actual current results become visible.
The saved-history guide explains how to recheck with both apps current and
open, and keeps unresolved damage/conflicts blocked for owner review.

## Verification and handoff

The new fake-HTTP regression first failed before the product fix: history review
advanced the protected floor without retaining its signed revision. It passes
after the fix. Further cases cover existing schema-3 floor/journal skew,
permission recovery, exact-floor preservation across bounded catch-up/retry,
legacy jumps, changed floors, invalid signatures, missing parents and explicitly
confirmed legacy group changes. Only synthetic files and fake authenticated
HTTP are used; no app, game, console fixture or listener is launched.

The added scheduled-receive regression starts with a protected schema-3 floor
ahead of its journal, recovers the exact signed parents, receives two chunks
(256 KiB plus 31 bytes), verifies the full retained file and sends the exact
signed completion receipt. Its fake Host checks the current roster, device,
version and signature before confirming. The receiver reports
`ReceiptConfirmed: true`. A retry reuses that receipt without downloading
again. Missing consent and later withdrawal still block receiving.

Final local validation passes all **1,429 UI tests in 108 files**, plus all
**65 source/synthetic groups** through `scripts/verify-code-only.ps1`.
The gate verifies pinned Node 24.14.1/.NET SDK 10.0.301, matching app/UI versions,
locked dependencies, type checking, lint, solution compilation (zero warnings
and errors), whitespace and desktop diagnostic isolation. One existing
synthetic symbolic-link subcase skips because this Windows account cannot
create symbolic links. The getting-started corruption/write-failure fixture's
expected diagnostic messages remain in the log; its group passes.

The initial focused check exposed the intended new regression before the fix.
An early focused invocation used an unregistered group label and was rejected
before checks ran; the corrected registered groups and final complete gate pass.
The final focus-safe gate log is ignored local output at
`local-data/shared-hosting-history-code-only-20261022.log`.

Physical PC transfer, native rendering, the selected LAN/Wi-Fi route and
real-game load/change/Stop/restart remain external acceptance. Minecraft
remains fixture-tested. The owner's foreground and running app are preserved
throughout local verification. Static test-build identity is recorded below.
