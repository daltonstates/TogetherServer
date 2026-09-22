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

## 2026-09-20 - Slice 8 Valheim 1.0 world folders

Starting Git HEAD: `8592e351f9d833c7181745135d40c9686b3fa5c8`, clean `main` worktree. Host discovery now recognizes complete chunked world folders in `worlds_local` and Steam's `userdata/<account>/892970/remote/worlds`, alongside older `.db`/`.fwl` pairs. The native window offers a folder picker. An owner-selected folder is copied into a separate profile import while every source file is open against writes; a changed file list, an incomplete latest `_main` revision, a missing chunk, or a duplicate target refuses the import. Host Start accepts an imported folder and refuses a new seed when a folder with that name already exists. The file checks do not prove that Valheim can load or save the copy.

A read-only discovery call against the current PC found one `V1release` world in a Steam cloud cache. No real world file was imported, launched, moved, or modified. The build still does not change a router, firewall, DNS, or game terms.

| Check | Result |
| --- | --- |
| `scripts/build.ps1`: React, fixtures, Windows x64 single EXE | Pass; 0 errors, existing `MSB3277` WebView2 WPF reference warning |
| `TogetherServer.ValheimChecks`: local/cloud folder discovery, complete copy, source integrity, incomplete latest revision, imported Start and new-seed guards, fixture lifecycle | Pass: 5 synthetic groups, 0 failures |
| `checks/desktop-smoke.ps1`: no-argument EXE, rendered React, three native pickers, fixture start/stop/restart, relaunch, mode and Quit | Pass: 8 groups, 0 failures |
| `checks/served-smoke.ps1`: bundled React assets, local routes, synthetic pair and cloud-folder import, fixture lifecycle | Pass: 10 groups, 0 failures |
| Current PC `V1release` detection | Pass: one folder found by read-only app discovery; no content copy or game load |
| Early exploratory runs | Two failures corrected: concurrent UI/.NET builds raced an embedded asset filename (`CS1566`); the first new test used a different profile ID than its imported folder. Final sequential build and checks pass. |
| Process-only and companion suites | Skipped in this slice; those paths were unchanged. |
| Real Valheim load/join/save/restart, Friend public-IP connection, permitted-player and idle behavior | Not run; requires a disposable real-world test and an actual Friend PC. |

Double-click `local-data\release\TogetherServer.exe`, choose Host, click **Find Valheim installs and saves**, and review the `V1release` cloud-folder entry. For a development recheck, the exact next command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance action is a real game test on a disposable copy after owner approval for any terms-gated installation or operation that could change a real world.

## 2026-09-20 - Slice 9 real V1release copy and simpler Host UI

Starting Git HEAD: `04df72658f5fec9476314ce88220742395f7e7c7`, clean `main` worktree. The owner requested a full test using `V1release`. The literal `worlds\_local\V1release` path does not exist, and `worlds_local\V1release` contains only older map cache files. Read-only discovery found the complete newer `V1release` folder in Steam's Valheim cloud cache. TogetherServer imported all 23 files into an ignored, separate test directory; all 23 import hashes matched the source. The original source still had 23 unchanged hashes after all three start/stop cycles. No original world file was deleted, moved, or overwritten.

The installed Valheim Dedicated Server Steam build ID was `25390671` (executable SHA-256 `E01757027E08D35C5FC926ADFEEC164344B73EADB4D4C61196945642B787E4FD`); the installed client build ID was `25390630`. The test used a disposable password, UDP start port `45678`, Steam backend, no public listing or crossplay relay, and an isolated Host app/data copy. Three server runs showed `Game server connected` after loading `V1release` revisions 107, 108, and 109. Each accepted TogetherServer Stop, exited gracefully, and logged `World save (5/5) done`; the test copy advanced to revision 110. Windows showed UDP ports `45678` and `45679` owned by the recorded server PID while running. No Valheim client joined; the third run was stopped rather than left unattended. The isolated Host app also exited. The logs contain private player history and remain ignored outside Git. These observations prove server load, port binding, readiness signal, graceful save, and restart on the test copy. They do not prove a client join, in-game change, or public reachability.

The Host GUI now separates **Servers**, **Setup**, and **Friends**. Setup edits one server at a time, leads with world/import, installed server, and password, and collapses uncommon paths, game options, limits, and idle settings. One **Save setup** action saves settings followed by the protected password. The copied world source stays separate. The published self-contained EXE continues to open in its own WebView2 window.

| Check | Result |
| --- | --- |
| Real `V1release` import, source integrity, three starts, three readiness checks, three graceful saves, restart load | Pass; on a separate copy, 23 source hashes unchanged, revision 110 saved |
| Real client join, recognizable world change and return after restart | Pending owner participation; no client launched during the live test window |
| `npm run build` and `scripts/build.ps1`: React/TypeScript and Windows x64 single EXE | Pass; 0 errors, existing `MSB3277` WebView2 WPF reference warning |
| `checks/served-smoke.ps1`: bundled React, local API and synthetic fixture lifecycle | Pass: 10 groups, 0 failures |
| `checks/desktop-smoke.ps1` on the combined-save UI | Pass: 8 groups, 0 failures; no Valheim client process was active during the brief native-window check. A later removal of the redundant App label was covered by the final build and served check. |
| In-app browser visual review | Skipped: browser runtime returned no available browser. A native screen-capture attempt was rejected by automatic approval review as blocked by policy; no screenshot was taken. |
| Public-IP Friend connection, permitted-player coverage and idle shutdown | Not run; a real Friend PC and owner network setup are still required. Auto shutdown remains off. |
| Process and companion suites | Not rerun; no lifecycle, pairing or remote protocol code changed in this slice. |

Double-click `local-data\release\TogetherServer.exe` to inspect the new GUI. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance action is to restart the isolated test Host when the owner is ready, then have the owner join, make a visible change, leave, and verify it after a graceful stop and restart of the copied world. Friend PC network and permission checks follow.

## 2026-09-20 - Slice 10 setup guidance and Steam install detection

Starting Git HEAD: `72dfd66`, clean `main` worktree. The owner hit `InvalidSettings: Executable and save directory must be absolute paths` while the normal app had no saved Host profile. Setup now lists the missing world, dedicated-server install, and password beside a disabled **Save setup** button. The API returns a field-specific explanation for an absent or relative path; incomplete settings are not saved. Adding a Host server starts the read-only drive and Steam-library scan, and exactly one discovered `valheim_server.exe` is selected automatically. Several installations remain a visible choice. Friend mode can discover `valheim.exe` for its heartbeat path; Host can fill the owner's client path. Neither mode launches the Valheim game client.

The Host continues to launch the Steam-installed dedicated-server executable directly with the selected profile arguments. The [official Valheim guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) allows direct Windows launch and Steam launch, and documents that Steam's shortcut points at the original startup script. Direct launch keeps process identity and graceful Ctrl+C stop under TogetherServer's control. No Steam install, game terms, public network setting, or real world was changed in this slice.

| Check | Result |
| --- | --- |
| `npm run build` and `scripts/build.ps1 -ReleaseName release-candidate` | Pass; TypeScript and Windows x64 candidate EXE built with 0 errors, existing `MSB3277` WebView2 WPF warning |
| `TogetherServer.ValheimChecks` | Pass: 5 synthetic groups, 0 failures; includes Steam manifest game-client detection |
| `checks/served-smoke.ps1 -AppPath .\local-data\release-candidate\TogetherServer.exe` | Pass: 11 groups, 0 failures; incomplete world/server settings rejected with actionable messages, Host and Friend discovery route served |
| `TogetherServer.CompanionChecks` against the final candidate | Pass: 10 groups, 0 failures; saved Friend client path survives clear, restore, poll and reconnect |
| Read-only discovery in an isolated candidate Host process on this PC | Pass: one installed dedicated server, one Valheim game client, and one complete `V1release` folder detected; no game process launched |
| Normal release EXE hash, native window and served GUI | Pass: normal release SHA-256 matched the candidate; `checks/desktop-smoke.ps1` passed 8 groups, 0 failures, and `checks/served-smoke.ps1` passed 11 groups, 0 failures |
| Real client join/world-change, public Friend network, idle behavior | Not run; previous real copy load/save evidence still stands, and these require owner/client participation |
| Process-only suite | Not rerun; managed-run identity and stop code did not change. The Valheim fixture and served smoke cover the touched setup and discovery paths. |

The owner authorized replacing the normal EXE. The first Quit API attempt returned connection refused because the old window had already closed; no process was stopped by that attempt. Three exploratory companion runs failed at the new client-path field because the check still launched the older normal release EXE. The check now accepts an explicit app path, and the final candidate passed all 10 groups. After verifying no TogetherServer process remained, the final candidate was copied to `local-data\release\TogetherServer.exe`; the two SHA-256 hashes matched (`388F0C33D73BF264BA320542F5520939DA8C91003D520C9C4F065FF56B6B3C6E`). Users launch the updated normal EXE by double-clicking it; no command line is required. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance action is for the owner to set up and start the separate `V1release` copy in Host mode, then join it with a real Valheim client and verify an in-game change after restart.

## 2026-09-20 - Slice 11 Friend invitation and Valheim join address

Starting Git HEAD: `6287c73`, clean `main` worktree. The owner reported that the Friends tab had no visible invite code and showed a local Valheim join at `127.0.0.1:2456`. The Host GUI now separates the **Valheim Join IP** from the **TogetherServer Friend app invitation**. The owner enters a public IPv4 address once; saved Valheim profiles show copyable `<public IP>:<game port>` addresses in Host Servers and Friends. A paired Friend receives that configured join address in authenticated status even when remote Start/Stop is disabled. Loopback, private, shared-address, and documentation IPv4 ranges cannot be saved as the public game address. This field is owner-entered and does not prove the public IP or router path works.

