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
