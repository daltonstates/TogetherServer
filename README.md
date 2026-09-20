# TogetherServer

TogetherServer is a small, Windows-first Valheim hosting app for one owner and a known group of friends. This folder is a **new project**. It does not depend on or modify `G:\repo\LetsServive`.

The same app will run in **Host** mode on the owner's PC and **Friend** mode on each player's PC. One app process on each PC serves its own bundled React interface; the Valheim dedicated server remains a separate game process managed by Host mode. Friends use the companion to report whether their Valheim game is running and, when the owner permits it, request start and stop actions over a public-IP connection.

The first local Host slice is implemented. It uses a synthetic process fixture and does not launch Valheim or modify a world. Friend pairing, public control, heartbeats, auto shutdown, and real game readiness remain future slices.

## Build and run locally

On Windows, install the .NET 10 SDK and Node for **building** the React assets. Node is not needed to run the published app.

```powershell
.\scripts\build.ps1
.\local-data\publish\TogetherServer.exe --host
```

Open `http://127.0.0.1:5127/`. The GUI is bound to loopback. The same executable starts in Friend mode with `--friend`, and the GUI can switch modes when no managed run is active. Friend mode currently shows an honest Not paired state and makes no network request.

To try the local fixture, create an empty disposable directory under ignored `local-data/`. In the GUI, add a profile with that existing directory, a unique world ID and UDP port pair, and the absolute path to `src\TogetherServer.Fixture\bin\Release\net10.0\TogetherServer.Fixture.exe`. Save settings, then use Start fixture, Health check, and Stop. The fixture never reads or writes a world. Host settings and run identity are stored under `%LOCALAPPDATA%\TogetherServer` by default. `TOGETHERSERVER_DATA_DIR` can override that location for isolated development.

The publish output has one self-contained app executable with the React assets embedded. `TogetherServer.Fixture.exe` is a separate development-only fixture and is not part of the app publish. There is no public companion listener yet.

## Checks

```powershell
dotnet run --project checks\TogetherServer.Checks\TogetherServer.Checks.csproj -c Release
.\checks\served-smoke.ps1
```

The process checks run real Windows fixture processes and leave only ignored disposable test data. The served smoke copies the published EXE alone to an isolated folder and verifies its loopback assets and API. See [implementation status](docs/06-IMPLEMENTATION-STATUS.md) for the latest pass, fail, and skip record.

[docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md](docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md) remains the v1 acceptance gate. Fixture process identity is not Valheim readiness or save evidence.

## Owner gates

Do not accept game legal terms, download terms-gated binaries, open host/router firewall ports, change DNS, spend money, or alter/delete real worlds without explicit owner authorization. The owner will install/approve Valheim Dedicated Server and perform the real friend join test. The app must never silently treat an interrupted heartbeat as proof that a player left.