The Host Friends tab now exposes the app's HTTPS endpoint, its distinct TCP port, the prerequisites for **Create invite for this PC**, and the entire one-time invitation after it is created. **Use game IP for Friend app** only fills the endpoint and bind setting in the unsaved form; the listener remains off until the owner explicitly enables it, saves, and restarts. The Friend GUI explains that the invitation pairs the app for status and permitted controls; the player still joins Valheim separately with the Join IP. Valheim's Crossplay join code is generated by the game, not TogetherServer. No public network settings, real IP, real credentials, or world data changed in this slice.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate`: React/TypeScript and Windows x64 single EXE | Pass; 0 errors, existing `MSB3277` WebView2 WPF reference warning |
| `TogetherServer.ValheimChecks` | Pass: 6 synthetic groups, 0 failures; includes public/local/shared/test address handling |
| `TogetherServer.CompanionChecks` against candidate | Pass: 10 groups, 0 failures; paired Friend receives only the Valheim join address while controls are Disabled |
| `checks/served-smoke.ps1` against installed EXE | Pass: 12 groups, 0 failures; bundled GUI text, public-IP rejection and persistence, local API and fixture lifecycle |
| `checks/desktop-smoke.ps1` against installed no-argument EXE | Pass: 8 groups, 0 failures; visible native window, rendered React, fixture lifecycle, pickers, mode and Quit |
| Candidate and installed EXE SHA-256 | Pass: both `0A53B7A7605C762120B8E83ACB965C1FDE42A48C77DF157CA3F2E82F71B101C9` |
| Interactive browser layout review | Skipped: the in-app browser had no available browser instance. The native window rendered and the served asset checks passed, but they are not a visual layout review. |
| Real Friend PC public game join, pairing over public IP, router/firewall reachability | Not run; requires the owner's public network setup and a Friend on another network. The owner's `127.0.0.1` screenshot demonstrates local use only. |
| Process-only identity suite and real save/restart | Not rerun; managed process identity, launch, and stop code was unchanged. Prior real-copy load/save evidence still stands. |

Two exploratory failures were resolved before the final checks: parallel UI and .NET builds raced an embedded asset filename (`CS1566`), so the final build ran sequentially; the first expanded companion check assumed one profile and failed after 3 groups. It now selects the intended profile by ID. Its exact synthetic fixture process was matched by PID, start time, and executable before cleanup; no unrelated process was stopped. The final companion run passed all 10 groups. The normal EXE was replaced only after the old TogetherServer process had exited.

Double-click `local-data\release\TogetherServer.exe`, choose **Host → Friends**, enter and save the public game IP, fill and save the separate HTTPS endpoint, create one invitation per Friend PC, then explicitly enable the companion listener and restart when public access is approved and configured. The exact next developer command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance action is a real Friend PC pairing and Valheim Join IP test from a different network; no localhost result can substitute for it.

## 2026-09-20 - Slice 12 automatic public address lookup

Starting Git HEAD: `45ede0a`, clean `main` worktree. The owner rejected typing a public IP. Host mode now makes a bounded outbound HTTPS lookup through ipify when the GUI opens and every 15 minutes while it remains open. The detected IPv4 and check time are stored locally; the Host shows copyable Valheim Join IP addresses and a retry button. A paired Friend receives an address only if the Host's check is less than one hour old. Invalid, stale, or failed lookup results cannot become a claimed Friend address. An older unsaved form cannot replace a newer detected address. The main Friend app setup has a **Use detected address** button; a custom endpoint remains under advanced settings for local tests. No user needs to type their public IP for the normal path. The game and Friend app address are still separate ports.

The lookup is an address hint, not evidence that the Host accepts inbound connections. The app still defaults to no public companion listener and remote Start/Stop off. This slice did not change Windows Firewall, a router, DNS, game worlds, credentials, or the server launch process.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: bundled React/TypeScript and Windows x64 single EXE, 0 build errors; existing `MSB3277` WebView2 WPF reference warning |
| `TogetherServer.ValheimChecks` | Pass: 7 synthetic groups, 0 failures; includes valid, loopback, and oversized lookup responses plus stale form preservation |
| `TogetherServer.CompanionChecks` against candidate | Pass: 10 groups, 0 failures; paired Friend hid a stale Valheim Join IP, received a fresh one after update, and saw the disabled controls notice |
| `checks/served-smoke.ps1` against candidate and again against installed EXE | Pass: 12 groups each, 0 failures; bundled GUI contains detected address controls and no old manual game-IP field |
| Isolated candidate Host, real outbound HTTPS lookup | Pass: detected and persisted a public IPv4; actual address was omitted from test output. This did not test inbound reachability. |
| `checks/desktop-smoke.ps1 -AppPath .\local-data\release-candidate\TogetherServer.exe -Port 0` | Pass: 8 groups, 0 failures; native window, React, pickers, synthetic lifecycle, modes, and Quit |
| `checks/desktop-smoke.ps1` against installed no-argument EXE | Pass: 8 groups, 0 failures; same native desktop checks. Installed and candidate SHA-256 both `7A6B6A1396947F608F2105086F9046B887CB0CA9BC6F7CFAB022D7BF0AE2B01D`. |
| Actual public Friend PC pairing and Valheim game join | Not run; needs a different network and owner-approved router/firewall setup if required. |
| Real client world change/save/restart and idle behavior | Not run in this slice; previous real-copy load/save evidence still stands, but it is not client join evidence. |
| Interactive visual layout review and process-only suite | Skipped; the native window rendered and the changed bundled UI text was checked, while process launch/stop code did not change. |

Double-click `local-data\release\TogetherServer.exe`, choose **Host → Friends**, and wait for the detected address. The Join IP for each server can then be copied without entering an IP. To prepare a Friend app invitation, click **Use detected address**, save, and create an invite for that PC. Public listener and remote controls still need separate owner actions. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance action is pairing and joining from a real Friend PC on another network; verify the game connection before treating the detected address as reachable.

## 2026-09-20 - Slice 13 simpler Host and Friend connection

Starting Git HEAD: `606bf1c`, clean `main` worktree. The owner asked for a TeamViewer-like connection with an IP and a standard port that can be overridden. Host **Friends** now shows the TogetherServer app address and each Valheim game Join IP side by side, with copy buttons. TCP **5131** is the standard Friend app port; the Host may choose another under advanced settings before creating its first pairing code. Creating that first code now prepares the detected app address and bind setting without opening the listener. The owner still enables Friend connections, saves, and restarts deliberately. The Friend screen accepts `IP` or `IP:port` and a one-time pairing code. Pasting the code can fill the address; afterward the paired address is saved on that PC. Game client path, bind IP, custom endpoint, and the pinned fingerprint are tucked into advanced details. Unpaired or disconnected remote-control status says Unknown.

The code remains necessary once per Friend PC: it carries the pinned TLS identity and individual revocable credential. Friend mode rejects a typed IP/port that differs from the code. Host mode refuses to change an address after its identity is pinned, instead of silently breaking existing pairings. A future recovery flow is needed if the owner's public IP changes after pairing. This slice does not claim public Internet reachability or dynamic-IP recovery.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: React/TypeScript and Windows x64 single EXE, 0 errors; existing `MSB3277` WebView2 WPF reference warning |
| `TogetherServer.ValheimChecks` | Pass: 7 synthetic groups, 0 failures; fixture launch, stop, save guards, and unrelated process identity still pass |
| `TogetherServer.CompanionChecks` against the candidate backend before the final display-only rebuild | Pass: 10 groups, 0 failures; default and explicit port parsing, mismatched address rejection, pinned Host address guard, pairing, permissions, disable notice, restart, and stale heartbeat |
| `checks/served-smoke.ps1` against final candidate | Pass: 13 groups, 0 failures; bundled Host/Friend controls, first-invite default address preparation with listener still off, and synthetic lifecycle |
| `checks/desktop-smoke.ps1 -AppPath .\local-data\release-candidate\TogetherServer.exe -Port 0` | Pass: 8 groups, 0 failures; native window, React, pickers, synthetic lifecycle, modes, and Quit |
| Headless Edge rendering of isolated Host Friends and unpaired Friend screens | Pass: both full-page views captured and visually reviewed. The screenshot files remain in ignored local test data because they show the detected IP. |
| Installed normal EXE SHA-256 and `checks/desktop-smoke.ps1` | Pass: installed and candidate hashes both `3069A8AA42F11B5DBD99BFDBC56A44F0B7400D94D5E81F3A45222D15B4385CDD`; 8 desktop groups, 0 failures with no arguments |
| Public Friend network, real Valheim join/world change/idle | Not run; still requires the owner's real Friend client and approved public network setup. |

One exploratory parallel test attempt failed to compile with `CS2012` because two .NET check projects wrote the same build output at once; the projects were run separately and both final suites passed. The first Edge visual probe clicked before the app page was ready; the later full-page Host and Friend render passed. No public firewall/router/DNS setting, real credential, or world was changed.

Double-click `local-data\release\TogetherServer.exe`. In **Host → Friends**, copy the address, make one pairing code per PC, then explicitly allow Friend app connections, save, and reopen the app. A Friend enters the Host IP and code once; the standard port is assumed. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance step remains a real Friend PC connection and Valheim join from a different network, followed by game save/restart and recovery evidence.

## 2026-09-20 - Slice 14 Host IP and password pairing

Starting Git HEAD: `248bdfb`, clean `main` worktree. The owner reported that Connect was not visible and asked for Host IP plus password, without entering a Friend IP. The Friend screen now keeps **Connect to Host PC** visible and asks only for Host IP (`:port` only when changed) and a generated password. Clicking Connect with an empty field gives an inline message. A paired PC has a visible **Check connection now** action and can choose another Host. Host **Friends** generates a separate password per PC without asking for that PC's name or IP, shows the auto-assigned device label, and keeps explicit Start/Stop permissions and revocation. The password is a long copy/paste token containing the full pinned TLS fingerprint, one-time device secret, ID, and expiry. The app saves a distinct protected credential after pairing. Existing JSON invitations remain accepted until their original expiry; they are no longer shown in the GUI. The Host public listener and remote controls remain off by default.

| Check | Result |
| --- | --- |
| Final `scripts/build.ps1 -ReleaseName release-candidate` | Pass: bundled React/TypeScript and Windows x64 EXE, 0 errors; existing `MSB3277` WebView2 WPF reference warning |
| `TogetherServer.CompanionChecks` against candidate backend | Pass: 10 groups, 0 failures; password round trip, invalid/expired password, required Host IP, wrong TLS pin, two local Friend processes, one-time use, permissions, disabled notice, revocation, and stale heartbeat. Run before the final display-text-only rebuild. |
| `checks/served-smoke.ps1` against final candidate | Pass: 13 groups, 0 failures; served React contains visible Host IP/password controls, local invite returns the generated password without enabling the listener, and synthetic process lifecycle works. |
| `checks/desktop-smoke.ps1` against final candidate and then normal installed EXE | Pass: 8 groups each, 0 failures; visible native window, React, pickers, synthetic lifecycle, mode persistence, and Quit. |
| Headless Edge render of installed unpaired Friend screen | Pass: full 1100 × 850 screenshot reviewed; Host IP, password, and enabled Connect button are visible without scrolling. Screenshot stays in ignored local test data. |
| Candidate and installed EXE SHA-256 | Pass: both `1865D7AFD7458CD05A39A8A02F5D3B59733A8D66ECEB87FDAEB83EF00F262458`. |
| Real Friend PC public-IP pairing, public network reachability, Valheim client join, idle/Unknown, save/restart/recovery | Not run in this slice; requires a Friend PC on another network and owner-approved network changes if needed. The local fixture tests are not real game evidence. |

Exploratory failures resolved: the first build failed with `CS0136` from a duplicate local variable; renaming it made the final build pass. The first Edge screenshot command used a malformed profile argument and produced no file; the corrected installed-build render passed. No public firewall/router/DNS setting, real credential, or world was changed.

Double-click `local-data\release\TogetherServer.exe`. On the Host PC, choose **Host → Friends**, click **Generate password**, copy the Host IP and password, explicitly enable Friend app connections, save, and reopen the Host app. On each Friend PC, open the same EXE, choose **Friend**, paste that Host IP and its own password, and click **Connect to Host PC**. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. The next acceptance action is a real Friend PC pairing and Valheim join from another network, followed by the remaining save/restart, idle, disable-notice, and recovery checks in `docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md`.

## 2026-09-20 - Slice 15 guided Host and Friend pages

Starting Git HEAD: `43a969e`, clean `main` worktree. The owner wanted fewer tabs and a guided sequence. Host mode now has one page. With no saved server it shows one prominent **Add Valheim server** button. The setup form shows the world first, the installed server step after the world is chosen, and the game password step after an executable is selected. After saving, the form closes and the same page shows **Start server**, honest status, Stop/Health/Edit, and the share section. The share section puts **Copy Join IP**, **Copy game password**, Host app IP, and per-device **Generate password** in order; technical settings remain collapsed. A local Host-only POST returns the protected game password only when its copy button is used. The Friend page now opens directly to Host IP, password, and Connect, without the three pre-pairing Unknown cards. No Host tab navigation remains.

| Check | Result |
| --- | --- |
| Final `scripts/build.ps1 -ReleaseName release-candidate` | Pass: React/TypeScript and Windows x64 single EXE, 0 errors; existing `MSB3277` WebView2 WPF reference warning |
| `checks/served-smoke.ps1` against final candidate | Pass: 14 groups, 0 failures; bundled guided labels, local-only game-password copy with a synthetic secret, denial without local headers, denial in Friend mode, and synthetic server lifecycle. |
| `checks/desktop-smoke.ps1` against final candidate and installed no-argument EXE | Pass: 8 groups each, 0 failures; visible native window, pickers, synthetic start/stop/restart, mode persistence, and Quit. |
| Headless Edge visual review of fresh Host, saved synthetic Valheim Host, and unpaired Friend | Pass: all three screens rendered and reviewed. The Add button, Start button, both game copy buttons, Friend password generation, and Friend Connect button were visible in the expected sequence. Screenshots stay in ignored local data and may contain the detected public IP. |
| Candidate and installed EXE SHA-256 | Pass: both `C0D920019B73EEC11E917F808D8807474CC8499094BEC3D3D8C511A9327202AC`. |
| Interactive browser click probe | Not passed: Edge rejected a local debugger WebSocket with HTTP 403. Automatic approval review then rejected the debugger-origin retry as "blocked by policy"; the static renders and served checks above are the available UI evidence. |
| Process, Valheim, and companion suites | Not rerun in this UI slice; process supervision and companion pairing code were unchanged. The new local password-copy route was covered by the served check. |
| Real Friend PC public-IP route, Valheim join, world change, idle/Unknown, and recovery | Not run; still needs an owner-approved public route and a real Friend PC. Synthetic evidence does not satisfy the v1 acceptance gate. |

Double-click `local-data\release\TogetherServer.exe`. On Host, click **Add Valheim server** and follow the visible steps, then click **Start server** and use the share controls on the same page. On a Friend PC, choose **Friend**, enter the Host IP and that PC's password, and click **Connect to Host PC**. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. No public firewall/router/DNS setting, real credential, or world was changed.

## 2026-09-20 - Slice 16 Host and Friend connections together

Starting Git HEAD: `382c236`, clean `main` worktree. The owner found that opening Friend after a server stopped failed with `CompanionListenerActive`, then clarified that joining another Host must also work while this PC is hosting. **My server** and **Friends' servers** are now views of one running app. Changing views no longer stops a managed game or closes the authenticated Host listener. The app polls its own game process and its paired remote Host in the background regardless of the visible view. Quit checks managed runs from either view. The Host server card always offers **Invite friends**, so the owner does not need to open Friends' servers to manage invitations. A generic HTTP 403 from a disabled listener now shows Disconnected/Unknown instead of incorrectly claiming the Friend credential was revoked.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: bundled React/TypeScript and self-contained Windows x64 EXE, 0 errors; existing `MSB3277` WebView2/WPF reference warning. |
| `TogetherServer.CompanionChecks` against candidate | Pass: 11 groups, 0 failures. Two separate local Host processes were used: the first kept a synthetic server and its paired Friend connection active while it paired with the second Host. A switch of views did not bypass Quit's managed-run guard; reopening on Friends' servers restored the Host listener. Disabled-listener 403 became Unknown; actual revocation remained Revoked. |
| `checks/served-smoke.ps1` against candidate | Pass: 14 groups, 0 failures; bundled page labels, synthetic Start/Health/Stop, switching views with a running process, and Quit guard. |
| `checks/desktop-smoke.ps1` against candidate and installed no-argument EXE | Pass: 8 groups each, 0 failures; native window, React, pickers, synthetic launch/stop/restart, mode-view persistence, and Quit. |
| Headless Edge render of saved synthetic Host page | Pass: visually reviewed the My server page, its Friends' servers navigation, and the Invite friends button beside a stopped server. Screenshot remains in ignored local test data. |
| Candidate and installed EXE SHA-256 | Pass: both `E1087068335045D752CA247BC23888DA3381E77EAAA460FB4E40B3A2A16AED7C`. |
| Real public Friend PC, game client join/world change, idle/Unknown, and recovery | Not run. Requires a real Friend PC/public route and the separate owner approvals described in `docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md`. |

Two intermediate companion checks failed while the now-replaced exclusive-mode behavior was being tested: a generic 403 initially appeared as Revoked, and a test then expected 403 after the paused response was changed to 503. The owner clarified that Host and Friend must be concurrent; the exclusive-mode path and those temporary expectations were removed. Final checks above passed. No public firewall/router/DNS setting, real credential, or real world was changed. The app currently remembers one other Host connection per PC; pairing a different Host replaces it, so several saved Host PCs need a separate slice if wanted.

Double-click `local-data\release\TogetherServer.exe`. Use **My server** to start/stop and invite friends; use **Friends' servers** to connect to another Host without interrupting your own. The exact next developer recheck command is `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1`. Real public Friend/client acceptance remains the next external gate.

## 2026-09-20 - Slice 17 smaller self-contained EXE

Starting Git HEAD: `e21d787`, clean `main` worktree. The owner asked why the app was large. The prior EXE measured 145,387,017 bytes (138.65 MiB); the React assets measured about 270 KiB. The self-contained .NET, ASP.NET Core, and Windows desktop runtime accounts for most of the binary. The publish command now enables .NET single-file compression while retaining one Windows x64 EXE and the same no-.NET-install launch flow. The compressed EXE measured 64,098,317 bytes (61.13 MiB), about 56% smaller. WebView2 still uses its existing Windows Runtime.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: React/TypeScript and compressed self-contained Windows x64 EXE, 0 errors; existing `MSB3277` WebView2/WPF reference warning. The direct compressed publish and script-built candidate had identical SHA-256. |
| `checks/served-smoke.ps1` against compressed EXE | Pass: 14 groups, 0 failures; bundled GUI and synthetic process lifecycle. |
| `checks/desktop-smoke.ps1 -AppPath .\local-data\publish-compressed\TogetherServer.exe -Port 0` | Pass: 8 groups, 0 failures; native window, pickers, synthetic stop/restart, and Quit. |
| `TogetherServer.CompanionChecks` against compressed EXE | Pass: 11 groups, 0 failures; local pairing, pinned TLS, Host/Friend concurrency, disable/revoke, and stale heartbeat. |
| Replacement of normal `local-data\release\TogetherServer.exe` | Pending: the owner's normal EXE was running in a visible window, so the tested candidate was kept at `local-data\release-candidate\TogetherServer.exe`. Do not overwrite the running app or discard its form. |
| Real Valheim client join/public Friend connection | Not run; no claim of game or public network acceptance follows from compression checks. |

Once the owner closes the running window, copy the candidate EXE to `local-data\release\TogetherServer.exe`, verify matching SHA-256, then run the exact next command `powershell -NoProfile -ExecutionPolicy Bypass -File .\checks\desktop-smoke.ps1` against the normal no-argument launch. No terms, firewall/router/DNS setting, credential, or real world was changed.

## 2026-09-20 - Slice 18 simpler daily flow and guarded remote Stop

Starting Git HEAD: `7a4a626`, clean `main` worktree. The Host page now puts Start or Stop, Copy game details, and Invite friend on the saved server card. Initial setup asks for a new or copied world and a game password, with automatic server discovery and **Save and start**; paths and network options are collapsed. A new world defaults to an app-managed save folder. TogetherServer records the exact new world it created so its save files do not prevent a later Start, while an unrecorded existing world remains protected. The Friend page asks for one current invite and shows the available action. Current `TS2` invites contain the Host IP, port, one-time secret, and full TLS pin. Older `TS1` invites remain readable with a separate Host IP until expiry. Creating an invite can enable the HTTPS companion listener in the running app, with no save/reopen step. Disabling it stops the listener immediately. The local GUI stays on loopback.

Remote Stop can now be granted per paired Friend PC after the owner assigns unique Valheim Platform User IDs. While offline, TogetherServer can create `permittedlist.txt` in an app-managed new world or imported copy; it will not overwrite an existing list. A Ready run must have loaded that exact list at Start. The Host checks the list fingerprint and exact enrolled IDs, fresh closed-game reports from all paired Friends, and the owner's local client before sending Ctrl+C. The check is repeated immediately before the signal. Missing or changed coverage denies remote Stop. Local Stop remains available to the owner. Auto shutdown remains off.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1 release-candidate`: React/TypeScript, fixtures, compressed Windows x64 single EXE | Pass; 0 errors; existing `MSB3277` WebView2 WPF reference warning |
| `TogetherServer.Checks` | Pass: 7 process groups, 0 failures |
| `TogetherServer.ValheimChecks` | Pass: 8 synthetic groups, 0 failures; exact list, false/true heartbeat, changed list denial, Ctrl+C Stop, app-owned new-world restart |
| `TogetherServer.CompanionChecks` against normal release EXE | Pass: 12 groups, 0 failures; single-invite pairing, immediate listener enable/disable, permissions, stale Unknown, and a paired Friend's HTTPS Stop of a restricted synthetic Valheim process delayed for 7 seconds |
| `checks/served-smoke.ps1` against candidate | Pass: 14 groups, 0 failures; bundled simplified labels, local API, synthetic game lifecycle |
| `checks/desktop-smoke.ps1` against normal no-argument EXE | Pass: 8 groups, 0 failures; visible native React window, pickers, synthetic Stop/restart, mode persistence, Quit |
| Normal installed EXE | Pass: `local-data\release\TogetherServer.exe`, 64,110,601 bytes; SHA-256 `C991BA9A806455F734E72D12443D730BCE7AA9CC512C795CF92ECFE6D2656265` matches candidate |
| In-app browser visual interaction | Not run: the Browser runtime reported no available browser; the desktop smoke checked a visible native window and rendered React |
| Real Valheim Friend join, recognizable world change, public-IP reachability, real permitted-list enforcement | Not run; local fixtures and an outbound public-IP lookup do not prove these paths |

