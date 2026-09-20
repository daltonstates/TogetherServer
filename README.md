# TogetherServer

TogetherServer is a small, Windows-first Valheim hosting app for one owner and a known group of friends. This folder is a **new project**. It does not depend on or modify `G:\repo\LetsServive`.

The same app will run in **Host** mode on the owner's PC and **Friend** mode on each player's PC. One app process on each PC serves its own bundled React interface; the Valheim dedicated server remains a separate game process managed by Host mode. Friends use the companion to report whether their Valheim game is running and, when the owner permits it, request start and stop actions over a public-IP connection.

The Host can launch an owner-selected `valheim_server.exe` directly, watch its unique log for the server-connected signal, and request Ctrl+C in its isolated Windows console to stop it. Real startup, graceful save, and restart have been verified on a separate copy of `V1release`; an actual Valheim client join and world change are still pending. Pairing, pinned HTTPS, heartbeat, permissions, and remote-control notices work in local tests. A real public-IP route and auto shutdown remain unverified or unavailable.

## Open the app on Windows

Double-click `local-data\release\TogetherServer.exe`. It opens the React GUI in its own TogetherServer window. Choose **Host** or **Friend** inside the app; it remembers your choice for the next launch. Double-clicking again restores the running window. Minimize it to keep Host monitoring or Friend heartbeat active. Closing the window or using **Quit app** exits after managed servers stop. Friends use the same EXE on their own PCs. No terminal, .NET SDK, Node install, or separate web server is needed to run the published EXE.

