# Shared world saving, transfer, and Friend hosting audit

Audited on 2026-10-04 against source revision `9f71717`, with the fixes described below. This is source, disposable-file, in-memory API, and React component evidence. It is not real-game or separate-PC acceptance.

## Findings fixed

| Finding | Result after the fix | Evidence |
| --- | --- | --- |
| A receive retry with an existing verified payload bypassed the final connection/history gate and omitted its Host confirmation. This can follow a crash between the payload move and latest-pointer write. | Both new and retained payloads pass the same final gate, preserve consent/cancellation checks, send an exact-copy confirmation, and clean up a redundant partial copy. | An in-memory Host/Friend regression repairs a missing pointer, confirms the copy, then races a separate Check against the final manifest response and rejects the stale save. |
| The two-minute Host-loss timer counted an unobserved polling gap toward recovery eligibility. | A gap over 30 seconds or a backwards monotonic timestamp starts a fresh loss window. Any Host response still resets eligibility. | Deterministic checks cover 119/120-second boundaries, continuous failures, stale evidence, an hour-long interruption, clock reset, and Host return. |
| Recovery buttons depended on the old Host's currently advertised capability. After a Friend restart with the Host offline, that capability is unknown even when a verified vault copy survives. | Preparing and voting remain usable from a consented verified copy. The backend still checks Host loss, keys, membership, permissions, save ancestry, and the signed majority. Receiving new saves still requires confirmed Host support. | React regression renders an unavailable Host with a verified copy, exercises both local recovery requests, and preserves backend denial messages. |
| Planned handoff accepted a URL path, query, or fragment before stopping the game. It could record an unusable successor route. | The backend validates the complete IP HTTPS endpoint before Stop and records its canonical authority. The UI prevents submission of an invalid route and explains the accepted format. | Backend rejection checks cover paths, queries, fragments, user information, HTTP, and host names. The component test rejects a page path and sends a canonical address. |

## Second pass

The follow-up audit started from `50ed1a1` on the same date and found five more issues:

| Finding | Result after the fix | Evidence |
| --- | --- | --- |
| An approved Friend with Manage sharing could publish a delegated membership change during a pending handoff. Owner publication was blocked, but delegated publication was not. Changing the exact roster could leave completion and cancellation unavailable. | Both publication paths reject changes while the durable handoff marker exists. The response explains that the signed roster must stay unchanged. Immediate credential/access revocation remains available. | A signed delegated revision is rejected without changing the served roster or history; it succeeds once the pending marker is removed. |
| A crash after writing the received-copy pointer but before signing its confirmation left every retry on the Already received path, which could only reuse an existing receipt. | Retrying a fully hash-verified current copy can recreate and send its missing signed receipt. The response also says whether Host confirmation succeeded or remains pending. | A disposable in-memory Host/Friend check deletes only the local receipt, then verifies retry recreates and sends it. |
| Old Host and successor connections keep the same device ID and received vault, but their copy/staging locks were per connection. Concurrent transfers could share partial files, pruning, and the current pointer. | Receive and planned-handoff staging use one lock for the actual vault path across saved connections. A queued receive rechecks that its connection still maps to the same vault. | A deterministic paused-response regression failed before the fix. It now verifies that a second receive and handoff stage wait, then preserve the newest verified copy. |
| The planned-handoff API still accepted loopback, unspecified addresses, or port zero. Those cannot satisfy the later successor direct-route checks, despite passing before Stop. | It applies the successor direct-route validator before Stop, matching the existing UI restriction on loopback and unspecified addresses. | Invalid IPv4/IPv6 addresses and port zero are rejected without backup or handoff mutation; valid IPv4 and IPv6 routes proceed to the separate eligibility check. |
| After a signed handoff, the retained original Host connection could qualify recovery using failures at the old address. That does not establish loss of the new Host. | Recovery checks, offer creation, and voting require the observed endpoint to match the unique current signed Host. Offer creation and durable voting recheck this under the authority mutation lock. Split or damaged authority fails closed. | A deterministic two-minute loss regression failed before the fix. It now rejects old-route eligibility, probing, proposal, and voting after a signed handoff; fresh failures at the current Host address remain eligible and its return cancels eligibility. |

Second-pass validation passed 13 selected source checks, shared-transfer alert checks, and shared live core/owner-request checks on the final source. The locked code-only gate, final .NET solution build (zero warnings/errors), focused C# formatting, and whitespace checks also passed. The same symbolic-link subcase was skipped because this Windows account could not create a link. No UI code changed in this pass; the 142 React tests below belong to the first pass. Real game/PC/network and visual acceptance remain unverified.