During development, the first restricted fixture run used a server name that the synthetic console intentionally rejects; the fixture was changed to its required test name. A test then restored a list using a different line ending, and the production fingerprint correctly denied Stop; restoring the original bytes made the check pass. One exploratory fixture run had a transient unrelated-process start failure and passed on rerun. No real game binary, world, router/firewall/DNS setting, credential, or game terms were changed in this slice. The normal EXE was not running when it was replaced.

The Friend request timeout for Stop is now 105 seconds, longer than the Host's 90-second graceful Valheim exit wait. The synthetic console deliberately delays its exit by 7 seconds in the HTTPS Stop check, so the Friend must receive the completed result rather than timing out at its former six-second default.

Double-click `local-data\release\TogetherServer.exe`. The next acceptance work needs a real Friend PC on a separate network and an owner-approved disposable Valheim world: pair, join, leave, request Stop, restart, and verify a recognizable in-world change. The app's restricted-list and heartbeat policy is implemented, but real-game and public-network acceptance remain open.

## 2026-09-21 - Slice 19 one server code and compact daily flow

Starting Git HEAD: `07ec8f3`, clean `main` worktree. Each saved server now owns one current `TS3` code. The owner can copy that same code to several Friend PCs; every successful pairing still receives a separate 90-day device credential, permissions, heartbeat identity, and individual revoke control. Refreshing the code replaces its protected generation and revokes the old code plus every credential for that server. Credentials and codes for other saved servers remain valid. Older `TS1` and `TS2` per-device invites remain readable until their original expiry. The current code is stored with Windows data protection and can only be read through the local action gate.