The window uses Microsoft's WebView2 Runtime. It is present on Windows 11 and many Windows 10 PCs; if missing, TogetherServer shows an in-window link to Microsoft's installer. It never installs the Runtime without your click. [Microsoft's distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) explains this shared Windows component.

If Windows Security asks whether to allow public and private networks while you are using only the local GUI, choose **Cancel**. The GUI uses `127.0.0.1` and needs no public firewall access. Windows may ask again after a new build or when you run another copy of the EXE. The app never changes firewall rules. Public Friend access requires a separate owner decision and network setup.

To build the EXE from source, developers need the .NET 10 SDK and Node:

```powershell
.\scripts\build.ps1
```

The GUI is bound to loopback. The GUI can switch modes when no managed run or companion listener is active. An unpaired Friend makes no network request. Command-line mode, desktop, and port flags exist only for isolated developer checks.

To try the local fixture, create an empty disposable directory under ignored `local-data/`. In Setup, add a server and choose **Synthetic test fixture** under **Advanced server options**, with that existing directory, a unique world ID and UDP port pair, and the absolute path to `src\TogetherServer.Fixture\bin\Release\net10.0\TogetherServer.Fixture.exe`. Save setup, then use Start, Health check, and Stop in Servers. The fixture never reads or writes a world. Host settings and run identity are stored under `%LOCALAPPDATA%\TogetherServer` by default. `TOGETHERSERVER_DATA_DIR` can override that location for isolated development.

The `local-data\release` folder has just the self-contained app EXE with the React assets embedded. Both test fixtures are separate development binaries and are not part of that folder.

## Valheim profile

In Host mode, use **Servers** to start and stop, **Setup** to configure a world, and **Friends** to manage pairing and remote access. Setup shows one server at a time and keeps uncommon options collapsed. Click **Find installed server and worlds**. The read-only search checks ready fixed and removable drives for common Steam folders (including `G:\Steam`), root-level custom folders containing `steamapps`, libraries listed in Steam's `libraryfolders.vdf`, local world folders directly under a drive root, the current user's local Valheim saves, and Steam `userdata` cloud cache folders. It does not crawl every nested folder on every drive. Use **Browse for valheim_server.exe** to open a Windows file picker and choose an installation anywhere, including a deeper custom Steam path. If the tool is missing, **Open install in Steam** opens Steam's install flow only when you click it; confirm any install and terms yourself, then rescan. TogetherServer does not download a game binary or accept terms in the background. The [official server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) also explains Steam's Tools install path.

To start with an existing single-player or multiplayer save, close Valheim and choose **Use an existing save** in Setup. For a Valheim 1.0 save, click **Browse for a world folder** and select its named folder under `worlds_local` or Steam's `userdata\<account>\892970\remote\worlds`. For an older save, expand **Older saves and custom paths** and choose a `.db` or `.fwl` file inside `worlds_local`; both files must exist. Search also lists recognized local and cached Steam Cloud world folders. Wait for Steam Cloud to finish syncing before copying a cached folder. The app copies all files in a complete world folder or the older file pair into `%LOCALAPPDATA%\TogetherServer\world-imports` and points the profile at that separate copy. It never moves or overwrites the source. A folder import requires the latest `_main` revision's `.db2`, `.fwl2`, `.chunks`, and `.ok` files plus chunk data; an incomplete folder is refused. The `V1release` Steam cache copy loaded and saved through a graceful stop and restart, but a client join and recognizable game change have not been verified yet. The [Valheim 1.0 patch notes](https://store.steampowered.com/news/app/892970/view/508485755865137730) describe the new folder save format. The [official server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) documents the server's `-savedir` override. The [developer's save FAQ](https://steamcommunity.com/app/892970/discussions/0/591774445091193259/) explains local and cloud saves. You can select **Create a new world** explicitly and supply an existing save root; Start refuses a new-world name if a matching save folder or file already exists there.

Enter a server name, choose the installed executable, and enter a password; **Save setup** writes the profile and protects the password in one GUI action. The UDP start port and other uncommon choices are under **Advanced server options**. TogetherServer does not modify Steam's startup script or delete a world. The password is encrypted in the current Windows user's local data; Valheim still requires it in its launch arguments, which Windows users with sufficient process access may inspect.

Local Start launches the selected executable with fixed Valheim arguments. `Starting` changes to `Ready` only after the unique log contains `Game server connected`; this is not a verified client join. Local Stop rechecks the recorded PID, start time, and executable, sends Ctrl+C only to its isolated console, and waits for exit without force killing. A timeout or uncertain identity leaves the run recorded and blocked. A real client join and recognizable world change through stop/restart must still be tested before trusting this path with a valued world. The [official dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) documents these arguments, readiness text, and Ctrl+C stop.

Other games can later use owner-authored local action scripts as approved profiles. Friends would still request a profile ID only. A custom script needs a trackable server process and a proven stop behavior before remote Stop or automatic shutdown can safely use it.

## Local companion setup

Host mode defaults to no companion listener and remote controls off. For a **loopback-only** test, save `https://127.0.0.1:5131` as the companion endpoint, `127.0.0.1` as the bind IP, and port `5131`. Create one invite per Friend device in the Host GUI; this explicitly creates a Host TLS identity. Then enable the authenticated companion listener, save, and restart the Host app. On each Friend PC, double-click the same EXE, choose Friend, paste its one-time invite, and pair. Enable remote controls only after a Friend has activated its credential. Turning them off is enforced by Host immediately while authenticated status and heartbeat continue.

Friend mode can check an approved Valheim client executable path; the path can be updated or cleared after pairing. If the path is absent or cannot be verified, its game-running signal is Unknown. Turning off the companion listener rejects public requests immediately; restart the app to release its HTTPS port. Remote Stop stays denied until actual permitted-player coverage and player state can be verified. The current fixture tests do not enable auto shutdown. Do not treat loopback pairing as public-IP reachability. The app never changes Windows Firewall, a router, or DNS.

## Checks

```powershell
dotnet run --project checks\TogetherServer.Checks\TogetherServer.Checks.csproj -c Release
dotnet run --project checks\TogetherServer.ValheimChecks\TogetherServer.ValheimChecks.csproj -c Release
.\checks\served-smoke.ps1
.\checks\desktop-smoke.ps1
dotnet run --project checks\TogetherServer.CompanionChecks\TogetherServer.CompanionChecks.csproj -c Release
```

The process checks run real Windows fixture processes and leave only ignored disposable test data. The Valheim checks also use disposable fake save pairs and a fake Steam library listing; they do not open a real world. The served smoke runs the published EXE from its normal path with isolated test data and verifies its loopback assets and API. The companion check uses separate local Host and Friend processes with temporary credentials and takes about a minute because it verifies a stale heartbeat. See [implementation status](docs/06-IMPLEMENTATION-STATUS.md) for the latest pass, fail, and skip record.

[docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md](docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md) remains the v1 acceptance gate. Fixture process identity is not Valheim readiness or save evidence.

## Owner gates

Do not accept game legal terms, download terms-gated binaries, open host/router firewall ports, change DNS, spend money, or alter/delete real worlds without explicit owner authorization. The owner will install/approve Valheim Dedicated Server and perform the real friend join test. The app must never silently treat an interrupted heartbeat as proof that a player left.
