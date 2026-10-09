# Six-feature delivery

This delivery adds six bounded features to the existing local Host and authenticated Friend application. Repository implementation, integration and authorized focus-safe validation are complete for all six UI/backend paths as of 2026-10-08. Native, real-game and Friend-PC acceptance remains pending. The integration worker used configured gpt-6.1-sol max for implementation and high for the authorized validation phase, with no additional workers.

| Feature | Ownership | Integration state |
| --- | --- | --- |
| 1. Named backup bookmarks | Backup/weekly worker: backup service, new HostManager partials, BackupBookmarks UI and dedicated checks | Connected to Host Backups and guarded owner routes; canonical refresh remount fix included |
| 2. Pre-join game compatibility requirements | Integration worker: bounded requirements observation, protected owner fallback, authenticated Friend comparison, Host and Join UI | Connected to Host overview and assigned Join cards; synthetic/JSDOM regression sources added |
| 3. Guided game settings | Settings worker: GameSettings service, HostManager partial, settings panel and dedicated checks | Connected before raw Files editor; all preview/save/list/Undo routes guarded; Java ASCII-whitespace fix included |
| 4. Open game from Join | Integration worker: fixed local client launch mapping, fake-adapter checks and Join UI | Explicit Join action; completed installed Steam metadata and fresh authorization gates; Minecraft manual steps |
| 5. Durable server notices | Notices worker: signed chat/notice helpers and UI checks | Owner edit/clear route, capability and final companion authorization connected; existing Host/Join chat renders notice |
| 6. Weekly summary | Backup/weekly worker: new summary projection, HostManager partial, WeeklySummary UI and dedicated checks | Connected to Host Sessions and fixed owner read route |

Worker-owned files must remain disjoint. Shared Program.cs, main.tsx, contracts.ts, CSS, CompanionServer.cs, CompanionProtocol.cs, FriendService.cs, check registration and documentation belong to the integration worker. Handoffs live under ignored local-data/feature-workers.

## Safety and verification

The coordinator explicitly authorized serialized focus-safe validation after implementation. The integration worker performed the code-only script, JSDOM tests, UI bundle and compilation only. Workers do not commit; the coordinator commits after validation. The separate FeatureChecks project registers eight groups using only pure logic, synthetic filesystem/protected-state checks and fake adapters; the existing process-launching checks runner is excluded. Its three linked worker checks, two notice checks and new compatibility/input/launch checks do not invoke an app entry point, start a game/helper process, attach a console, open a listener, or use real HTTP/native UI.

TogetherServer, games, fixtures, console helpers, browsers, native dialogs, listeners, process runners, release gates, installers and staging scripts must not be launched on the owner's active desktop. Hidden launches are also excluded. Production state, worlds and credentials must not be inspected or changed. No terms acceptance, game download, network policy change, tags, releases, push, deployment or installation is part of this delivery.

Compatibility is informational. Unknown or manual/owner-reported versions must be labeled, and no comparison proves game readiness, occupancy, a real join or save integrity. Open game is an explicit local user action over fixed reviewed mappings; Host data cannot supply a command, path, executable or raw argument.

Native Steam handler behavior, real installed-client version discovery, real add-on loading, actual Friend-PC authorization and game joining, valued-world backup/load/save/restart, and responsive visual acceptance remain skipped or unverified pending an explicitly approved separate PC or unattended Windows session. Synthetic checks cannot satisfy these acceptance boundaries.

## Implemented behavior and bounds

- Backup names and pins mutate only the completed catalog. Labels allow at most 64 reviewed ASCII characters. Pins have separate 20-entry/50-GiB payload limits and are additive to normal unpinned retention; unpin performs no immediate delete. Immutable completion, payload, setup and sharing hashes stay independent. No Friend route exists.
- Requirements report exact recorded game-version text, its Observed/OwnerReported/Unknown source and a bounded reviewed enabled add-on inventory. Java observation verifies the recorded JAR checksum and embedded version ID; Factorio/Terraria/Bedrock executable metadata requires fixed filename and product identity. Valheim stays Unknown without an owner statement. Owner statements are protected and bind to the selected game/world/executable/JAR profile identity. No Steam build ID or Unity version is treated as a game version.
- Factorio reads bounded activation and reviewed ZIP info metadata. Missing enabled packages, duplicate package names/versions or unreadable bundled components return Unknown. Space-age/quality/elevated-rails are retained as requirements when their fixed bundled metadata is available. Bedrock active UUID/version must match bounded world-local pack manifests; external/shared/missing packs make the inventory Unknown. No Paper/modded Java or Valheim/Terraria mod loader is introduced.
- Friend version discovery reads only fixed Steam install metadata and conservative Factorio/Terraria product-version resources. Other/unreadable installs use a protected manual version scoped to the saved Host/profile/game. Factorio default mod-folder comparisons are labeled as such; custom launch options and Bedrock local pack compatibility remain Unknown. Equality means metadata/version text match, not certified protocol compatibility.
- Game settings/list previews are read-only. Save/Undo preserve the existing maintenance, definite Offline, complete setup checkpoint, exact current hash and protected single-file revision gates. Fixed request parsers reject duplicates/unknown fields/types. Missing game-generated lists remain unavailable; unrelated properties/comments/newlines survive. Java key whitespace uses its actual ASCII properties semantics.
- One signed owner notice per room has at most 2000 plain-text characters and no chat-age cutoff. Expected revisions protect edit/clear races. Signed tombstones and monotonic verified Friend copies prevent resurrection. Cached copies are visibly labeled; no Friend mutation, log/support/activity export or lifecycle effect is added.
- Weekly summaries use at most the newest 500/30-day archived records. Known intervals are clipped to seven days and unioned; completed outcomes/backups and trusted peaks are scoped honestly. Unfinished, legacy, undated, clipped, overlap and retention-cap gaps remain visible. No logs/process reads or player identities are used.