The everyday interface is shorter. First use opens a ready-to-fill server form immediately and auto-selects a single detected dedicated-server install. The normal server card keeps Start or Stop and Invite together; its code, Copy, and Refresh controls expand inline. Secondary paths, multi-server controls, safety, and network details stay collapsed. Friend setup is one code field with Connect on the same row, and Enter submits it. Game-path controls remain hidden until that PC is paired.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: React/TypeScript, both fixtures, and compressed self-contained Windows x64 EXE; 0 errors and the existing `MSB3277` WebView2/WindowsBase reference warning. `npm ci` reported 0 vulnerabilities. |
| `TogetherServer.CompanionChecks` against the final candidate | Pass: 12 groups, 0 failures. Proved one stable code and saved Start default per server, two PCs sharing it with distinct credentials, pinned TLS, public GUI isolation, permissions, idempotency, Host restart, per-device revoke, scoped refresh revocation, cross-server preservation, stale Unknown, rate limiting, and guarded HTTPS Stop. Isolated data: `local-data/companion-checks/6d182c0601f544a4a4b7a8fe555f89e8`. |
| `TogetherServer.Checks` | Pass: 7 process groups, 0 failures. Isolated data: `local-data/checks/8b6ba8742d3f4c758736361df1f2c5f3`. |
| `TogetherServer.ValheimChecks` | Pass: 8 synthetic groups, 0 failures, including server-scoped remote Stop coverage. Isolated data: `local-data/valheim-checks/6014c5078d134409b5570841025c0576`. |
| `checks/served-smoke.ps1` against the final candidate | Pass: 14 groups, 0 failures. Proved the bundled compact labels, protected current-code read, persistent code and Start default, local request gate, settings, import guards, synthetic lifecycle, concurrent Host/Friend views, and Quit. Isolated data: `local-data/served-smoke/bfc9292289684898b8b5da0969653c57`. |
| `checks/desktop-smoke.ps1` against candidate and installed no-argument EXE | Pass: 8 groups each, 0 failures. Candidate data: `local-data/desktop-smoke/4e714cb7de944b5db85b1ca81030e223`; installed data: `local-data/desktop-smoke/fdb61b9fbc3746d89bb5b8ca46385395`. |
| Headless Edge and CDP layout review | Pass: fresh Host setup, saved Host with inline server code controls, and unpaired Friend were reviewed at 1100 x 850. The setup and its Save actions fit without scrolling. A 390 x 844 emulated view kept the mode switch, Quit, server actions, Copy, and Refresh visible with `innerWidth=390`, `scrollWidth=390`. The final follow-up bound the already-saved Start default to the same controls without changing their structure or CSS. Ignored evidence: `local-data/ui-final/b0ffaed006d64066a4872c41f37f45ce`. |
| Installed EXE | Pass: candidate and `local-data/release/TogetherServer.exe` both SHA-256 `52081D829E7FCF8D7616B2914D8251F00A7E9945278F702D7A4B4571B341EA72`, 64,114,664 bytes. No TogetherServer process was left running. |
| Real public Friend PC, public route, and Valheim client join | Not run. This still requires another PC/network and any owner-approved router or firewall setup. Local fixtures and loopback HTTPS are not real public or game acceptance. |

Exploratory failures were resolved before the final checks. The first compile found a duplicate local variable (`CS0136`). One parallel UI/test attempt replaced a bundled asset during compilation (`CS1566`), so final checks ran serially. A visual process briefly locked the candidate during an intermediate rebuild and was stopped by its exact path and PID. The in-app Browser runtime could not create its kernel assets (`os error 3`); the established headless Edge fallback produced the reviewed screenshots. Edge's direct 390-pixel screenshot initially used a wider minimum layout, so CDP device emulation was used and proved no horizontal overflow. A redundant second CDP capture stalled after the final screenshots and metrics already existed; its exact Node process was stopped. The existing `MSB3277` warning remains. No real world, credential, public listener, firewall, router, or DNS setting was changed.

Double-click `local-data\release\TogetherServer.exe`. On a saved server, choose **Invite friend**, create or copy its current code, and send the same code privately to the Friend PCs for that server. A Friend chooses **Join a friend**, pastes the code, and presses Enter or **Connect**. Use **Refresh code** only when every paired PC for that server should lose access and reconnect with a new code.

## 2026-09-21 - Slice 20 game drivers, connection checks, compact flow, and custom window

Starting Git HEAD: `2cab614`, clean `main` worktree. Host lifecycle behavior now dispatches through an explicit registry of built-in `IGameServerDriver` implementations. Each driver owns its validated executable contract, fixed launch arguments, declared ports, readiness evidence, join-address shape, and graceful stop. `HostManager` still owns serialization, exact process identity, world ownership, concurrency, and authorization. Unknown game kinds fail closed. Valheim is the only normal game; the second registered driver is an isolated test fixture. `docs/07-ADDING-A-GAME.md` records the bounded extension and evidence contract without adding a plugin or public scripting surface.

The saved server card now shows Server, Game ports, Friend controls, and Friend route at a glance. Windows listener tables supply local TCP/UDP evidence. Local listeners never claim a public route: only a fresh authenticated Friend heartbeat shows **Friend reached**, and a real game join remains separate. Valheim Crossplay reports relay readiness. Initial Valheim setup places world name and password together and offers direct **Start server** with **Save only** secondary. One **Invite friend** click creates or reuses the server code, enables the listener when possible, and copies it; **Refresh access** retains the existing server-scoped revocation behavior.

The native WinForms shell is borderless and uses TogetherServer colors, an app mark/title, draggable title bar, resize edges, and custom minimize, maximize/restore, and close buttons. Close still uses the guarded local quit action, so a managed game blocks exit. The desktop smoke invokes the controls through Windows UI Automation rather than assuming that a borderless frame rendered correctly.

