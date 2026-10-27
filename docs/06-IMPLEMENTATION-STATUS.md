# Current implementation and acceptance

This is the maintained status and delivery note for the current source.
Use the [user guides](README.md), [engineering rules](00-PRODUCT.md), and
[acceptance requirements](04-IMPLEMENTATION-AND-ACCEPTANCE.md) for behavior and
required checks. Previous delivery logs and release evidence remain in Git
history; published versions have their own
[release notes](https://github.com/daltonstates/TogetherServer/releases).

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

The next .17 slice adds read-only transfer observations to the existing shared
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