## Current workspace presentation

The later [QoL delivery](15-QOL-UX-DELIVERY.md) builds on these six features. Its validation is recorded separately; the passing checks below certify the six-feature snapshot described here.

**Backups** now presents one completed-copy catalog with search, pin/kind/local-date filters, names, pins, existing guarded actions, comparison of two recorded summaries, and explicit review before Restore. Local integrity, vault transfer, hash restore test, and owner-reported game load/change/restart stay separate evidence. The pin quotas, immutable payloads and restore gates remain unchanged. **Sessions** adds daily recorded runtime and links to matching retained runs without converting gaps into uptime claims.

Setup shows source/destination and available source-file sizes and dates before **Confirm copy**; unavailable metadata stays labeled, and originals remain untouched. Simple settings, typed lists and the raw file editor offer bounded Windows-protected local drafts with explicit recovery against the current file, plus before/after review. A recovered draft does not apply a server change. Save and Undo still require maintenance, definite Offline state, a current file hash and a complete setup checkpoint. Passwords and custom scripts do not enter recoverable setup drafts.

The access-deadline editor accepts a local date/time in the displayed time zone and previews the exact UTC submission. Missing or repeated times during clock changes are rejected; the loopback API continues to require a future UTC deadline within 365 days. Shared worlds separates **Receive saves** from **Host on this PC**, presents canonical manual checklists and distinct copy/receipt evidence, and can review route details together. Signed lineage, explicit grants, owner override and manual guarded takeover remain authoritative. Real-game live-save capture remains disabled; real Friend-PC/game/load/save/restart acceptance remains external.

## API integration

All following local routes require the exact local header, current page role and update/mode gate; query strings and GET bodies are refused. Mutating payloads have declared/streamed byte caps and strict fixed fields.

| Role | Routes |
| --- | --- |
| Host | GET/PUT `profiles/{id}/requirements`; GET `profiles/{id}/backup-bookmarks`; PUT `profiles/{id}/backups/{backupId}/bookmark`; GET `profiles/{id}/weekly-summary`; PUT `profiles/{id}/chat/notice` |
| Host | GET/PUT `profiles/{id}/game-settings`; POST `game-settings/preview` and `game-settings/undo`; GET/PUT `game-settings/lists/{key}`; POST each list's `preview` and `undo` |
| Join | GET `friend/{id}/compatibility`; PUT `friend/{id}/client-version`; empty-body POST `friend/{id}/open-game` |

Paths in this table are under `/api/local`. The sole new companion read is GET `/api/companion/servers/{profileId}/requirements`, gated by `game-requirements-v1`, compatible protocol, the exact capability header, saved credential and atomic current assignment/access decisions before and after the asynchronous read. Host observation is limited to four seconds; requirement responses to 64 KiB/64 add-ons/48-character versions; Friend comparison to twelve seconds, with fresh authorization after discovery. Launch preparation has an eight-second enclosing deadline and a final authenticated status check. Chat advertises `server-notices-v1` and attaches notices only after its final current room authorization. No new listener or generic command/path/argument endpoint exists.

## Open game protocol review