| Final check | Result |
| --- | --- |
| `TogetherServer.Checks` | Pass: 9 groups, 0 failures. Added explicit Valheim/fixture registry and unknown-kind denial, plus two UDP listeners, one TCP control listener, fresh Friend route, and missing-port states. Isolated data: `local-data/checks/c7171dd9307248ec8e5ab19d96f21b0e`. |
| `TogetherServer.ValheimChecks` | Pass: 8 synthetic groups, 0 failures, including typed launch, readiness log, restart identity, Ctrl+C, world preservation, and remote Stop rules. Isolated data: `local-data/valheim-checks/3d9ca6c3a531455ea5ec14d1c7bacb73`. |
| `TogetherServer.CompanionChecks` | Pass: 12 groups, 0 failures, including shared server code, distinct credentials, pinned TLS, local/public isolation, replay safety, scoped refresh revocation, fresh/stale heartbeat, rate limiting, and HTTPS Stop. Isolated data: `local-data/companion-checks/dba4a4c699264796a612001fd9414073`. |
| Final `scripts/build.ps1 -ReleaseName release-candidate` | Pass: `npm ci` found 0 vulnerabilities; TypeScript typecheck, Vite bundle, both fixtures, and compressed self-contained Windows x64 publish completed with 0 errors. The existing `MSB3277` WebView2/WindowsBase reference warning remains. |
| Final `checks/served-smoke.ps1` against candidate | Pass: 16 groups, 0 failures. Proved embedded compact labels, explicit game catalog, mutation gate, settings and invite boundaries, synthetic lifecycle/import protections, local UDP game listeners, honest unverified Friend route, mode switching, and Quit. Isolated data: `local-data/served-smoke/8e1aa39ea69d48e29ddad29ba17851fb`. |
| Final `checks/desktop-smoke.ps1` against candidate and installed no-argument EXE | Pass: 9 groups each, 0 failures. Proved visible custom chrome and React, accessibility and behavior of custom minimize/maximize/restore, all native pickers, synthetic Stop/restart, second-launch restore, custom close, and local Quit. Candidate data: `local-data/desktop-smoke/bfd55258e8fb48c4803510c861de08bf`; installed data: `local-data/desktop-smoke/3c4fa7699b6543e4ace247977bcf9313`. |
| Standalone Edge/CDP interaction and layout review | Pass: fresh compact setup and one-click invite at 1440x900, 768x1024, and touch-enabled 390x844; no horizontal overflow or escaped interactive control at any size. Start and Invite stayed inline at all widths. One real CDP touch switched to the compact Friend page. Receipt/screenshots: `local-data/ui-slice20/final-eb4220ebb627445fa46af61213e7f535`. This is the established standalone fallback; the in-app Browser runtime reported no available browser. |
| Native frame visual review | Pass: final 1180x820 capture shows the app-styled frame, title, and conventional minimize/maximize/close order. Evidence: `local-data/ui-slice20/native-final-1fe5f79deaf84a0581852ab57b117910/native-custom-chrome.png`. |
| Installed EXE | Pass: candidate and `local-data/release/TogetherServer.exe` both SHA-256 `5B82F723291879CEA738A817277AB1C5BF2A060411B763104A57B1C2D0C0698E`, 64,128,713 bytes. No TogetherServer process remained after validation. |
| Real public Friend PC and Valheim game join | Not run. A local listener and synthetic game sockets do not prove router/firewall reachability, relay behavior, or a real client join/save/restart. |

Development failures were retained as evidence and corrected before the final runs. Early compilation found the not-yet-added `CustomChrome` property, protected `MaximizedBounds` access, and a missing `server` icon union member. The first expanded process run passed 8 groups and failed fixture cleanup because an attached .NET `Process.ExitCode` read was not reliable; both drivers now preserve the verified native handle through graceful exit, and the terminal run passed 9/9. The first CDP pass used an invalid zero touch-point count; the next proved the touch request changed the saved mode but waited for old copy, and the terminal pass corrected both. A native capture first requested an unavailable `System.Drawing.Common` assembly and was rerun with the available `System.Drawing` assembly. Visual review then found reversed window-control order and a hidden title label; both were corrected and recaptured. One exploratory PowerShell UI Automation listing command had a parse error and was rerun successfully. No real game, world, credential, router, firewall, DNS setting, or external Friend PC was changed.

Double-click `local-data\release\TogetherServer.exe`. First use now needs the world name, game password, and **Start server**. On a saved server, **Invite friend** prepares and copies the current code in one click. Read the four connection cards as local evidence; a real Friend on another network must still test the control connection and Valheim join separately.

## 2026-09-21 - GitHub Release updater

Starting Git HEAD: `1f18b59`, clean `main` worktree. The published Host/Friend EXE now checks the public `daltonstates/TogetherServer` GitHub Releases feed on opening and about every six hours. A compact version button allows an immediate recheck. A newer stable release offers one **Update and restart** action. The app accepts only a fixed `TogetherServer-win-x64.exe` release asset from that repository, bounds its metadata and download size, checks the release SHA-256 digest, and waits for the verified updater copy to signal readiness before closing. Installation is blocked while any managed game run is active or unresolved. The helper waits for the old app to exit, atomically replaces its EXE, keeps the immediate prior EXE beside it, and relaunches under the same Windows account. The local GUI and companion permission boundaries remain in force.

The current app version is `0.1.0`. The public repository has no Releases yet. The installed app's live update endpoint returned `NoRelease` in isolated data `local-data/update-live-check/9e08e75768b345ddab8d8a71398c5a46`. The first upload is prepared at `local-data/github-release/v0.1.0/TogetherServer-win-x64.exe`; it has **not** been published. Pre-updater EXEs need this build installed once manually.

| Check | Result |
| --- | --- |
| `scripts/prepare-github-release.ps1` | Pass: `npm ci` reported 0 vulnerabilities; TypeScript and Vite built; fixtures and compressed self-contained Windows x64 EXE built with 0 errors. The existing `MSB3277` WebView2/WindowsBase warning remains. |
| `TogetherServer.UpdateChecks` | Pass: 6 groups, 0 failures. No-release state, exact release URL/digest, packaged version matching the tag, tampered download removal, prerelease/foreign URL denial, repeat atomic replacement and prior-EXE backup. Isolated data: `local-data/update-checks/1cd280146cfe44d9b7442773b7358e38`. |
| `TogetherServer.CompanionChecks` against candidate | Pass: 12 groups, 0 failures. Isolated data: `local-data/companion-checks/db34676e7449406d810ecac9be4b3ad2`. |
| `checks/update-handoff-smoke.ps1` against final candidate | Pass: 3 groups, 0 failures. The actual new EXE signaled readiness, waited for an isolated parent to exit, replaced a disposable old file, retained its backup, relaunched, and quit cleanly. Isolated data: `local-data/update-handoff/68b20c7e32fd485f8d0d504c77ca590c`. |
| `checks/served-smoke.ps1` against final candidate | Pass: 17 groups, 0 failures, including bundled update controls and local-only desktop update action. Isolated data: `local-data/served-smoke/53f7658c60e94ec4b8f16c4f9aa18048`. |
| `checks/desktop-smoke.ps1` | Pass: 10 groups, 0 failures on final candidate and 10 groups, 0 failures on the normal installed EXE. Includes denial of an update while a synthetic managed game is running. Isolated data: `local-data/desktop-smoke/e75358256df7462e93a6c06157ec5dd9` and `local-data/desktop-smoke/7b060620062e452786bcd19d73789330`. |
| Edge/CDP update UI | Pass: inspected 1440x900, 768x1024, and touch-enabled 390x844. No horizontal overflow; update prompt and button remain visible. A real CDP touch reached the guarded install route. The release-available response was mocked solely for this UI check. Screenshots and receipt: `local-data/update-visual/810629c18b67438e903b183d20158f60`. |
| Candidate, local install, prepared GitHub asset | Pass: all three are 64,139,810 bytes and SHA-256 `D6BF6DB690CA84EB30FD7C727F587151D53861D9916099B0ECA01168541C7CF3`. The previous normal EXE is backed up as `local-data/release/TogetherServer.exe.previous`; the original pre-updater EXE is retained as `local-data/release/TogetherServer.exe.pre-updater`. File and product versions are both `0.1.0`. |
| Actual GitHub Release download | Not run: there is no published GitHub Release. The isolated HTTP/download and helper replacement paths passed, but they do not prove a live published update. |

Two exploratory visual-harness runs failed before the final pass: the first sent an invalid zero touch-point count to Edge, and the second used a fresh Host setup with unsaved changes, where the update button was correctly disabled. The harness was corrected to use valid touch emulation and a clean Friend page. The first helper-handoff test also failed before readiness because PowerShell added spaces inside quoted path arguments; the test launcher was corrected to pass one argument string, and the real helper then passed the isolated handoff. Targeted whitespace verification passed for the new updater files, CompanionServer, and new update checks. A broad `Program.cs` whitespace verification still reports pre-existing compact route formatting from line 180 onward; the new HostOnly branch was formatted. No real world, router, firewall, DNS, public listener, credential, or GitHub release was changed for this updater.

## 2026-09-21 - Windows sign-in and close to tray

Starting Git HEAD: `ab3e7865fff673f2c4230c62a8bce24b1de8fe52`, clean `main` worktree. The shared Host/Friend header now has an app settings menu with two independent options, both off by default: **Open at Windows sign-in** and **Close to tray**. The first writes one current-user Windows Run entry for the exact EXE with `--startup`; that launch starts quietly in the tray. The second saves a local preference and makes the custom X and normal window close hide the window. The same Host process, managed game and Friend polling continue. The tray icon opens the existing window and offers Quit; Quit still uses the managed-run guard. A manual second launch restores the hidden app. The release version is `0.1.1`.

