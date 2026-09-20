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
