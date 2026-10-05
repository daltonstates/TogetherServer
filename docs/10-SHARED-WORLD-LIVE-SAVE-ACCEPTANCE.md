# Live save sharing acceptance

**Live capture remains disabled for every real game.** Production shares completed, hash-verified post-Stop backups. The marked staging app offers the fixed owner **Save and share now** action only for its synthetic fixture. That action exercises exact-process completion, immutable staging, signed publication and normal Receive/receipt; it proves no real game save or load.

The real-game candidate implementations now connect fixed controls and fresh managed-run completion observations to a typed, sealed private snapshot. Unknown output, an open writer, a changed run, a changed setup or a failed copy rejects capture. A game is enabled individually only after its version-specific acceptance record passes. The acceptance set is empty; there is no global enable flag.

| Game | Implemented candidate | User-facing action and external gate |
| --- | --- | --- |
| Fixture | Exact process/nonce completion, sealed immutable copy and signed current version | Staging only. Synthetic process and loopback receipt evidence. |
| Valheim | Existing exact-run autosave observer, then reviewed world-file leases and sealed copy | Disabled. Confirm the installed version's save signal, copy, separate-PC transfer, load, recognizable change and restart. |
| Minecraft Java | Fixed `save-all flush`, fresh captured flush/completion grammar, reviewed world-file leases and sealed copy | Disabled. Confirm installed-version grammar and that the running world can be copied consistently; complete the separate-PC game checks. |
| Minecraft Bedrock | Durable hold/query/copy/finally-resume sequence; bounded queried file paths/sizes, fresh acknowledgement parser and sealed copy | Disabled. Confirm installed-version hold/query/resume grammar and held-file behavior; complete the separate-PC game checks. |
| Factorio | Fixed `/server-save`, fresh start/completion for the exact reviewed ZIP target, closed archive/CRC checks and sealed copy | Disabled. Confirm the installed server saves to that exact target and emits the reviewed completion; complete the separate-PC game checks. |
| Terraria | Owned console capture, fixed `save`, fresh saving/completion grammar and sealed `.wld` copy | Disabled. Confirm installed-version completion and a consistent running copy; complete the separate-PC game checks. Readiness/player count remains Unknown. |

## Immutable capture and publication

`ManagedLiveSnapshotStore` derives paths from the saved profile and registered driver. No API caller chooses a file, world, executable, command or argument. It checks the saved operation, native PID/start time/executable, Stop intent, owned save root, completion identity and reviewed setup. Links, traversal, oversized inventories, changing files and insufficient storage fail closed.

The copy acquires read leases across all selected files before reading, denying writes and deletes, and holds source directory identities against replacement. File creation, opening and cleanup use the exact parent handle and reject reparses; a held private guard protects promotion without entering the payload. It rechecks inventory, file identity, hashes, setup and exact run before completing a Windows-protected seal. An installed game that keeps incompatible writer handles open cannot pass this candidate; hashes or timestamps do not waive the lease requirement. The adapter cannot expose that mutable tree to the publisher.

Private captures are bounded to eight per profile, 512 files and 64 GiB, with space for two copies plus a 1 GiB reserve. A durable incomplete intent precedes copying. The already-durable request ID also identifies its private snapshot, including a crash before the adapter returns. Cancellation and normal failures remove only exact owned partial data. Explicit withdrawal removes that request's sealed private copy and interrupted incomplete intents. A durable Published attempt can clean its own redundant private copy after restart. Other requests' sealed copies and all signed interrupted versions remain intact.

The owner action serializes with lifecycle, backup, handoff and recovery. It consumes only the sealed payload, revalidates bytes/setup/run, and publishes through the existing signed lineage. It checks the exact native and recorded run again immediately before moving the current pointer. A crash or failure after the signed directory but before that pointer leaves an orphan for explicit review; it never silently promotes it. A pointer committed before the attempt journal is recovered durably without publishing twice. Withdrawal is scoped to the matching current request and cannot cancel another attempt.

Fixture readiness checks remain strict. For a real candidate, a completed sealed snapshot authorizes only this owner-requested capture after game acceptance; it supplies no readiness or player count authority. Friend and automatic Stop still require the existing fresh exact-zero evidence and lifecycle recheck.

## Completion candidates

Valheim reuses its timestamped `World save (5/5) done` observation from the recorded operation's owned `-logFile`, including the reviewed suffix. The cursor rejects replaced/truncated logs, stale runs and incomplete or oversized lines. There is no reviewed manual Valheim save command. The surfaced owner action is bounded to 30 seconds, so an autosave outside that window requires a retry rather than a completion claim.

Java and Terraria require fresh stdout captured by the exact recorded console host. Their strict saving/completion grammars are version/locale candidates. Unknown, truncated, replaced or out-of-order output cannot complete the request. Terraria now uses the same bounded owned capture mechanism while retaining fixed `exit` and Unknown occupancy. Java sends only `save-all flush`; Terraria sends only `save`.

Bedrock writes a durable exact-run attempt before `save hold`, then uses fresh query output to select bounded files under the reviewed world. Copying occurs inside the hold operation. Every exit, including parser, copy and cancellation failures, attempts fixed `save resume`; an unresolved acknowledgement remains visible and blocks another capture. Startup and owner retry also attempt exact-run resume. Only a fresh acknowledgement matching the stored attempt clears the marker. Microsoft's [save command](https://learn.microsoft.com/en-us/minecraft/creator/commands/commands/save?view=minecraft-bedrock-stable) documents hold/query/resume, but it does not establish a stable dedicated-server query grammar. The implemented grammar therefore remains unaccepted until observed on the owner's installed edition.

Factorio keeps literal `/server-save` over the existing protected loopback RCON route. The driver records an app-owned log using the documented [console-log option](https://wiki.factorio.com/Command_line_parameters). The candidate requires fresh start and completion for the exact reviewed `<world-id>.zip`, then verifies a closed ZIP, bounded entries, CRCs and sealed copied bytes. An RCON response, autosave at another path, timestamp or ZIP integrity alone cannot complete capture. The [console reference](https://wiki.factorio.com/Console) documents `/server-save` without a target argument; the adapter does not invent one. Actual installed-server target selection and log grammar remain external acceptance gates.

## Per-game acceptance record

For each game separately, use an owner-installed disposable world on a real Windows Host and another owned PC. Record app/game versions, exact copy identity, completion signal, immutable copy result, transfer/hash/receipt, game load, recognizable in-game change and graceful save/Stop/restart. Include cancellation, failed output, changed process, storage exhaustion, interruption/retry and Bedrock resume recovery where applicable. Keep endpoints, credentials, personal identifiers, worlds and production logs outside Git.

Loopback, source fixtures, TCP reachability and owner confirmations are separate evidence. No owner-controlled second PC/network or approved real-game setup was available for this delivery. All real completion, immutable snapshot, transfer, load, change and restart stages remain Unverified. See [the delivery record](13-FOUR-FEATURE-DELIVERY.md) for repository and packaged checks.