| Final check | Result |
| --- | --- |
| `TogetherServer.Checks` | Pass: 10 groups, 0 failures, including an isolated current-user registry key, exact quoted startup command, invalid-EXE denial, opt-out removal, and persisted tray preference. Isolated data: `local-data/checks/4de213125d534da9a4f8951aaa1716cc`. |
| `scripts/prepare-github-release.ps1` | Pass: `npm ci` reported 0 vulnerabilities; TypeScript, Vite, both fixtures, and self-contained Windows x64 publish completed. Existing `MSB3277` WebView2/WindowsBase reference warning remains. |
| `checks/served-smoke.ps1` on final candidate | Pass: 18 groups, 0 failures, including the local mutation gate and rejection of startup changes from a headless process. Isolated data: `local-data/served-smoke/5cbbb37454fa46bdbdd06c43b8aa357d`; full output: `local-data/tray-validation/served-candidate.txt`. |
| `checks/desktop-smoke.ps1` on candidate and installed no-argument EXE | Pass: 12 groups each, 0 failures. Closing to tray kept a managed synthetic server and API alive, hidden Quit was denied while it ran, reopening worked, and duplicate `--startup` launches remained hidden until a manual second launch. Candidate data: `local-data/desktop-smoke/bab8a72fc4be401e871a963071b8a947`; installed data: `local-data/desktop-smoke/02b28ff361704fa2a9d64bffc01fd7a3`. |
| `TogetherServer.UpdateChecks` and `checks/update-handoff-smoke.ps1` | Pass: 6 and 3 groups, 0 failures. Isolated data: `local-data/update-checks/04e348319d2648b28111289c114202a3` and `local-data/update-handoff/62e47c988b834e7ca6514b977cabc4e3`. |
| `TogetherServer.CompanionChecks` | Pass: 12 groups, 0 failures; isolated data: `local-data/companion-checks/b03f858133f547d0b3e8590882415ad2`. |
| Edge/CDP app settings UI | Pass: menu stayed within 1440x900, 768x1024, and actual-touch 390x844 viewports with no horizontal overflow. A touch enabled Close to tray through the real local API without enabling Windows startup. Screenshots and receipt: `local-data/tray-visual/8d37234b81f14d7eb640efa2e3a95792`. |
| Candidate, local install, prepared GitHub asset | Pass: all three have file version `0.1.1.0`, 64,144,651 bytes and SHA-256 `6D116BE11459BBE26915AD6AA3C82A6D9D52B01D7A715E3F1A3F5F6EB7FBFA2A`. The prior local EXE is backed up as `local-data/release/TogetherServer.exe.pre-0.1.1`. The `v0.1.1` upload asset is at `local-data/github-release/v0.1.1/TogetherServer-win-x64.exe`; it was not published by this work. |
| Actual Windows sign-in cycle and real Friend/game traffic while hidden | Not run. The per-user registry entry, hidden `--startup` process, synthetic hosted game, and companion protocol were tested independently; a real logon and external Friend session were not performed. |

Exploratory failures were fixed before the final passes. The first desktop script had a PowerShell parse error. Its picker helper then targeted a WebView2 popup rather than the native dialog, including on the prior installed EXE; the final helper enumerates the owning process's dialog. Early smoke runs using a relative `-AppPath` left exact disposable test processes behind; those verified PIDs were stopped and the script now resolves the absolute path for cleanup. The first 390-pixel menu screenshot showed left clipping, which was corrected. One intermediate rebuild could not replace the candidate while those test processes held it; the final build, visual check, and both desktop smokes passed. No real world, public listener, router, firewall, DNS setting, or actual Windows startup entry was changed by validation. No TogetherServer process remained running.

## 2026-09-21 - Minecraft Java and Bedrock built-in drivers

Starting Git HEAD: `64b4db1`, clean `main` worktree. The existing driver seam now has separate Java and Bedrock implementations with owner-selected, prepared server folders. Java requires an installed `java.exe`, a JAR in that folder, and an existing `eula=true`; Bedrock requires `bedrock_server.exe` in the folder. Both check `server.properties` against the saved world name and port without changing it. Bedrock also declares its IPv6 port and any default LAN discovery ports, including their address families, for conflict and listener checks. Java starts with fixed `-jar` and `nogui` arguments; Bedrock starts its saved executable. A local Java status response or Bedrock RakNet pong advances a matching process to Ready. Local Stop sends only `stop` to the exact isolated process console and waits for exit. Minecraft remote Stop and auto shutdown remain unavailable.

Shared Host rules now record each run's declared protocol, address family, and port, recover older run ownership from its saved profile, and allow the same world name in different save folders while blocking the same folder. Friend mode stores each invite's credential and game-client check separately, polls all saved connections, and remembers which one is displayed. The UI shows a game selector, Minecraft setup and native pickers, game names and addresses, and a saved connection selector. Minecraft setup fields and validation live in a separate React module. The public status includes the game kind; Friend requests remain fixed Start/Stop actions against saved profile IDs. The architecture, game-extension guide, and user setup instructions reflect these boundaries.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: `npm ci` found 0 vulnerabilities; TypeScript/Vite, three fixtures, and self-contained Windows x64 EXE published. The existing WebView2/WindowsBase `MSB3277` warning remains. Artifact: `local-data/release-candidate/TogetherServer.exe`, 64,157,552 bytes, SHA-256 `8BD916474E36F5AE83B52BA92114358957BAB7805D58912AC33BC66B568AA250`. |
| `TogetherServer.MinecraftChecks` | Pass: 3 groups, 0 failures. Prepared-file validation, concurrent Java TCP and Bedrock IPv4/IPv6 UDP fixtures sharing a numeric port and world name in separate folders, LAN discovery declarations, IPv6 port conflict, local status, graceful `stop`, remote Stop denial, and older-run port recovery. Disposable data: `local-data/minecraft-checks/9d77dbd5f169408989299f91159a20e3`. |
| `TogetherServer.Checks` and `TogetherServer.ValheimChecks` | Pass: 11 and 8 groups, 0 failures. Shared process/world/port rules and synthetic Valheim lifecycle regression. Disposable data: `local-data/checks/20d8df46ec654e1d8987920942b2e2fb` and `local-data/valheim-checks/150f31759648448da2a4a8a3cc6e6496`. |
| `TogetherServer.CompanionChecks` against candidate | Pass: 12 groups, 0 failures. Separate saved Friend pairings, credential scopes, selected connection persistence across restart, heartbeat, permissions, revocation, and restricted Valheim remote Stop. Disposable data: `local-data/companion-checks/6cb2358494974a728be870375241b856`. |
| Served and desktop smokes against candidate | Pass: 18 served and 15 desktop groups, 0 failures. Included game catalog, bundled React, native Minecraft file/folder pickers, mode switching, and existing desktop controls. Disposable data: `local-data/served-smoke/d060ea2b674740bb9a60a05208eee410` and `local-data/desktop-smoke/e7c2a609a3fd4b6b90d0f6aebbc2af8c`. |
| Visual layout, actual Minecraft binaries, client joins, and save/restart | Not run. The in-app Browser reported no browser available. Native desktop/React rendering passed, but the new setup and connection-selector layout was not visually reviewed at desktop/tablet/phone sizes. Java/Bedrock process and status checks used disposable fixtures; no real Minecraft executable, real world, external Friend, public route, or recognizable save change was tested. |

An initial attempt to publish over `local-data/release/TogetherServer.exe` failed only when copying because that installed EXE was in use. The final candidate build and all listed candidate checks passed. The running installed app was left alone. No terms-gated binary was downloaded, EULA was accepted, real world was touched, or public firewall/router/DNS setting was changed.

## 2026-09-21 - Minecraft detection and owner-initiated installation

Starting Git HEAD: `454b364`. Minecraft setup now searches bounded common folders, saved profile folders, and the app's own install folder for `bedrock_server.exe` and manifest-bearing Java server JARs. It lists multiple JARs separately and reads each folder's world name and port from `server.properties`. A Java runtime is detected from app-managed installs, saved profiles, `JAVA_HOME`, common Program Files locations, or `PATH`. The owner can select a detected server or browse elsewhere.

An owner-only local action now installs the current official Java or Bedrock dedicated server into a new app-managed folder after an unchecked EULA/privacy consent box is affirmatively selected. Java downloads a matching Eclipse Temurin JRE and checks the JAR/runtime sizes and hashes from official metadata. Bedrock uses the official Windows ZIP link, validates its location and archive paths, and configures a fresh `server.properties`; the official link response does not currently provide a digest. An installation never reuses or overwrites an existing server/world folder. Java writes `eula=true` only for the fresh install after the owner's consent. The desktop opens only the two exact legal links in the external browser. Existing Java server folders still require the owner to set their own EULA file. Start and Stop continue through the existing game drivers; Minecraft remote Stop and automatic shutdown remain unavailable.

| Final check | Result |
| --- | --- |
| `TogetherServer.MinecraftSetupChecks` | Pass: 4 groups, 0 failures. Bounded multi-JAR and Bedrock scan, denial before a network request without consent, synthetic Java/Bedrock installs, existing file preservation, JAR/runtime checksum, URL, and ZIP rejection. Final disposable data: `local-data/minecraft-setup-checks/fb15c7eeeba7444f92b1a54d117803f5`. No real game binary or runtime was downloaded. |
| Existing process checks | Pass: Minecraft 3, shared 11, Valheim 8 groups, 0 failures. All use disposable fixtures. |
| Published Windows EXE | Pass: bundled React build and self-contained Windows x64 publish. Final artifact: `local-data/minecraft-setup-final/TogetherServer.exe`, 64,172,292 bytes, SHA-256 `6EB131B52CBE26EAB32025DDE1A9A329BDF31B42BA161089541338453EB66592`. Existing WebView2/WindowsBase `MSB3277` warning remains. |
| Served and native desktop smoke on final EXE | Pass: 19 and 15 groups, 0 failures. Includes host-only Minecraft discovery, missing-consent denial without a download, Friend-mode denial, bundled setup controls, native window/pickers, and existing lifecycle controls. Disposable data: `local-data/served-smoke/53fbad4dacb84de68e8f22faaba68291` and `local-data/desktop-smoke/7da23ae7b639478f8f2a02c5f8488521`. |
| Real installation, Minecraft client join, saved world restart, visual setup layout, and public Friend route | Not run. No browser control tool was available for viewport inspection. Real binaries and terms-gated downloads were not used during agent validation. |

The normal candidate copy was in use by an existing TogetherServer process, so the first build's final copy step failed after publish; the final EXE was published and copied to a separate directory. The desktop smoke's default port was also occupied by that existing process, so the final desktop run used a free isolated port and passed. The existing process was not stopped or replaced. No EULA was accepted by the agent, and no real world or network setting was changed.

## 2026-09-21 - Cancel setup and direct Add new server

