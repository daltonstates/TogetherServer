# Shared-hosting history repair and action feedback

Owner report, 2026-10-22: two current apps can remotely Start/Stop across
Ethernet and Wi-Fi, but **Host locally** reports **The saved sharing history
changed unexpectedly**. The original Host has no confirmed save download.
The setup guide also gives too little feedback after its buttons are pressed.

Preview [0.3.3.5](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.5)
is published following the owner's explicit request. The initial local test
build was superseded by the corrected immutable candidate below. Publication
and exact release evidence are recorded here. No networking, installation or
running app is changed on the owner's PC.

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

## Initial local test build (superseded)

The production UI bundle and Release publish pass. Vite retains its existing
JavaScript chunk-size advisory. Static package inspection confirms a Windows
GUI executable, the exact current app assembly and all seven current embedded
UI assets. No executable or game was launched to perform that inspection.

| Field | Value |
| --- | --- |
| Artifact | `local-data/shared-hosting-history-production-20261022-6a41ca6/TogetherServer.exe` |
| Source | `6a41ca6af5052c6dbefff60e7bc923242ed8f6a1` |
| File version | `0.3.3.5` |
| Product version | `0.3.3.5+6a41ca6af5052c6dbefff60e7bc923242ed8f6a1` |
| Bytes | 66,147,099 |
| SHA-256 | `05784cef3bafd4f7adf9c2e6782278f1272a5b39389d7fed6bc0d22700130aff` |
| Authenticode | `NotSigned` |

`build-info.json`, `package-evidence.json` and `TogetherServer.exe.sha256`
are beside the EXE in ignored local output. It has not been launched, installed,
published or used to replace production. This is a portable test build for both
PCs, with existing settings, pairing and save history retained. Its real-PC
transfer and confirmed receipt still need the owner's two-PC test; synthetic
confirmation above does not claim that the laptop already has the save.

This documentation-only follow-up records the unchanged built bytes. It does
not alter the tested product source or create a release/tag.

## Prerelease preparation

