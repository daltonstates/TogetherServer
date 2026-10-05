# Four-feature delivery record

The owner requested all four features below on 2026-10-04. The approved scope was prepared at `e6ab1f2`; the delivery record below describes implemented repository slices and their separate acceptance boundaries. No candidate has replaced the running production app. Recheck the live checkout before further work.

Read `AGENTS.md`, `README.md`, docs 00 through 04, `docs/10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md`, and `docs/12-SHARED-WORLD-AUDIT.md`. Current-chat owner instructions take precedence. Start a bounded implementation slice in the first coding turn and continue through all four features. Preserve unrelated changes, the running production app, paired access, and real worlds. Use an isolated candidate or the marked development instance for process tests.

## Scope and order

| Order | Feature | User outcome |
| --- | --- | --- |
| 1 | Remote rehearsal | The owner can run a repeatable Host/Friend check using another owned Windows PC, without asking a friend to keep testing. |
| 2 | All-server overview | The Host sees which saved server needs attention and what action to take without opening each server. |
| 3 | Guided world-load rehearsal | The owner can test whether a received or backed-up copy loads in a disposable real-game setup without replacing the live world. |
| 4 | Game-specific live save sharing | A supported game can publish a complete, immutable save while running, but only after that game's completion, copy, and load evidence passes. |

These are four deliverables, not four proposals. Implement in vertical slices, update the relevant product/architecture/acceptance docs as behavior changes, and commit validated repository work. Do not add a cloud control plane, relay, public dashboard, automatic takeover, generic shell endpoint, or automatic router/firewall/DNS changes. Keep the Host GUI loopback-only and all Friend operations fixed, authenticated, and scoped to a saved profile and current grants.

## 1. Remote rehearsal

Build a compact **Run rehearsal** flow for a disposable staging Host and a separately paired test PC. After normal test pairing and grants, one action on the test PC should exercise the current pinned HTTPS connection, authenticated status, a two-way exchange in a disposable server chat room, and a synthetic shared-save transfer through verified receipt. Include a fixed game-endpoint query only when a supported game is running. Use the existing Host/Friend transport, chat IDs, shared-world manifests, receipt, and authorization checks; do not create a general-purpose remote command or accept caller-selected URLs, ports, files, or world paths.

Return a bounded, redacted report with a separate result for each stage: local listener, outside TCP if independently checked, pinned/authenticated connection, chat exchange, transfer/hash/receipt, game endpoint, and human game join/load. A successful TCP connection or synthetic save must never mark the later stages passed. A second process on loopback, a same-LAN PC, and a PC on another network must be labeled differently. Do not infer WAN reachability merely from the advertised public IP or a hairpin route. If no second Windows PC/network is available, still implement and package-test the flow, and record the external run as unavailable rather than asking a friend to test repeatedly.

Keep test credentials and worlds separate from production. Require the existing owner-controlled pairing and Receive consent; do not weaken or silently grant them for a test. Clean up only exact disposable test data. Cover retries, Host restart, wrong room/profile, stale or forged receipts, revoked/expired access, interrupted transfer, and redaction. Extend the packaged Host/Friend journey with meaningful chat and save assertions, then run an actual separate-network staging rehearsal when an owner-controlled endpoint is available.

Starting points: `ui/src/ConnectionDoctor.tsx`, `ui/src/FriendConnectionDoctor.tsx`, `ui/src/ServerChat.tsx`, `ui/src/SharedWorldControls.tsx`, `src/TogetherServer/FriendLink.Chat.cs`, `src/TogetherServer/FriendLink.SharedWorlds.cs`, `src/TogetherServer/SharedWorldReceipts.cs`, and `checks/TogetherServer.CompanionChecks/CoreRemoteJourney.cs`.

## 2. All-server overview

Add one compact Host overview above or beside the saved-server list. Each server should show its canonical lifecycle state, the latest server-reported player count with source/freshness or **Unknown**, the next exact-run shutdown deadline when present, latest completed backup and its age/result, and its most urgent reviewed warning. Sort servers needing action first, but keep the owner's selected server stable. Make the primary action obvious and send deeper actions to the existing server tab or Connection Doctor.

