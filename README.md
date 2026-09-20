# TogetherServer

TogetherServer is a small, Windows-first Valheim hosting app for one owner and a known group of friends. This folder is a **new project**. It does not depend on or modify `G:\repo\LetsServive`.

The same app will run in **Host** mode on the owner's PC and **Friend** mode on each player's PC. One app process on each PC serves its own bundled React interface; the Valheim dedicated server remains a separate game process managed by Host mode. Friends use the companion to report whether their Valheim game is running and, when the owner permits it, request start and stop actions over a public-IP connection.

The Host can now launch an owner-selected `valheim_server.exe` directly, watch its unique log for the server-connected signal, and request Ctrl+C in its isolated Windows console to stop it. This path has passed only a synthetic console fixture test. Real Valheim startup, joining, and save/restart are not yet verified. Pairing, pinned HTTPS, heartbeat, permissions, and remote-control notices work in local tests. A real public-IP route and auto shutdown remain unverified or unavailable.

## Build and run locally

On Windows, install the .NET 10 SDK and Node for **building** the React assets. Node is not needed to run the published app.

```powershell
.\scripts\build.ps1
.\local-data\publish\TogetherServer.exe --host
```

Open `http://127.0.0.1:5127/`. The GUI is bound to loopback. The same executable starts in Friend mode with `--friend`, and the GUI can switch modes when no managed run or companion listener is active. An unpaired Friend makes no network request.

To try the local fixture, create an empty disposable directory under ignored `local-data/`. In the GUI, add a **Synthetic fixture** profile with that existing directory, a unique world ID and UDP port pair, and the absolute path to `src\TogetherServer.Fixture\bin\Release\net10.0\TogetherServer.Fixture.exe`. Save settings, then use Start, Health check, and Stop. The fixture never reads or writes a world. Host settings and run identity are stored under `%LOCALAPPDATA%\TogetherServer` by default. `TOGETHERSERVER_DATA_DIR` can override that location for isolated development.

The publish output has one self-contained app executable with the React assets embedded. Both test fixtures are separate development binaries and are not part of the app publish.

## Valheim profile

After the owner has installed and approved Valheim Dedicated Server, choose **Valheim** in the Host GUI. Enter its installed `valheim_server.exe`, a server name, world ID, existing save directory, and UDP start port. Save the profile, then set its server password in the separate protected field. TogetherServer does not modify Steam's startup script, download the game, accept terms, create the save directory, or delete a world. The password is encrypted in the current Windows user's local data; Valheim still requires it in its launch arguments, which Windows users with sufficient process access may inspect.

Local Start launches the selected executable with fixed Valheim arguments. `Starting` changes to `Ready` only after the unique log contains `Game server connected`; this is not a verified client join. Local Stop rechecks the recorded PID, start time, and executable, sends Ctrl+C only to its isolated console, and waits for exit without force killing. A timeout or uncertain identity leaves the run recorded and blocked. A real client join and recognizable world change through stop/restart must still be tested before trusting this path with a valued world. The [official dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) documents these arguments, readiness text, and Ctrl+C stop.

Other games can later use owner-authored local action scripts as approved profiles. Friends would still request a profile ID only. A custom script needs a trackable server process and a proven stop behavior before remote Stop or automatic shutdown can safely use it.

## Local companion setup

Host mode defaults to no companion listener and remote controls off. For a **loopback-only** test, save `https://127.0.0.1:5131` as the companion endpoint, `127.0.0.1` as the bind IP, and port `5131`. Create one invite per Friend device in the Host GUI; this explicitly creates a Host TLS identity. Then enable the authenticated companion listener, save, and restart the Host app. Launch the same EXE in Friend mode with a different local GUI port and a separate data directory, paste its one-time invite, and pair. Enable remote controls only after a Friend has activated its credential. Turning them off is enforced by Host immediately while authenticated status and heartbeat continue.

Friend mode can check an approved Valheim client executable path; the path can be updated or cleared after pairing. If the path is absent or cannot be verified, its game-running signal is Unknown. Turning off the companion listener rejects public requests immediately; restart the app to release its HTTPS port. Remote Stop stays denied until actual permitted-player coverage and player state can be verified. The current fixture tests do not enable auto shutdown. Do not treat loopback pairing as public-IP reachability. The app never changes Windows Firewall, a router, or DNS.

## Checks

```powershell
dotnet run --project checks\TogetherServer.Checks\TogetherServer.Checks.csproj -c Release
dotnet run --project checks\TogetherServer.ValheimChecks\TogetherServer.ValheimChecks.csproj -c Release
.\checks\served-smoke.ps1
dotnet run --project checks\TogetherServer.CompanionChecks\TogetherServer.CompanionChecks.csproj -c Release
```

The process checks run real Windows fixture processes and leave only ignored disposable test data. The served smoke copies the published EXE alone to an isolated folder and verifies its loopback assets and API. The companion check uses separate local Host and Friend processes with temporary credentials and takes about a minute because it verifies a stale heartbeat. See [implementation status](docs/06-IMPLEMENTATION-STATUS.md) for the latest pass, fail, and skip record.

[docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md](docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md) remains the v1 acceptance gate. Fixture process identity is not Valheim readiness or save evidence.

## Owner gates

Do not accept game legal terms, download terms-gated binaries, open host/router firewall ports, change DNS, spend money, or alter/delete real worlds without explicit owner authorization. The owner will install/approve Valheim Dedicated Server and perform the real friend join test. The app must never silently treat an interrupted heartbeat as proof that a player left.
