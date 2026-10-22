# Valheim shared-save publication and 0.3.3.4 prerelease

Owner direction, 2026-10-22: both PCs run 0.3.3.3, but the Host reports that its
completed backup cannot be published and the laptop has never received a save.
Diagnose and fix that failure, test it and publish another prerelease. The next
four-part version is `0.3.3.4`; stable/latest remains `v0.3.3`.

Published [v0.3.3.4](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.4)
on 2026-10-22. All required functional checks pass. Native notification
acceptance remains blocked by Windows QuietTime; physical Friend-PC/WAN/game
acceptance remains separate. The publication record below binds the tested
source and unchanged public asset bytes.

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

Compiled source is `12894af89759ab69721264f7c557aa2507846dec`.
[Windows CI 39847937830](https://github.com/daltonstates/TogetherServer/actions/runs/39847937830),
attempt 1, builds the candidate once in job `120066117642`. Candidate artifact
`12457035105` is 61,023,261 bytes, ZIP SHA-256
`d35ecad27bd8cdd359c1d04d09cdc33af8d6381e26c854e6d9c2f9ba0e452b59`.

Independent static reads of the downloaded EXE verify GUI subsystem 2, exact
source version, bundled assembly SHA-256
`20f6908db91188cc68c8cc8d1a7507cad083b64b3357dfbaf8886d41a0fb0850`
and all seven UI assets against the candidate artifact and same-source local
build. No app or assembly is loaded or launched by this inspection.

Package job `120067537474` passes six installer smoke checks and all three
actual Host, Friend and Decide later wizard choices. Artifact `12456246887` is
120,270,260 bytes, ZIP SHA-256
`4de5332a55bd830c0fa27d30e8816d6d763fa663cabba67992f7291e7578b88c`.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 66,145,490 | `d9cf7f8e5b946cad892fb510aad596e1713cbd393f3f046fb364c121c017f0d0` |
| `TogetherServer-Setup-win-x64.exe` | 60,608,295 | `7014b35091ce8d8af97c1648802e122bb79490fc7f3077d08ce52dac76efa74e` |

Downloaded package checks confirm numeric version `0.3.3.4`, portable source
version `0.3.3.4+12894af89759ab69721264f7c557aa2507846dec`, adjacent checksum
contents and `NotSigned` for both executables. Packaging retains
`fullyAccepted=false` and `reviewOnly=true`; it does not claim that independent
functional or native gates passed at packaging time.

[UI review 39847942177](https://github.com/daltonstates/TogetherServer/actions/runs/39847942177),
render job `120066132382`, captures **60 of 60 plans** with zero failed/skipped
plans or layout findings. Its 328 captures include 51 intentional unavailable
fixture cases. The same-source rebuilt index matches the candidate and local
build: `268e84b639fab68aa625e3b40207e3fd20b064cc9892eb36a69ec24572eaa14b`.
Representative owner-sharing captures were visually inspected at desktop,
narrow and narrow 150% text, including the full retry action after scrolling.
Artifact `12457005145` is 18,300,709 bytes, ZIP SHA-256
`f648f44567da7fea7fc33e211953195243d8a14582064b88b4c44bd5ab1a1d09`.
This is same-source fixture rendering evidence, not physical-PC/game acceptance.

The functional outcomes and anonymous publication verification below complete
this checkpoint. Frozen release bytes were not rebuilt, the 0.3.3.3 release
was not overwritten and this prerelease was not promoted to stable/latest.

## Hosted outcomes

All six compatibility jobs pass: 0.3.0, 0.3.1, 0.3.2, 0.3.2.1, 0.3.2.2 and
0.3.3. Browser and packaged rehearsal also pass against this candidate.

Desktop job `120067537323` passes all **14 native chrome cases**. Its separate
QoL native checks pass 12 of 13 required cases and remain failed because
Windows stays in `QuietTime`, blocking
actual notification delivery, click and destination navigation. The blocked
notification case is retained rather than marked passed or retried by changing
the runner's Windows settings. Full native acceptance remains false.
Artifact `12456900811` is 307,081 bytes, ZIP SHA-256
`cb1cf772751bfc2440748804ad26161837170dacb6886a996c6495c3e066382a`.
The release notes retain that limitation.

QolApi job `120067537462` passes all **65 source groups with no skips**,
including the default-comment, exact-backup, clock-order, stale-error and
interrupted-publication regressions. Its packaged API journey also passes,
and the exact candidate hash stays unchanged. Artifact `12456537110` is 699
bytes, ZIP SHA-256
`8ee6c51c33a48cb1b9baf4de73d7d30108dc3c20a47eb2fef846f9aae5a85b54`.

Local pairing job `120067537511` passes **18 LAN API stages**, the **three-stage
UX wrapper and all 10 actual UI stages**, and **11 loopback stages**. Its
reports bind the same candidate before and after, confirm zero visible owned
windows in every mode and zero owned processes at cleanup. The UI performs one
preparation and one successful Start after its deliberate network confirmation.
It verifies received and hosted payload hashes, fresh destination storage and
an unchanged original before and after candidate Stop. Missing server files,
permission/policy withdrawal, stale intent and cancellation also pass. These
are synthetic same-PC processes, not physical Friend-PC or game evidence.
Artifact `12457286157` is 1,267,089 bytes, ZIP SHA-256
`c65b75794869f7a56c10711ccd48ed438f666dd901301ca745620e3c70137ddc`.

Automatic sharing job `120067537454` passes both ordinary **eight-stage** runs
against the frozen release candidate. Each records one Friend Start and one
owner Start, descendant save transfer/relay and empty owned-process jobs after
cleanup. Artifact `12456587662` is 2,719 bytes, ZIP SHA-256
`c1d889e281f5b174244c10084d3241edbdbca6b9b5c8acd79ef8e445f8e261e1`.
The independent same-source packaged review job `120066133075` also passes both
ordinary runs. Its separately built EXE is not substituted for release bytes.
Existing job `120067537411` passes all **27 serial stages** on the unchanged
candidate. This includes **1,418 UI tests in 108 files**, **114 core checks**,
Valheim 11, Factorio five, Terraria two, Minecraft 12, Minecraft setup five,
Custom six, update 15 and Companion 46. Shared history, core remote,
world-load, served package, staging isolation, hidden and visible desktop,
solution formatting and update handoff also pass.

The full Shared Worlds journey passes **11 groups**, with **zero failed or
skipped recovery scenarios**. Its first group now exercises the normal Valheim
comment through actual publication and receipt/attestation by two disposable
Friend apps. The packaged exact-backup retry, caller-file rejection and
Friend-mode rejection assertions pass in that same group. The legacy voter
review, override Off and typed majority resolution cases pass. LiveSave owner
action and transfer pass three and five groups; real-game acceptance stays false.

Existing artifact `12457638413` is 1,135 bytes, ZIP SHA-256
`e54c77a06974f09632ea60edcadfe96d1a189ab4941b6725a4613cbeae8fc29b`.
Its downloaded report binds exact source, product version and candidate hash;
every stage is passed. Across Windows CI, **14 jobs pass** and the separate
Desktop job remains failed solely for its recorded QuietTime notification
blocker. All required functional checks are complete; the release retains the
native and physical-machine acceptance limits.

## Published prerelease and public download verification

Release `421190080` is published at
[v0.3.3.4](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3.4)
at `2026-10-22T20:52:35Z`, with `draft=false`, `prerelease=true` and
`make_latest=false`. Annotated tag object
`af33b2f412aafb74bfc217ae34fa24539685b0cf` points to compiled source
`12894af89759ab69721264f7c557aa2507846dec`. This later documentation-only
commit does not move that tag or rebuild any release file.

| Public asset | Asset ID | SHA-256 |
| --- | ---: | --- |
| `TogetherServer-win-x64.exe` | 667056799 | `d9cf7f8e5b946cad892fb510aad596e1713cbd393f3f046fb364c121c017f0d0` |
| `TogetherServer-win-x64.exe.sha256` | 667057326 | `6a0742b64fa8f8933e3b6c34c022a7205b5fd065ec2794bfabb242639d6090b8` |
| `TogetherServer-Setup-win-x64.exe` | 667057366 | `7014b35091ce8d8af97c1648802e122bb79490fc7f3077d08ce52dac76efa74e` |
| `TogetherServer-Setup-win-x64.exe.sha256` | 667057813 | `7a2bd00c3a2d56262230c9668ab330717e2ec44297bacef938cdca7549b91be4` |

Anonymous public verification completes at
`2026-10-22T20:52:47.2292955Z`. All four public browser-download URLs return
bytes matching their asset IDs, API digests, sizes, frozen manifest and
adjacent checksums. Both executables have numeric version `0.3.3.4` and remain
`NotSigned`; the portable product/source version matches the annotated tag.
The package manifest hash is
`ba4e8e78f7f8db77f29130acd461859bf5764014c4d5cafe36bb530b79727791`.
No asset was launched during download verification.

Stable/latest remains release `418702775`, tag `v0.3.3`. Flags, inventories and
exact asset metadata for `v0.3.3.3`, `v0.3.3.2`, `v0.3.3.1`, `v0.3.3` and
`v0.3.2.2` remain unchanged. Release notes link both validation runs, explain
updating both PCs and the new Host action, and retain the unsigned, QuietTime
and real-machine acceptance limits. The task guide and documentation index
link the new recovery path and this record.

Private receipts, downloaded binaries and the prepared release body remain
ignored under `local-data/release-0.3.3.4/`. The owner's running app, games,
worlds, configuration and networking were not changed.
