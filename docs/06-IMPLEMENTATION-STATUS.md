# Current implementation and acceptance

This is the maintained status and delivery note for the current source.
Use the [user guides](README.md), [engineering rules](00-PRODUCT.md), and
[acceptance requirements](04-IMPLEMENTATION-AND-ACCEPTANCE.md) for behavior and
required checks. Previous delivery logs and release evidence remain in Git
history; published versions have their own
[release notes](https://github.com/daltonstates/TogetherServer/releases).

## 2026-10-28: shared transfers, local ports and update retention

- The Friend Play action now observes passive save-transfer progress every
  second. Receiving, verification and receipt stages disable **Host locally**;
  missing or stale progress also blocks the action. Original-Host downloads and
  peer pull/seed transfers share an exact-world activity projection. The final
  canonical launch check uses the shared membership mutation gate, so a
  transfer beginning after a UI/status check still prevents Start.
- Save downloads negotiate gzip; outbound peer seeding compresses only after
  the pinned peer advertises support. Compression is skipped when it grows the
  payload. Encoded and decoded request limits, exact chunk lengths, original
  offsets, signatures and file hashes remain enforced. Older peers retain
  uncompressed transport.
- Successful shared Start sends signed session metadata separately from save
  payload scheduling, and visible Host notices poll every second. Short peer
  request exchanges serialize within each world so concurrent status and save
  requests cannot invalidate one another's single-use challenges.
- **App Settings > Game ports** stores reusable per-game local choices with
  add/edit/remove controls. First shared preparation explicitly approves or
  chooses local ports; later preparation reuses them. Ordinary owner setup
  records its selected ports, and a one-time migration imports existing
  ordinary owner profiles. Received/rehearsal/authority profiles do not supply
  inferred approval. Deleting an approval stays deleted across restart.
  Prepared servers retain their saved ports until changed in local setup.
- Update checks and checkpoint creation prune only recognized flat app update
  artifacts. Completed matching downloads are eligible immediately; abandoned
  downloads/staging wait 24 hours. Exclusive leases protect active helpers.
  Retention keeps the newest two completed checkpoints plus current/pending
  and previous-EXE-matching recovery material. Unknown/linked artifacts stay
  intact. Each pass removes at most eight folders of each kind.

Before integration with published .22, source `661899d` used pinned .NET
10.0.301 and Node 24.14.1, locked dependency
installation/restore, full UI lint, TypeScript, production bundling, and a
bundled app build with zero warnings/errors. All 1,542 UI tests passed with no
skips. The full 75-group source run passed 73 groups; two changed fixture
expectations were corrected and both groups passed on focused rerun. The
synthetic source-link cases remain skipped because this account cannot create
symbolic links. Whitespace and desktop diagnostic isolation checks passed.

Validation was code-only on the owner's active desktop. No production update
cache was manually removed. Native/viewport review, an actual update handoff,
separate-PC save transfers, same-router port routing, real-game joins and saved
restarts remain external acceptance checks. Workers used the coordinator's
inherited model configuration without a model override.

The .23 prerelease integrates this work with the complete published .22 source.
Its candidate validation and hosted packaging are recorded separately below;
the preceding measurements describe the original feature commit.

## Owner-installed .21 physical results and unpublished performance follow-up

The owner manually installed .21 on both PCs and confirmed both unattended with
V1release stopped. The laptop's running EXE matches the exact public
`5f990e721d02d51745a569f8452dbb10f7f54d82` bytes; the main PC reports .21 through
its authenticated connection. Its EXE and filesystem were not independently
inspected. Only V1release was exercised. The test client issued no manual tunnel,
firewall, router, DNS, listener-policy, pairing, permission or installation
changes. Normal hosting-pipeline network effects were not independently diffed.

The existing laptop preparation was based on save 11; the original PC had
announced save 12. One explicit normal preparation verified and adopted 12 in
fresh managed storage, preserving the old continuation. It had no Receiving
phase. It took 144.821 s: Permission 0.667 s, PeerSync 9.074 s, LatestSave
2.674 s, Verifying 55.913 s, PreparingCopy 67.406 s and ServerSetup 8.454 s.
Those phase durations include their waits and metadata work; they are not pure
hashing, copying or network timings. Preparing genuinely newer progress is
separate from repeating preparation on an unchanged Start.

| Physical operation | Input/output save | Launch response | Request to real Ready | Native creation to Ready | Exit after Stop | New publication after Stop |
| --- | --- | --- | --- | --- | --- | --- |
| First Start and graceful Stop | 12 to 13 | 26.716 s | 61.167 s | 37.869 s | 4.086 s | 14.212 s |
| Independent saved restart and graceful Stop | 13 to 14 | 26.500 s | 48.779 s | 25.418 s | 4.093 s | 14.234 s |

Each Start and Stop was issued exactly once, with separate create-new sentinels.
Both Starts had no Receiving, PreparingCopy or ServerSetup phase. Exact recorded
native PID/start/path agreement and fresh trusted zero-player `ValheimLogReady`
observations established real Ready. Working-file verification ran once per
Start, taking 412 ms and 381 ms respectively. LatestSave took 7.472 s and
5.388 s; Verifying took 10.937 s and 12.675 s. Stop rechecked two distinct fresh
zero-player observations and the exact world/process identity. Publications 13
and 14 have exact parents 12 and 13. The approximately 10.1 s exit-to-publication
windows include capture, verification, publication and observation.

Independent read-only verification of each publication passes its signature,
branch proof, strict ancestry and current root/roster checks, plus all 315 file
hashes with zero mismatch. Both payloads total 119,452,903 bytes. Whole independent
proofs initially took 4.095 s for 13 and 2.776 s for 14, including metadata,
signatures, ancestry, payload hashes and receipt inspection. These are not pure
payload-hash timings. No game-client join or owner-recognized gameplay change
was observed.

**Timely ordered acceptance remains incomplete.** Save 13's twenty-minute
observation ending 2026-10-28 06:12:40 UTC did not verify an exact original-owner
receipt. Saved restart was then tested as a separate component because ordinary hosting does
not require an owner receipt; its sentinel explicitly records that the ordered
acceptance had already failed. Save 14's twenty-minute observation ending
06:36:02 UTC likewise did not verify an exact owner receipt. No manual Sync, transfer replay,
Start retry or receipt substitution was used. Existing immutable publications
and pending deliveries remain protected; the game is stopped.

A later independent check at **07:21:42 UTC** verifies an exact valid
original-owner receipt for publication 14, with its complete signed history,
current roster/root and all 315 payload hashes still valid. The one recently
authenticated eligible external peer has that exact receipt; a second eligible
peer remains pending. This supersedes the pending-14 status at the earlier
observation. The receipt carries no completion timestamp: it was verified at
approximately 66 minutes after publication, but its actual arrival time is
unknown. This does not turn the failed twenty-minute delivery window into a
pass or establish receipt-before-restart ordering for 13.

The original PC remains Connected and reports Offline through fresh authenticated
companion status. There are two eligible external peers; only one initially had
recent successful peer synchronization. The peer contact's later age does not
establish that a PC is offline. In particular, stale outgoing peer-contact
telemetry must not replace the fresh original-PC companion status. Outgoing
`SeedPaused` reports identify the pending publication while the receiver owns a
pull; zero submitted bytes there says nothing about separately served inbound
chunks. No receiver filesystem or exact receive cursor was independently read.

A 122.784 s stopped idle baseline used 28.766 parent-app CPU seconds on 16 logical
processors: 1.4643% of total machine CPU. Its six WebView descendants used
0.921875 CPU seconds, 0.0469%; the combined observed average was 1.5112%, with no
child churn or sample errors. It recorded 20 inventory scopes (3.364 s aggregate)
and eight peer-request scopes (10.887 s), with no payload/working verification or
signed-registry scopes. The normal preparation used 90.812 parent-app CPU seconds
over 144.856 s, 3.9182% of total machine CPU. Its zero PayloadVerification counter
does not mean zero hashing: direct preparation validation is outside that
transfer/receipt counter. Counter wall times overlap and cannot be added into
CPU time. These measurements do not establish a controlled improvement over .20.

A bounded 40 s managed thread sample used `dotnet-trace` 10.0.750501 against the
exact .21 process; capture and conversion took 44.239 s, during which the parent
app used 20.016 CPU seconds. It observed inbound ReadChunk handling, current
Access checks, roster-chain Ready/validation, canonical-key parsing/CNG imports
and monitor waits. Managed thread sampling includes waiting, safe-point bias and
missing native symbols; its percentages are not exact on-CPU attribution.
See [Microsoft's profiling guidance](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).
Raw traces stay in the existing owner-private diagnostic directory outside Git;
only allowlisted conclusions are retained with the physical evidence.

Five TCP connects to the existing configured DirectInternet companion route all
succeeded, median 27.911 ms. The advertised target is a public address; this is
not proof of the actual network path, TLS timing or wire throughput. At the
owner-reported 50 Mbps, 119,452,903 raw bytes take approximately 19.1 s before
protocol/verification overhead. Neither that theoretical bound nor connect time
explains the observed delivery delay. A tunnel is not established as the missing
requirement and was not created; any network change must be planned with the
owner when awake.

Signed metadata comparison of 14 with its exact parent 13 finds 310 unchanged
files totaling 119,289,182 bytes; five changed/added files total 163,721 bytes.
Installed .21 receiving code reuses exact publications but does not hydrate a new
publication's staging from matching files in another verified publication. This
is a source-confirmed opportunity to avoid redundant downloads, not a measured
wire-byte saving on either physical PC.

One combined **unpublished .22** follow-up contains:

- Keep the complete operation-local inventory during ancestry selection instead
  of rebuilding it per parent. Candidate filtering, exact parent verification
  and final current-scope fences remain unchanged; subsequent operations rebuild.
- Cache only positive canonical public-key parsing for exact strings in a bounded
  256-entry set. No key handles, signatures, grants, roster, expiry, revocation or
  permission decisions are cached. The original parsing predicate stays intact.
- Let actual transfer completion schedule its own frozen result exactly once.
  A canceled joining wait currently can read an old LastResult before its owner
  finishes and charge stale backoff. This is a proven source race, not a proven
  cause on the unobserved main-PC receiver. In-flight and preparing routes yield
  without spinning; completion and outer preparation release wake the existing
  scheduler without inventing a material-change generation.
- Reuse matching files from one authorized same-world prior save while filling
  disposable target staging for both pull and Seed. Match signed normalized
  path, length and SHA-256; hold relative unlinked source handles, hash the copy,
  preserve existing bytes and repeat current provenance/consent fences. This
  subset helper grants no receipt or Start proof. Complete target verification
  and exact receipts remain unchanged; local copy bytes are not network bytes.

These source changes are not installed and have no measured physical speedup.
Strict code-only solution compilation passes with zero warnings/errors. All 15
selected process-free groups have passed, including matching-file pull/Seed
wire-byte assertions, target receipt/pointer integrity, cancellation/prefix
resume, source/target link and reparse refusal, current permission/consent
changes, canonical-key cache safety, operation-local ancestry and completion-owned
scheduling. The first adjacent run had two failures, both retained: the priority
fixture used identical retained/latest/competing bytes while requiring chunk
traffic, and the unchanged bounded-history cursor replacement returned
UnauthorizedAccessException. The priority fixture now uses genuinely distinct
signed bytes with all ordering/chunk/receipt/budget assertions unchanged, then
passes. The cursor check passed a fresh-fixture recheck without a product change;
its original file-access cause is unknown. Neither pass erases the first result.
The final .22 version/bundle alignment, UI typecheck/lint/bundle and formatting
pass, and the final strict solution rebuild has zero warnings/errors. Hosted
Windows validation found a returning-owner discovery fixture failure at source
`298ab8c`: it required Chunk even though all later signed publications retained
the original bytes. The corrected fixture separately requires exact selected
save and Catalog for both shapes, Chunk for genuinely changed signed bytes and
zero Chunk for unchanged bytes. The complete bounded prepared-hosting discovery
group now passes locally, making 16 unique audited process-free groups passed
across this batch. No product checks, deadlines or permissions were relaxed.
The first correction build caught an invalid IDisposable assumption in the
fixture; its log is retained, the unnecessary using was removed, and the strict
focused rebuild has zero warnings/errors. The initial hosted failure is also
retained; corrected-source hosted validation remains pending.
No further physical cycle or installation is planned tonight. Delivery-time
attribution still requires the original PC's receive cursor,
accepted-byte progress, current errors and CPU work; the
[read-only main-PC handoff](development/main-pc-returning-host-check.txt) remains
the next diagnostic surface if delivery is slow or pending. Physical safe JSON and
measurement scripts are under ignored `local-data/physical-test-5f990e7/`.

## Integrated .21 returning-host and live report correction

The owner reported both PCs on .20: a remote hosting session did not update the
original PC's open Overview until navigation, app and game ports appeared to
disagree, and returning-owner Start initially refused the newest save before
later succeeding without a file picker. Ordinary Start selects an exact signed
verified save automatically; a file picker is not part of that operation.

Final application source: `5f990e721d02d51745a569f8452dbb10f7f54d82`.
Release: [v0.3.3.21 review preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.21),
published 2026-10-28T04:20:02Z, release `426257994`. Annotated tag
`da141016a05100fa810f91aa96f645141194e7e3` targets that application source.
All four anonymous public downloads match the reviewed bytes and server digests.
All 35 prior releases and the four .20 asset identities/digests remain intact;
stable/latest remains `v0.3.3`.
The [exact-source Windows run](https://github.com/daltonstates/TogetherServer/actions/runs/40628192561)
has completed: 16 successful jobs, one Desktop notification block and the
expected dispatch-only skip. All 27 broader gates pass, including Minecraft
12/12, Companion 46/46 and Shared Worlds 11/11 with no recovery scenario skipped.
All 94 source groups, eight packaged API journeys and 1,597 UI tests in 109 files
pass. Both ordinary automatic-hosting journeys pass all nine stages on unchanged
candidate bytes, with one Friend Start and one returning-owner Start each, no
repeated preparation and no diagnostic replacement. Pairing/both-ended UX,
browser, six compatibility baselines, core, rehearsal and UI review pass.

Desktop passes 12 required cases and 14 window-control cases. Windows QuietTime
blocks notification delivery/click; this is not fully green or full acceptance.
The separate production-mode startup, desktop and update handoff gates pass.
All six installer smoke cases and three actual wizard choices pass. The reviewed
portable is 66,277,099 bytes, SHA-256
`48d75541db7fa461f7606ef4dd0b1355a7ae8b3fa4d9e778f1d3055f57d4f4ec`;
the installer is 60,735,695 bytes, SHA-256
`24da936cac2abc164b1e87d456868d6ff6cca05547135b5583cf1eb4792560f0`.
Both are unsigned. Package provenance, embedded UI, sidecars and artifact digests
have been reviewed without launching either executable locally. Publication is
limited to a review preview; no .21 physical cycle or measured CPU/transfer
improvement is claimed. Earlier candidate failures below remain part of the
record.

The current .21 candidate keeps the original world as the sharing/report scope
when a prepared continuation is selected. Local run controls remain on that
concrete continuation. The existing read-only poll now remains mounted on the
original peer group across Preparing, Hosting and Stopped reports. Stopped
reports include their date and uncertainty about other PCs and save delivery.
The displayed pinned HTTPS address is explicitly the TogetherServer app route;
the game's address/port comes from fresh authenticated Host status after
connecting, through Join. An app route such as 5132 does not establish the
game port, and a signed Hosting report does not establish live game readiness.

Original Start previously treated a known newer publication without a complete
local payload as generic history/permission review before entering common
catch-up. The candidate distinguishes pending progress only after verification
of the available local payloads and their unambiguous ancestry. It grants no
Start source: the existing bounded recovery transaction must still receive and
verify the required history and repeat current permission, consent, expiry,
revocation and exact-source checks. Missing copies receive a specific pending
message. Corrupt copies, incomplete required ancestry and known forks retain
their refusal. Unknown remote histories may be retained, then still fail the
all-heads descendant selector. There is no older-save fallback or automatic
merge, and planned-move requirements remain stricter.

An authenticated incoming chunk request is only a scheduling hint. Repeated or
regressing positions now cannot renew its bounded ownership indefinitely.
Bounded high-water marks survive expiry, so replay cannot immediately recreate
the hint. An active pull of the newest publication keeps that exact save pending
instead of falling through to an older acknowledged copy. Incoming catch-up
does not replace the pending publication's displayed totals/cursor; no bytes or
receipt are invented. The existing single transfer flight, current request
authentication, pinned connection reuse, adaptive backoff and exact signed
receipt checks remain in force. This source correction addresses a provable
liveness risk; it has not been established as the cause of the physical delay.

Local validation is code-only. Strict Release solution compilation passes with
zero warnings/errors. The 119 AppFlows checks and 47 SharedHostNotice checks
pass, including unchanged-selection Preparing/Hosting/Stopped updates and the
app/game port distinction; typecheck, production bundle and lint pass. Nine
focused generated-file/fake-HTTP backend groups pass, including retained
historical reuse, signed-anchor/missing-copy routing, current access refusals,
publication priority, pull-hint expiry and scheduling. The final code-only
rebuild has zero warnings/errors and formatting passes. Final hosted Windows CI
results are recorded above. No general fixture, native/browser/installer or
production hosting operation was run on this active laptop for this follow-up.

The first focused backend pass had five successful groups and one discovery
assertion still expecting the old generic review code for a missing newer save.
The revised check distinguishes pending-copy from fork refusal and retains the
same no-source, metadata-only, no-run and unchanged cached-payload assertions.
The failed log and scratch data remain preserved for comparison.

Before publication, caller review found that an absent original publication
record could still reach a legacy pristine-world fallback after required
catch-up failed. The first .21 build, source
`70b3db736193635c205ef08d082539d2f5b14994`, is held from release despite its
passing focused/source/API checks and ordinary hosting journeys. Its
[CI evidence](https://github.com/daltonstates/TogetherServer/actions/runs/40613895713)
is retained. The integrated correction removes the post-recovery fallback and
reserves first Start for an explicit no-known-publication outcome. Known signed
progress without any local copy enters bounded catch-up without a Start source;
corrupt or ambiguous original publication metadata stays refused. Generated
fake-driver coverage checks zero validation/setup/Start after unavailable
catch-up, no-copy routing, corrupt records and genuinely pristine first Start.
The corrected source passes both focused discovery and adopted-hosting groups,
including those new guards, and strict compilation with zero warnings/errors.
All 16 functional/packaging lanes of the first CI pass, including 94 source
groups, eight API journeys, 1,597 UI tests and the broader fixtures; its Desktop
lane has 12 required passes and the explicit QuietTime notification block.
Those results do not substitute for final-source CI. No .21 tag/release or owner
installation has occurred at this checkpoint.

The subsequent `3ae1d0dd43b3b1937ff591129dec92fd70a868fe`
[CI run](https://github.com/daltonstates/TogetherServer/actions/runs/40617364954)
passes all 94 source groups, eight API journeys and 1,597 UI checks. Its first
ordinary Friend preparation returns `LatestSaveChanged` before creating a
local profile/branch; the separate diagnostic journey then passes all nine
stages. That diagnostic pass does not replace the failed ordinary case, and the
changed observation component is unknown. The strict freshness fence remains
unchanged. A dispatch-only hosted workflow can recheck the exact original
candidate, with pinned source/run/hash provenance, two fresh ordinary journeys
and separate failure evidence. It neither rebuilds the app nor retries Start
inside a journey or weakens a deadline/permission/freshness check.

That run also fails Minecraft's pre-game-failure retry with a null-reference
launch error, followed by four fixture-file lock failures. Source review finds
an immediate game `MainModule!` dereference where Windows may temporarily
return no module; this is a strong source match, not a retained launch-stack
proof. The integrated correction uses the existing bounded identity reader for
that exact game PID/start/path and keeps ambiguous runs unresolved. Generated
observations cover delayed module availability, identity mismatch, exit and
deadline without launching a process. They pass locally; strict compilation has
zero warnings/errors. The fixture's retry assertion now lies inside exact-record
cleanup, so a failed assertion cannot bypass cleanup and cause later file-lock
cascades. Native verification remains hosted. Both earlier .21 packages are
withheld; no .21 release, tag or installation exists at this checkpoint.

The `8539a13c401a20918b041bc4c2621918a62ccbe8`
[run](https://github.com/daltonstates/TogetherServer/actions/runs/40620498133)
passes all 94 source groups, eight API journeys, 1,597 UI tests, both ordinary
nine-stage hosting journeys without diagnostic replacement, pairing and all 12
Minecraft groups. Its broader lane then exposes an overbroad pristine check:
after an explicit owner-reviewed source change signs a new group, the retained
old-group pointer makes the new group's first Start return
`SharedSaveHistoryNeedsReview`. This candidate is also withheld.

The correction scopes unpublished-source proof to the exact current binding and
signed roster. It reuses the publication path's existing previous-group pointer
verifier, fences the durable profile before/after the read, and requires absent
current-group metadata. A healthy retained old group can coexist with a
legitimate first Start of the explicitly reviewed new source; malformed or
unpointed current-group publication state and known-progress refusals remain
protected. A generated regression retains old manifest/payload/roster bytes,
performs the explicit source review and verifies new-group publication 1 without
touching those old files. Strict compilation passes with zero warnings/errors;
focused verification passes, including the reviewed-source regression.

The `a1aa73ea08e37e47342ac9b3023a1e7e2118809f`
[run](https://github.com/daltonstates/TogetherServer/actions/runs/40624449500)
passes all 27 broader gates, including the reviewed source rotation, Companion
46/46, Minecraft 12/12, all 94 source groups and 1,597 UI tests. Its first
ordinary hosting journey passes all nine stages; the second and the separate
diagnostic preparation return `LatestSaveChanged`. The diagnostic contains two
successful selections of the same single canonical copy before the trailing
observation changes during final verification. The changed component is still
unknown. This candidate remains withheld.

Recovery-source preparation now permits one bounded revalidation when only copy
evidence changes and each newly advertised/received hash is in the exact
selected save's already verified signed ancestry. Root, roster, device, consent,
sharing, peer identity/access/conflict, branch and session fields must stay
identical. The retained source is verified again, current permission and strict
all-head selection are repeated, and the fresh full hash must remain stable.
Unknown hashes (checked per peer), new heads, authority/consent/session/conflict
changes, source tampering or a second change still refuse. The original and
prepared-Start full fences retain their semantics. No discovery, network,
retention, setup or Start instruction is replayed. Stable-path permission is
also checked after selection to cover expiry during verification.

Strict code-only compilation has zero warnings/errors. Nine unique focused
backend groups have passed across the integrated work; final discovery,
freshness and recovery-head groups pass, including signed copy-only positive
cases and unknown-copy, new-head, grant, conflict, tamper and second-change
negative cases. They assert no extra transfer/retention or hosting mutation.
Fixed categorical diagnostics identify future refusal boundaries without paths,
identities or save hashes. Final hosted verification passes the functional gates
recorded above. No .21 installation or physical test has occurred.

### Separate physical .20 observations

These are read-only observations of the running .20 app, not .21 acceptance.
The laptop EXE matches the published .20 SHA-256. Main-PC authenticated metadata
reports .20; its installed files/native processes were not independently read.
At 2026-10-28 00:26:54 UTC, publication 11 had valid signed metadata, branch
proof, strict retained chain and current permission, with zero exact receipts.
At 00:52:34 UTC, the unchanged private observer verified one exact valid receipt
for 11 from the owner domain. Payloads were not inspected by that metadata-only
observer. Intermediate signed metadata reports owner publication 12 with base
and parent 11 and a Stopped session based on 11. This is consistent with a
continuation, but does not independently establish native Ready, loaded game
contents or a completed physical acceptance cycle. The receipt's exact arrival
time is unknown between the observations. Earlier faulty PowerShell date/key
and nested-variable comparisons are marked superseded/unreliable in private
evidence and must not be interpreted as damaged history.

One uncontrolled 148.091-second diagnostic interval used 29.890 parent-app CPU
seconds on 16 logical processors: 1.261% of total machine CPU. It excludes game
and WebView child processes and is not a healthy-idle or transfer benchmark.
It recorded 21 inventory scopes (4.170 s aggregate), eight peer-request scopes
(8.738 s), and no signed-registry/payload/working verification scopes. The
dominant transfer bottleneck and a measured .21 improvement remain unknown.

Only the laptop Codex host is connected here. The owner-authorized
[main-PC read-only diagnostic handoff](development/main-pc-returning-host-check.txt)
is available for the other PC; no other chat was messaged. Its earlier pending
receipt statement is timestamped and superseded by the later receipt above.
Evidence is retained privately/under ignored
`local-data/hosting-return-followup/`. Existing worlds, histories, pairing and
the installed app are preserved. Owner manual installation and fresh
unattended/stopped confirmation remain necessary before a new physical cycle.
Exact reachable-recipient delivery, saved restart and separate preparation,
transfer, verification, game startup and CPU measurements remain acceptance
requirements for the new candidate.

## Integrated .20 historical reuse, delivery and idle correction

One integrated `0.3.3.20` candidate contains the historical-copy correction,
new-publication delivery priority and bounded idle work. Packaged app source:
`fcfa7b6972c1722e9356941e7b743d7c92c850f2`.
Release: [v0.3.3.20 review preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.20),
published 2026-10-27T23:59:08Z, release `426118196`.
Annotated tag `e422a967c6e55ef821d09759e329839fda636110` targets the app source above.
CI: [40601918559](https://github.com/daltonstates/TogetherServer/actions/runs/40601918559).
Final review: 16 successful lanes; all 27 broader fixture gates passed. Desktop
reports the explicit notification block below. No unexpected current-lane failure
remains; this is a review preview and not full physical acceptance.

An exact offered signed descriptor now finds a retained complete local save in
the known separate-copy, received, received-branch, retained-history or numbered
original-publication locations. This does not enumerate every historical
payload or advertise old saves. Current protected source/head, root, key, roster,
authority, expiry, revocation and receiving consent remain required. A new exact
receipt follows held full-payload hashing and strict bounded signed ancestry;
the commit repeats current scope/source and ancestry checks. A damaged or
ambiguous historical source fails closed. Reuse neither moves the current head
back nor creates another payload copy. Missing receive pointers use normal
fully verified completion; present invalid pointers remain refused. An already
confirmed exact receipt poll produces no new Start proof and skips repeated
payload hashing, receipt writes and delivery.

The existing per-world transfer flight gives the newest locally produced
publication a bounded ten-second outgoing opportunity before incoming catch-up.
Paused/stalled recipients yield the remaining budget to legal incoming work.
One operation-owned outgoing catalog serves priority and fallback work; current
source/access fences remain fresh at use. Only the exact authenticated
recipient's current signed receipt establishes delivery. Forwarded receipts and
same-number other-hash receipts cannot stand in for it. Accepted outgoing
progress survives later incoming observations. Numbers order delivery work;
signed ancestry selects a history. Competing histories and stricter planned-move
requirements remain protected, and offline recipients stay pending.

Idle scheduling compares a compact material heartbeat projection rather than
serializing and sorting unchanged snapshots. Stop notices wake replication
without repeating enrollment. Peer-owned and not-yet-due legacy work avoid
redundant protected consent reads; actual operations still validate consent.
Two prompt stalled opportunities lead to existing adaptive backoff up to five
minutes; accepted bytes or an advancing outgoing cursor remain prompt. Replayed
cursors do not renew fast retries. Equal tray summaries avoid repeated desktop
dispatch. Existing pinned connection reuse and certificate/current-request
checks remain intact. Heartbeat and lifecycle/player observation intervals
remain five and three seconds. Receipt payload-verification measurements now
use one scope that excludes network delivery. Physical CPU savings and the
dominant transfer bottleneck have not been established.

Local evidence is code-only: UI typecheck/bundle, strict compilation with zero
warnings/errors, formatting/lint, and selected generated-files/fake-HTTP groups.
All general process/native/browser/installer tests ran on hosted Windows CI.
The exact packaged candidate passes all 94 source groups, eight packaged API
journeys and 1,582 UI tests in 109 files. Automatic sharing passes both ordinary
journeys with exactly one Friend Start and one returning-owner Start each;
both first preparations return
`LocalHostingPrepared`. Pairing/both-ended UX, six compatibility baselines, core,
rehearsal, UI review and installer/package lanes pass. Browser passes 14 journeys
and explicitly leaves three external/native scopes unexercised. Its first Start
returns `ValheimStarting`, then the exact synthetic profile reaches real Ready.
Desktop passes all 12 independent required cases. QuietTime blocks actual OS
notification delivery/click; development staging explicitly defers Windows
startup to the separate production-mode gate. This is not fully green or full
physical acceptance.

Earlier failures remain recorded, separately from the final candidate results:

- Source `154fdd1`, run 40597526187: 93/94 source groups passed. The coalescing
  test's aggregate count omitted the new paused priority Seed before its held
  Catalog. Corrected checks retain consent denial, one Catalog, zero incoming
  chunks, unchanged request/challenge counts for joined/canceled preparation,
  original deadlines, proof freshness and cached-copy integrity. Desktop also
  rejected a disposable process identity before maximized API/window readiness;
  four later cases were skipped. Its exact cause is unestablished and it did not
  repeat in the following two native runs.
- Source `9449d13`, run 40600312679: 94 source groups and eight API journeys
  passed. Browser's saved screenshot showed a pre-launch port refusal, which its
  old harness mislabeled a Ready timeout. The exact port owner/socket refusal
  cause remains unknown. Both ordinary and diagnostic automatic-sharing runs
  rejected first Friend preparation; their returned preparation codes were not
  retained. Two original publications succeeded and no Friend/returning-owner
  Start was attempted. Host-only selection diagnostics do not explain that
  refusal. Fresh-run passes do not establish these failures' root causes.

Safe first-Prepare and first-Start diagnostics now preserve fixed returned codes
and status flags without retries or weaker readiness/permission/port guards.
Waiting for the Start acknowledgement does not extend the original Ready
budget. A subsequent diagnostic-only field correction uses `runOperationId`;
the final CI report's older `operationRecorded=false` queried `operationId` and
must not be interpreted as an absent run identity. Existing archived-run
identity assertions passed independently. That one-line correction passes local
syntax/lint and does not change the packaged application or functional tests.

Portable: 66,267,081 bytes, SHA-256
`156f095d3529cacd545cc2dc37e9bc149fc71b2c6036b95d9fb338f6f322b457`.
Installer: 60,731,353 bytes, SHA-256
`6e147b3c983a0ec3125a53e037d02984b271085e5b6b88cf0dfb5458da0a1fd0`.
Both EXEs are unsigned. Package provenance and six installer smoke cases plus
all three wizard choices are verified. All four public assets were anonymously
downloaded and hashed, matching local bytes, sizes and GitHub server digests.
All 34 earlier release IDs remain, all four .19 asset identities/digests are
unchanged, and latest stable remains `v0.3.3`. Public verification is recorded
at 2026-10-28T00:00:29Z.
Evidence: `local-data/hosting-history-reuse-20/`, with final candidate artifacts
under `candidate-fcfa7b6/`; all earlier failed logs/artifacts are preserved.

No production data, installed app, pairing state or real world was modified by
this code task. Owner manual installation on both PCs is required. Before a
new physical V1release-only cycle, obtain current unattended conditions and
fresh main-world stopped evidence. Acceptance still needs unchanged prepared
save reuse without transfer/copy/setup repetition, real Ready, graceful Stop,
new verified publication, exact reachable-recipient receipts and saved restart,
with preparation/transfer/verification/native-start timings and CPU measured
separately. The .19 physical cycle and delivery failure remain distinct below.

## Physical .19 V1release cycle, 2026-10-27 EDT (not accepted: delivery)

The owner reported both PCs updated to .19, confirmed the laptop unattended and
the main world stopped, and authorized this V1release-only cycle. The running
laptop EXE independently matched public SHA-256
`e4b6cde759eb770f2fd7553ae94f22b75f0f025ef4bd5aa1b3030ebff86d1bea`
and source `36c4be272ace4da0c9ae2ad379d9e77430c94698`. Authenticated main-PC
metadata reported .19; its EXE/filesystem were not independently inspected.
Initial C: space was about 589 MiB, below the app's reserve. Automatic approval
review rejected the owner's subsequently authorized obsolete-artifact deletion
command as `blocked by policy` before it ran; no files were deleted by that
command. The owner freed space manually. Recheck before Start found about
50.7 GiB free, both relevant world rows Offline and no native Valheim server.
The unchanged existing private observer was reviewed and hash-matched; older
observer outputs were preserved before its normal proof writes. No production
LocalData inspector was constructed and no signing/pairing state was reset.

Exactly two Start and two graceful Stop requests were issued, each with a
CreateNew sentinel. Both Starts selected the exact prepared branch and entered
the common local pipeline. Neither recorded Receiving, PreparingCopy or
ServerSetup. Each reached real Ready with a separately matched recorded native
executable/start identity and fresh trusted zero players. Each Stop was preceded
by two distinct fresh zero-player observations plus exact native/world identity.
The second Start loaded newly verified publication 9, so saved restart passed
without an owner receipt prerequisite. The final Stop left V1release Offline.

| Observed Start timing, seconds | First, input 8 | Saved restart, input 9 |
| --- | ---: | ---: |
| Permission | 1.048 | 1.141 |
| Signed discovery/selection | 6.507 | 5.955 |
| Verifying phase | 16.585 | 12.751 |
| Connection announcement | 0.402 | 0.204 |
| Launch-request phase | 10.993 | 4.236 |
| Response first observed | 37.806 | 26.630 |
| Request to first real Ready observation | 81.313 | 52.442 |
| Exact native creation to first Ready observation | 46.738 | 28.303 |

The held working-copy proof ran once per Start: 3.238 s initially and 0.392 s on
restart. It is only part of Verifying. Phase timings end at the launch result;
they are not game startup time. Response/Ready observations use two-second
polling plus read latency; native creation timestamps are exact. These are
physical observations under current desktop/background load, not a controlled
before/after speed benchmark. No setup, Prepare, manual Sync or Start replay
was used to advance the operation.

| Observed Stop/verification timing, seconds | First, 8 to 9 | Final, 9 to 10 |
| --- | ---: | ---: |
| Response first observed | 20.313 | 16.244 |
| Request to native exit first observed | 5.126 | 4.081 |
| Request to new publication first observed | 20.380 | 16.281 |
| Exit to publication first observed | 15.254 | 12.199 |
| Independent full verification observation | 3.609 | 4.436 |

Both new publications contain 315 files / 119,452,903 bytes and pass all payload
hashes, signature, branch, current root/roster and strict ancestry checks. Exact
parents form 8 -> 9 -> 10. Five file hashes changed at each Stop. Exit-to-
publication combines capture, verification, publication and observation; it is
not pure hashing. The independent verification observations also include
metadata/roster checks and proof overhead. Stop exit observations use one-second
polling plus reads. Actual game joining/gameplay was not tested.

| TogetherServer process CPU window | Wall s | CPU s | Average machine capacity (16 logical processors) |
| --- | ---: | ---: | ---: |
| Stopped preflight, before disk cleanup | 61.262 | 9.203 | 0.94% |
| First Start through Ready | 81.344 | 41.406 | 3.18% |
| First Stop through publication | 20.401 | 15.765 | 4.83% |
| Post-Stop automatic replication observation | 352.253 | 194.016 | 3.44% |
| Saved restart through Ready | 52.464 | 29.313 | 3.49% |
| Final Stop through publication | 16.293 | 11.782 | 4.52% |

CPU percentages are process CPU/wall deltas divided by 16; game and WebView
child CPU are excluded. Windows task-group CPU may therefore differ. The windows
include normal background work and diagnostic reads, are not per-world CPU
attribution, and overlapping stage wall times cannot be added as CPU time. The
preflight sample occurred with low space, not in a fully synchronized healthy
idle state. No prior physical CPU baseline exists for a reduction claim.
Native game CPU from creation to the Ready observation was 34.625 CPU-seconds
initially and 25.516 CPU-seconds on restart. Those game windows differ from the
app windows, so their percentages should not be added directly.

**Automatic delivery failed acceptance.** Publication 9 had zero exact signed
recipient receipts after 352 seconds despite a reachable authenticated owner.
At the final 17:51:07 EDT observation, both 9 and 10 still had zero valid exact
recipient/owner receipts under the current roster. The owner's connection was
fresh at the 17:46 observation (4.38 s), and its route was still serving
authenticated chunks in the final sample; the other eligible peer had no current reachability evidence and
remains pending. Input publication 8 now has a valid exact owner-domain receipt;
its completion time is unknown and that receipt does not cover 9 or 10.

The concrete coordination problem is unnecessary historical receive work.
After local publication advanced to 9, the OriginalHost route repeatedly pulled
publication 8 in canceled/resumed passes. Its same-branch signed staging history
matches the local historical 8's exact version, signature, parent, scope and all
file identities. The complete local historical 8 was rehashed independently:
315 files, zero mismatches (1.080 s enumeration/hash observation). The matching
partial had 44 staged files totaling 18,736,954 bytes after 352 s, including an
incomplete file; this is not a percent-complete or link-throughput measure.
The active requested file was share-locked in that exact partial and its writes
were current. The final route sample was still pulling 8 at requested index 135
and offset 524,288; this cursor is not a completion percentage.

During that 352-second window, process-wide counters added 122 signed peer
operations / 233.925 s peer-request wall time, 34 inventories / 11.917 s,
zero wire-gate wait and zero completed received-payload verification scopes.
This identifies redundant historical replication and repeated signed exchanges
as the observed obstruction. Per-exchange remote CPU, network latency and link
throughput were not isolated. In particular, zero client-offer submitted bytes
does not prove zero bytes served to incoming pulls; an earlier commentary claim
that the new save had not been sent was narrowed accordingly. Exact receipt
absence, rather than that byte counter, determines the failed delivery stage.

Read-only source review explains the reuse coverage gap: LocalSavesAsync includes
only a separate branch's published head (or retained base fallback). Once head 9
exists, complete historical 8 is omitted before ExistingCopy's exact identity
lookup. The downstream 32-entry catalog cap is not the cause of that omission.
A bounded exact historical-copy lookup with all existing provenance, signature,
ancestry, consent and commit fences needs coverage before another acceptance
candidate. No code or installed app was changed during this physical cycle.

Safe evidence is in `local-data/physical-test-36c4be2/`: runtime/prepared proofs,
two Start/Stop sentinels and results, phase/work samples, full save proofs,
historical-copy comparison and final exact-receipt/state verification. No IDs,
keys, private routes/paths or payload contents were exported. Final state:
V1release Offline, main world Offline, zero native Valheim servers, Host mode
restored. Delivery continues under the app's existing policy; no sharing policy,
listener, port or permission was changed to force progress. The .19 CI results
below remain synthetic/hosted evidence and do not override this physical failure.

## Integrated hosting correction, 2026-10-27 EDT (physical acceptance pending)

Current candidate source is `36c4be272ace4da0c9ae2ad379d9e77430c94698`, version
`0.3.3.19`, in [Windows run 40576795814](https://github.com/daltonstates/TogetherServer/actions/runs/40576795814).
Strict local compilation is clean. Hosted Windows has passed all 91 source groups,
eight packaged API journeys, pairing/two-ended UX, automatic sharing, Core,
rehearsal, six compatibility lanes, UI/access captures and installer packaging.
The full Existing lane passed 1,582 UI tests, 115 general checks, all eleven
Shared Worlds groups with zero recovery skips, all 46 companion groups and the
remaining packaging/isolation/update gates. Its earlier quota failure did not
recur. Browser timed out waiting for synthetic Valheim readiness with no captured
browser/request error; its cause is unknown. One fresh-runner rerun of the exact
same EXE passed (job `122461937111`, attempt 2), including synthetic lifecycle,
logs, sessions and Friend signed delivery. Attempt 1 retains 15 successful lanes
and the Browser/Desktop failures; across the retained attempts 16 of 17 lanes
pass, while Windows QuietTime still blocks Desktop notification acceptance.
Both EXEs and their
checksum sidecars were statically verified against the exact-source package.
The [v0.3.3.19 review preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.19)
is published at the source above (annotated tag
`cfb207335b4bdd891b3a2fcd6f406e24c5968ba6`, release `425975041`). Both EXEs and
both sidecars were independently downloaded anonymously and hashed. All 33 older
release IDs remain; stable is still `v0.3.3`. Owner installation and subsequent
physical measurements are recorded above. Full acceptance remains false because
automatic delivery is still incomplete; the earlier unmeasured physical claims
below are historical observations before that cycle.
The paragraphs below retain the earlier integrated-candidate attempts and fixes;
their intermediate packages are not separate releases.

The architecture-reset handoff and all pre-existing changes were reviewed before
reuse. This candidate builds on .18 rather than replacing its trust, retention or
common HostManager launch boundary. Prepared Start first checks current scoped
signed permission, discovers only the requested world's signed metadata within
the existing budget, then holds the selected immutable source. Host and Join now
reuse one operation-owned full working-save verification through the final launch
fence. Working handles are released immediately before the game needs write
access; the signed source remains held. Current scope, source, complete layout,
setup, ancestry, process identity and ports remain final guards. Readiness and
player evidence are observed afresh for every run. There is no permanent setup
or authorization cache and no original-Host round-trip requirement.

Peer replication retains its existing single-flight boundaries. One background
owner wakes after verified publication, new completed receive, changed signed
metadata/contact and an authenticated reconnect after inactivity. It discovers
unchanged routes about once a minute and backs off failed routes with bounded
jitter toward five minutes. Resumable work remains due promptly, bounded group
and route rotations stay fair, and removed routes are pruned. Heartbeats and
lifecycle/player observation intervals are unchanged. The legacy background
receiver yields for the same enrolled world only when that Host advertises the
peer protocol; older companion versions retain their receiver.

Serving an authenticated Chunk records only a bounded scheduling hint for that
exact world/save/recipient, deferring a competing Seed while that pull progresses.
It grants neither access nor receipt. Current exact responder receipts suppress
repeat offers. Publication, active bytes and exact receipts remain separate;
offline recipients stay pending. No wire schema changes and no relaxation of
planned-move final-save/authority/receipt rules are introduced.

Chunks retain bounded capped-catalog source hints instead of scanning all vaults
and history on each chunk. Every use rechecks current signed scope, protected
provenance, authority, consent and source pointer, before and after bytes. Existing
full signed-file/per-chunk digests and final whole-payload verification remain.
Registry parsing/signature results are reused only for identical protected bytes;
current expiry, trust floors, revocation and consent are still evaluated per
operation. Protected no-op challenge-cursor writes are skipped. Pinned clients
are bounded by exact origin+pin, with idle eviction, active leases, no shared
mutable headers/cookies/proxy/redirects, certificate-validity checks and fresh
single-use signed challenges on every request.

Hidden-document UI polls suspend and resume without overlapping ignored-abort
requests. Directory reads share the snapshot cycle, while later companion/port
diagnostics cannot delay run/player snapshots. All polls remain read-only.
The fixed local-header-only GET `/api/local/world-peers/work` returns process
CPU time/uptime plus counts and elapsed wall time for inventory, registry
verification, signed peer requests, peer-gate waits, received-copy verification
and working-save verification. Verification includes hash/lease/path overhead,
and received-copy timing includes promotion; it is not pure hash CPU time.
CPU utilization needs two sample deltas; wall
durations can overlap. Existing hosting phases and transfer byte/elapsed records
separate preparation/discovery/verification/launch-request work; exact native
creation-to-Ready still requires fresh process/readiness observation. These
counters do not establish the physical bottleneck or a measured speedup.

The focused UI checks pass 126 tests, and strict solution compilation passes with
zero warnings/errors. The initial source run passed eight of ten selected groups;
the two older fixtures were updated for exact-acknowledgement skips and the new
scheduler parameter. Follow-up caught a new permission precheck using a protected
load that could quarantine a malformed key; it now inspects the retained key
without recovery/mutation before discovering peers. A flaky synthetic registry
mutation used string replacement against JSON-escaped base64; it now changes the
parsed signature and proves equal-length bytes actually differ. These failures
are preserved. Corrected focused runs pass all ten distinct selected groups,
including exact owner/online delivery, forwarded-receipt isolation, prepared
source reuse, held working proof/concurrent refusal, chunk provenance, pinned
connection reuse, scheduling invalidation and nonmutating bounded discovery.
The separate native namespace-substitution case is registered for hosted Windows
CI and was not run locally. Exact-source hosted Windows CI remains pending.
The first integrated source `17f4b04` is retained in Windows run `40551688495`.
Its packaged Core remote lifecycle passed 14 groups, then rehearsal, automatic
sharing and pairing all exposed delayed automatic enrollment: an empty registry
could idle for a minute after pairing or enabling sharing, beyond their bounded
membership wait. This is a scheduling invalidation regression, not acceptance.
Canonical background heartbeat snapshots now wake enrollment on connection,
assignment, sharing-policy or reconnection changes; timestamp/player observations
do not. The existing sharing-enabled/disabled notice is scoped to assigned Friends
through the existing activity protocol. No new request or trust field is added.
Current signed enrollment/consent checks still decide permission. The earlier
candidate assets will not be published. Exact corrected-source CI is required.
That first run finished with 11 successful and six failed lanes. All 89 source
groups and the eight packaged API journeys passed, including actual native
namespace substitution with no substitution skip. Existing stopped at two UI
failures (1,578/1,580 passed): scope cleanup did not abort the most recent
completed signal, and a log-reader test expected a second request before its
abort-ignoring old reader settled. Cleanup now retains/aborts that signal; log
scope changes clear old content before paint and polling/manual refresh share
the same pending reader. Tests require eventual current data and reject old
errors/content after settlement. The corrected three UI suites pass 115 tests,
the enrollment-wake regression passes, and strict solution rebuild is clean.
The independent Desktop lane retained the OS QuietTime notification block.
The second integrated source `8250c1a` is retained in Windows run `40554881714`.
Core, rehearsal, automatic shared hosting and browser journeys passed. Pairing
still exposed a missed invalidation: the simple hosting toggle committed its
signed roster through a different setter than the older save-sharing action.
All three policy setters now publish the same assigned-Friend notice and local
wake only after their signed policy commit. The focused hosting-policy and
scheduling groups pass, with zero-warning strict compilation. This remains one
unpublished .19 candidate; no earlier integrated build is being released.
The second hosting-wizard capture passed its functional timeout/navigation
assertions, but recorded one canceled read-only world-peers GET in its narrow
150% text case (107/108 captures). The artifact does not establish the abort's
cause. No network-error guard is relaxed; the unchanged UI will be checked again
with the corrected backend. Desktop still reports QuietTime. The second Existing
lane was still running at this observation. Exact final-source CI is required.
The second Existing lane subsequently passed UI/source/API/core/compatibility
work through the 4,102-version case, then failed its first Shared Worlds group:
neither Friend observed the first published save within the bounded wait.
The third source `81f43ef`, run `40557139684`, passed the unchanged wizard and
delivery captures, Core, rehearsal, browser and all six mixed-version lanes;
automatic shared hosting and LAN pairing still missed automatic membership.
These failures remain blockers and its packaged .19 bytes are not published.
Source review found discarded incomplete roster-refresh results followed by
the new idle delay, and a separate missing receive wake: the assigned-Friend
post-Stop notice was ignored when a receiver had no incoming peer route.
The old five-second retries had hidden both coordination gaps. Incomplete
enrollment will retain a bounded prompt retry budget, and a new Stop notice will
wake signed discovery once. Neither notice nor retry outcome grants permission.
Peer completion also needs to invalidate the legacy transfer health epoch so
an old failure cannot cover the current verified copy or return on late completion.
These corrections pass strict solution compilation with zero warnings/errors and
four focused generated groups: offline-owner replication, completed owner/online
delivery, adaptive coordination and canonical peer/legacy status invalidation.
The enrollment regression actually advances a signed owner roster between this
PC's current/page reads, retains no first-pass group, then completes through the
scheduled pending retry without a new heartbeat wake or game process. It also
checks transient roster publication and retry exhaustion/deadline. The status
regression performs a signed fake-HTTP canonical receive and rejects late old
progress/completion, wrong scopes and failed commits; receipt-only commits leave
newer progress intact. Neither regression establishes physical performance.
The third Existing lane also failed at the same first-save wait after its prior
source/UI/core work passed. No earlier candidate assets are approved for release.
The fourth source `e075c40`, Windows run `40560884826`, passed both complete
nine-stage automatic-sharing journeys, including listener-less receive,
outbound-only returning-owner delivery and latest-save owner Start. UI passed
1,582 tests, and Core/rehearsal, all six compatibility lanes, wizard/delivery
captures and source groups passed. LAN pairing still missed automatic membership;
loopback passed, and the first Shared Worlds save wait still failed. The retained
LAN report lacks the failed group flags/lookup outcome, so its exact cause is
not established. A deterministic source gap was separately identified: a fresh
policy observation could not start registration while an older peer pass was
active (up to 45 seconds). Registration now retains its one bounded flight and
generation independently of payload work; a signed held-response regression
requires enrollment before that response is released. Pending registration
retries now use that same independent deadline owner. Retry-only wakes do not
advance transfer generation. The held-response regression covers normal and
transient roster-unavailable refreshes, preserving one registration flight,
the 15-second pass budget and the existing eight-attempt/25-second retry cap.

The fourth UX capture reached graceful Stop/publication and displayed an accepted
peer receipt, then waited for the legacy copy-status receipt flag that the peer
owner never wrote. An exact original-owner acknowledgement now updates only the
existing signed display observation after current root/member/roster/consent and
canonical receipt-identity checks. Forwarded acknowledgements cannot confirm the
owner, and missing receive time remains unknown. New fixed registration
pass/lookup observations and identifier-free timeout reports preserve the next
actual failure's predicates without changing its deadline or invoking sync.
An initial held-response fixture put the policy notice only on the aggregate
Friend view, whereas canonical snapshots carry per-connection activity. The
fixture now uses that actual shape; its corrected run passes. All five focused
groups pass (lab route boundaries, chunk provenance, pinned reuse, adaptive
coordination including both held-response variants, and canonical peer receipt
status/persistence), and strict solution compilation has zero warnings/errors.
The fixed lookup diagnostic describes sharing-management lookup results; for
example PermissionDenied with a verified roster can coexist with valid hosting
roles. It is never authorization. Exact next-source Windows CI is still required.
No candidate from these runs has been published or installed.
The fifth source `698cfe5`, run `40565481173`, passed two-ended UX, loopback,
both automatic-sharing journeys, Core/rehearsal, browser, all compatibility and
wizard/delivery captures. LAN PC2's new diagnostic captured no registered group
after 99 reads/25.13 seconds, with one EnrollmentDenied lookup and four completed
registration passes. Its companion-state diagnostic used a nonexistent read
route and is now corrected to the canonical read-only Friend snapshot.
Source inspection found that enrollment GET/POST returned bare 403 for both
missing assignment and the temporary dirty-roster publication fence. Dirty
publication now returns fixed RosterUnavailable/503 only after normal
authentication/assignment checks; nonce consumption, replay, signing-key and
revocation/expiry checks remain. Publication failure uses the same unavailable
response. Generic 403 remains denial. Strict compilation and the fake-client
concurrent-enrollment group pass. A new actual TestServer-route publication-fence
case is registered for hosted general checks only; it was not run locally.
The exact LAN cause still requires the next hosted observation. Full fifth-run
Existing was still running at this note; notification delivery remains blocked
by hosted Windows QuietTime. Earlier .19 assets remain unpublished.
The sixth source `8d1fb9b`, run `40568091438`, passed the full pairing lane:
three UX, eleven loopback and eighteen LAN API stages, with PC2 membership now
confirmed. Both automatic-sharing journeys and the other completed functional
lanes passed; Existing was still running at this observation. The repeated
first-save failure exposed a separate deterministic ownership bug: the full
Shared Worlds fixture uses a loopback companion address, which is not a legal
peer route, yet registry presence alone suppressed its legacy receiver.

Ownership now requires a valid enrolled bootstrap or an explicitly published
current approved peer/contact route snapshot. Zero routes retain the legacy
receiver; approved peer-only routes can own without the original Host reachable.
Learned hints bind root/device/roster signature and clear on changed source,
empty/failed route projections or registry invalidation. Ownership reads stay
memory-only and grant no access. The new generated group proves actual fallback
scheduling, valid-route suppression, offline/absent-owner peer routes, 100 probe-
free snapshot reads, route removal, contact approval and source invalidation.
It and adaptive coordination pass locally; strict solution compilation is clean.
There are now 91 source groups. The failed first-save diagnostic also omitted
its required local GET header; it now reuses the existing fixed read helper.
Exact corrected-source CI is required; no intermediate .19 bytes are released.
The seventh source `1017cfe`, run `40570777586`, passed all 91 source groups,
packaged API, automatic sharing, compatibility/captures and the full eleven-group
Shared Worlds journey with no recovery skips. This proves the first-save fallback
regression is corrected synthetically. Existing subsequently failed after 25
companion groups: an expected permission denial was RateLimited under the
unchanged 180/minute device quota. Its first UX attempt reached verified copy,
prepared Start and the exact running session, then timed out waiting for owner
discovery. One fresh-VM rerun of the unchanged portable passed UX/loopback/all
18 LAN stages (job `122439402232`, attempt 2). The initial timeout and its hashed
masked artifact remain retained; its intermittent cause is not established.

Source review found transfer/run/publication generations also forced network
enrollment. Those wakes now retain immediate local registry/owner-roster refresh
while remote enrollment is reserved for changed Friend membership, pending retry,
the independent minute deadline or explicit Sync. The device quota and lifecycle
checks are unchanged. New generated counters assert repeated manager wakes add
no companion enrollment/heartbeat calls, while local-owner policy, material
Friend changes, periodic and explicit refresh remain covered. Strict solution
compilation passes with zero warnings/errors. The focused runtime attempt stopped
at the 1 GiB storage-reserve guard (C: had 0.83 GiB free), before the new cases ran;
hosted validation is required. Automatic approval review rejected deletion of
superseded task-generated CI package downloads as blocked by policy; no files
were deleted and the reserve was not bypassed. At that observation .19 was still
unpublished; the final review and publication are recorded above.

Final exact-source CI for `36c4be2` passed the new membership-call counters,
actual native namespace substitution and actual TestHost enrollment publication
fence, in addition to the complete source/API/Existing/pairing/automatic-sharing
journeys. The old quota failure did not recur with the unchanged 180/minute quota.
The initial Browser artifact (SHA-256
`5ec95dec6c8195a23c8dbc4c35cb594861c4f7390d6c6213bcba4643981e22d4`)
and both attempts remain retained; a passing rerun does not establish its cause.
The final portable is 66,257,956 bytes, SHA-256
`e4b6cde759eb770f2fd7553ae94f22b75f0f025ef4bd5aa1b3030ebff86d1bea`;
installer is 60,720,073 bytes, SHA-256
`b0cdd2397994950cdc021926616773b105b416cf367daf925edfa13d76e2e63e`.
Both remain unsigned and review-only, with `fullyAccepted=false`. Safe source,
CI, artifact, static and public-download proofs are under
`local-data/hosting-integration-36c4be2/`. The C: volume remains below the
generated-fixture 1 GiB reserve; further local runtime work needs adequate free
space. No rejected cleanup or reserve bypass was retried.

No new app was
installed, replaced or launched, and no production data was inspected by this
implementation work. Manual owner installation and fresh confirmation of an
unattended V1release-only session plus stopped main world are required before
physical acceptance: unchanged prepared save with no copy/transfer/setup repeat,
real Ready, graceful Stop, new fully verified save, exact current reachable-peer
receipts and saved restart. The .18 evidence below remains incomplete, and its
last sampled delivery state is historical rather than current telemetry.

For physical acceptance, record one operation at a time with UTC observations
and elapsed times. Capture the exact installed source and prepared version first;
do not install, relaunch, Start or Stop until the owner has manually installed
the reviewed candidate and confirmed the current unattended/stopped conditions.
Keep permission, discovery/selection, Receiving, PreparingCopy, Verifying and
Starting phase timings separate. An unchanged prepared save must show no new
payload receive, copy or setup operation. Phase timing is display observation:
the first hash-progress callback follows its first chunk, and Verifying includes
source, working-save and status checks. It is not pure hashing CPU time.

Take local read-only `/api/local/world-peers/work` snapshots before and after an
idle interval, Start and delivery. Report differences in uptime, process CPU and
stage counts/wall milliseconds. CPU milliseconds divided by elapsed milliseconds
is CPU use in logical cores; dividing again by logical processor count gives
the process share of machine capacity. Counters reset on app restart, overlapping
wall stages are not additive, and PeerRequest counts logical challenge/exchange
operations rather than raw HTTP requests. WorkingVerification covers the held
working-copy proof; PayloadVerification covers received-payload/receipt checks,
held rehashing and staged-directory promotion, excluding final pointer/receipt
commit, post-Stop capture and other immutable-source/backup hashing.

Record request-to-response, request-to-real-Ready and exact native creation-to-
Ready separately, with fresh run identity and player evidence. On Stop record
graceful request-to-exit, exit-to-new-publication and an independently timed full
signature/ancestry/layout/file-hash verification. Exit-to-publication includes
capture and observation overhead. For delivery record the exact version, bytes,
first/last active observations and each currently eligible recipient's valid
exact acknowledgement; offline recipients remain pending. Submitted bytes are
handed to transport and received bytes accepted into staging; neither is link
throughput or a receipt. Use byte/time rates only with their observed interval
and routing limitations. Finally restart the newly published save and observe
real Ready again. Preserve both histories if signed progress competes. Synthetic
and hosted results must remain separate from every physical observation.

## Exact .16 candidate and laptop test, 2026-10-26 EDT

The reviewed .16 preview is published at tag `v0.3.3.16`, pointing to
`73dd922df717141ba0d3c2b3601fe78f179660fe`. This replaces the proposed historical
candidate; it does not publish f768c23. The portable SHA-256 is
`598bca8979f57cac09dc7bffbfde1cebbe26edca039f8045125cef438443c66a`
(66,217,097 bytes), and installer SHA-256 is
`bff36a4a9b0612fd14d3b0fad190f9af37d8e6fc4d8ca687d38e1d00269d50c0`
(60,681,665 bytes). Anonymous downloads verified all four public assets. The
30 older releases remain present and stable/latest remains `v0.3.3`.

[Windows run 40422036891](https://github.com/daltonstates/TogetherServer/actions/runs/40422036891)
finished with 16 successful lanes and the Desktop QuietTime notification block.
Existing passed all 27 gates with zero NotRun: UI 1,552 tests, 114 core checks,
all 12 Minecraft fixture checks, the 4,102-version history case, Shared Worlds
11 groups with zero recovery skips, Shared LiveSave five groups and Companion
46 groups. Pairing passed all 32 stages, source/API 82/eight groups, Core 14 and
rehearsal two groups. Both ordinary nine-stage automatic-sharing journeys passed.
The retained returning-owner observations show two Start requests in the first
journey: `LatestSaveChanged` after 19,705 ms with zero active runs, then success
after 21,059 ms with one matching run. The other journey succeeded with one
request after 21,288 ms. The first refusal's boundary remains unclassified;
these results do not prove reliable single-action returning-owner adoption.
The previous composite failure did not recur; its cause remains unknown, and
the new failure diagnostics did not fire. Hosted success is separate from the
physical result below.

The owner reconfirmed the unattended target-world-only session and stopped main
world, then manually installed the preview. The one running laptop process was
independently matched to the exact published portable hash/source, same account,
production root and local API listener. The authenticated main snapshot reported
.16 and the exact source world Offline immediately before each Start; its
running EXE hash was not independently accessible. No app replacement/relaunch,
pairing reset, key regeneration or protected-record reconstruction was performed.

Two explicit local Starts each reached actual Ready with a fresh trusted
ValheimLogReady count of zero. Both native processes were bound to the selected
profile, exact installed image, process creation time, world and managed save
directory before issuing one graceful local Stop per run. No Start was retried.

| Physical measurement | First prepared Start | Saved restart from publication 5 |
| --- | ---: | ---: |
| Start response | 18,285 ms | 20,211 ms |
| Permission phase | 332 ms | 802 ms |
| Signed latest-save discovery | 3,803 ms | 6,372 ms |
| Save verification phase | 6,014 ms | 7,242 ms |
| Connection phase | 297 ms | 323 ms |
| Launch phase | 7,108 ms | 5,279 ms |
| Receiving / protected-copy preparation phases | Neither entered | Neither entered |
| Request to first observed actual Ready | At most 82,064 ms | 42,619 ms |
| Native process creation to observed Ready | At most 65,175 ms | 23,554 ms |
| Graceful Stop response | 14,750 ms | 13,021 ms |

Both Starts reused the verified 315-file, 119,452,903-byte save without another
transfer or setup wizard. Ready observations are distinct from launch completion;
the first observation has a coarser upper bound. This was an idle real server
test, not client-join or recognizable-player-change acceptance.

Stops published new backups and signed publications 5 and 6, each an exact child
of the previous head with `PostStopBackup`. Independent non-mutating verification
passed signatures, branch proof, strict parents, current roster/root and all
315 file hashes for each. All five completed backups remain visible, including
the original Rolling/Manual pair and the earlier recapture, and historical
publication-failure activity remains. Read-only observers initially rejected
incorrect argument/schema assumptions before Stop; no Stop was issued by those
failed observations. The two intended Stop requests both succeeded.

**Automatic delivery has not passed.** Publication 5 still had no exact main-Host
receipt after more than 20 minutes despite fresh authenticated metadata contact.
Publication 6 also had zero exact receipts at 01:11 UTC, one recently authenticated
eligible external peer and one offline/unreachable eligible peer. At 01:17 UTC
the metadata-only follow-up still had zero receipts and both external contacts
were stale; this does not establish either game's process state. The main did
not claim the current save. The laptop's incoming own-branch vault still named
publication 4 with no partial directory, while its published vault named 6.
Those are local observations; the receiver filesystem was not directly inspected.
An offer for this actual branch is 110,372 JSON bytes, approximately 459,898 with
a maximum chunk, below the 1 MiB payload bound. This is not proof of the complete
wire catalog or of the failing transfer boundary. Metadata success alone is not
payload success, and no manual Sync/share action was used to substitute for
automatic delivery. The world is left Offline with no Valheim server process.
At 01:43 UTC, a further non-mutating signed-metadata check still found zero
receipts for 6, now with freshly authenticated main-Host contact again. Earlier
private observations were preserved before that repeat check. A separate GET
confirmed the target Offline and zero native Valheim server processes.

Public-safe measurements are under ignored `local-data/physical-test-73dd922/`;
private signed metadata remains outside Git. The unchanged previously reviewed
private observer was used for verification, without LocalData constructors,
LoadProtected, chain recovery, signing-key export or live-state writes. A proposed
new observer/ACL setup was rejected by automatic approval review and never ran;
it was not retried. The existing observer provided the safe alternative.
The laptop is improved for prepared Start and saved restart, but is not accepted
as fully repaired while automatic exact acknowledgement remains unresolved.

The .17 slice adds read-only transfer observations to the existing shared
world panel: active phase, fixed request/HTTP result, elapsed time, bytes handled
in that pass, and retained failures. Metadata-only discovery cannot erase a
failed payload operation. The memory store is bounded to 256 routes; snapshots
contain no endpoint, identity, key, signed bytes, path or exception text. Receipt
display records the historical publication number only after existing receipt
verification and binding its signer to that route's authenticated responder.
A valid forwarded receipt remains stored without becoming main-Host receipt
proof. Polling never schedules work, reauthorizes Start or retries a transfer.
Explicit Get world updates text now accurately describes its existing catch-up
behavior. Publication, metadata contact, byte progress and receipt proof stay
separate.

This is visibility for the unresolved physical failure, not a claimed delivery
repair. Transfer order, request deadlines, signatures, ancestry, trust floors,
permissions and planned-move requirements are unchanged. One cancellation bug
is repaired independently: completed-copy staging now passes its existing
cancellation token into full payload verification. Generated fake-HTTP checks
use the production six-second request timeout rather than HttpClient's default
100 seconds. Regressions retain a real Catalog HTTP503 after fresh authenticated
metadata, show that seeding was never reached, and preserve the failed phase
through later successful metadata discovery. Another case cancels verification
of a deliberately corrupt partial file before it can be hashed/reset, promoted
or acknowledged. These characterize possible boundaries without attributing
either to the laptop. The published .16 assets/tag remain unchanged and .17
requires its own hosted validation and owner-installed physical evidence.
The first forwarded-receipt regression assertion failed without retaining its
individual values. Source review found that its one extra background pass could
skip the owner because only two of three contacts are visited. That probe now
uses the same full exchange flight for the exact fake owner route, with bounded
typed assertion fields. The ordinary distribution journeys remain unchanged;
the initial failed assertion is not evidence of a product receipt failure.
The corrected exact-route regression and pure observation group pass, including
positive owner attribution, forwarded-member refusal and cancellation. The
coalesced-background group passed the preceding focused run. Strict solution
compilation reports zero warnings/errors; the current 72 focused UI tests,
type check, targeted lint, production bundle and formatting pass. No native
fixture, installer, browser or new app was launched on the laptop for this slice.

The first .17 source `0aedd37d9acde2ba8e818ac70e4b941e49dfe7ee` ran in
[Windows CI 40434779406](https://github.com/daltonstates/TogetherServer/actions/runs/40434779406).
Its candidate passed static source/version/hash/GUI bundle checks. The access/UI
review stopped before rendering: 57 of 59 pure fixture checks passed, with two
`ReferenceError` failures because the standalone decoder extractor omitted the
new background parser and its dependencies. This is a harness dependency error,
not a browser success or product-delivery diagnosis. The extractor now includes
those actual functions/constants and has a populated failure/historical-receipt
case; all 60 pure checks pass locally. Product source remains unchanged from
0aedd37. That run finished with 13 successful lanes and four failures: the
extractor, Desktop QuietTime, LAN incoming preparation (`LatestSaveChanged`
after 12,829 ms), and ordinary automatic-sharing Friend preparation
(`SimpleFriendPreparationRejected`, whose underlying result was not retained).
Its separate diagnostic journey passed nine stages with two returning-owner
Start requests. Existing passed all 27 gates, including all 1,573 UI tests.
Those failed preparation results remain unexplained; a later green run is not
evidence that the extractor correction repaired a product race.

The corrected exact-source
[Windows run 40435440400](https://github.com/daltonstates/TogetherServer/actions/runs/40435440400)
for `554e795225e81932286775c94e9dfd76c44f01fe` finished with 16 successful lanes
and only Desktop QuietTime blocked. Existing passed all 27 gates with zero
NotRun: 109 UI files/1,573 tests, Checks 114, Minecraft 12 and every later gate.
Source checks pass 83 groups, eight packaged API journeys and the final
unchanged-candidate hash check. Pairing passes all
32 stages; both ordinary nine-stage automatic-sharing journeys pass, each
returning owner using one Start request. The final candidate hash is unchanged;
hosted cleanup completed without orphan termination entries. All six installer
smoke cases and three actual wizard choices pass. The 60 pure fixture checks
and hosted access/wizard capture manifests pass; those screenshot plans do not
contain populated background transfer observations. No visual acceptance of
that populated panel is claimed from those PNGs.

The reviewed [v0.3.3.17 preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.17)
is published, with annotated tag `d3739caffb6b3e89339207e055370dd1c34533d0`
pointing to 554e795. Portable: 66,225,822 bytes, SHA-256
`400e4f37a48fc53387b8120183af7c0862c0d207cea0d7b5fed9b85016872b3f`.
Installer: 60,685,599 bytes, SHA-256
`0c9a4c4569e404fd36d2d9aa682891d7fbd19b7767c2a0ab8fd0ff47ffaf0c98`.
Source/run/attempt, static native versions, embedded UI, manifest and checksum
sidecars were checked without loading or launching either binary. All four
public assets were anonymously downloaded and rehashed. All 31 older release
IDs remain present (32 total), and stable/latest remains `v0.3.3`. Both EXEs
remain unsigned and review-only; full acceptance remains false.

While the target remained Offline, bounded read-only hashing also matched all
315 files/119,452,903 bytes in each older local received cache (canonical 3 and
branch 4), with read leases held and stable pointers. No payload hash mismatch
was found in those copies; this does not establish remote receipt or reverify
their current signed authorization. A source review separately found that an
incoming echo of an already-published/retained exact save can still use another
receive vault and change the material observation during preparation. That is
a plausible source schedule for a benign refusal, not runtime attribution.
No observation fence, ancestry check or incoming-transfer behavior was changed
on that basis.

After the owner confirmed manual .17 installation, the running laptop process
and local API were independently matched to the published source/hash, same
Windows account and Production instance. The target stayed Offline, with zero
native Valheim server processes; this observation issued no Start, Stop,
preparation, access-check or manual-sync request. At 22:45:31 EDT the recorded
OriginalHost exchange reported `KnownReceiptAccepted` for publication 6, with
zero submitted/received payload bytes in that particular 13,305-ms pass. This
does not establish when the main Host finished receiving the payload or whether
earlier passes transferred data.

Independent unchanged-observer verification at 22:48:57 EDT passed publication
6's signatures, branch proof, strict parents, current roster/root and all 315
payload hashes again. It also verified one exact original-owner-domain receipt
for 6. The one recently authenticated eligible external peer has acknowledged;
the other eligible offline peer remains pending. The receiver filesystem was
not directly inspected. Earlier private proofs were preserved before repeating
the observation, with no production state writes or key export.

Receipt recovery across the manual app restart is physically demonstrated.
The earlier delay's cause and actual transfer duration remain unknown.

The owner then reconfirmed an unattended laptop and stopped main world for one
V1release-only .17 Start/Stop. Fresh authenticated main-world metadata was
Offline immediately before the single Start. It reused prepared publication 6,
entered neither Receiving nor PreparingCopy, and did not repeat setup. Its
response took 26,044 ms: permission 550, latest-save discovery 4,843, verification
11,192, connection 465 and launch 8,416 ms. Actual Ready was observed 59,207 ms
after the request, 34,989 ms after the exact native process was created, with a
fresh trusted ValheimLogReady count of zero. These are separate observations,
not an inference from process existence or the Start response.

One Stop, after binding the exact local profile, process image, account,
creation time, world and managed save directory, returned ValheimStopped in
14,242 ms. Native exit was first observed at 4,470 ms and signed publication 7
at 17,468 ms. The 12,998-ms interval between those observations includes capture,
verification, publication and polling; it is not an isolated hashing time.
Publication 7 has a new PostStopBackup and exact parent 6. Independent read-only
verification passed its signature, branch, strict parents, current roster/root
and all 315 file hashes (119,452,903 bytes), with no hash mismatches. The entire
independent verification observation took 3,061 ms, not payload hashing alone.
The target is left Offline with no native Valheim server. No Start was retried,
and no manual sharing operation substituted for background delivery.

Background delivery remained slow: several offer/chunk requests timed out while
later passes advanced receiver-requested offsets. Submitted bytes describe
attempts, not verified receipt or an acknowledged percentage. At 03:38:52 UTC,
roughly 36 minutes after Stop, the unchanged observer verified one exact
original-owner-domain receipt for 7. Receipts have no completion timestamp, so
this bounds observation rather than proving delivery duration or its cause.
The other eligible peer remains pending; this snapshot cannot establish that
all currently reachable peers acknowledged. The main's authenticated snapshot
reported .17, but its running EXE was not independently hashed.

At 03:38:50 UTC the laptop's own transfer telemetry also recorded Pulling its
already-published 7 back from the main Host: 2,773,567 received bytes and two
completed files in that pass before its caller budget canceled. This is direct
evidence of a redundant copy across local vaults. The receiver filesystem was
not directly inspected. Public-safe evidence is under ignored
`local-data/physical-test-554e795/`; signed private evidence remains outside Git.

### Bounded .18 sharing changes, 2026-10-27 UTC

Source review found that a background pull holds its receive writer while
awaiting chunks, while inbound seed requests previously queued on that writer
despite a shorter request deadline. Both ordinary and retained-history seed
gates now return the existing SeedPaused result immediately when busy. The wire
schema and deadlines are unchanged; no incomplete copy receives a receipt.
The generated fake-HTTP regression fails with TimeoutException against the old
waiting behavior, then restores the source. Corrected compilation and all three
focused groups pass: completed-save distribution (including both busy gates),
read-only transfer evidence and coalesced background scheduling. Exact-source
hosted validation remains pending.
This establishes a source contention schedule, not the physical timeout's cause.

Catalog pulls and initial seed offers now look for an exact authorized local
inventory pair (branch, including canonical null, and signed version hash)
before creating a receiving vault. Original numbered publications use the
existing fenced publisher/authority reader for ancestry; branch and retained
copies use their signed received history. A full immutable payload lease is held
through current member, roster, key, consent, ancestry and sender checks. A
matching damaged copy fails for review rather than being hidden by another
download. Competing histories are retained. This slice reuses exact inventoried
heads; it does not introduce an archive-wide discovery or default selection.

An acknowledgement reuses a valid current selfreceipt or creates one immutable
sidecar bound to world, branch, version, signed roster, device and retained key.
Legacy receipts and old scopes are preserved; malformed current evidence is
not overwritten. No received pointer, SaveObservation, working world, prepared
copy or Start authorization is changed. A confirmed receipt delivery skips a
new pull hash/transfer; returning a SeedKnown receipt still requires full held
verification. New inbound seed work yields while explicit preparation is active.
Already-active passes keep their existing cancellation/budget rules. Listener
enablement stays separate from permission to receive through an outbound request.

Review caught an inverse consent/world lock order in the initial implementation;
it was corrected before publication, with Friend consent read before the world
monitor and final acknowledgement under the existing consent-then-world commit.
The dedicated generated-files/fake-HTTP group covers original, published branch
and retained copies, receipt reuse after reopening, exact branch/null identity,
fork preservation, corruption and current-permission/key changes, malformed
receipt preservation, outbound-only receiving and a bounded competing-lock
schedule. Its initial run stopped on an invalid synthetic roster baseline in
the fixture. The fixture now changes actual pairing grants before confirming
the signed revision; production roster checks were not relaxed. Corrected
solution compilation has zero warnings/errors. Exact-copy reuse, read-only
transfer evidence, coalesced background scheduling and prepared Start groups
pass. The distribution group's older projection fixture initially required the
unnecessary automatic echo this slice removes. It now first asserts no echo,
then explicitly creates a fully verified disposable legacy cache to retain the
old duplicate-head regression; the corrected group passes. No .18 native fixture
or production process has been launched on the laptop.

The initial .18 [Windows run 40449701230](https://github.com/daltonstates/TogetherServer/actions/runs/40449701230)
uses source `8b4f28d727466e76bde867b6bf938221dea89694`; that source was not published.
It finished with 13 successful jobs and four failures. Existing passed all 27
gates with zero NotRun, including 1,575 UI tests; its source suite passed 82/84
groups and left the packaged API journey unrun after those two failures.
Its source suite also exposed the older replication receipt boundary: a forged
legacy record was ignored before creating a new valid selfreceipt. Legacy and
current-scope records now all require the exact self/world/save, retained-key
signature and no future roster revision before hashing. A valid scoped record
cannot hide a forged legacy record. Invalid records remain unchanged. A genuinely
missing record can earn one new current-scope acknowledgement after full proof;
the missing file is not recreated and the new receipt ID persists after reopening.
Dedicated negatives cover foreign identity, bad signature, future roster and
wrong save/world, and the replication case retains its corrupted-payload refusal.
The corrected offline-owner replication and exact-copy receipt groups pass after
a clean strict build, including the original foreign-identity refusal. Local
copy hashing now reports the existing VerifyingLocal phase and exact save totals,
after the cheap confirmed-receipt skip. Its actual fake-flight progress assertion
passes. Final targeted formatting, UI type/bundle checks and the 64 corrected
pure capture checks also pass; full hosted validation of the next source remains
required before publication.

The initial hosted UI job passed its older access/wizard captures, then all nine
new states failed the observation audit on the existing POST `/api/local/ui-drafts/read`.
That endpoint is a protected draft read; the guard now permits only that exact
method/path alongside GET and still refuses writes, clear, query variants,
sync, preparation and Start. All 64 pure fixture checks pass. The retained
failed PNGs were actually inspected for sending, timeout and historical receipt
states across desktop/narrow/150-percent examples. The status is visible outside
disclosures; a tiny recipient heading found in those images now uses body-size
text. This is a synthetic rendered UI review, not an observation of the laptop's
screen or a passing capture manifest. Exact-source recapture remains required.

The same run's LAN lab failed `AutomaticReceivingMissing` after verified peer
membership and signed role checks; loopback and two-ended UX passed. The failing
predicate was an immediate Friend-status Consented read, independent of duplicate
vault presence. Its actual failure cause is unknown. The harness now observes
that asynchronous projection using GET only for at most 25 seconds, retains fixed
PC-ordinal/state/error/number/poll-count diagnostics, and still requires true
consent plus a final exact world/device/role read. It does not retry the prior
sync, prepare or Start. This harness correction is not a claimed product consent
race repair. The existing QuietTime notification boundary is also retained.

The corrected .18 candidate is
`8aae31299bea9e5b29599d17d456f6ea756f8086`, in
[Windows run 40452218883](https://github.com/daltonstates/TogetherServer/actions/runs/40452218883).
All 84 source groups pass; the packaged API output contains eight journey groups
plus its final unchanged-candidate hash check. Both ordinary nine-stage sharing
journeys pass with one Friend Start and one returning-owner Start each. Pairing
passes UX three, loopback 11 and LAN 18 stages. Both LAN receiving observations
passed on their first GET (161/105 ms) with true consent and final exact world,
device and role checks. This does not identify the earlier consent failure's cause.

All nine populated transfer captures pass with zero layout findings; all nine
primary PNGs and three 150-percent bottom views were actually inspected. The
seven rendered UI files match the reviewed candidate ZIP and embedded bundle.
These are synthetic screenshots; neither failed computer-use helper observed the
laptop screen. Six installer cases and three actual wizard choices also pass.
The portable is 66,230,798 bytes, SHA-256
`97e12384d4d4e18c2c1d02107836dcd02ac24e1f156a01771f9259f20ce47a24`;
installer 60,687,387 bytes, SHA-256
`8824eebc0d5df5341eb2a9c9bc1b3097273429a8e0066d302e4a3a2f4d1d9f77`.
Manifest, sidecars, versions and source/run identity were checked without loading
or launching either binary. Installer resource strings have ordinary fixed-width
padding, trimmed for exact numeric-version comparison. Both assets remain unsigned.

Attempt 1 finished with 15 successful jobs and two failures. QuietTime remains
blocked (61 polls/15,240 ms). Existing passed 15 gates, failed Core remote Start
with typed `PortInUse` before process creation, and left 11 later gates NotRun.
Its UI 109 files/1,575 tests, Checks 114 and Minecraft 12 had passed. The diagnostic
does not retain the chosen UDP pair or failed bind error, so its resource conflict
is unclassified. The conflict check and Start behavior were not weakened. Only
that CI job was rerun once on a fresh hosted runner with the same candidate.
Attempt 2's Existing job passed all 27 gates with zero failures/NotRun, including
the later Core 14/rehearsal two, World load, Shared Worlds 11 with zero recovery
skips, Live/Shared LiveSave, Companion 46, formatting and all five smoke gates.
Final bytes still match `97e12384...ce47a24`; cleanup contains no orphan game/app
termination entries. This was a new isolated test session, not a product Start
retry, and does not identify or repair the earlier port refusal's cause. Latest
checks report 16 successful jobs and only the preserved QuietTime failure.

The reviewed [v0.3.3.18 preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.18)
was published at 05:51:11 UTC, release 425078208, annotated tag
`935b62cd213f4cbe9acbf6e5be8e10136c6f11ba` pointing to exact source 8aae312.
All four public assets were anonymously downloaded and rehashed against the
reviewed files, manifest and checksum sidecars. All 32 older release IDs remain
present (33 total); stable/latest stays `v0.3.3`. No app was installed or replaced
by the agent. Public-safe release evidence and both Existing attempts are retained
under ignored `local-data/simplified-hosting-8aae312/`.

The actual Host view placed the read-only panel inside a closed Other Hosts &
connections disclosure. Earlier suggested UI directions were not verified on
the screen. Both computer-use helpers failed before initialization, with no
screen observation or UI input. The .18 panel exposes Save sharing status and
transfer progress directly in Sharing & hosting, keeping permission controls
and transfer diagnostics collapsed. Component coverage passes 192 tests across
the three affected suites. Nine populated hosted screenshots passed alongside
the unchanged older access/wizard inventories, with actual visual inspection
recorded above. No local browser/native fixture was launched.

An initial local compilation overlapped the UI bundle write and failed on a
missing embedded asset. That was an avoidable build-order error; UI output and
.NET compilation are serialized for subsequent checks. This failure is not a
product or native test result. The completed .17 laptop cycle remains separate
evidence from the .18 physical test below.

### Physical .18 laptop test, 2026-10-27 EDT, incomplete

The owner confirmed manual updates on both PCs, an unattended laptop, a stopped
main world and a V1release-only Start/Stop plus saved-restart scope. The one
running laptop process independently matched the published .18 portable hash and
source, current Windows account, Production instance and owned loopback listener.
No Valheim process was running. The main's authenticated snapshot reported .18
and fresh Offline state; its executable was not independently inspected.

One Start at 02:43:18 EDT reused prepared publication 7. It returned success in
36,054 ms: permission 1,509, latest-save discovery 6,650, verification 15,743,
connection 742 and launch 11,393 ms. It entered neither Receiving nor PreparingCopy.
Actual Ready was observed 75,599 ms after the request and 41,098 ms after native
process creation, with fresh trusted zero occupancy from ValheimLogReady. These
figures are slower than the preceding .17 run; no speedup or client-join claim
is inferred from them.

One local graceful Stop at 02:46:37 EDT followed exact profile/run/PID, native
image, account, creation time, world and managed save-directory binding and a
fresh trusted zero-player check. It returned ValheimStopped in 18,016 ms. Exit
was first observed at 5,656 ms and publication 8 at 25,731 ms. The 20,075-ms
post-exit observation interval includes capture, verification, publication and
polling rather than an isolated hashing time. Publication 8 is an exact child of
7 with a new PostStopBackup; all 315 files total 119,452,903 bytes. Independent
non-mutating verification passed signature, branch, strict parents, current
roster/root and every payload hash. The entire independent observation took
3,410 ms, not payload hashing alone. At that observation the target was Offline
with no Valheim process. No Start, Stop, preparation, access check or manual sync was replayed.

Automatic delivery was slow in these observations and was not accepted as repaired. The exact
main-Host receipt for 8 was absent at the first 02:49 verification. Receiver
requests subsequently advanced through file indices 19, 69 and 142; these are
resumption hints, not verified byte acknowledgements or a delivery percentage.
Current busy offers return SeedPaused while a receiving writer is occupied.
The retained canceled pass near Stop still names receipt 7, not receipt 8.
The authorized saved-restart step was not issued before testing stopped. Last
sampling at 07:21:44 UTC showed publication 8 sending at requested file 286/315,
offset 524288, with no accepted exact receipt 8 in that sample. This is a resume
hint, not a verified percentage or a statement of present delivery status.
Ordinary Start does not require an owner receipt. Offline recipients were pending.
Safe measurements are in ignored `local-data/physical-test-8aae312/`.

Read-only source review identified two full local inventory builds per chunk and
an identical protected cursor rewrite after unchanged roster challenges. A
bounded next optimization is being implemented separately with current-source,
role/consent/authority, digest, cancellation and publisher fences retained. That
source finding does not attribute the physical transfer delay. No new candidate
has replaced either running app.

## Current behavior

- One Windows app hosts local servers and connects to Friends, with separate
  revocable access, pinned HTTPS, and fixed approved actions.
- Friend PC lists omit internal shared-world session credentials. Ordinary
  PCs keep their existing permissions. Removing access from a selected server
  keeps the PC's other assignments and settings; **Remove all server access**
  is a separately named action. A fresh approved invitation can grant access
  again without restoring the removed credential or scope.
- Shared hosting uses **Allow friends to host this server** and **Host locally**.
  Receiving and forwarding completed saves are automatic for approved members.
  Only a complete, verified local copy can become playable; signed announcements
  and interrupted transfers cannot. A prepared **Start hosting** action stays
  explicit and does not repeat preparation.
- Hosting checks retain their last result, failed preparation step and next
  action across Refresh. Refresh reads setup; it does not repeat preparation.
  Local protected-copy preparation and hash verification report measured
  bytes/files. Preparation uses a two-minute inactivity deadline and a fixed
  ten-minute ceiling; repeated polls do not extend either authorization intent
  or the overall ceiling. Generic verification failures do not assert a fork.
- Returning original owners can acknowledge a complete peer save through a
  separate root-bound production receipt. Member, original-owner and private
  test receipt domains remain disjoint. Lost seed acknowledgements reuse the
  existing verified receipt. A terminal hosting session records its final
  published backup head and stays immutable after it is closed.
- A newer local publication advertises its hosting history once even when an
  older copy of that same history returns from a peer. That projection retains
  the received copy, payload catalog and signed journal; it chooses no fork by
  counter or timestamp. Terminal heads advance only through proved ancestry.
- Signed original-world identity survives successor-local profile IDs and
  roster changes. Missing valid ancestry can be caught up; invalid signatures,
  changed ownership and divergent histories still require review.
- Companion history checks and background peer sync can converge while a page
  request is in flight. A redundant legacy snapshot must fit a strictly proven
  retained owner-signed interval. Replaying an exact committed revision checks
  its protected index and signed file under the mutation lock, preserves the
  current head, and grants no access from an earlier roster.
- A retained standalone legacy roster can join the owner's schema-3 encoding
  of that exact same edit. The journal preserves the legacy signed bytes and
  protected lookup, and retains the new revision's real signed parent in its
  protected transaction and lookup. Both encodings must match counters, owner,
  world, ordered members, keys, grants, deadlines, revocations and owner baseline.
  Reads and recovery revalidate the proof; peer pages carry the exact parent.
- An adopted connection's permission prompt and saved receive consent use the
  pinned original world and member identity. Original-owner permission requires
  its actual signing key and keeps local receive consent separate. Cached save
  versions retain their signed original profile identity.
- The original owner's remote Stop resolves authority against that signed
  original world. Current membership, permission, remote-control and fresh
  exact-zero player checks still apply at the successor.
- The sidebar groups locations by verified original-world identity. New
  shared locations wait for the canonical directory before entering the list;
  snapshot status alone cannot briefly create another world entry. The row
  identifies the hosting PC and routes actions to that actual location.
- Valheim query recovery preserves the Steam challenge and bounds silent-query
  retries. Silence, malformed or contradictory replies remain Unknown.
  Remote and automatic Stop require a fresh trusted zero inside the lifecycle
  gate; local owner Stop remains available separately.

## Open work and acceptance limits

- At initial diagnosis, the laptop's installed process matched the published 0.3.3.10 version,
  source and SHA-256. Its same-user production root has sufficient reserve.
  A private, non-mutating metadata observation found a valid legacy revision 2
  journal, while the Host's signed lineage contains its equivalent schema-3
  revision 2 with a different exact parent. Root fallback skips legacy revision
  1 as superseded, then `Append.ValidateNext` rejects schema-3 revision 2 as
  unauthorized by its exact parent (`InvalidDataException`). The protected
  connection's revision 7 is present in the verified Host lineage through 11.
  The synthetic pre-fix regression reproduces the exact reported phase suffix.
  This diagnosis is a deterministic replay of captured signed metadata and
  source predicates, not an attached runtime stack trace.
- The owner manually retried on 2026-10-25 at approximately 12:53:48 EDT and
  reported the same error. Stable repeated before/after observations retain
  byte-identical signed journals, floors, indexes and lookups; five ordinary
  protected rewrites have identical decrypted bytes. No production recovery,
  migration, key helper or repair ran. Captures used repeated bytes/timestamps
  and structural validation rather than an atomic application lock. Evidence
  remains owner-only on that laptop, outside Git; no worlds, logs or key files
  were copied. The other PC's actual executable is inaccessible through the
  available Windows remote read, so its version/hash remain unconfirmed.
- Post-publication inspection confirms the laptop's installed 0.3.3.11 process
  matches the published EXE hash/source and uses the same production root.
  A stable read-only capture at 14:10:55 EDT shows the affected journal advanced
  from one to 11 revisions with one protected projection proof and no pending
  transaction. Every signature, canonical hash, index, lookup and transition
  validates; the old connection floor is retained and the new floor matches
  the head. Original legacy revision and lookup bytes remain unchanged.
  This is physical-laptop ancestry-import evidence. The owner now sees the
  hosting action and reached preparation. That import established no game or
  save-delivery acceptance; the later unattended Ready/Stop evidence is below.
  The earlier
  [laptop handoff](development/laptop-sharing-history-handoff.txt) records the
  investigation's starting constraints, not a current success claim.
- The laptop's original-Host connection reached exact-source retention after
  the 0.3.3.11 ancestry repair. Its next hosting attempt ran from approximately
  14:17:54 to 14:19:55 EDT. The protected source vault was created at 14:19:19;
  its partial copy held 75 of 315 files (26,315,719 of 119,452,903 bytes), last
  written at 14:19:54, with no promoted pointer or complete directory. The
  progress record ends as canceled/interrupted in `LatestSave`, matching the
  UI's absolute 120-second preparation timer. This is evidence of interrupted
  local preparation, not a proved fork, missing parent, or permission denial.
  A repeated byte/timestamp metadata capture excludes payloads, keys and logs;
  payload directory inspection reads counts, sizes and timestamps only.
- Root-aware adopted-copy preparation now resolves original profile/member
  identity through branch/status/restore/solo-offer operations, skips legacy
  HTTP save pulls for adopted sessions, and rechecks current permission and
  consent. Generated member and original-owner cases cover preparation and
  guarded Start refusal without any process launch. Physical successor
  rehosting remains unverified. Remote Stop authority does not grant local
  receiving or hosting consent.
- A separate review found that the target-world sync's 30-second network
  budget begins after its global gate wait and all saved-connection membership
  refreshes. Slow unrelated connections can delay discovery. No deadlock was
  reproduced. The prepared Start path now avoids that global work; initial
  unprepared recovery can still perform full synchronization. This differs
  from the earlier measured retention cancellation.
- The approved unattended laptop test reached real server Ready and graceful
  Stop. Physical multi-PC transfer, client joins, recognizable saved changes,
  restart, recovery and valued-world acceptance remain separate checks.
  Hosted or synthetic results do not close them.
- Minecraft remains fixture-tested. Factorio and Terraria remain previews;
  Terraria has no trusted occupancy and cannot use Friend or automatic Stop.
- Real-game live capture requires the
  [six-stage exact-installation acceptance](10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md).
  No real installation is accepted by source or fixture checks.
- Current release builds are unsigned. The latest recorded hosted native check
  could not prove notification delivery, click or navigation because Windows
  was in QuietTime. Full native acceptance remains incomplete.

## Current delivery

The hosting simplification preserves the prepared-publication fix below and
the existing common HostManager launch gate. Passive hosting/access GETs now
project retained signed permission without enrollment. An explicit, scoped
empty-object POST performs the access check; ten-second polls cannot replay
it. Prepared Start reads current signed local permission and uses its bounded
peer discovery/final proof instead of enrolling or querying planned moves
against the original Host. Starting an already prepared shared continuation
from the Host screen now uses that same held-source proof and common launch
gate, including when an original-owner profile remains on this PC. Planned
move actions keep their exact final-source,
receipt and authority fences. Ordinary status's advisory move lookup has a
two-second budget. Peer preparation no longer performs the redundant legacy
Host pull before the peer resolver.

Background sharing has an independent app loop, coalesced periodic passes,
bounded membership refresh, and per-world/per-route transfer flights. Initial
preparation uses its registered signed world instead of waiting for every
saved Host; it joins a matching transfer and gives new background routes for
that world lower priority through the complete preparation/verification/setup
operation. Payload work holds no global refresh or metadata
transaction. Completed-save publication, pending offline recipients and exact
owner/member receipts retain their existing durable signed protocol. This
does not infer a stopped remote game from a disconnected route.

The initial all-source run caught a rejoin ordering regression in the new
parallel membership refresh: an explicit sync could exchange with the old
member before the fresh signed registration finished. A bounded identity
recheck now performs one follow-up for newly registered worlds or changed
device/bootstrap identities only. Ordinary roster progress does not add another
handshake round; the existing two-handshake bootstrap budget remains enforced.
The existing rejoin assertions remain unchanged; no protected identity or
history is reset. Unchanged worlds do not repeat their payload work.

Explicit peer sync now targets its requested registered world and follows any
joined transfer with a fresh metadata-only barrier. A response predating a
signed disable cannot satisfy that request. An unavailable peer returns an
inconclusive result while retaining cached signed history and copies; ordinary
offline Start keeps its separate permission and complete-save rules. Regressions
cover a disable arriving during an older transfer, unavailable routes and an
unrelated world's blocked metadata operation.

First original-owner Start registers only that saved world's existing signed
membership locally before resolving progress. It does not wait for periodic
Friend enrollment or generate a key, roster or announcement. Regressions cover
immediate first Start with and without a completed publication, strict identity,
consent and roster refusal, and preservation of other protected records.

Review also caught an existing first-publication boundary: a published-roster
read could create a replacement owner key when a signed roster existed without
a completed-save pointer. Published peer reads now require the retained key;
the first-owner path checks it before reading the roster. A bounded direct
protected-byte reader refuses missing, malformed or undecryptable keys without
quarantining or replacing them. Five actual first-Start fault cases, direct
published roster/version reads and background registration all refuse before
the fake driver, preserving the damaged bytes/path and other protected records.
Explicit initial setup/publication retains its separate key-creation path.

Cache boundaries remain explicit:

| Responsibility | Reuse and invalidation |
| --- | --- |
| Installation/setup | Saved selected/profile server candidates are verified before another installation scan. Java reuses at most 32 successful archive-format inspections in process memory, bound to full artifact/folder paths, current JAR SHA-256 and the exact provenance-byte SHA-256. Every call still verifies current bytes against provenance SHA-1. Content/provenance changes, eviction or app restart require inspection again. This is not a measured Valheim startup optimization. |
| Per Start | Current signed roles, expiry/learned revocation, local consent, exact branch/source/setup and material newest-head observation remain authoritative. Held immutable source files and the working continuation are checked; changed or missing sources require explicit preparation. World/process/port conflicts and cancellation are checked in the common manager gate. |
| Per run | Recorded process identity, actual game readiness and fresh trusted counts are observed anew. Unknown occupancy never becomes zero. No setup/permission cache or earlier Start substitutes for those observations. |
| Background sharing | Registered world identity and bounded coalesced flights avoid duplicate work. Current signed membership/consent and full bytes/receipt verification remain required before promotion or acknowledgement. Offline routes remain pending; known competing/newer missing progress cannot fall back silently. |

Explicit operations now retain bounded monotonic phase timings for permission,
signed discovery, save transfer, local copy preparation, verification, receipt,
setup and launch-request work. Accepted work callbacks checkpoint elapsed time;
passive reads, duplicate byte/file callbacks and UTC clock changes cannot renew
progress or authorization. Terminal timings freeze and the next explicit
operation starts a new scoped record. The UI's Measured step times distinguishes
launch-request time from actual game Ready. The physical test must separately
observe its exact managed process and first real Ready result; no timing here
accepts game readiness, a join, saved progress or another PC's verified receipt.

The old frozen candidate run completed with 14 successful jobs and three failed
jobs: Pairing (empty HTTP403 at the lab's owner-recovery preparation), Existing
(eight formatting findings in SimpleSharedHostingJourney after its process
gates passed), and Desktop (required OS notification delivery/click blocked by
QuietTime). The new source admits only the lab's exact fixed recovery routes,
retains all normal local-header/owner/security handlers, and corrects the fixture
whitespace. These fixes require a fresh hosted run; the old run is still failed.
The release inventory was rechecked: 30 releases, latest preview 0.3.3.15,
stable/latest 0.3.3, no 0.3.3.16 release. No existing release/tag/asset was changed.

The subsequent hosted run for cdfd898 completed 12 lanes successfully and failed
five. Core, Browser, Rehearsal, UI/access review, Package/installer and six
compatibility lanes passed. QolApi exposed the extra roster handshake above;
Pairing and the ordinary automatic-sharing attempt refused their first owner
Start before launching a fixture; the separate automatic diagnostic exposed
stale permission after an explicit sync. Exact local registration and fresh
targeted sync now have passing source regressions, and initial Start failures
retain bounded typed diagnostics. These changes require another hosted run.
Desktop's required notification delivery/click remained blocked by OS QuietTime.
Existing passed all 1,545 UI cases and several game checks but failed Minecraft
capture-host identity verification and Bedrock fixture readiness; their cause
is not established, and subsequent Existing gates were not run. No failed or
unrun gate is accepted by the successful lanes.

The follow-up [5ee78b4 hosted run](https://github.com/daltonstates/TogetherServer/actions/runs/40396913228)
also completed 12 successful lanes and five failed lanes. All 82 source groups
and the packaged QoL API passed. Both automatic-sharing journeys passed with
one explicit Friend Start each, no repeated preparation, returning-owner Start,
signed global-disable enforcement and empty owned-process cleanup. These are
same-runner synthetic results. Core passed its 14 groups but then failed the
same rehearsal-enrollment assertion as the separate Rehearsal lane: that fixture
still expected target-world sync to enroll an unknown world and claim fresh
loopback peer information. Rehearsal now observes automatic membership before
one honestly inconclusive loopback check; the shared-world convergence helper
now uses only GET observations within its existing deadline. Pairing passed
the three UX and 11 loopback stages, then reached a later LAN scoped-removal
failure after 15 passing stages: the returned signed roster still showed the
removed member active. A generated source case reproduces that exact predicate
with the prior baseline rule: its journal starts before the second member's
later valid legacy enrollment, and removal retains that member while leaving
the other member and sibling server unchanged. The correction takes the
current legacy owner's baseline, or walks exact verified ancestors of a
delegated head; it never treats delegated membership as the owner's local
choice or rewrites prior signed records. The corrected scoped-removal and
one-action permission groups pass, including preservation of a delegated
grant, sibling access, the original credential, legacy signed files and the
existing exact-parent schema-3 migration. Related legacy trust-floor,
projection, concurrent-import and revoked-rejoin groups pass. Existing
passed 11 Minecraft cases but its same-name/numeric-port case lost the Java
fixture before readiness; the earlier run lost Bedrock there. Cause remains
unestablished, and later Existing gates were not run. Desktop again retained
the required QuietTime notification block. No candidate from either failed
run was published or installed.

The repeated Minecraft readiness failure now has bounded fixture-only exit and
startup/socket-code diagnostics. They retain an exact recorded process identity
and operation log before run archival, emit no paths or captured text, and
cannot authorize a lifecycle action. A pure early code-only parser check covers
malformed, truncated, wrong-stream, spoofed and out-of-range records. Native
identity, port selection, startup deadlines and readiness requirements remain
unchanged; the unknown exit cause has no speculative workaround.
The strict diagnostic project build, pure parser branch and targeted formatting
pass without launching a fixture or touching production state. The key-fault
prepared-discovery group and strict solution build/formatting also pass; the
final integrated discovery and 315-file prepared-publication reuse groups pass.
The integrated corrections still require their own hosted candidate.

The [4f4eb8a run](https://github.com/daltonstates/TogetherServer/actions/runs/40401773440)
passed all 82 source groups with no skips and the packaged QoL API. Pairing now
passes all 32 stages (UX 3, loopback 11, LAN 18), including the unchanged scoped
member-removal assertions, exact new-host receipt, lost-response cleanup and
empty owned-process cleanup. Twelve lanes passed and five failed. Core passed
14 groups before its rehearsal read hit HTTP 403: the newly reused test helper
needed the same fixed local-read header as the other lab clients. That harness
header is corrected without changing production access checks. Existing stopped
at one unchanged 5,000 ms AppFlows timeout (1,544/1,545 tests); all 22 later gates,
including Minecraft diagnostics, were not run. Release verification now selects
one UI worker, retaining DOM isolation, every assertion, the same timeouts and
zero retries; the recorded local one-worker suite passed all 1,545 cases. This
mitigates observed resource contention without establishing its exclusive cause.
Desktop again retained the QuietTime notification block.

The ordinary automatic-sharing attempt reached one successful Friend Start with
no repeated preparation, then an explicit peer sync returned inconclusive; its
last completed stage label did not identify that later operation. The separate
diagnostic journey passed all nine stages. Retained evidence cannot identify the
failed sync's route or prove a deadline cause. Source review separately proves
that a later discovery deadline discards an earlier authenticated response and
that a stalled first route can consume the whole discovery budget. Bounded
per-route discovery and retained partial-success checks address those cases;
unreachable peers still cannot prove universal freshness or authorize Start.

The 4f4eb8a portable and installer were independently checked without execution:
portable 66,207,987 bytes, SHA-256
`ba903e4fd03269d37d009952cd1ee30adecd2d35ce0ca907cfa9e69bfaf5e4f8`;
installer 60,670,558 bytes, SHA-256
`57694abf7cff11f59c871669f52b19411d31172276550a634d381a650cf8beb4`.
Artifact digest/source, embedded assembly and seven UI assets match; six hosted
installer smoke checks and three wizard choices passed. Both assets remain
unsigned, review-only, unpublished and uninstalled. Evidence is retained under
ignored `local-data/simplified-hosting-4f4eb8a/`; corrected source needs new bytes.

An already prepared ordinary branch can Start locally while its incoming
listener or outside-connection warning remains visible with Connection help.
Local readiness still requires
current signed permission/consent, exact held save/setup and world/process/game
port checks. Only the exact incoming-connection result is advisory; membership
and permission errors still stop Start. The explicit Host instruction registers
outbound completed-save sharing after a second exact identity/role check, even
with the incoming listener off, without enabling it or changing an address.
Explicit incoming-connection preparation and planned moves retain their stricter
route checks, final-save/source fencing and exact receipt requirements.

The rendered ordinary-ready connection-warning regression failed before the UI
change and passes afterward: reads/polls send no writes, one explicit Start sends
its exact request once, and no wizard/preparation/access check is replayed.
Active planned moves and unknown/denied/review permission cases remain blocked.
All 217 related UI tests, type checking and targeted lint pass with pinned Node.
The discovery regression likewise failed before the fix and passes with a
verified cached-source proof after a stalled bootstrap, no payload work, retained
partial authenticated success after the actual overall deadline, no-response
inconclusive reporting and propagated caller cancellation.

The original-owner path had the same extra-copy class: it selected and verified
its produced-here publication, retained it into a per-version recovery cache,
then compared that hash with its original publication before ordinary Start.
Neither Stop nor background sharing prepopulates that recovery cache, so every
new publication repeated the copy and extra free-space reservation. Original
Start now uses the same latest signed ancestry/selection and material recheck,
holding its existing immutable publication through a distinct original-source
proof. No recovery-source path or provenance contract is aliased. Its one-use
observation and exact profile/setup/permission scope are checked in the common
manager gate and again after preparation immediately before the launch call.
Different selected progress keeps the existing recovery/adoption path.
Original Host intent also registers outbound sharing without enabling a listener.

Two fresh original publications with 315 identical payload files and distinct
signed hashes reach the deliberately refusing fake Start with their original
leases held, without a recovery cache, payload transfer or listener change.
Newer/forked progress, current permission/consent/payload, cancellation and late
material/settings/roster changes retain refusal checks. Adjacent recovery-head
selection and prepared-publication groups pass. A temporary old-dispatch replay
exceeded the fixture's ten-second Start bound; it is not claimed as a demonstrated
cache-assertion failure. One original-fixture publication setup later returned
SharedSavePublishFailed before Start; its former assertion lost the underlying
exception, so the exact I/O cause is unknown. Diagnostic reruns passed. The
assertion now retains the full result message; only independent negative cases
were reduced to three signed files, while both positive cases retain 315.

Finished planned successors retain the original profile ID with sharing enabled.
Their Host-card Start now dispatches by existing authority state to the common
manager before the original-owner-only registration path. Classification grants
no permission: missing/corrupt authority, final payload, route and receipt,
fenced originals and rotated TLS still fail their existing gates. The common
roster read is noncreating. The final planned-request and discovery groups pass
with no global owner key or protected-record changes in those planned cases.

The [0da81b4 hosted run](https://github.com/daltonstates/TogetherServer/actions/runs/40413053906)
checks source `0da81b4ac3ae5e0eac7c770610c4183073371261`. Core passes all 14
remote lifecycle groups and both packaged rehearsal groups; the independent
rehearsal lane also passes both groups. Desktop passes its other 12 required
cases and all 14 window-control cases, but real notification delivery/click
remains blocked by Windows QuietTime. Exact cleanup succeeds. Neither packaging
nor these results establishes full acceptance.
The API/source lane passes all 82 feature groups with no skips and all eight
packaged API groups, including its protected-state restart checks.
Existing passes all 27 gates: 1,552 UI tests in 109 files, core checks 114/0,
all 12 Minecraft fixture cases, Shared Worlds 11/0 with zero recovery skips,
Shared LiveSave 5/0, Companion 46/0, the 4,102-version history scenario and every
later gate through update handoff, with no gate left NotRun. No native
readiness failure occurred, so the new failure diagnostics were not exercised.
The previously failing Minecraft cases did not recur; their earlier exit cause
remains unknown. The run finishes with 14 successful and three failed lanes:
pairing's stale preparation assertion, the ordinary sharing refusal and the
QuietTime notification block. It is not a full acceptance pass.

That run's pairing UX passes all three cases. Both network modes then stop at a
stale harness expectation: ordinary Prepare returns LocalHostingPrepared with
Ready true and its incoming-route warning retained, while the harness requires
LocalConnectionSetupRequired. Later pairing stages are unverified for these
bytes. The harness correction preserves the explicit incoming-connection and
planned-move checks and consumes one successful ordinary Start in both modes.
Its new initial-preparation fake regression and adjacent guided group pass on
a fresh root. The first focused run failed earlier at an atomic protected-file
replacement in synthetic publication setup (UnauthorizedAccessException), before
that fixture opened its source or started anything. The actual contender/cause
is unproven; no I/O retry, timeout extension or storage change was added. Both
edited check projects compile strictly and their targeted formatting passes.

The first ordinary automatic-sharing journey passes all nine stages in
161,879 ms. Its second journey refuses original-publication-2-start with
LatestSaveChanged after 6,586 ms, with no active run. A separate diagnostic
journey passes all nine stages in 152,776 ms. All three report empty disposable
jobs. The failed ordinary attempt remains failed; its report lacks the refusal
boundary and cannot establish a cause. A deterministic source schedule fails
before the correction: publishing this PC's current advertisement during
verification invalidates the unchanged exact publication. An initial proposed
stale-background schedule did not pause its snapshot as intended: the manager
returns a cached snapshot when its lifecycle gate is busy. That test was
discarded and replaced with an exact build/publish barrier. The valid regression
establishes the source race, not attribution of the CI failure.

The correction serializes only local advertisement snapshot/sign/publication
per world. Explicit discovery settles current local metadata before its
observation, using retained keys and existing signed routes; it does not create
a TLS identity. Background publishers yield before building during preparation
and discard a captured snapshot if preparation begins before publication.
After the build, exact root/device/key/roster, current access, consent and sharing
policy are rechecked. No transfer or payload hashing holds this gate, and
Refresh remains read-only. Changed material still invalidates Start.

The unchanged-publication regression passes after the fix, including the
prepared 315-file publication at its real verification callback. An exact private,
normally null build/publish barrier proves both early background yield and
post-build discard; its release result is asserted outside the callback so a
swallowed diagnostic timeout cannot pass the test. The discarded manager-gate
test is not counted as proof. Missing and malformed owner/member keys retain
their exact bytes/names without quarantine or creation. Post-build identity,
membership and consent changes refuse publication, and canceled gate waits
release without changing protected identity records. The focused prepared
discovery, published-progress reuse and independent background groups pass.
Strict builds of both edited check projects pass with zero warnings/errors.
The integrated solution also builds with warnings as errors (zero warnings and
errors) and passes whole-solution formatting verification.
The ordinary hosted report now classifies only allowlisted refusal boundaries;
unknown messages remain Unclassified and never authorize a retry. Fresh hosted
verification of this corrected source is required.

All 108 hosting UI captures pass with 667 screenshots, zero failed/skipped/layout
or fatal findings. The retained archive digest, source/run context and all seven
UI assets independently match the exact candidate. Four actual PNGs were sampled
for desktop play, the single original-world overview, narrow 150% save-verification
timings and the unconfirmed-Start action. This is a bounded visual review, not a
claim that every screenshot was inspected or that a real game ran.

The 0da81b4 portable is 66,215,462 bytes, SHA-256
`66eea78802d801637aab28fa63f82e33a8d6fbc576481a3f8c40550297d91b90`;
installer is 60,681,551 bytes, SHA-256
`83b4b8260e96253765b1033c59e397c2699d8665879f22c6e517b49478ceeb8f`.
Static bundle parsing independently matches its application assembly and UI
assets; six hosted installer checks and all three wizard choices pass. Both
EXEs are unsigned, review-only, unpublished and uninstalled. Evidence is retained
under ignored `local-data/simplified-hosting-0da81b4/`. These bytes retain the
failed acceptance cases above and are not a physical repair claim.

The [0c0a069 run](https://github.com/daltonstates/TogetherServer/actions/runs/40418879316)
checks source `0c0a069113cbad644bfd26ce42aa556c9d42e2b3` and finishes with 15
successful lanes and two failures. Source/API passes all 82 feature groups with
no skips and all eight packaged groups. Pairing passes all 32 stages (UX 3,
loopback 11, LAN 18), including one ordinary Start per mode with the same
session/PID consumed, scoped removal, exact receipts and cleanup. Ordinary
Prepare takes 1,234/4,686 ms and Start 707/3,829 ms in these synthetic modes;
these are not laptop preparation or real game-readiness measurements.
Both automatic-sharing journeys pass all nine stages in 126,810 and 118,035 ms.
Each Friend starts once without repeated preparation, and both initial original
publications start successfully. The first returning-owner stage uses two Start
requests in the existing polling test loop; the second uses one. That proves
eventual success, not reliable single-action returning-owner acceptance. Its
first refusal code/boundary was not retained. A late duplicate incoming copy can
change the material fence even with the same selected version; that is a possible
source schedule, not attribution. Product UI polling still does not issue Start.

Core/rehearsal, browser, UI capture, six compatibility lanes and packaging pass.
Desktop again retains the QuietTime notification block. Existing passes all
1,552 UI tests, then records 113 passed core checks and one failed composite
settings/Start/Stop assertion before the first signed roster. The assertion's
message does not prove that Stop itself failed: the individual action results
were discarded. Its gate summary is six passed, one failed and 20 NotRun;
Minecraft and all later gates remain unverified for these bytes. Hosted cleanup
terminates an orphan fixture, whose source case is unproved. The final immutable
hash guard is NotRun after that failure; other completed lanes retain their own
unchanged-hash checks. The old 0da81b4 all-27-gate pass remains separate evidence.
The failed composite is now split into typed, sanitized per-action diagnostics;
no retry, timeout, cleanup or product behavior changed. Next hosted evidence must
identify a recurrence before attributing or claiming a repair of that failure.
The returning-owner report now retains each existing attempt's allowlisted code,
boundary classification, duration and anonymous run/profile-match observations.
The retry count remains visible; no new retry was added. These evidence-only
changes compile strictly and pass targeted formatting. Product source,
dependencies and packaging files are unchanged from 0c0a069. The next build has
a different revision identity and must exercise the revised diagnostics.

The 0c0a069 portable is 66,217,096 bytes, SHA-256
`5bb6ad6a19639db170816fa33c74e057e91eb9afe20e75e6cd671351665c1e92`;
installer is 60,682,444 bytes, SHA-256
`567fa6f66e8fd6b9e7bcb190df696b808089bed3f16829e840cc4bddf4e95331`.
Archive/source/run, native resources, embedded assembly and assets/checksums were
independently verified without loading or launching the app. The six installer
checks and three wizard choices pass. All seven UI assets are byte-identical to
the earlier four visually sampled images; fresh PNGs were not visually sampled.
Both EXEs remain unsigned, review-only, unpublished and uninstalled. Evidence is
under ignored `local-data/simplified-hosting-0c0a069/`. No physical repair or full
acceptance is claimed.

The owner reconfirmed the unattended target-world-only session and main world's
stopped state. Static read-only inspection still finds one installed/running
0.3.3.15 process with the published source/hash below. No new app was installed
or launched. New Ready, graceful Stop, new exact receipts and saved restart
remain physical acceptance work after manual installation of a reviewed new
candidate; source/CI success cannot close those gates.

Local verification of this simplification compiles the solution with zero
warnings/errors and passes whole-solution formatting. The first full source
run recorded 80 passes and two failures; the scheduling/rejoin correction and
the new Host-screen fixture's typed fake-driver refusal now pass focused
retests. The final background group also covers nested whole-operation
priority, canceled joiners, changed registration and independent Host polling.
The 315-file regression exercises both Host and Join without a recovery-cache
copy, with an immutable source held at the refusing fake driver and no managed
run. One internal symbolic-link scenario remains skipped for local account
capability. Both initial complete UI attempts recorded 1,544 passes and one timeout in
different unmodified navigation cases; each timed-out case passes in isolation
without extending its timeout. The final one-worker run passed all 1,545 cases
with unchanged timeouts. Hosting/progress cases pass. Typecheck, lint,
production bundle and all 59 pure render/fixture checks pass. The four final
focused groups (rejoin, offline-owner catch-up, prepared discovery and independent
background sharing) pass, including the registration and sync corrections.
The corrected solution builds with warnings as errors and passes whole-solution
formatting. Fresh hosted validation of the corrected source remains required;
these local results do
not erase the initial failures or establish physical game acceptance.

The current 0.3.3.16 source removes the redundant recovery-cache copy from an
already prepared Start. A separate held-source proof derives only this exact
protected local branch's existing publication or retained base; callers cannot
supply a path or alias a publication as a recovery cache. It verifies the
immutable source files, current signed ancestry/selection, local permission,
consent and full decoded setup. The lease stays held through the final manager
gate, which checks scope and material observations around working-tree hashing.
A missing, newer or changed source requires explicit preparation and invalidates
readiness. Ordinary recovery-source storage checks are unchanged. Start now
uses measured inactivity within the original ten-minute intent limit and keeps
an unconfirmed Start blocked until matching terminal evidence and fresh exact
status arrive. Unconfirmed-result guidance consistently directs the owner to
check that attempt rather than retrying it. No 0.3.3.16 binary has run on either PC.

The generated regression uses ordinary preparation at version 3, a real
synthetic Rolling backup and strict publication 4 with 315 files. The exact
published 0.3.3.15 Start source and prepared resolver, with a temporary compatible
helper signature, reproduce a new recovery cache and measured copying. The
fixed case reaches only the refusing fake-driver boundary, with the immutable
publication held, all 315 files verified and no recovery copy or managed run.
Seventeen refusal variants retain current permission/expiry, consent, source,
selection, setup and material-observation fences; separate cases cover caller
cancellation, cancellation during hashing, released native handles and disposed
proofs. Missing protected branch history retains its strict read exception;
the local route maps that to a fixed rejection without rebuilding history.

All 80 source groups pass in a shorter disposable workspace, with one internal
symlink scenario skipped for local account capability. The initial default-path
attempt failed before Start while creating a synthetic backup, reporting staging
access denial; its exact I/O cause is not established. Strict solution compilation
has zero warnings/errors. All 1,543 UI tests pass after two navigation tests await
their existing asynchronous transitions without extending timeout budgets. The
final guidance change passes 144 focused tests, typecheck, lint and bundle.
All 58 pure renderer-plan checks and changed-code formatting pass. The whole-
solution formatter retains findings in the unmodified
`SimpleSharedHostingJourney.cs` fixture, so whole-solution formatting is not
claimed. Hosted candidate validation is in progress; physical hosting and saved
restart remain separate acceptance requirements.

The real 0.3.3.15 laptop process independently matches the published EXE
version/source/hash and uses the same account and production root. Authenticated
metadata reports 0.3.3.15 on the main Host; its actual EXE path/hash remain
inaccessible. The owner confirmed both updates and the main world stopped.
One native Share click at 2026-10-26 16:45:13 UTC completed in 15.47 seconds,
publishing signed child 4 of base 3. Independent read-only checks verify its
signature, branch proof, strict parents and all 315 files (119,452,903 bytes).
A subsequent observation confirms the main Host's exact owner-domain completed-
copy receipt under the current roster. This proves protocol-level completed-copy
acknowledgement, not direct inspection of its filesystem or a game load. The
second eligible external member has no known route/contact; its availability
and delivery remain unconfirmed. The owner also returned true for exact world,
original owner, receive consent, receiving permission, sharing and sufficient
reserve on the main Host's actual data-root drive. Receipts carry no completion
timestamp, so the exact transfer duration is unavailable.

One native Start click at 16:59:16 UTC then copied that same publication into
the recovery-source cache. After 120 seconds the UI canceled; progress ends
Failed with 154 of 315 files and 58,779,344 of 119,452,903 bytes copied. No game
process launched. Source inspection identifies the unconditional full-retention
resolver before the prepared continuation comparison: received base 3 cannot
reuse the cache for publication 4. This is a measured copy/cancellation result
and deterministic source-path diagnosis, not an attached runtime stack. The
working world, both original backups, their records and failure history remain
unchanged in stable read-only comparison. Share publication/delivery success
does not establish Start, graceful Stop of a new session, client join or saved
restart; those physical checks remain open.

Prerelease 0.3.3.15 addresses a directory/publication lock-order inversion found
during the real 0.3.3.14 test. The installed 0.3.3.14 laptop EXE matched that
release's hash/source and used the same production root, with 10.37 GiB free. Its initial
check correctly reports `LocalStoppedProgressNeedsSharing`, current permission
and consent, and the exact retained local profile/branch. One native **Share
stopped progress** click at 2026-10-26 14:54:56 UTC creates a third backup record
bound to the original backup and stopped operation, but does not confirm a signed
publication or receipt. The UI's absolute two-minute deadline releases its busy
state and reports an unconfirmed result. No game starts. The instance endpoint
remains responsive; Friend snapshot and progress reads time out after ten seconds.

Source shows opposing edges: directory projection holds `FriendService.sync`
while waiting for `SharedWorldMutationGate`; stopped publication holds the world
gate while its final selected-connection callback requests `FriendService.sync`.
These synchronous monitor waits cannot be canceled by aborting the HTTP request.
No runtime stack was attached. A generated-data regression using the exact old
directory source reproduces blocked snapshot/progress/selection reads and fails
at the expected bounded assertion; its finally block releases and drains every
worker. The fix retains the service/links, snapshots connection scope under the
list lock, projects rows outside that lock, and rejects changed/disposed scope
before returning. Publication's final selection, current permission/consent,
signature and ancestry checks remain unchanged. The fixed concurrency regression
passes, including changed-selection rejection, fresh rows and balanced retains.
Strict solution compilation passes with zero warnings/errors. The broader local
suite records 74 passes and five backup-related failures; one focused rerun passes
two of those groups and still fails three. Initial catalogs show `BackupFailed`
and generated staging-directory access denial, before sharing authorization;
one reported operation is stage promotion. Backup code is unchanged from
0.3.3.14, and no external cause has been established. These failures remain
recorded for independent hosted validation. One internal source-link case lacks
local symlink capability. The later physical publication and receipt results
are recorded above; hosting and saved restart remain unaccepted.

The [frozen 0.3.3.15 run](https://github.com/daltonstates/TogetherServer/actions/runs/40349982642)
uses source `4179a1ab1b38c2a45bb407171fc40750640a62ea`. Fresh Windows checks
pass all 79 source groups and eight packaged API cases, including all five
locally failing groups. This does not establish the local I/O failures' cause.
UI tests 1,528/109 files, hosting captures 108/108, server-access captures 36/36,
browser, rehearsal, all six compatibility/update lanes and packaging pass.
All six installer checks and three wizard choices pass. Existing records 12
successful gates, one Custom-driver gate failure (`CustomStopTimedOut`, 5/6
checks), and 14 later gates NotRun, including the mandatory packaged core journey.
Core source checks 114/114 do not substitute for that missing journey. The
[dedicated core verification](https://github.com/daltonstates/TogetherServer/actions/runs/40354145322)
subsequently passes all 14 original core groups and the appended two-group
rehearsal, in order, with one invocation and exit zero. Verification source
`1d800d094e92276c2e3462fdb28236e5e46a8be4` changes no product/build inputs or
dispatcher sources. All 35 candidate/fixture/proof files remain unchanged. No
cleanup warning is observed; global process emptiness is not asserted. This
supplies only the missing required core evidence, leaving the other failed and
unrun gates unaccepted.
Automatic hosting fails initial preparation before any Start; its separate
diagnostic attempt fails too. Pairing UX 3/3 and loopback 11/11 pass; LAN records
three passes, governance GET HTTP 409 failure and 14 stages NotRun. The response
code was not retained, so its exact refusal predicate remains unavailable.
Windows QuietTime again blocks required OS notification delivery/click proof.
No failed lane is erased or accepted by the independent source/UI passes.

The published [0.3.3.15 prerelease](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.15)
is release ID `424461125`, published 2026-10-26 16:01:38 UTC. Annotated tag
`76420a028d3197d60add50c9e7441be3183f9cc2` points to source `4179a1ab` above.
Portable is 66,191,977 bytes, SHA-256
`dfd981452639c7143f34fe24748bc3ae09195b4bd322b74ad2d1b31027483958`;
installer is 60,654,227 bytes, SHA-256
`b9a0be49b6ea6249ccea2e445070aa58ac2d92b0acc02a6b974fae635be42cc9`.
All four public downloads/checksums, version resources and tag/source verify
anonymously. Both EXEs remain unsigned. All 29 earlier releases/assets are
preserved; inventory is 30 and stable/latest remains 0.3.3. The owner manually
updated both PCs, retaining data/pairing/history. The physical 0.3.3.15 test above
supersedes the earlier stalled-app state without accepting the remaining stages.

Stable private observations before and after the stalled action contain 38
metadata files. Signed journals, floors, indexes and lookups are byte-identical,
with no pending roster record. Seven other protected files were rewritten. All
315 working files, both original backup trees, their records and failure history
remain unchanged; the new recapture is additional. Independent read-only hashing
verifies all 315 new payload files, its protected setup hash, decoded setup equality
with the original Stop and both original-backup/stopped-operation anchors. Its
publication root, staged publication and latest pointer are absent. Evidence stays owner-only
outside Git. The timed-out dialog was closed without repeating Share or Start.

Candidate 0.3.3.14 adds an explicit **Share stopped progress** action for a
retained local session whose completed Stop has not been published. Its save
check is actionable and cannot claim Start readiness. Refresh keeps the failure
and repair guidance visible. Sharing accepts the exact selected local profile
and signed branch, carries no Start intent, and requires a separate Start click
after successful publication and fresh checks. Current permission, consent,
history and running-state changes take priority over retained guidance.

Recovery preserves the old backup and every world/history record. It requires
an unambiguous latest typed graceful-stop/Completed archive, definitive absence
of that process, no active or uncertain overlapping writer or pending world
operation, the latest Rolling record by append order, and exact whole-world and
decoded full setup equality with its completion metadata. A changed old payload
remains rejected. A new candidate must match the expected files/setup before
completion/cataloguing and then pass ordinary current-authority, key, consent,
fork, orphan and signed-parent publication checks. It skips retention and keeps
failure history. Fresh recapture metadata binds the original backup and stopped
operation for retry; new real Stop archives bind their completed backup ID.
Legacy time intervals are corroborating local metadata, not signed exit proof.

The physical laptop is now independently confirmed as the published installed
0.3.3.13, source `1448f87d8acf156af883e96ea6c2ad2fda662d54`, SHA-256
`831e0206801d5f326e54b403391512fdca26d4810c9cd607f22cc197f66ded79`, same Windows
session and production default root. The owner reports both PCs updated and
main world stopped; actual remote EXE path/hash remain inaccessible. No app-data
reset, re-pairing, key replacement, trust-floor reset or world overwrite was used.
An automatic guarded replacement/relaunch was rejected before execution by
approval review; the owner performed the update manually.

The prior installed 0.3.3.12 test hashed all 315 received files (119,452,903
bytes) in 1.647 seconds. Its UI hit the absolute 120-second Start deadline.
The actual guarded backend Start succeeded after 196.3 seconds, reached real
Valheim Ready with fresh trusted zero occupancy, and stopped gracefully in 6.5
seconds with the exact process gone. Its rolling backup was catalogued complete
but publication rejected it: pure verification found `ReadAndVerifyManifest ->
VerifyTree`, `InvalidDataException`, exactly one reviewed text file whose
47 bytes/hash differed from the manifest's 43. Paths and count matched.
Its writer/replacement mechanism is unestablished. The failed backup is kept;
a separate normal offline manual checkpoint verified every payload/setup hash.

The real 0.3.3.13 UI check completed in 3.32 seconds with the same prepared
profile/branch, **Start hosting**, no fabricated zero-byte row and no timeout.
The explicit UI Start at 23:02:20 UTC failed after 23.58 seconds in Verifying,
before any game process. All 315 protected-source file checks completed. A
non-mutating deterministic replay identifies the local-continuation predicate:
the started working copy has five file differences from shared base version 3,
while no published continuation exists. Pure replay of `VerifiedSuccessorCopy`
returns false at its exact file-inventory predicate: five working-only paths
and five base-only paths, with 315 files in each. Its caller would throw
`InvalidDataException`; this is deterministic predicate replay, not an attached
runtime stack. Passive status then returned Prepared
and the progress store used its generic interrupted fallback, hiding the cause.

Private read-only comparison shows all 315 working files exactly match both
the last Rolling completion manifest and the healthy manual manifest; one old
Rolling payload entry fails its manifest. The full DPAPI-decoded setup snapshots
match. Exactly one matching summary-version-1 archive has typed GracefulStop,
Completed backup and Ready observed, a matching game/world, ordered Stop/backup/
archive interval and a definitively absent prior process. That legacy archive
has no exact backup-ID field. Evidence is owner-only outside Git; diagnostics
instantiate no LocalData, recovery or key helper against production. Metadata
captures use repeated bytes/timestamps rather than an atomic app lock. No
0.3.3.14 binary has been tested physically yet, and no new signed save or exact
owner/online-peer delivery receipt has been proved.

Current 0.3.3.14 validation passes all 78 source/synthetic groups and all 1,528
UI tests in 109 files. The final recompiled hosting/recovery groups pass;
unreadable publication metadata cannot project a prepared result. The 181
focused UI checks and 57 pure fixture checks pass. The render inventory retains
all previous 99 captures and adds nine stopped-progress captures, totaling 108;
all 108 now pass the unchanged-candidate hosted retest. Strict solution compilation has zero
warnings/errors; formatting, TypeScript, lint, production bundle and desktop
diagnostic isolation pass. One internal source-link scenario is skipped for
this local account's missing symlink capability. The positive fake-peer late
publication check passes: source, owner and observer select child 4 by ancestry;
exact owner/member receipts and payloads verify while the closed terminal and
original world/default pointer remain unchanged.
The source regression uses generated identities and disposable files.
No fixture, installer or full-release suite runs on this laptop. Real client
join, recognizable saved change, new-save receipts and saved restart remain
separate physical acceptance.

The [0.3.3.14 candidate run](https://github.com/daltonstates/TogetherServer/actions/runs/40248543185)
tests application source `774751679a61835dd532b8275283bcf4025926cd`, portable
SHA-256 `debcc1aef87cccfdc77147e50c1ea53dad6788701ef81f61ee288c44b0377287`.
All 27 Existing gates pass, including core checks 114/0, the required packaged
core remote journey 14/0, Shared Worlds 11/0 with zero recovery scenarios skipped,
Shared LiveSave transfer 5/0, Companion 46/0 and 4,102-version history catch-up.
API/source, browser, rehearsal, all six compatibility/update lanes and packaging
pass. The six installer checks and three wizard choices pass. Its original
automatic-hosting, pairing and hosting-capture failures remain recorded.
Native notification acceptance remains blocked by QuietTime: 12 required cases
pass, one required case is blocked, one development-startup case is inapplicable;
all 14 separate window-control cases pass. Full acceptance flags remain false.

The [unchanged 0.3.3.14 retest](https://github.com/daltonstates/TogetherServer/actions/runs/40250355051)
uses verification source `7395c214a2a48f05d6d54d165e787e416b642ee4` without
changing product/build inputs or the packaged EXE. Both ordinary nine-stage
hosting handoffs pass, each advancing version 2 to 3 with exact cleanup; no
diagnostic attempt was needed. All 108 hosting captures pass with 653 screenshots,
zero failed/skipped/layout/fatal findings, including the intentional fixture
error states. This does not claim every screenshot was visually reviewed.
Pairing UX 3/3 and loopback 11/11 pass. LAN passes 16 stages, fails lost-response
cleanup with `JsonException`, and leaves the final receipt stage NotRun. Exact
tracked process identities are exited and job cleanup is empty. The JSON boundary
is under investigation; this run remains failed.

The owner subsequently requested GitHub prerelease publication so both PCs can
update for the physical test. The published
[0.3.3.14 prerelease](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.14)
is release ID `424371648`, published 2026-10-26 14:46:07 UTC. Annotated tag
`01facce1c27db9831a54c317a1c45944cc10fc66` points to the exact application source
above. Its portable is 66,191,617 bytes with the candidate hash above. Installer
is 60,653,522 bytes, SHA-256
`04a45a38cae4598e869fded35c5337e8348bf2438c809e45c630f1f482e45b8a`.
All four public assets/checksums, version resources and tag/source were verified
anonymously. Both EXEs remain unsigned. All 28 earlier releases/assets are
preserved; inventory is 29 and stable/latest remains 0.3.3. Publication is for
coordinated testing and does not accept the remaining LAN or physical tests.

The existing 0.3.3.13 EXE is preserved separately. Read-only comparison on
2026-10-26 still finds all 315 world files, both original backups, their records
and failure history unchanged. No game is running on the laptop. Automatic app
replacement was rejected by policy; the owner has been asked to update both PCs
manually, keeping app data and pairing. Actual 0.3.3.14 hosting, new signed-save
receipts on the main Host and eligible online peers, and saved restart remain
unverified. The failed LAN JSON boundary now has narrow verification-only
sub-operation/HTTP/content-kind/allowlisted DTO-path observations; those changes
compile with zero warnings/errors but have not had a further hosted run.

The published [0.3.3.13 prerelease](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.13)
is release ID `423712444`, published 2026-10-25 22:57:55 UTC, annotated tag
`517e3dfac428fefbd4bc8301984e7852b2c05a22` pointing to the source above. Its
portable is 66,177,213 bytes with the hash above. Installer is 60,636,705 bytes,
SHA-256 `931e005cd546389f595266713134f499f4ad1a33dfd9db50d511a701e2139290`.
All four public assets/checksums, version resources and tag/source were verified
anonymously; installer version resource strings contain padding. Both EXEs are
unsigned. All 27 prior releases/assets were preserved; inventory is 28 and
stable/latest remains 0.3.3. Version/tag 0.3.3.14 were checked unused.

The [0.3.3.13 candidate run](https://github.com/daltonstates/TogetherServer/actions/runs/40240756633)
passed all 27 Existing gates, core checks 114/0, core remote journey 14 groups,
Shared Worlds 11/0 with zero recovery scenarios skipped, Companion 46/0 and the
4,102-version history catch-up. It passed 77 source groups, 1,509 UI tests in
109 files, strict compilation, formatting, TypeScript, lint, bundle, two ordinary
nine-stage handoffs, six compatibility/update lanes, six installer cases and
all three actual wizard choices. Browser has 14 passes and three unexercised
boundaries. Local source-link cases were skipped for account capability; hosted
source checks exercised them. Full native acceptance is blocked by QuietTime:
12 required passes, one notification block, one development-startup skip; all
14 separate window-control cases pass. Packaging acceptance flags remain false.

That original run also failed pairing preparation expectations and three
hosting timeout captures. The [unchanged-candidate retest](https://github.com/daltonstates/TogetherServer/actions/runs/40242713141)
uses separate verification source `03c51f5666e2dc3d5f73b6440d126d5881638ae0`.
It passed 30 pairing stages, failed one later LAN lost-response cleanup Start
with LatestSaveChanged and left one receipt stage NotRun. All exact cleanup
reports show zero remaining disposable processes. Its 99 hosting attempts
include 12 captures, 84 intentional-error captures and three failures: two
aborted passive progress reads and one changing scroll capture. Start-count,
no-Prepare and no-replay assertions passed. Those failed runs remain evidence;
harness correction and new-candidate verification continue. Capture completion
alone is not a claim that every screenshot was visually reviewed.

## Repository maintenance

Keep application source, tests and fixtures, dependency locks, maintained build
and CI tools, current guides, and these engineering rules. Generated packages,
test data, logs, screenshots and reports belong under ignored `local-data/`.
Keep current limitations here instead of adding chronological delivery files.
Record release-specific candidate identities and measured evidence with the
release or CI run; do not commit raw results or generated UI prototypes.

The 2026-10-24 cleanup removed 105 obsolete tracked files and about 9.1 GiB of
generated output: delivery records, prototype atlases,
screenshots, worker handoffs and version-specific release recovery tools.
The UI review workflow builds the checked-out source instead of downloading a
frozen past candidate. Existing app changes, running app files and worktrees
with uncommitted work were preserved. Validation passed all 46 pure renderer
checks, 137 local documentation links/anchors, three workflow files, 20
PowerShell files and 21 embedded PowerShell blocks, plus whitespace checks.
The 28 existing changed/untracked files outside the edited documentation index
remain byte-identical; its ongoing feature link is also preserved. No app,
game or browser was launched. Native, physical-network and real-game
acceptance were not rerun for this maintenance-only change.
