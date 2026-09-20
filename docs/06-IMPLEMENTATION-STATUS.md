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

## 2026-09-20 — Slice 3 Valheim launch path, synthetic console evidence

Added a Valheim profile to the local GUI. The owner selects an installed `valheim_server.exe`, server/world names, existing save directory, game port, Steam or Crossplay mode, and public listing. The server password is entered separately and stored with Windows data protection, outside `host.json`. Host launches the selected EXE directly with fixed arguments and `SteamAppId=892970`; Friends still send only a saved profile ID. Each run gets a unique log file under local app data. Starting changes to Ready only when that run's log contains the upstream `Game server connected` signal. This is server-log readiness, not proof of a client join.

The Windows launch gives the game its own console. Stop rechecks PID, start time, and executable path, checks that the console contains only Host and the recorded game process, sends Ctrl+C, and waits for actual exit without force kill. The synthetic fixture confirms this control path and writes a disposable marker on Ctrl+C. The app reports save integrity as unverified; it has not run a real Valheim binary or inspected a real world. Remote Stop and auto shutdown remain unavailable until real permitted-player and companion coverage is verified. Owner-authored scripts for other games are the agreed next profile direction, but are not implemented in this slice.

| Check | Result |
| --- | --- |
| `scripts/build.ps1`: React/TypeScript, both fixtures, Windows x64 single EXE | Pass; 0 build warnings |
| `dotnet run --project checks/TogetherServer.ValheimChecks/TogetherServer.ValheimChecks.csproj -c Release` | Pass: 4 synthetic groups, 0 failures in final run |
| Protected password gate, typed argument quoting, log readiness, duplicate/world guards, Host restart, isolated Ctrl+C, unrelated process survival, second launch, existing disposable file | Pass in synthetic fixture |
| Original process checks | Pass: 7 cases, 0 failures |
| Served single-EXE GUI/API smoke including Valheim profile/password endpoint | Pass: 5 groups, 0 failures |
| Local Host/two-Friend companion regression | Pass: 10 groups, 0 failures |
| Visual browser rendering/interaction | Skipped: browser connection was unavailable in this environment; embedded assets were served and checked over HTTP |
| Owner-installed Valheim, actual game readiness/client join/world-save restart, public Friend network | Not run; require owner-installed binary and real clients/networks |

During development, the first Ctrl+C check terminated the synthetic test process because its Host Ctrl+C ignore state was removed before asynchronous signal delivery. Keeping that state until the fixture exited fixed it. Two subsequent runs found that .NET's `Process.ExitCode` was unavailable for an externally created process and that its native handle must be opened before exit. The final synthetic check passed all four groups. These three earlier failures do not count as real Valheim stop evidence.

One later served-smoke rerun failed to bind a randomly selected local TCP port (`10013`, Windows access denied). The smoke now asks Windows for an available loopback port and fails promptly if the app exits during startup; its final rerun passed all five groups.

Launch now: `./local-data/publish/TogetherServer.exe --host`, open `http://127.0.0.1:5127/`, and choose Valheim only after the owner approves an installed server and a safe test world. Exact next local check: `dotnet run --project checks/TogetherServer.ValheimChecks/TogetherServer.ValheimChecks.csproj -c Release`. No terms were accepted, game binaries downloaded, public ports changed, real credentials used, or real worlds touched.

## 2026-09-20 — Slice 4 local world import and Steam path discovery

Host mode now has a read-only search for Valheim Dedicated Server in fixed-drive Steam folders, the current user's Steam install registry path, and libraries listed in `libraryfolders.vdf`. It also lists local worlds with matching `.db` and `.fwl` files in `worlds_local`. An owner can point to another local save root when a single-player or multiplayer save came from another PC. The GUI offers an owner-clicked `steam://install/896660` link for Steam to handle installation; TogetherServer does not download or accept terms. The owner can rescan after installing.

For an existing save, the owner clicks **Use copy** or **Copy named world**. The local Host route copies both save files into a new profile-specific folder under local app data, never replacing an existing copy and never writing to the source. Valheim Start requires this imported copy and both files, so a mistyped name cannot silently create a new seed. **Create a new seed** is explicit and Start rejects a name with existing world files. Existing profile settings from earlier slices may need an import before Start. This code has only been exercised with disposable synthetic files; actual save compatibility and consistent import of a real world are unverified.

| Check | Result |
| --- | --- |
| `scripts/build.ps1`: React/TypeScript, fixtures, single Windows x64 EXE | Pass; 0 build warnings in final run |
| `dotnet run --project checks/TogetherServer.ValheimChecks/TogetherServer.ValheimChecks.csproj -c Release --no-restore` | Pass: 5 synthetic groups, 0 failures; includes second-library discovery, original/copy separation, missing pair, direct-source refusal, existing-name new-seed refusal, and synthetic restart |
| `dotnet run --project checks/TogetherServer.Checks/TogetherServer.Checks.csproj -c Release --no-restore` | Pass: 7 process cases, 0 failures |
| `checks/served-smoke.ps1` | Pass: 7 loopback HTTP groups, 0 failures; verifies bundled setup controls, read-only discovery route, import copy, and missing-save guard |
| `dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release --no-restore` | Pass: 10 local Host/two-Friend groups, 0 failures in the final run |
| Visual browser interaction | Skipped: browser runtime reported no available browser; served assets and routes were checked over HTTP |
| Steam installation, actual Valheim save import/start/join/stop/restart, public Friend network | Not run; require owner-approved installation, safe real-world test, and Friend network/client access |