Derive this from existing snapshots, Sessions, backup status, and Attention data. Avoid a new polling loop or persistent telemetry store unless an identified contract gap requires one. Never use overview or stale statistics to authorize Stop, replacement, recovery, or takeover. A missing backup, unavailable count, and stale observation need distinct labels. Keep Friend-only data and private paths out of the Host summary. Verify zero, one, and many servers; loading/error/partial data; narrow and desktop layouts; attention sorting; and that selecting a server still works.

Starting points: `ui/src/main.tsx`, `ui/src/RecentSessions.tsx`, `ui/src/ServerReadiness.tsx`, the Host snapshot contracts, and the existing backup/Attention projections.

## 3. Guided world-load rehearsal

Extend **Test restore**, which currently verifies hashes in disposable scratch storage but does not prove the game loads the save. The new owner-only workflow should select a completed backup or verified received copy, revalidate its manifest and signed lineage where applicable, prepare a fresh disposable world directory, and guide a real game load and restart. Record the installed game/app versions, exact copy identity, load outcome, and an explicit owner confirmation that a recognizable change survived. Label every unobserved or owner-confirmed step accurately; do not turn a confirmation into automatic proof or takeover authority.

Never swap, delete, or write to the active world. Reuse reviewed game drivers and exact process identity. Check distinct save roots and nonconflicting ports before any rehearsal launch. Never open a public Friend listener or change network policy for this drill. If a game cannot be safely isolated or an owner-installed binary is missing, provide the safe manual path and leave the automated load result unverified. Stop and clean up only the exact disposable rehearsal process and files after an owner-approved action; retain a bounded result without secrets or real world payloads. A failed drill must leave the original copy and its signed receipt intact.

Cover tampered manifests, linked paths, low space, interrupted launch/cleanup, an already running world, changed game binary, failed load, and restart with a copied save. Run a real disposable-world drill per game before claiming that game's load support. Keep the existing hash-only rehearsal available for games without a proven load path.

Starting points: `src/TogetherServer/HostManager.Backups.cs`, `src/TogetherServer/RecoveryAndBackups.cs`, `src/TogetherServer/HostManager.SuccessorRestore.cs`, shared-world receipt/authority code, and the World Safety Center in `ui/src/main.tsx`.

## 4. Game-specific live save sharing

Keep the current verified post-Stop publication path working. Build live capture as an explicit per-game capability, with a fixture implementation and a game-by-game acceptance matrix. For each game, bind a fixed save request and completion signal to the exact managed run; obtain a stable snapshot source; copy only reviewed save files into immutable app-owned staging; verify bytes and hashes; then publish a signed version through the existing lineage and receipt path. Serialize with lifecycle, backup, handoff, and recovery work. A failed, partial, stale, or interrupted capture must publish no current version and must not block the game's ability to save again.

Start from the existing candidates in `docs/10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md`: Valheim log observation, Java `save-all flush`, Bedrock hold/query/resume, Factorio `/server-save` and archive staging, and Terraria `save`. Do not treat command dispatch, log text, a file timestamp, ZIP integrity, or a hash alone as completed live-save evidence. For Bedrock, always attempt and durably recover `save resume`; unresolved hold stays visible and blocks another capture. Enable the user-facing **Save and share now** action for a specific real game only after an owner-installed disposable world passes completion, immutable copy, transfer to another PC, game load, recognizable change, and restart checks. Leave unaccepted games disabled with a concrete reason. Do not enable all games through a single global flag.

Cover process identity changes, concurrent Stop, duplicate requests, cancellation, file mutation during copy, storage exhaustion, crash between signed directory and latest pointer, withdrawal/retry, and old-Host fencing. The fixture route should pass packaged process tests, while each real-game claim needs its own external acceptance entry. Keep manual, guarded takeover and signed authority unchanged.

Starting points: `src/TogetherServer/SharedWorldLiveCapture.cs`, `src/TogetherServer/SharedWorldLiveSaveAdapters.cs`, the per-game candidate command/observation files, `checks/TogetherServer.CompanionChecks/SharedWorldLiveTransferJourney.cs`, and docs 10 and 12.

## Verification and handoff

### Delivery record

