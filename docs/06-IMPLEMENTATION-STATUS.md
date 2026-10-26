# Current implementation and acceptance

This is the maintained status and delivery note for the current source.
Use the [user guides](README.md), [engineering rules](00-PRODUCT.md), and
[acceptance requirements](04-IMPLEMENTATION-AND-ACCEPTANCE.md) for behavior and
required checks. Previous delivery logs and release evidence remain in Git
history; published versions have their own
[release notes](https://github.com/daltonstates/TogetherServer/releases).

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

Candidate 0.3.3.15 addresses a directory/publication lock-order inversion found
during the real 0.3.3.14 test. The installed laptop EXE matches that release's
hash/source and uses the same production root, with 10.37 GiB free. Its initial
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
local symlink capability. No 0.3.3.15 physical result or release exists yet.

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