Reviewed on 2026-10-08 against first-party sources: [Valve Steamworks API overview](https://partner.steamgames.com/doc/sdk/api) documents `steam://run/<AppID>`; the [ISteamApps launch-parameter reference](https://partner.steamgames.com/doc/api/ISteamApps) describes the same scheme. Store identities are [Valheim 892970](https://store.steampowered.com/app/892970/Valheim/), [Factorio 427520](https://store.steampowered.com/app/427520/Factorio/) and [Terraria 105600](https://store.steampowered.com/app/105600/Terraria/).

The only dispatched URIs are `steam://run/892970`, `steam://run/427520` and `steam://run/105600`, without query/arguments/password/address. The fixed local registry/library/app manifest and installed filename must conservatively identify a completed installation before dispatch. Metadata absence/incompleteness fails to a manual fallback without requesting installation. A preparatory read never launches; assignment, access and shown kind/address are checked again immediately before the one explicit adapter call. The adapter refuses dispatch unless its same fixed URI was prepared. These primary sources establish the launch scheme and identities; no first-party game-specific direct-join support has been accepted here. Every game therefore joins manually through its existing instructions and Copy address. Minecraft opens through Windows Start/its own launcher, with Multiplayer/Servers steps and no configured executable endpoint.

## Source regression packet and remaining checks

`TogetherServer.FeatureChecks` registers: compatibility metadata/scope/expiry, exact fixed launch and fake pinned Friend reads, strict local inputs, backup catalog pins/names, weekly projection, guided settings, signed notices, and verified Friend notice copies. New JSDOM files cover strict compatibility decoding, required/manual/mismatch/unknown panels and explicit click-only Open game, including no-handler/manual fallback and no mount/rerender launch. Existing worker JSDOM files cover bookmarks, summaries, settings and notices.

The code-only script compiles the solution and invokes only this standalone feature DLL for these groups. It does not invoke the legacy checks Program, an application entry point or a process/console-attachment journey. The new FeatureChecks lockfile was generated using pinned .NET SDK 10.0.301 and existing dependency versions; final locked solution restore passed. No existing dependency lockfile or pinned version was changed.

Source review corrections include null/malformed status profiles, numeric-only/integer-bounded notice revisions, ambiguous Factorio ZIP versions, bundled component differences, stale pin editor props and Java Unicode whitespace. Their regression sources passed in the authorized safe harness/JSDOM runs.

Status adds the canonical saved driver `gameKind` independently from legacy Custom display names. New discovery/launch requires that reviewed kind, so a Custom server named Valheim cannot select Valheim's client mapping. Old Hosts without the required capability/kind return update-required/manual fallback. Requirements' four-second deadline includes lifecycle-gate waits; local feature role-gate acquisition also returns a typed retryable busy result after four seconds.

## Actual validation evidence: 2026-10-08

- PASS final `scripts/verify-code-only.ps1`: locked UI install, TypeScript, lint, locked solution restore, source compilation with zero warnings/errors, all eight safe FeatureChecks groups, Git whitespace and desktop diagnostic isolation. Log: ignored `local-data/feature-workers/code-only.log`.
- PASS complete JSDOM suite: **274 tests across 40 files**, no failed or skipped UI tests. Log: `ui-tests.log`. PASS targeted canonical backup refresh and Host notice edit/clear regression files: **38 tests across two files**, `ui-targeted.log`.
- PASS production UI bundle: TypeScript plus Vite output; one non-blocking warning for the 679.55-kB JavaScript chunk exceeding 500 kB. No code splitting/refactoring was added. Log: `ui-build.log`.
- PASS final app compilation embedding the freshly built UI, without running the app: **zero warnings/errors**, `app-build.log`. The earlier full source solution build also had zero warnings/errors (`source-build.log`).
- PASS separate safe harness: **8/8 groups**, `feature-checks.log`; repeated successfully by the final code-only gate. Counts refer to registered groups, not fabricated counts of individual assertions.

| Slice | Passing source group(s) and exercised cases | Passing JSDOM files |
| --- | --- | --- |
| Backup names/pins | Backup group: catalog-only changes, pin quotas/retention, immutable bytes, corruption/duplicate protection; canonical refresh submits the current pin | BackupBookmarks.test.tsx |
| Pre-join requirements | Compatibility plus shared input group: hash/metadata/manual scope, mod ambiguity/bundled components/pack manifests, assignment and exact expiry; fake Friend group: caps, malformed status, revocation | gameCompatibilityWire.test.ts, GameCompatibilityPanel.test.tsx |
| Game settings | Settings group: typed lists/properties, Java ASCII whitespace, preview/BOM/hash, maintenance/offline/checkpoint and protected Undo | gameSettings.test.ts, GameSettingsPanel.test.tsx |
| Open game | Fake launch/Friend group: exact URIs, unsupported/no-handler/failure, tampered/changed address, canonical kind, unassignment/revocation/cancellation and no poll/read launch | OpenGameButton.test.tsx |
| Pinned notices | Signed notice and fake Friend notice groups: edit/clear, revisions/key/room/tampering, durable tombstones/cache/capabilities/denials; shared strict revision input group | ServerChat.test.tsx, including strict notice decoding |
| Seven-day summary | Weekly projection group: clipping/union, outcomes/backups/untimed peaks, legacy/invalid/unfinished/retention gaps | WeeklySummary.test.tsx |

Initial validation honestly failed: the first code-only run stopped at five wire-decoder regex lint errors; the first harness run passed seven groups and exposed an uncaught `InvalidDataException`, followed by an invalid synthetic pairing seed without issuing lineage; the first complete UI run had 273 passing tests and one notice assertion selecting disappearing draft text. Fixes preserve validation semantics, include `InvalidDataException` in safe metadata failure classification, use the real pure pairing state machine in synthetic storage, and select the published notice paragraph in its test. Final relevant reruns and full gates pass. Initial logs are retained as `code-only-initial.log`, `feature-checks-initial.log`, `ui-tests-initial.log`.

Repository delivery is implemented, connected and validated. Real native/installed-game/served-browser/TLS/network acceptance remains skipped or unverified under the active-desktop restriction, and every real game's live-save sharing stays disabled. No tag/release/push/deployment/installation is authorized.
