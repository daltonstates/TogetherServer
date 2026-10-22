# Valheim shared-save publication and 0.3.3.4 prerelease

Owner direction, 2026-10-22: both PCs run 0.3.3.3, but the Host reports that its
completed backup cannot be published and the laptop has never received a save.
Diagnose and fix that failure, test it and publish another prerelease. The next
four-part version is `0.3.3.4`; stable/latest remains `v0.3.3`.

## Confirmed cause

Read-only Host checks found completed rolling backups with setup checkpoints,
valid current signed receiving permissions, sufficient space and no published
shared save. No game, world, settings, sharing grants or network configuration
was changed during diagnosis. Private paths, player IDs, keys and production
logs are excluded from this record and Git.

The backed-up Valheim permitted-player file has a normal full-line `//` comment.
`SharedWorldPortableSetupReader` previously excluded only `#` comments. It
treated the header as a player entry, then correctly refused the slash in that
entry. `PublishAfterStop` reduced the resulting setup-validation failure to
the same persistent free-space/integrity warning as every other failure. The
first shared save therefore never existed for the laptop to receive.

## Delivered change

- Trimmed full-line Valheim `//` comments are excluded along with existing `#`
  comments. Real player entries retain their complete validation, exact values,
  duplicate detection and count/length limits. Inline comments are not stripped.
- The original owner can choose **Share latest backup** beside a missing or
  failed shared-save status. This retries an existing completed rolling backup
  after Stop, with its original protected setup and payload. It creates no new
  backup and does not read or change the live world, grants or process state.
- The fixed local-only publication request accepts only `{}` and a saved
  profile ID. Friend mode, unresolved permissions/history, inherited sources,
  recovery and pending moves remain blocked. No companion endpoint is added.
- Backup selection follows durable catalog completion order. Equal timestamps
  or a backward-moving clock cannot select older progress. Advancing an existing
  shared head requires its retained post-Stop baseline; unproven ordering fails
  closed rather than replacing that head.
- The interface explains that no shared save exists yet, preserves retry errors
  and distinguishes an available save from confirmed receipt on another PC.
  Polling never invokes publication. Busy, timeout, selection and late-response
  guards remain active.
- Persisted publication failures identify permissions/history, backup integrity,
  setup, storage space or copy/access checks instead of always blaming disk
  space and backup integrity.

## Validation checkpoint

The focused parser and completed-backup retry groups pass. The regression uses
a disposable Valheim backup with the default comment, then changes its synthetic
live world and creates a newer manual backup. Retry must publish the exact
earlier rolling copy without rewriting its setup/catalog, live files, settings
or pairing state, invoking any driver action, or claiming a receiving receipt.
Damaged newer rolling copies remain refused while the prior shared head stays
intact. Off, inherited, unresolved and absent-backup cases remain blocked.

The first full source run passes **65 groups**. One existing symbolic-link
subcase is skipped because this local account cannot create symbolic links.
The full UI run passes **1,418 tests in 108 files**, including 19 Host sharing
presentation tests. An initial test-variable collision is corrected; the next
full solution build has zero warnings/errors. Whitespace checks pass.

Read-only review caught two additional test/correctness issues before packaging:
timestamp-based retry selection and missing local headers in the new raw HTTP
rejection assertions. Completion-order selection and equal/backward clock plus
missing-baseline regressions replace the former; raw requests now retain the
same Origin and local header as the existing mutation helper. Review also
identified stale errors on idempotent retries and recovery of a sealed next
version after the manager's ordering check. Retry now verifies already-published
payload bytes before clearing an old notice and checks the reconciled head
against its expected identity. Regressions cover damaged published bytes,
restored exact bytes and interrupted pointer updates with retained or pruned
backup records; recovered newer progress is kept.

All final focused recovery cases pass. The final full solution build again has
zero warnings/errors. UI lint, TypeScript and the production UI build pass;
Vite retains its existing large-chunk advisory. Focused .NET whitespace
verification and Git whitespace checks pass. Hosted validation remains pending.

The existing hosted Shared Worlds journey now includes the default Valheim
header before the first automatic publication. It must carry that copy to two
disposable Friend apps, retain exact hash/version identity on an explicit retry,
reject caller-selected files and reject publication from Friend mode. It is
compiled locally but may run only on the dedicated hosted Windows runner.

No app, game, console fixture, native dialog, installer or browser automation
has been launched on the owner's active desktop. Source/JSDOM, hosted synthetic
processes and public package verification remain distinct evidence. Physical
Friend-PC/WAN/game join and recognizable save/load/restart acceptance remain
external checks. Builds are unsigned unless a validated signature is recorded.

## Release checkpoint

Candidate source, immutable artifact and asset hashes, hosted outcomes and
anonymous public-download verification will be recorded here before completion.
Never rebuild frozen release bytes to complete a later check, overwrite the
0.3.3.3 release or promote this prerelease to stable/latest.
