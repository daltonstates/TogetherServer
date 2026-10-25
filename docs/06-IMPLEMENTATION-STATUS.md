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

- The laptop's extended error identifies signed-ancestry import. Two concurrent
  import schedules reproduce this failure in disposable source checks: an
  anchored response carrying an intermediate legacy snapshot after peer sync
  advanced, and an exact revision committed elsewhere between checking and
  appending it. Both paths are repaired without resetting history. The owner
  reports the same error on the laptop after the latest retry; its running
  binary identity and exact failure have not been independently captured.
  The physical laptop failure remains unresolved. Revoke/reinvite alone did
  not reproduce it. Continue with the
  [laptop diagnosis handoff](development/laptop-sharing-history-handoff.txt).
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

[Preview 0.3.3.10](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.10)
is published from application source `c84973f2096f1b697e978e2badf54fc999a3a5de`.
Both concurrent-import failures were reproduced before their fixes. The new
anchored-page regression covers six rejection boundaries, and exact historical
replays preserve the protected journal and newer revocation.

All 72 source/synthetic groups, 1,483 UI tests in 109 files, compilation,
TypeScript, formatting, lint and production bundling pass. The
[Windows run](https://github.com/daltonstates/TogetherServer/actions/runs/40143882850)
passes all 15 functional jobs on the original attempt, including all 27 ordered
Existing gates, 114 core checks, 11 shared-world journeys with no recovery
scenarios skipped, and 46 companion checks. Hosted source-link cases ran;
the local account's unsupported synthetic-link cases remain separately skipped.

Pairing passes UX 3 outer/10 inner, loopback 11 and same-PC assigned-LAN 18
stages. Signed sidebar clicks reach the hosting PC before and after Stop.
Two automatic-sharing runs each pass nine stages with one Friend and one owner
Start. Six legacy compatibility/update-handoff lanes, six installer smoke
cases and three actual wizard choices pass. Access review passes 36 plans and
164 PNGs with no failures, skips or layout findings; three images were inspected.
Browser evidence retains 14 passed and three unexercised journeys, 148 layouts
and zero browser/request errors.

The raw Desktop job fails solely on the required QuietTime notification block:
12 required native cases pass, one remains blocked and the development-startup
case is inapplicable. All 14 window-control checks pass. The original package
manifest retains incomplete acceptance flags, and the builds are unsigned.
Full native acceptance remains incomplete.

Anonymous downloads verified all four public assets, checksums, source/tag
identity and unchanged prior releases; stable/latest remains 0.3.3. The owner's
active desktop and 29 unrelated pending files remain preserved. Physical
laptop diagnosis, adopted-copy preparation and game/network acceptance remain
open as listed above. Exact hashes and run/artifact identities stay with the
release and CI.

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