One earlier parallel check invocation failed while two `dotnet run` builds tried to write the same `TogetherServer.dll` (`CS2012` file lock). Rerunning the Valheim check alone passed; the final checks above were run serially. No game terms were accepted, binary downloaded, public settings changed, real credentials used, or real world contents read or modified in this slice.

Launch now: `.\local-data\publish\TogetherServer.exe --host`, then open `http://127.0.0.1:5127/`. The next code action is to validate an owner-approved real Valheim installation and disposable copy through a client join and graceful save/restart, then verify permitted-player/idle behavior with real companions. Until then, remote Stop and auto shutdown remain blocked by the current policy.

## 2026-09-20 - Slice 5 double-click launch and Windows firewall prompt

Starting Git HEAD: `27d45cb2c07857e038771751b9bfd0b5dc44ceec`, clean `main` worktree. The published Windows GUI-subsystem EXE now opens its loopback GUI in the default browser with no arguments and no lasting console window. Host/Friend mode is selected in the GUI and saved locally. A second launch returns to the running app, and **Quit app** exits only after managed servers stop. Closing the browser tab leaves the app running. The one-file release path is stable; the served test no longer makes a fresh copy of the EXE for each run.

The owner-provided screenshot identifies the repeated permission dialog as **Windows Security / Windows Firewall network access**, not UAC. A local GUI run needs no public access; choose **Cancel** for that dialog while using only `127.0.0.1`. The app does not add firewall rules, disable notifications, or enable the public companion listener by default. Windows may ask again for a changed build or another EXE path; this has not been proven eliminated on the owner's machine.

An initial published-EXE synthetic Stop test timed out even though in-process checks passed. Windows can inherit a Ctrl+C ignore setting into a child server process. Host now clears that inheritable setting before launch; the final published-EXE checks observed the synthetic Ctrl+C stop marker and clean exit. The synthetic fixture is not Valheim save evidence. No real game executable, world, credentials, or public network setting was changed.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1`: UI, two fixtures, Windows x64 single EXE | Pass; 0 warnings, 0 errors |
| `local-data/release`: one EXE, PE GUI subsystem value 2 | Pass |
| `checks/desktop-smoke.ps1`: no-argument launch, synthetic start/stop/restart, second launch, mode persistence and Quit | Pass: 5 groups, 0 failures |
| `checks/served-smoke.ps1`: embedded GUI assets, local routes, synthetic import and start/stop, Quit | Pass: 9 groups, 0 failures |
| `TogetherServer.Checks`: duplicate, world, maximum, port, identity and unrelated-process guards | Pass: 7 cases, 0 failures |
| `TogetherServer.ValheimChecks`: synthetic import, readiness, Ctrl+C, restart and world-file preservation | Pass: 5 groups, 0 failures |
| `TogetherServer.CompanionChecks`: local Host/two-Friend HTTPS, heartbeat, permissions and notices | Pass: 10 groups, 0 failures |
| Rendered browser inspection | Skipped: the Browser runtime reported no available browser; HTTP asset and route checks passed |
| Real Valheim join/save/restart, public-IP Friend connection, real-world idle behavior | Skipped: owner installation and real client/network tests still required |

During development, six published synthetic Stop attempts timed out and one headless startup failed with `AllocConsole` error 5. The published Host's inherited Ctrl+C setting is the likely cause of the timeouts, based on the Windows API behavior and the passing tests after clearing it. A separate immediate Stop on a just-restarted fixture returned `StopUnconfirmed` before readiness; the managed run stayed recorded, and a restart/Stop after readiness passed. These exploratory failures are outside the final check counts above. The owner may still see the Windows firewall dialog for a changed executable. No firewall choice was made by the app or these tests.

Launch now by double-clicking `local-data\release\TogetherServer.exe`; no command line is needed. The next developer command for this slice is `.\checks\desktop-smoke.ps1`. The next acceptance action is an owner-approved Valheim Dedicated Server installation and disposable copy, then a real client join, graceful save/restart, and Friend network test. Remote Stop and auto shutdown remain gated on real permitted-player and heartbeat coverage.

## 2026-09-20 - Slice 6 native app window

Starting Git HEAD: `e1b5143a2aa8dd53b232adff8751fcee0cc4fc6e`, clean `main` worktree. Double-clicking the same single-file `TogetherServer.exe` now opens its bundled React interface in a TogetherServer window using WebView2. The local API remains bound to loopback. A second launch restores the existing window. Minimizing keeps Host monitoring or Friend heartbeat active; closing the window asks the existing Quit route to exit and stays open when a managed run must be stopped first. Host and Friend modes use the same window and saved mode choice.

The app uses the shared Microsoft WebView2 Runtime. If it is missing, a native message offers an owner-clicked link to Microsoft's installer. The app does not install it automatically. No game binary, real world, credential, or public network setting was changed. The Windows Firewall permission dialog may still appear for this EXE because the app has a local listener; this slice did not change firewall rules or prove that the dialog is gone on the owner's PC.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1`: React/TypeScript, two fixtures, Windows x64 self-contained single EXE | Pass; 0 errors, 1 `MSB3277` WindowsBase version warning from WebView2's unused WPF reference |
| `local-data/release`: one `TogetherServer.exe` | Pass |
| `checks/desktop-smoke.ps1`: visible native window and rendered React, synthetic Valheim start/stop/restart, second-launch restore, Friend mode persistence, window close and Quit | Pass: 5 groups, 0 failures |
| `checks/served-smoke.ps1`: bundled assets and local API through the released EXE | Pass: 9 groups, 0 failures |
| `TogetherServer.Checks`: duplicate, world, maximum, port, identity and unrelated-process guards | Pass: 7 cases, 0 failures |
| `TogetherServer.ValheimChecks`: synthetic discovery/import/readiness/Ctrl+C/restart | Pass: 5 groups, 0 failures |
| `TogetherServer.CompanionChecks`: local Host/two-Friend HTTPS, permissions, heartbeat and disable notice | Pass: 10 groups, 0 failures |
| Manual window interaction or screenshot | Not run; the smoke test checked a visible Win32 window and queried the rendered React DOM in WebView2 |
| Real Valheim join/save/restart, public-IP Friend connection, real-world idle behavior | Not run; owner-approved installation, safe test copy, and real client/network testing are still needed |