Starting Git HEAD: `ba968e7`. The Host page now shows **Add new server** directly below saved servers in place of the More hosting settings dropdown. **Cancel** is available while setting up a new or existing server and restores the last saved settings. Canceling the first unsaved server leaves a clear empty Host page with an Add new server button. It does not save a partial profile or change an existing saved server. The setup-only More setup options section still owns the maximum managed-server setting.

| Final check | Result |
| --- | --- |
| TypeScript/Vite and Windows x64 publish | Pass. Artifact: `local-data/cancel-final/TogetherServer.exe`, 64,172,300 bytes, SHA-256 `033B490370710BE30099267B0D5DCB9CB20442EDBC1E4E63C44E2CEA594FB823`. |
| Rendered Edge interaction | Pass in two final runs: first-run Cancel, repeat add/Cancel, canceled setup staying closed across page switches, second-server Cancel while one saved profile remains, and desktop/phone layout with no horizontal overflow. Final disposable data and screenshots: `local-data/cancel-ui-test/4f4b24522f274ec89b69db403ba15961`. |
| Served and native desktop smoke on the final EXE | Pass: 19 and 15 groups, 0 failures. Disposable data: `local-data/served-smoke/254f789b6f184295bbacf3e672742458` and `local-data/desktop-smoke/cf87a0e939fd44a7a20363a2ccea1300`. |
| Real game or Friend network acceptance | Not run; this change only affects the local setup draft and Host controls. |

One exploratory browser run reached setup before its Cancel button was enabled while startup discovery was pending. A later run intermittently left Add new server disabled after Cancel; delayed path detection was a plausible cause. The check now waits for enabled controls, and automatic path filling runs only while setup is open. Two final browser runs passed. The previously running TogetherServer process was left untouched; validation used isolated data and a free local port.

## 2026-09-21 - Server grid and settings dialogs

Starting Git HEAD: `d7cee04`. Saved Host servers now form a responsive two-column grid on desktop and stack at smaller widths. Each card keeps Start/Stop and Invite visible, with a Settings button that opens the existing server setup in a native modal dialog. Add new server opens the same dialog. Settings and safety opens a separate modal from the grid header. Cancel, Escape, and closing after a successful save use the existing draft and persistence rules. A second Cancel button sits beside Save and Start so long Minecraft forms can be exited after scrolling to the bottom.

| Final check | Result |
| --- | --- |
| TypeScript/Vite and Windows x64 publish | Pass. `local-data/release/TogetherServer.exe`, 64,172,830 bytes, SHA-256 `CB1CD8000EC7F737E2ACF1ABB1C7B6D58620BB7112B2E286C94022DECF201357`. The existing WebView2/WindowsBase `MSB3277` warning remains. |
| Rendered Edge interaction and screenshots | Pass: two saved servers side by side at 1280 px; first-run setup, per-server setup, Add new server, and Settings and safety opened as dialogs; Cancel and Escape closed them; canceled global changes and a third unsaved server were absent from the saved snapshot. Save only persisted an edited server name and closed its dialog. At 1280, 768, and 390 px there was no horizontal overflow. The long Minecraft Java setup scrolled to Save/Start/Cancel at 390 px. Disposable evidence: `local-data/grid-modal-ui-test/92a142a8d8c3430c93eddcb238ab31ff`. |
| Served and native desktop smoke on the final EXE | Pass: 19 and 15 groups, 0 failures. Disposable data: `local-data/served-smoke/3ee9f149e7814c3eb1aafc4efc79b045` and `local-data/desktop-smoke/14e79c7e1a6b4b3f98dc52001aa98d97`. |
| Real game and public Friend network acceptance | Not run. This UI change was tested with disposable profiles and synthetic game processes. |

The first exploratory grid check ran before setting a desktop browser viewport and saw the browser's narrower default width; the corrected 1280 px run and the final rerun passed. No game terms were accepted, real world files changed, or public network settings modified.

## 2026-09-21 - Compact server card actions

Starting Git HEAD: `927e952`. Each saved Host card now places Start/Stop, Invite friends, and an icon-only settings gear in one row, with the gear at the right. When available, Game details or Game address stays on a separate row below. The gear has a server-specific accessible label and still opens that server's setup dialog. Labels wrap inside the buttons at narrow phone widths.

| Final check | Result |
| --- | --- |
| TypeScript/Vite and Windows x64 publish | Pass. `local-data/action-row-final/TogetherServer.exe`, 64,172,958 bytes, SHA-256 `B4D4B6DD058256A2DBB704848C65A188B89F957EDD835C99CA1EDDB9005F35B2`. The existing WebView2/WindowsBase `MSB3277` warning remains. |
| Rendered Edge interaction on the published EXE | Pass: two saved cards and the action row at 1280, 390, and 320 px; no horizontal overflow at 1280, 768, 390, or 320 px; the gear opened the selected server's modal; Cancel, Escape, Save only, global settings, and the scrollable Minecraft setup path passed. Disposable screenshots and data: `local-data/action-row-ui-test/6b52008f68c34eca9f179c1affafd60f`. |
| Native Windows desktop smoke | Pass: 15 groups, 0 failures, including visible WebView rendering, native window controls and pickers, and synthetic lifecycle actions. Disposable data: `local-data/desktop-smoke/7e155f8e88864a5fa25aae5774dfec90`. |
| Real game and public Friend network acceptance | Not run for this layout change. The rendered test used disposable fixture profiles in an isolated loopback Host process. |

The normal `local-data/release/TogetherServer.exe` was in use by an existing TogetherServer process, so Windows refused to replace it. The running app was left untouched; launch the new build after ending that session safely.

## 2026-09-21 - Prepared v0.1.2 GitHub upload

The installed EXE reported `0.1.1.0`, so the app and UI package versions were raised to `0.1.2` before preparing the next update. The public GitHub repository showed no Releases; `git ls-remote --tags origin` showed only `v0.1.0`. The release preparation script built the current sources without publishing anything.

| Final check | Result |
| --- | --- |
| Prepared asset | `local-data/github-release/v0.1.2/TogetherServer-win-x64.exe`, 64,172,965 bytes, file version `0.1.2.0`, SHA-256 `375DDAEB29EB8527E518CD12907B4680FE504A293BB791074EE4EAA59C4655D5`. The adjacent `.sha256` file and release candidate matched. |
| Build and exact asset smoke | Pass: `scripts/prepare-github-release.ps1`, 19 served groups, and 15 native desktop groups, 0 failures. Disposable smoke data: `local-data/served-smoke/3115221d63b34079a756f16f1a222c4a` and `local-data/desktop-smoke/81b2d6d7780242e495e3835240ee6ba9`. The existing WebView2/WindowsBase `MSB3277` warning remains. |
| GitHub publishing and live update | Not run. The asset is ready for owner review and upload as the `v0.1.2` release. |

## 2026-09-22 - Outside Friend connection diagnosis and v0.1.3 local build

The owner reported that a Friend could not connect from another network. The saved Host already had its authenticated HTTPS listener enabled on TCP 5131 and bound to all IPv4 interfaces. While the old app was running, loopback and LAN TCP connections succeeded, but an outside TCP checker saw the same public IPv4 address as the invite and could not connect. The installed EXE had an enabled Windows Firewall allow rule for inbound TCP on the active Public profile. This evidence points to the router or upstream route; the router's read-only admin page returned HTTP 401, so its WAN address and forwarding table were not inspected or changed.

The Host now reports the configured bind scope, current local listener, fresh or stale invite address, and active private LAN address for a router forwarding target. A read-only **Test Friend app port from internet** action uses portchecker.io to confirm that it sees the invite's public IPv4 before testing the fixed configured TCP port. Checker failure is inconclusive. The Friend route status requires a fresh authenticated heartbeat and does not claim that a paired PC is off LAN. Invite reuse attempts to start the HTTPS listener and shows its warning before copying; reusing a code cannot silently resume paused remote controls. Friend pairing reports timeout, closed port, unreachable Host, and TLS identity mismatch separately. The local GUI remains on loopback.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: npm reported 0 vulnerabilities, React/TypeScript bundled, three fixtures built, Windows x64 single EXE published. Existing WebView2/WindowsBase `MSB3277` warning remains. |
| `TogetherServer.CompanionChecks` on the candidate | Pass: 17 groups, 0 failures, including fixed-target outside-probe responses, IP mismatch, invite reuse while controls are paused, pinned TLS, heartbeat freshness, permissions, and synthetic HTTPS remote Stop. Isolated data: `local-data/companion-checks/f4eed978f2934e42b73005134bd1b764`. |
| `TogetherServer.Checks` | Pass: 11 groups, 0 failures, including listener diagnostics and process identity. Isolated data: `local-data/checks/4acec9dd28e84a24a07c4ec1ca8a2722`. |
| Served and desktop smokes on the candidate | Pass: 19 served and 15 native desktop groups, 0 failures. The standalone EXE served its bundled UI on loopback and rendered a visible native window. Final served data: `local-data/served-smoke/2ee72b1df7844618a9e1a54486535b22`; desktop data: `local-data/desktop-smoke/e1ce84ea29e54f8ab5c0e801af445969`. |
| Installed v0.1.3 EXE and saved Host settings | Pass: `local-data/release/TogetherServer.exe`, 64,182,660 bytes, SHA-256 `56FAE891FB11AA9EF9BD2492CC1767754AF861763C5C415D192CEC1BA7FA7A38`. The previous EXE is backed up as `local-data/release/TogetherServer.exe.pre-0.1.3`. Native window rendered, HTTPS TCP 5131 was listening on all IPv4 interfaces, and the router forwarding target shown was `192.168.1.26`. |
| Outside TCP route and real Friend pairing | Outside checker result: **Not reachable** on TCP 5131, even with the installed Host listener active. A real Friend pairing after router setup was not run. Router forwarding, WAN/ISP NAT status, and a game join remain unverified. |
| Browser visual inspection | Not run: the in-app Browser reported no available browser. Native desktop rendering and served asset/API checks passed, but the new connection screen was not visually inspected at multiple viewports. |

No real world, game terms, router, firewall, or DNS setting was changed. The installed Host app was left running for the owner to use. A router TCP forwarding change and a real Friend retry still need owner approval and participation.

