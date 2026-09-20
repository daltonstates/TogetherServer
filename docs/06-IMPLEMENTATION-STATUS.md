# Implementation status

## 2026-09-20 — Slice 1 local Host and fixture

Starting Git HEAD: `e55a6199e560710510cf55b40706da5ae1e8ff50`, clean `main` worktree. All work stayed in this repository.

Built a .NET 10 Host/Friend executable with embedded React/TypeScript assets. The local GUI binds only to `127.0.0.1`; the mode switch works when no managed run is active. Host settings are atomically saved under Windows LocalAppData, with a single-instance file lock. The fixed fixture Start, Stop, and Health actions record PID, creation time, executable path, profile/world, and a private stop pipe. Starts are serialized and check duplicate runs, managed count, world/save-directory ownership, game-port overlap, and local UDP binding. An uncertain identity blocks start and stop. The fixture never touches save data. Remote controls and auto shutdown remain off.

Exact validation:

| Check | Result |
| --- | --- |
| `scripts/build.ps1`: npm lockfile install, TypeScript/Vite build, fixture build, Windows x64 self-contained publish | Pass (4 stages, no build warnings) |
| `dotnet run --project checks/TogetherServer.Checks/TogetherServer.Checks.csproj -c Release` | Pass: 7 cases, 0 failures |
| Duplicate Start, world conflict, managed maximum, port conflict, occupied UDP port, restart reattachment, identity mismatch/no unrelated Stop | Pass: 7 cases |
| `checks/served-smoke.ps1`: isolated single EXE, embedded HTML/JS/CSS, local mutation guard, served Start/Health/Stop, active-run mode guard, Host/Friend switch | Pass: 4 groups, 0 failures |
| Visual/interactable browser check | Skipped: browser connection unavailable in this environment; HTTP asset/API check does not replace it |
| Real Valheim startup/readiness/join/save/restart, Friend PC, public-IP reachability | Skipped: not implemented or external owner gates; no real-game claim |

One process test first exposed a race when the fixture exited before Stop reopened its PID. Stop now opens and rechecks the process handle before sending the signal; the full process check passed on rerun. Isolated check data stays under ignored `local-data/`.

Launch now: `./local-data/publish/TogetherServer.exe --host`, then open `http://127.0.0.1:5127/`. Build again with `./scripts/build.ps1`. The next local implementation slice is per-device pairing, pinned TLS, Friend heartbeat/status, and permission-checked remote actions with focused two-process tests. Real Valheim use still needs the owner's approved installation and real client/friend evidence. No game terms, public networking changes, credentials, or world operations were performed.

## 2026-09-20 — Slice 2 local Friend and companion HTTPS

Added one-time, 30-minute pairing invites; a self-signed Host TLS identity pinned by SHA-256 fingerprint; 90-day per-device credentials; rotation and revocation; and Windows protected storage for Host certificate and Friend credential. Host keeps credential hashes only. Friend mode checks a configured exact game-client executable and sends a heartbeat about every 15 seconds. Host uses receipt time and makes missing or 45-second-stale reports Unknown. The Host performs its own client path check while its GUI tab is closed. The separate companion HTTPS listener starts only after an owner-enabled setting, pairing material, TLS identity, and restart; the GUI remains loopback-only. Remote Start uses a saved profile ID and a request ID. Remote Stop remains denied while real permitted-player coverage is unverified. Remote Start/Stop toggling is enforced by Host immediately, while authenticated heartbeat/status remains available to communicate Disabled.

| Check | Result |
| --- | --- |
| `scripts/build.ps1`: npm/TypeScript bundle, fixture build, .NET Windows x64 self-contained publish | Pass; 0 build warnings |
| `dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release` | Pass: 10 groups, 0 failures |
| Separate Host and two Friend processes, pinned TLS, wrong pin, one-time invite, invalid token, public GUI isolation | Pass |
| Permission checks, duplicate/idempotent Start, rejected extra command text, replayed heartbeat, remote Stop Unknown denial | Pass |
| Host restart, online/offline disable notices, disconnected/reconnected status, revocation of one device while another works, credential rotation | Pass |
| Synthetic game client true/false, 47-second stale heartbeat becomes Unknown, fresh reconnect, authentication rate limit | Pass |
| Disabling the companion listener rejects public requests immediately; Friend client path can be cleared to Unknown and restored | Pass |
| Original fixture process checks and served single-EXE HTTP smoke after slice 2 | Pass: 7 cases and 4 groups, 0 failures |
| Visual browser rendering/interaction | Skipped: browser connection unavailable; static assets and API were checked over HTTP |
| Real Friend on another network, public-IP reachability, real Valheim join/save/restart | Blocked on owner-approved external setup and real clients; loopback tests are not substitutes |

An initial TLS handshake test failed on Windows because Schannel did not accept an ephemeral imported private key. The certificate is now imported into the current Windows user's key set. The following local HTTPS check passed. The identity and Friend credential remain encrypted at rest with Windows data protection; tests use disposable data under ignored `local-data/`.

One process-check run reported a fixture Stop cleanup failure after a concurrent duplicate Start test. The test now prints the Stop code on failure, and the fixture pipe connection allowance increased from 3 to 8 seconds for slow startup. Four immediate process-check reruns and the final build's process, served, and companion checks passed. The cause of that one transient failure was not captured, so it is not counted as a real-game graceful-stop pass.

Launch now: `./local-data/publish/TogetherServer.exe --host` and open `http://127.0.0.1:5127/`. The next code slice is a typed Valheim launch/graceful stop path and verified permitted-player/idle rules. Real game terms, installation, public router/firewall/DNS changes, credentials, and world data remain owner gates. No such action was taken in this slice.