The owner explicitly requested creation of the prerelease after the local test
handoff. Initial hosted Windows run
[39877061919](https://github.com/daltonstates/TogetherServer/actions/runs/39877061919)
checked source `6a41ca6af5052c6dbefff60e7bc923242ed8f6a1`. Its Existing job
`120166055567` exposed two fenced-history review regressions: first-time
delegated voter proof and signed-majority conflict review returned
`RosterUnavailable`. The original failures remain recorded. The run was
cancelled after those failures to supersede its bytes; none of its successful
jobs qualify the corrected release candidate.

A new pure regression independently failed before the correction. Review now
avoids advancing trust when its fresh signed roster exactly equals the
protected floor. When a genuinely newer roster needs its parents, only an
explicit history review may use bounded `review=1` metadata paging after an
ordinary page returns unavailable. That mode reuses the existing read-only
history authorization before and after the read, including current device,
assignment and signed membership checks. Normal metadata/hosting requests,
save manifests, chunks, receipts and lifecycle actions keep their fences.
The existing signed review-roster selection is shared under the manager gate;
pages keep the strict offset, exact anchor and size bounds.

Older Hosts first receive the ordinary request, including their existing
unknown-anchor/root fallback. No ordinary hosting or receive request opts into
review mode. Missing, damaged or gapped review proof cannot advance the
protected floor. The expanded pure regression covers these cases, unchanged
voter-only floors and consent preservation. Its focused CodeOnlyVerification
build has zero warnings/errors and the exact source-check group passes. Real
companion-route verification belongs to the corrected hosted candidate.
Focused hosting-permission and fixed-Start checks also pass, as does targeted
solution whitespace formatting verification.

Ignored evidence, prepared notes and the preserved initial failures are under
`local-data/release-0.3.3.5/`. The initial portable test EXE above is retained as
historical evidence and will not be substituted for the corrected release.

## Corrected immutable candidate

Corrected source `111e2b503af58168e6951dc5b645310126cadf3f` is frozen in
[Windows CI 39878755154](https://github.com/daltonstates/TogetherServer/actions/runs/39878755154).
Candidate job `120170744729` builds the single portable app used by all its
functional lanes and installer packaging. Its file version is `0.3.3.5` and
product version is `0.3.3.5+111e2b503af58168e6951dc5b645310126cadf3f`.

| Frozen asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 66,148,067 | `00a774223b092fc039e5d8b933bcaca52c8f1e2b2f06997276c973b9c3ab436d` |
| `TogetherServer-Setup-win-x64.exe` | 60,610,876 | `b8243186e6e81421059503dd50632ac0ea44a9fe9dcf34b631265abab4ff0e22` |

Both executables remain `NotSigned`. Candidate artifact `12469991252` is
61,027,507 bytes, ZIP SHA-256
`5a3ed5dd3f3e60e0183bbf352e412312643eb397dd0870046cb222f4d5ea0b18`.
Release-assets artifact `12469862359` is 120,276,053 bytes, ZIP SHA-256
`0396124c791371cc7a2ecfc229dcd3f276aea9f6f24abb761d1ec4c6738afd2f`.
Their API metadata, ZIP digests, source/run bindings and extracted bytes have
been checked independently. Static binary reads verify the Windows GUI
subsystem, exact bundled assembly and all seven current UI assets without
loading or launching the app. Both packaged assets pass numeric version,
product/source version, unsigned classification and adjacent checksum checks.

Package job `120171744446` passes six installer smoke checks and all three
actual installer wizard choices: Host, Friend and Decide later. Its wizard
summary binds the unchanged portable and installer hashes, with cleanup
complete and no skipped cases. Artifact `12469573124` has ZIP SHA-256
`377005df105e010c9c88f61a602b3adf8201a7ab3bec9d04fe80dbd2e2856543`.
The package manifest deliberately retains `fullyAccepted=false` and
`reviewOnly=true`; its installer result does not stand in for the independent
functional and native checks.

## Corrected hosted UI, pairing and handoff checks

[Visual review 39878758112](https://github.com/daltonstates/TogetherServer/actions/runs/39878758112)
passes both jobs. Render job `120170754204` captures all **60 plans**, including
desktop, narrow and narrow at 150% scale, with **337 screenshots**, no skipped
plans and zero recorded layout, selector or runtime failures. Independent
inspection of 28 screenshots finds no blocking overlap, clipping or readability
defect. The long narrow guides remain scrollable; a minor feedback sentence
says "steps below" even though those instructions precede it.

Visual artifact `12469158766` is 19,012,864 bytes, ZIP SHA-256
`e0c51a04691789a229cef2d73bfed8f916c35869514fd804afdb8110addfea63`.
Its manifest binds the corrected source and bundled UI index SHA-256
`5ee5819461e1b37536d52eef9dc62bfce652919375ece89f49c107dd70651752`,
which matches the frozen release candidate's index. The separately built
same-source packaged review passes two ordinary eight-stage handoffs; that
review EXE is never substituted for release bytes.

Local-pairing job `120171744551` passes all **18 assigned-LAN API stages**,
**10 actual UI stages** with their three-stage wrapper, and **11 loopback
stages**. Reports bind the frozen candidate before and after. They verify a
complete received save, an automatic receipt, matching received/hosted hashes,
fresh destination storage and an unchanged original after candidate Stop.
Every mode records zero visible owned windows and zero owned processes at
cleanup. Artifact `12470086220` is 1,262,232 bytes, ZIP SHA-256
`fff2fdb4d274683e812d6b1d95aae9b98df8282496f5a24d332f65420d5a666a`.
These remain synthetic processes on one hosted PC, not the owner's two PCs.

Automatic-sharing job `120171744593` passes two ordinary **eight-stage** runs
against the frozen release candidate, with one Friend Start and one owner
Start in each, descendant transfer/relay and empty owned-process jobs at
cleanup. Artifact `12469284998` is 2,729 bytes, ZIP SHA-256
`c24ca106b9d0f3383510dbe49e9b2245e638c7564084ed38026f2abdced04653`.
All six older-version compatibility lanes, bundled browser journeys, remote
rehearsal and QolApi source/packaged checks pass on this corrected run.

QolApi job `120171744541` logs **65 of 65 source/synthetic groups passed with
no skips**, including the corrected signed-history regressions. Its report
binds the unchanged release candidate. Artifact `12470335330` is 695 bytes,
ZIP SHA-256
`7d33ed772f8cddd70b51ba5cf469a88872d25cda7ea26ccda750a4ba0b54c8b9`.

Desktop job `120171744632` remains failed solely because hosted Windows stays
in QuietTime and blocks actual notification delivery, click and navigation.
Its other 12 required native cases pass; the separate window-chrome gate also
passes. No notification setting is changed to bypass the blocker. Artifact
`12470290310` is 308,629 bytes, ZIP SHA-256
`18f18af64d99b0e0ac731a35e3ba7391ad1213007b720f89eed9f44fd1bf0c2f`.
Full native acceptance remains false. Physical-PC LAN/Wi-Fi transfer and real
game save/restart acceptance remain open.

## Final functional acceptance

Existing job `120171744669` passes all **27 serial stages**, including
**1,429 UI tests in 108 files**, **114 core checks**, Shared History, game
fixtures, Companion, core remote, world-load, served package, staging
isolation, hidden/visible desktop, formatting and update handoff. Both
previously failing named cases pass: schema-3 delegated voter-only fenced
review, and signed-majority authority/conflict review across restart.

The complete Shared Worlds packaged journey passes **11 groups**, with
**zero failures and zero skipped recovery scenarios**. Its fenced newer-floor
review and offline-owner recovery paths pass against the corrected companion
routes. Final serial verification records the exact unchanged candidate hash
and product/source version. Artifact `12470432947` is 1,120 bytes, ZIP SHA-256
`b85b65b1f380146983a39033fd0ecfb96b5a0909ec87190846aa4fbd2d4ee413`.
The downloaded report binds the corrected source and frozen portable hash;
every recorded stage passes.

Windows CI finishes with **14 passing jobs** and the separate Desktop job
failed solely for its recorded QuietTime notification blocker. All required
functional checks are complete. The release notes retain the incomplete
native and physical-machine acceptance boundaries rather than claiming a
fully passing Windows workflow. The original failed run is not reused.

## Published prerelease and public downloads

Release `421348309` is public at
[v0.3.3.5](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.5),
published `2026-10-23T01:03:26Z`, with `draft=false`, `prerelease=true` and
`make_latest=false`. Annotated tag object
`6f2a1919716aff483181e84944d282bb02e69395` points to the exact compiled source
`111e2b503af58168e6951dc5b645310126cadf3f`. This documentation-only follow-up
does not move the tag or rebuild any release asset.

| Public asset | Asset ID | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 667573899 | `00a774223b092fc039e5d8b933bcaca52c8f1e2b2f06997276c973b9c3ab436d` |
| `TogetherServer-win-x64.exe.sha256` | 667574343 | `4df7d37184734a12d5981b153e912863754ab284370e4446e2fe628418296bec` |
| `TogetherServer-Setup-win-x64.exe` | 667574394 | `b8243186e6e81421059503dd50632ac0ea44a9fe9dcf34b631265abab4ff0e22` |
| `TogetherServer-Setup-win-x64.exe.sha256` | 667574795 | `e151ad576351ffdb058b72b5b6816b64866675d7ef80a84e378066123b7cd22c` |

Anonymous public verification completes at
`2026-10-23T01:03:41.3097084Z`. All four browser-download URLs return the exact
uploaded bytes, matching API digests, sizes, the frozen manifest and adjacent
checksum contents. Both executables have numeric version `0.3.3.5` and remain
`NotSigned`; the portable product/source version matches the annotated tag.
The accepted package manifest SHA-256 is
`3c33d4a0bb83b1106eee216b0ebb63c961cccff6477ded576882efaf9b76e12c`.
No downloaded app or installer is launched during this verification.

Stable/latest remains `v0.3.3`, release `418702775`. Classification, inventories,
asset IDs, sizes, digests and download URLs for all eight prior releases in the
pre-publication snapshot remain unchanged. Prepared notes, API metadata,
artifact receipts, private logs and downloaded binaries stay ignored under
`local-data/release-0.3.3.5/`. The task guide and documentation index link this
preview and its shared-save retry instructions.