## 2026-09-22 - Direct connection guidance and v0.1.4 local build

The owner chose to keep direct public-IP connections without a required relay or cloud service. The Host card now separates the Friend HTTPS TCP listener, its invite address, the optional outside TCP result, and each game's route. It says that the Host may need manual inbound forwarding, while a Friend connecting outward does not. Valheim Crossplay reports its game relay; direct Valheim Steam and Minecraft ports are shown separately. A prior outside result is marked historical after the listener, endpoint, port, or time changes. A fresh checker result can still be useful when the separate public-IP lookup is old because the checker verifies the current observed public IP against the invite.

Join a friend now separates Friend-PC checks from Host-PC checks. Pairing and later heartbeats distinguish refused TCP, timeout, network failure, invalid or revoked access, HTTPS identity failure, Host errors, and malformed responses. An uncertain Host response remains `Disconnected/Unknown`; a timed-out Start/Stop tells the Friend that the action result is unknown before retrying. The outside checker remains optional and outside the connection path. No router, firewall, DNS, or game settings were changed.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass on rerun. The first attempt was blocked by an orphaned disposable Fixture EXE from an earlier check; its recorded identity and stop pipe were verified, then it exited through its normal `stop` command. TypeScript/Vite, fixtures, and Windows x64 single-file publish then passed. The existing WebView2/WindowsBase `MSB3277` warning remains. |
| `TogetherServer.CompanionChecks` | 17 groups passed, 0 failed. Includes outside-probe address binding, rejected current-code replacement, closed Host listener, pinning, heartbeat freshness, permissions, and synthetic remote Stop. Isolated data: `local-data/companion-checks/d10e814678e1481c9e688a99c71ae3fc`. |
| `TogetherServer.Checks` | 11 groups passed, 0 failed, including direct/relay game-port diagnostics and local listener checks. Isolated data: `local-data/checks/13e2b654e3df4620b65f9366e648e97e`. |
| Published candidate smokes | 19 served groups and 15 native desktop groups passed, 0 failed on the final build. The served check's UI marker was updated to the new outside-access wording. Isolated data: `local-data/served-smoke/7ba15c3e2b084e7b97ce53748c3c2f17` and `local-data/desktop-smoke/fe01a17d988542df8f76b299a2f7e40b`. |
| Installed local EXE | `local-data/release/TogetherServer.exe` has file version `0.1.4.0`, SHA-256 `A165B9969C4A98ACCF08BFBF24C296F5338AAF6907B8C314884027B3DB82336C`, and matches the tested candidate byte for byte. The old EXE is backed up beside it as `TogetherServer.exe.pre-0.1.4`. The installed EXE was tested with isolated data and was not launched against the owner's saved Host settings during this check. |
| Real outside-network acceptance | Not run. An actual Friend-PC Connect and game join are still needed after the Host reviews its router/WAN path. A previous outside TCP test of port 5131 was not reachable; no new claim is made about the current public route. |
| Browser visual inspection | Not run: the in-app Browser listed no available browser. Native WebView rendering and served assets/API were checked; this does not replace a manual inspection of the new advice at phone widths. |

The current Host identity pins the invite endpoint. If the public IP changes, diagnostics can detect an address mismatch, but changing an already pinned Host endpoint still requires a separate reviewed migration path. Do not treat a router forward as a fix for a stale invite address.

## 2026-09-22 - Unreleased Friend remote Stop explanation

Live inspection of the owner's connected Friend showed that the connection and heartbeat were working, but remote Stop was withheld for three independent safety reasons: the Host had not granted Stop permission to that device, the running Valheim server had no startup fingerprint for a complete `permittedlist.txt`, and the Friend had neither a saved Valheim player ID nor a known closed-game report. The Friend UI previously hid Stop without explaining these Host-side requirements whenever the device permission itself was off.

The Host UI had coupled durable permission to momentary safety: **Allow Stop** was disabled until the server was already Ready with every Stop check passing. The Host can now grant **Allow Stop requests** before setup is complete. This does not bypass safety because every request still passes the exact list, startup fingerprint, heartbeat, owner-client, and final pre-stop checks on the Host. An ordered Host guide now covers permission, player IDs, offline list creation, restart, client checks, and the remote-control switch. It labels the list and player conditions as safety checks rather than claiming full remote readiness.

The Friend card now lists each applicable blocker while authenticated: Host controls paused, this PC lacks permission, and the exact Host safety reason. Successful safety no longer arrives as a contradictory blocker when permission is off. The explanation is also visible while the server is Offline, Starting, Failed, or Unknown. No real server, world, player list, process, or network setting was changed.

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -ReleaseName release-candidate` | Pass: npm reported 0 vulnerabilities; TypeScript/Vite, three fixtures, and a self-contained Windows x64 EXE built. The existing WebView2/WindowsBase `MSB3277` warning remains. This is an ignored local candidate, not a GitHub release. |
| `TogetherServer.CompanionChecks` | Pass: 18 groups, 0 failures. The added flow grants Stop permission before setup, proves an early Stop remains denied, then completes the exact list and fresh closed-game state and performs the graceful HTTPS Stop. A ready profile no longer carries a positive safety message as an error. Isolated data: `local-data/companion-checks/0d520236610e454b9881f38807cd3ffc`. |
| Served smoke | Pass on the final candidate: 19 groups, 0 failures. The standalone EXE contains the ordered Host controls and Friend blocker UI. Isolated data: `local-data/served-smoke/b1ad498a266048f1b2eccd08227359ed`. |
| Native desktop smoke | Pass on the final candidate: 15 groups, 0 failures, including rendered React, native controls, synthetic lifecycle, tray, and startup behavior. An earlier overlapping run reached 14 passes and failed its final startup visibility assertion while the companion suite was still running; sequential reruns passed. Final data: `local-data/desktop-smoke/ef3c88b0d3c44b6aa54a5f15735dde7c`. |

The mistakenly published `v0.1.5` GitHub release and tag were deleted immediately after the owner's correction. The local install and source version remain v0.1.4. This Stop work is committed source only; future GitHub releases require a new explicit owner request.

## 2026-09-22 - Server player counts and shared UI controls

Starting Git HEAD: `47df354d`, clean `main` worktree. Player IDs, the app-created Valheim permitted-list flow, and game-client heartbeat coverage are no longer prerequisites for Friend Stop. Valheim now reads its local Steam-compatible query response; Minecraft Java reads the standard status response; Minecraft Bedrock reads its RakNet pong. Host and Friend cards show the reported online count. A missing, malformed, timed-out, unsupported, or positive count denies Friend Stop. A zero count creates only a short-lived permit, and the same built-in driver queries again inside the serialized lifecycle gate immediately before the graceful stop signal. A changed or unknown result sends no signal. Local Host Stop deliberately remains available even when players are reported online. Automatic shutdown remains disabled.

All page-level React controls now use the shared `Button`, `Input`, `Select`, and `TextArea` components. The shared base styles cover controls outside the older contextual selectors, including **Manage friend access** and first-run **Continue setup**. The served smoke fails if a page reintroduces a native lowercase control outside that component module.

| Final check | Result |
| --- | --- |
| `scripts/build.ps1` | Pass: npm found 0 vulnerabilities; TypeScript/Vite, three fixtures, and the self-contained Windows x64 EXE built. The existing WebView2/WindowsBase `MSB3277` warning remains; there were no build errors. |
| `TogetherServer.Checks` | Pass: 11 groups, 0 failures. Disposable data: `local-data/checks/54fd23d159fd44f9a224be1a6b4f5b3e`. |
| `TogetherServer.ValheimChecks` | Pass: 8 groups, 0 failures. Covers zero, positive, malformed/Unknown, both pre-stop checks, no signal after a count change, and local Host override at one reported player. Disposable data: `local-data/valheim-checks/7afa9e160e014784b0e52a0c369a63a5`. |
| `TogetherServer.MinecraftChecks` | Pass: 3 groups, 0 failures. Java and Bedrock expose 0/10, remain Ready with an invalid count represented as Unknown, deny Friend Stop for Unknown/positive, and preserve graceful Stop. Disposable data: `local-data/minecraft-checks/f94ab7a7e41d41b59be6f5f07c8d68db`. |
| `TogetherServer.CompanionChecks` | Pass: 19 groups, 0 failures against the final packaged EXE. A paired Friend saw 0/10, saw a one-player blocker, was denied over HTTPS, then stopped gracefully only after zero was reported. Disposable data: `local-data/companion-checks/aff7e7b9933b4536b0da8dcf03a49de5`. |
| Minecraft setup regression | Pass: 4 groups, 0 failures. Disposable data: `local-data/minecraft-setup-checks/035053b256524fa9bd2424bb40858eaa`. No game binary was downloaded and no terms were accepted. |
| Served and native desktop smokes | Pass: 20 served and 19 desktop groups, 0 failures on the final specificity-safe control styles. Includes bundled shared-control CSS, the no-raw-control source guard, a 0/10 Host snapshot, visible native WebView rendering, compact 390x600 sizing, native pickers, tray/startup behavior, and lifecycle actions. Disposable data: `local-data/served-smoke/74c063f86c584a3b99672f99e89b7b0d` and `local-data/desktop-smoke/334ef4b43f414a12ab3622671bc53cdd`. |
| Packaged local EXE | `local-data/release/TogetherServer.exe`, file version `0.1.4.0`, 64,190,700 bytes, SHA-256 `851D24E428276BBAC9BDFB54CFB7D20812CCFB37AB1661AF460C90C3B31A6213`. It was not published or pushed. |
| Browser screenshots and real-game acceptance | Browser screenshots were not captured because the Browser runtime listed no available browser; the native rendered WebView check passed. Real Valheim/Minecraft zero/one/disconnect counts, real joins, save/restart, and an outside Friend network were not run. Fixture protocol evidence is not a real-game claim. |

Exploratory checks found and corrected a Windows bind-scope mismatch in the Minecraft test port picker, stale player-ID-era test constructors and expectations, an unguarded JSON-number read for a malformed Java count, and a case-insensitive UI source audit that initially mistook `<Button>` for `<button>`. The terminal runs above passed. No real world, server installation, credential, public listener, router, firewall, or DNS setting was changed.