## Full flow audit

The deeper audit started from `4793883` on 2026-10-04. It followed the paths from game Stop through backup, publication, Receive, signed decision, fresh restore, successor restart, another takeover, and the original Host returning. Eight additional defect groups were fixed:

| Severity | Finding | Correction and evidence |
| --- | --- | --- |
| P1 | Recovery and restore expected the original owner's save signature and the earlier handoff's exact save hash. New progress from an authorized successor could not be offered, voted on, restored, or rehearsed. Warned separate-copy restore had the same signature restriction. | Keep the original owner as the trust root, authorize the current hosting signer from verified authority, and verify its full descendant history. File-based regressions follow two host changes, save progress, restore and rehearsal; unauthorized signers, missing ancestry, and a wrong owner pin are rejected. Separate restore reaches its independent listener gate with an authorized successor copy. |
| P1 | Recovery roles accepted only schema-2 rosters. Owner edits and delegated revisions produce schema 3. The majority builder also verified the decision before adding its required candidate Host acceptance. | Accept owner-signed or owner-countersigned current membership, retain the verified roster-chain gate, and sign the candidate acceptance before decision verification. Separate regressions exercise owner revisions and delegated revisions through offer, challenge, votes, decision receipt, restore, and successor publication while the owner is offline. |
| P1 | Offers omitted available ancestry, preventing votes from copies several saves behind. A voter also tried to persist a majority without the decision's streamed lineage proof. | Include up to 64 recent signed manifests within the existing 512 KiB offer limit; verify the applicable ancestry against accepted hosting transitions. Persist the exact reviewed decision with its verified metadata proof. The transport regression uses an older voter two saves behind and confirms that learning the decision does not advance its received payload pointer. |
| P1 | Metadata operations took authority, inbox, and sharing locks in opposite orders. Separate store instances also had independent locks for the same durable metadata. Friend proxies could hold the connection-list lock while entering these operations. | Authority, roster, sharing, inbox, and separate-copy metadata use the same normalized data-root lock. Friend proxies select a connection before entering it. Readiness and resolution operations retain their connection through completion. Concurrent inbox reads and authority binding from separate instances complete under a bounded timeout. Identical binding retries preserve durable state while rechecking private-key possession. |
| P1 | Once a successor had published one newer save, Start checks accepted arbitrary unpublished world changes. Rolling back to the earlier handoff copy also passed. | Verify the local world files against the latest authorized signed save, including after subsequent Stops. The regression reproduced acceptance of unpublished changes before the fix; altered files and rollback are now denied while the actual latest copy passes. |
| P2 | Successor storage checks kept using the initial handoff's payload size after the world grew. | Require backup and publication space for the current signed payload plus the reserve. A growing-world regression rejects space sufficient only for the old small copy. |
| P2 | Recovery preparation accepted loopback, unspecified addresses, and port zero even though they could not pass successor route checks later. | Production preparation, resolution, and voting enforce the existing direct-IP route validator before collecting votes. Regression rejects unusable candidate addresses without arming an offer. |
| P2 | Switching saved Host connections for the same world reused the original Host's sharing and setup form state. Save-status intervals could also overlap and apply a response after changing worlds. Local readiness misidentified approved recovery and conflict-resolution candidates as lacking permission. | Key Friend cards by connection and profile; serialize polling and ignore responses after effect cleanup. Use the verified hosting decision's device identity for eligibility, including majority and owner-override resolutions. Slow-response React and signed-resolution regressions verify these cases. |

The deeper coverage includes these boundaries:

| Boundary | Source reviewed | Verification |
| --- | --- | --- |
| Graceful Stop, exact process identity, final backup and publication | `HostManager.cs`, `GameServerDrivers.cs`, game drivers, `HostManager.Backups.cs`, `RecoveryAndBackups.cs` | Source audit and disposable backup integrity, retention, reserve and restore checks. Game processes were not launched. |
| Signed save and portable setup | `SharedWorlds.cs`, `SharedWorldLiveCapture.cs`, `SharedWorldPortableSetupReader.cs`, setup snapshots | Reviewed checkpoint provenance, file hashes, staged publication, signed order, Java JAR identity, and machine-secret exclusion. Real-game live capture remains disabled. |
| Transfer, confirmation, cancellation and shared vault ownership | `FriendLink.SharedWorlds.cs`, `CompanionServer.cs`, `SharedWorldReceipts.cs`, transfer health | Bounded bytes, authorization rechecks, withdrawal, retained-copy retry, receipt repair, cross-connection serialization, tampering, retention and low space. |
| Membership, delegated edits, expiry, revoke and durable history | `SharedWorldGovernance.cs`, `SharedWorldRosterChainStore.cs`, pairing, `SharedWorldAuthority.cs` | Owner and delegated chain, 270 revisions, offline catch-up, fork fencing, access expiry, immediate credential revoke, and authority journal/restart checks. |
| Planned handoff and recovery | `HostManager.SharedWorlds.cs`, `SharedWorldElection.cs`, `SharedWorldVoteInbox.cs`, `SharedWorldHostLoss.cs` | Pre-Stop address checks, pending-roster protection, exact-copy confirmation, current-host loss, durable votes, repeated takeover with new progress, and schema-3 owner/delegated recovery. Process-based handoff journeys were not rerun. |
| Successor restore, local checks and future Start | `HostManager.SuccessorRestore.cs`, `SharedWorldReadiness.cs`, route proofs, successor enrollment | Actual disposable-file restore and rehearsal, local key binding, synthetic signed control-route evidence, changed-world/rollback rejection and growing-world space checks. Synthetic route evidence proves signing logic, not network reachability. |
| Split history, old Host return, owner override and warned separate copies | `SharedWorldAuthority.cs`, separate-copy and route stores, Host/Friend authority ingestion | Source audit plus signed majority, complete split-resolution, protected journal, old-Host fencing and separate-save verification checks. No game was started. |
| UI state and guidance | `SharedWorldControls.tsx`, `SharedWorldReadinessPanel.tsx`, `main.tsx`, API decoders | 143 component/contract tests, including slow polling, offline recovery, route validation and guided manual setup. Live visual and viewport acceptance was not performed. |

Operational limits remain explicit. A signed majority does not establish that an unreachable old game process stopped; a network partition can leave it running. Recovery therefore remains manual and warned, and the returning Host is fenced when it learns the verified decision. Recovery uses a completed verified copy and can miss subsequent gameplay. A voter too far behind the bounded offer, or missing signed history, is refused rather than silently selecting an unproved copy. Planned handoff remains an original-owner operation; successors continue signed saving and guarded recovery.

Deeper-audit validation:

- PASS: 143 React tests across 29 files, UI lint, TypeScript checking and the production bundle build. The bundle still reports the existing size warning for its main chunk.
- PASS: 30 selected backend checks, including the three new recovery journeys, older-voter decision receipt, private-key retry checks, growing-world storage, tamper/rollback rejection and majority/owner-override readiness. These also cover the existing permissions, transfer, backup, membership and old-Host fencing regressions.
- PASS: shared-transfer alerts, shared live core and fixed owner-request checks using disposable files and in-memory API state.
- PASS: 4,102-version signed-history catch-up, cursor restart, interrupted-write/replay handling, tamper rejection, bounded proof paging and resolved-branch payload retention.
- PASS: locked code-only gate, final .NET solution build with zero warnings/errors, focused C# whitespace formatting and Git whitespace checks.
- SKIP: one symbolic-link fixture because this account could not create the link. Live visual/viewport, packaged process journeys, separate Friend PCs, network routes and real-game save/load/restart acceptance remain unverified. Installed apps, real worlds and network policy were not changed.

## Saving and transfer behavior

- Production sharing publishes completed post-Stop backups. The game driver must confirm graceful exit before the backup/publication path runs. Live capture for real games stays disabled.
- A game's own save and a transferred file copy are different results. File hashes, a command response, or a clean process exit cannot establish that a real game loads the world or preserves a recognizable change.
- Each Friend needs a separate Receive grant and local **Allow saves on this PC** consent. Hosting and recovery voting are separate grants.
- Save manifests, signed membership, pinned identity, ancestry, access expiry, and exact payload hashes are checked. Transfer reads recheck authorization after asynchronous work. Revocation prevents further reads; previously verified copies remain.
- Transfers use bounded chunks and resumable partial files. Completed copies become current through the verified receipt path. Retention keeps ordinary newest copies by signed version number, preserves decision/branch copies, and retains signed ancestry. Low-space checks preserve the receiver reserve.
- A Friend recovering after an unexpected Host failure uses its last completed verified copy. Changes made since that copy may be missing. The UI now states this next to the receive controls.

## Friend hosting behavior

A stopped game server does not establish loss of the Host PC. When the Host app remains reachable, Friends use its existing approved remote Start or arrange a planned handoff.

For a planned handoff, the original Host stops, publishes the final completed copy, waits for the chosen successor's signed exact-copy confirmation, and commits the durable hosting decision before reporting completion. The successor restores into fresh managed storage and completes local setup and a signed control-route check from another approved PC before manual Start.

