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
  and interrupted transfers cannot. Starting a prepared server stays explicit.
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

- The laptop's actual installed process matches the published 0.3.3.10 version,
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
- The bounded projection fix still requires owner installation and a fresh
  physical-laptop retry with existing data. Source/hosted passes do not establish
  that retry, save receipt, readiness, game join or saved restart. The earlier
  [laptop handoff](development/laptop-sharing-history-handoff.txt) records the
  investigation's starting constraints, not a current success claim.
- **Known adopted-copy preparation limit:** the permission prompt, consent
  alias and cached-version identity have focused source coverage, but preparing
  from an already adopted successor can still fail at the legacy received-copy
  or branch lookup. Complete successor rehosting remains open. Remote Stop
  authority does not grant local receiving or hosting consent.
- Physical two-PC/LAN/WAN transfer, real game joins, recognizable saved changes,
  graceful Stop/restart, recovery and valued-world acceptance remain separate
  checks. Hosted or synthetic results do not close them.
- Minecraft remains fixture-tested. Factorio and Terraria remain previews;
  Terraria has no trusted occupancy and cannot use Friend or automatic Stop.
- Real-game live capture requires the
  [six-stage exact-installation acceptance](10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md).
  No real installation is accepted by source or fixture checks.
- Current release builds are unsigned. The latest recorded hosted native check
  could not prove notification delivery, click or navigation because Windows
  was in QuietTime. Full native acceptance remains incomplete.

## Current delivery

[Preview 0.3.3.11](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.11)
is published from application/check/build source
`cbf67ee13edde8e314aabb0976606c7266a81e2d`. The original laptop failure is
reproduced before the fix with generated identities and the exact friendly
error. The captured private records also satisfy the new pure proof predicate;
no production store was constructed for that comparison.

The new group passes 65 scenarios: 21 positive and 44 rejection cases. These
cover exact parent/baseline proof, retained legacy bytes and trust floors,
pending/file-stage restart recovery, unchanged ordinary pending formats,
damaged protected evidence, newer revocation, symmetric peer exchange and
long-page parent cursors. An older numeric-cursor assertion now requires the
signed boundary proof replay. Local formatting findings in the new check file
were corrected before delivery.

All 73 source groups, 1,483 UI tests in 109 files, compilation, TypeScript,
lint, formatting and production bundling pass. The local account's synthetic
source-link cases are skipped for lack of symlink capability; hosted source
checks exercise them without that skip.

The [Windows run](https://github.com/daltonstates/TogetherServer/actions/runs/40216257190)
has passing results for all 15 functional jobs. The Existing gate passes core
114/0, Shared Worlds 11/0 with no recovery scenarios skipped, Companion 46/0,
and 4,102-version signed-history catch-up. Six compatibility/update lanes,
six installer cases and all three actual wizard choices pass. Two ordinary
shared-hosting handoffs pass against the unchanged candidate.

The first same-PC LAN lab passed 16 stages, then cleanup's fixture Start was
rejected as `LatestSaveChanged`; its final receipt stage was not run. The
changed observation field was not captured, so no more specific cause is
claimed. The targeted retest uses the exact same candidate SHA-256 and passes
UX 3/3, loopback 11/11 and LAN API 18/18 with zero skips and no remaining
disposable processes. Both first-failure and retest artifacts remain separate.

The overall workflow remains red because Windows QuietTime blocks required
OS notification delivery/click/navigation. Twelve required native cases pass,
one is blocked and the development-startup case is inapplicable. All 14
window-control cases pass. The original packaging manifest retains
`fullyAccepted=false` and `nativeNotificationVerified=false`; both builds
are unsigned. Full native acceptance is incomplete.

Anonymous downloads verify all four published assets, adjacent checksums,
versions and annotated tag/source identity. All 25 prior releases and their
assets are preserved; stable/latest remains 0.3.3. The laptop's foreground,
running app, original metadata and worlds were not repaired or reset by tools.
The new version still needs an owner-controlled physical retry; permission,
copy/receipt, readiness, game join and saved restart remain separate evidence.

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