| Slice | Repository implementation | Acceptance boundary |
| --- | --- | --- |
| Remote rehearsal | Implemented; 148 UI tests, source safety group, eight packaged core groups and two packaged staging rehearsal groups passed | Loopback and synthetic evidence only. Separate-PC/network and real game acceptance unavailable; browser/viewport skipped. |
| All-server overview | Implemented; five focused projection/component tests, lint, production bundle and packaged served smoke passed | Browser/viewport acceptance unavailable. Existing lifecycle authority unchanged. |
| Guided world-load rehearsal | Implemented; source safety group, three UI cases and packaged isolated process/copy and received-copy drills passed | Real game load/change/restart remains unavailable. Owner confirmations stay separate from observed process checks; other games use the manual path. |
| Game-specific live save sharing | Implemented staging fixture action plus five per-game completion and sealed snapshot candidates, exact request/crash cleanup, cancellation at current-pointer commit and acknowledged Bedrock resume recovery. Focused immutable snapshot, Windows reparse/lease, action/recovery and 43 UI trust-decoder cases pass, plus 12 Minecraft, five Factorio and two Terraria process-fixture groups; final integrated packaged gate pending | Every real game remains disabled. Owner-installed, version-specific save/immutable copy/separate-PC transfer/load/change/restart acceptance remains unavailable. Loopback fixtures certify none of those real-game claims. |

All four repository features have code implementations. The integrated fourth-slice follow-up passed its focused checks on 2026-10-05; final packaged verification is pending. External network and game acceptance remains a separate gate; no real game is enabled by candidate code or a fixture result.

The owner also requested Codex CLI and subagents for large tasks. `AGENTS.md` now requires that workflow, bounded worker ownership and a compact resumable delivery record. Two actual CLI workers used the installed configured `gpt-6.1-sol` model at max reasoning for a read-only review and a narrow UI decoder patch. Three subagents supplied the immutable store/console adapters, Bedrock/Factorio candidates and publication/recovery review in isolated worktrees. Their inherited runtime model ID was unavailable. The coordinator integrated changes and serialized all builds, process checks, packaging and commits. No Astra worker was selected.

Completed slice commits: `be5666b` remote rehearsal, `735b102` all-server overview, `0255fa5` world-load rehearsal, `f6f544e` staging live-save action, `568d0a6` CLI/subagent repository rule, and `e389caa` review fixes and handoff fixture repair. The final live-save candidate commit and gate evidence will be recorded after validation.

- Before each slice, inspect current contracts and recent commits so existing work is reused rather than duplicated. Update strict .NET/TypeScript decoders and capability negotiation when a wire shape changes. Older peers must fail with a typed unsupported/update state, not parse partial data.
- Use focused tests for authorization, lifecycle, file integrity, retry, and UI state. Run the relevant packaged journeys and `scripts/verify-release.ps1 -Build` on an isolated candidate when safe. Check the exact executable and data roots before process tests; do not replace or stop the installed production app.
- Inspect the rendered Host/Friend flows at supported desktop and narrow widths when a browser is available. Record visual checks as skipped if only component or served-asset tests ran.
- Record code/fixture, packaged multi-process, separate-PC network, real-game join, and real save/load/restart evidence as distinct stages. A portchecker TCP pass, loopback test, or same-LAN test is not an outside game route or world-integrity pass.
- Never accept game terms, download a terms-gated server, spend money, change public router/firewall/DNS settings, request real credentials, or touch a valued world without explicit owner authorization. Do not publish, deploy, replace production, tag, or create a GitHub release without authorization.
- At the end, report which of the four features works, which games have enabled live capture, exact tests and skips, remaining external gates, commits, and any migration or rollback requirements. Do not mark the four-feature program complete while a required implementation slice remains undone; explain any truly external gate separately.

## Prompt for a new chat

> Work in `G:\repo\TogetherServer`. Read `AGENTS.md` and `docs/13-FOUR-FEATURE-DELIVERY.md`, then inspect the live checkout and the referenced product, architecture, security, lifecycle, acceptance, and shared-world documents. Implement **all four** owner-approved features in that delivery brief, starting actual code in your first implementation turn and proceeding in bounded vertical slices. Reuse the current Host/Friend transport and safety gates. Preserve production, real worlds, credentials, and unrelated changes; use isolated staging and disposable data for tests. Validate each slice, run the applicable packaged release checks, and commit validated work. Attempt a separate-network and real-game acceptance run only when an owner-controlled endpoint and approved disposable game setup are available; never call loopback, TCP-only, or synthetic evidence a real outside join or save/load pass. Keep unaccepted real-game live capture disabled with explicit reasons, and report the exact remaining external gates. Do not stop after planning or after only the first feature.
