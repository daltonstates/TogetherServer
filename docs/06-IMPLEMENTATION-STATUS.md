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

Candidate 0.3.3.13 addresses the prepared-copy Start loop. A freshly checked
prepared copy now offers **Start hosting**, sends one exact guarded Start and
does not repeat Prepare. Typed blockers and timeout/navigation replay guards
remain enforced. Start resolves the latest save once after address registration;
its ten-second metadata budget covers its own world's waits and network work.
It does not refresh every saved Host or repeat payload exchange. The immutable
source, actual working world, current permission/consent, ancestry and final
in-gate observation remain verified. Repeated announcement counters alone do
not invalidate an otherwise identical material save observation.

Per-world metadata and challenge/exchange serialization preserve cursor and
single-use challenge rules without holding metadata through payload work.
Address registration advances only an already registered exact world's verified
local roster and effective consent. Membership refresh merges current values
atomically. Consent reads preserve retained connection identity and withdrawal
without waiting for unrelated network-held connection gates. A confirmed or
not-yet-due acknowledgement does not rehash a save; emitted receipts still
require current verified payloads, with exact held-lease reuse within a request.

Backups now verify the copied destination and protected setup before creating
completion metadata, promotion or catalog membership. Changed candidates return
`BackupCompletionIntegrityFailed`; prior backups and live worlds stay intact.
The generated regression covers the observed 43-to-47-byte configuration
mismatch, equal-size tampering, protected-setup changes and missing setup.

The owner explicitly approved an unattended laptop session limited to this
world, with the main Host stopped. The laptop is independently confirmed as the
published 0.3.3.12 installed EXE, hash
`0429caa78177872c5783ca9d5d071896190c60a8d6d80b1fbb731c035390bbac`, production
default root. The connected main Host reports 0.3.3.12 and compatible protocol;
its actual running EXE path/hash is not remotely available. No app-data reset,
re-pairing, key replacement, trust-floor reset or original-world overwrite was
used. Read-only evidence stays owner-only on this laptop, outside Git.

The actual 315-file, 119,452,903-byte received copy passed every file hash in
1.647 seconds. An observed UI Start ended at its exact 120-second deadline.
One guarded Start against the installed app then succeeded in 196.3 seconds:
first peer sync/verification at 84.8 seconds, address phase at 90.9, second peer
sync at 124.1, final verification at 184.3 and Start at 193.3. The real server
reached Ready with a fresh trusted zero count. Graceful Stop completed in 6.5
seconds; the exact managed process exited and no Valheim server remained.
Native automation read the dialog but clicks failed with `coordinate input
geometry is unavailable`; fixed same-origin app endpoints exercised the actual
installed backend instead. No fixture, installer or full-release suite ran on
the laptop.

That Stop completed a rolling backup but sharing refused it. A reviewed pure
read-only verifier identified `ReadAndVerifyManifest -> VerifyTree`,
`InvalidDataException`, ordered entry mismatch: exactly one reviewed text
configuration was 47 bytes instead of its manifest's 43 and had a different
hash. All 315 paths/counts matched. The source working configuration and captured
setup remained 43 bytes. The failed backup remains preserved; its writer or
replacement mechanism is unestablished. A separate normal offline checkpoint
completed and independently verified all payload/setup hashes. It did not
rewrite the failed backup or any signed record. Neither a signed newer save
nor delivery to the main Host is proved by those facts.

Current validation: four focused source groups pass, including guided hosting,
replication, completed-save delivery and bounded discovery; the two new
integrity/discovery groups pass with the material-observation cases. All 1,509
UI tests in 109 files pass with two workers. TypeScript, production bundle and
lint pass; compilation reports zero warnings/errors. The full 77-group source
run passes. Hosted candidate review and actual fixed-binary retry remain pending.
Local source-link cases are skipped for this account's missing symlink
capability. Real client join, recognizable saved change, exact new receipts on
every eligible online updated PC, and saved restart remain unverified.

No 0.3.3.13 tag or prerelease exists yet; both were checked unused. The published
[0.3.3.12 preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.12)
uses source `420c90963d6497f19735fca7eab585f4b9267a90`, tag object
`b008e574e6f6662b40a6615f2677313eb730a57e`, release ID `423645454`, published
2026-10-25 20:32:59 UTC. Its portable is 66,171,750 bytes with the hash above;
installer is 60,633,064 bytes, SHA-256
`78d05d7df9b2b0095b3fde5ef141c622e67fe3494cc632d8290a576446ede3c2`.
All four assets/checksums, version resources and exact tag/source were verified
anonymously. Both EXEs are unsigned. The inventory contains 27 releases;
all prior records/assets were preserved and stable/latest remains 0.3.3.

The [0.3.3.12 candidate run](https://github.com/daltonstates/TogetherServer/actions/runs/40225799422)
passed 75 source groups, 1,501 UI tests, 93 hosting captures, pairing, automatic
sharing, compatibility and packaging. Two Existing attempts failed separately
on a receiving-stage access denial and warned-copy Stop; their causes remain
unestablished. The [unchanged-candidate replay](https://github.com/daltonstates/TogetherServer/actions/runs/40229076009)
passed both isolated cases and all 27 Existing gates, including core 114/0,
Shared Worlds 11/0, Companion 46/0 and 4,102-version history catch-up. Later
passes do not erase those failures. Native notification acceptance remains
blocked by Windows QuietTime: twelve required cases pass and one is blocked;
all fourteen separate window controls pass. Full native acceptance remains
incomplete; packaging flags remain false.

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