Two exploratory desktop-smoke runs found that sending a native close message closed the window without stopping the backend. The close handler now routes that message through Quit; the final smoke passed. The WebView2 WPF warning does not affect the published WinForms EXE in the observed checks, but a clean warning-free build is not claimed.

Launch now by double-clicking `local-data\release\TogetherServer.exe`. The exact next local developer command is `.\checks\desktop-smoke.ps1`. The next v1 acceptance step is the owner-approved real Valheim/client test on a disposable world copy, followed by a Friend PC on the intended network. Remote Stop and auto shutdown remain gated on real permitted-player and heartbeat coverage.

## 2026-09-20 - Slice 7 browse saves and custom Steam folders

Starting Git HEAD: `671a9ac65d3049c3ee057d39e0583897be26b452`, clean `main` worktree. Host mode now offers Windows file pickers for an installed `valheim_server.exe` and a world `.db` or `.fwl` file. The world picker starts in the current user's local Valheim folder when present, but can navigate to any drive and depth. Selecting a file inside `worlds_local` verifies its matching pair and copies both files into a separate profile import; the source is never moved or overwritten. A legacy `worlds` file is rejected with a Move to Local explanation. The existing typed source-root field remains available.

Automatic discovery now checks ready fixed and removable drives, common root paths such as `G:\Steam`, root-level custom folders containing `steamapps` or `worlds_local`, the registered Steam path, and listed Steam libraries. It does not recursively crawl arbitrary nested folders; the native picker handles those. Steam installation and Valheim save locations are separate. No real save content was imported, launched, or modified in this slice.

The owner's prior `local-data\release\TogetherServer.exe` was initially running with zero managed server runs. The first build compiled and published successfully but its final copy failed because Windows held that EXE open. The app was left running while a one-file candidate was built and tested on an isolated port. Once that old process had closed, the normal release path was updated and rebuilt successfully; the final checks below used the normal release EXE. No running owner app was stopped by the developer checks.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1`: React/TypeScript, two fixtures, Windows x64 single EXE | Pass; 0 errors, existing `MSB3277` WebView2 WPF reference warning |
| Normal release folder | Pass: one `TogetherServer.exe` |
| `TogetherServer.ValheimChecks`: custom Steam and save folders at a drive root, selected world pair, incomplete/legacy rejection, import and process fixture | Pass: 5 synthetic groups, 0 failures |
| `checks/desktop-smoke.ps1`: no-argument native React window, native world and server pickers open/cancel, synthetic start/stop/restart, relaunch, Friend mode, Quit | Pass: 7 groups, 0 failures; normal release EXE |
| `checks/served-smoke.ps1`: bundled Browse controls, local API, synthetic import and start/stop | Pass: 9 groups, 0 failures; normal release EXE |
| Actual picker file selection and real world import, join, save/restart, public Friend network | Not run; real game and world acceptance remain owner-gated |
| Process-only and companion suites | Not rerun; this slice did not change their lifecycle or public protocol paths |

Launch the updated app by double-clicking `local-data\release\TogetherServer.exe`. The exact next developer command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next v1 acceptance action is an owner-approved real Valheim/client test using a disposable copy of a world, then an actual Friend PC connection. Remote Stop and auto shutdown stay gated on player coverage and real save evidence.
