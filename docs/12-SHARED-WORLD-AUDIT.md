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
```

Before relying on a valued world, use a disposable copied/staging world on separate Windows PCs to prove a recognizable change survives graceful Stop, transfer, successor load, and restart; test both planned handoff and recovery, interruption/retry, intended control and game routes, and the original Host returning. Record those results separately from the checks above. See [live-save acceptance](10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md) for each game's additional live-capture gate.