For recovery, an eligible PC needs at least two minutes of continuously observed failed Host checks, a verified copy and ancestry, an approved direct route, and the signed recovery majority. Preparing or voting never starts a game. Restore, local setup, another PC's control-route check, and manual Start remain separate steps.

Once the original Host records the verified successor decision, its Start and publication paths are fenced; competing or damaged histories also block hosting. A network partition can leave an original game process running before that PC receives the decision. A local-only recovery protocol cannot remotely prove or stop that inaccessible process. The group must confirm the situation and review competing copies when the Host returns. The UI preserves this warning.

## Validation

- PASS: 142 React tests across 29 files, including the offline-recovery and invalid-handoff-route regressions; UI lint, TypeScript checking, and production bundle build.
- PASS: 28 selected source checks covering the new retry/timing/route regressions, signed roster and delegated grants, offline membership catch-up, access expiry, bounded transfer/history reads, tampering, retention, backup/restore integrity, and durable majority/old-Host fencing.
- PASS: shared live core and fixed owner-request checks; transfer-alert checks. These use disposable files and in-memory/synthetic state, not running real games. One symbolic-link rejection subcase was skipped because this Windows account could not create the link.
- PASS: .NET solution compilation with zero warnings/errors and focused C# formatting verification.
- PASS: locked `scripts/verify-code-only.ps1` gate with pinned .NET 10.0.301 and Node 24.14.1; no app or game process was launched by that gate.
- PASS: 4,102-version signed-history catch-up, cursor restart, exact temporary-file replay, interrupted-write handling, tamper and missing-ancestor rejection, bounded takeover-proof paging, and resolved-branch payload retention. Its restart checks use hidden check-helper processes, not game/app instances.
- SKIP: visual/viewport review, because the Browser runtime reported no available connected browser.
- SKIP: packaged/desktop and managed-game-process journeys, production deployment, real Friend PCs, WAN/private-mesh routes, and real-game save/load/restart or takeover acceptance. Existing installed apps, production data/worlds, and network policy were not changed.

The new backend regressions can be rerun after a Release build with these filters:

```powershell
dotnet checks/TogetherServer.Checks/bin/Release/net10.0-windows/TogetherServer.Checks.dll '--filter=retained receipt retry'
dotnet checks/TogetherServer.Checks/bin/Release/net10.0-windows/TogetherServer.Checks.dll '--filter=Host-loss eligibility'
dotnet checks/TogetherServer.Checks/bin/Release/net10.0-windows/TogetherServer.Checks.dll '--filter=planned handoff validates'
dotnet checks/TogetherServer.Checks/bin/Release/net10.0-windows/TogetherServer.Checks.dll '--filter=delegated publication serves'
dotnet checks/TogetherServer.Checks/bin/Release/net10.0-windows/TogetherServer.Checks.dll '--filter=shared recovery'
```

Before relying on a valued world, use a disposable copied/staging world on separate Windows PCs to prove a recognizable change survives graceful Stop, transfer, successor load, and restart; test both planned handoff and recovery, interruption/retry, intended control and game routes, and the original Host returning. Record those results separately from the checks above. See [live-save acceptance](10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md) for each game's additional live-capture gate.

## Four-feature follow-up

The 2026-10-04 delivery adds the staging owner fixture action and five real-game completion/snapshot candidates under the canonical lifecycle gate. Every real candidate remains disabled by an empty per-game acceptance set. The typed publisher consumes a protected sealed snapshot, not a running real-world tree. Source files are leased together against writes/deletes; native child opens, writes, attributes and cleanup use exact parent handles, reject reparses and retain a private namespace guard during promotion. Actual Windows attribute-only reparse substitutions are included in the source checks.

The durable request also identifies its private snapshot across a crash before adapter return. Matching Published attempts and explicit withdrawal can discard that redundant private copy, while signed interrupted versions remain fenced for explicit review. Identity, Stop intent and cancellation are rechecked at the final current-pointer boundary. Bedrock copies before resume; every held exit attempts resume, refused dispatch stays a review state, and only fresh exact-run acknowledgement clears the durable marker. The normal pinned transfer, signed receipt, roster grants, manual guarded takeover and old-Host authority fences are reused. Guided load copies use the received-vault lock and cannot publish authority. Codex CLI and three scoped subagents supplied implementation and review work; the coordinator integrated it and serialized verification. Evidence is recorded in docs 04 and 13. No real game or separate-PC acceptance is claimed.
