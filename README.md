# TogetherServer

TogetherServer is one Windows app for hosting a Valheim, Minecraft Java, or Minecraft Bedrock server and connecting to a friend's server. The same EXE runs on every PC. It opens a borderless native window with app-styled minimize, maximize, and close controls around its bundled interface; Node and the .NET SDK are needed only to build it.

## Open the app

Double-click `local-data\release\TogetherServer.exe`. **My server** and **Join a friend** are pages in the same running app. Switching pages does not stop hosting or a Friend connection. Keep the app running while hosting or playing.

Use the gear in the app header to enable **Open at Windows sign-in** and **Close to tray**. Sign-in launches quietly in the Windows tray under your account. With Close to tray enabled, the window's X hides it while hosting and Friend checks continue. Double-click the TogetherServer tray icon to reopen it, or right-click it for **Open** and **Quit**. A normal manual launch also reopens the running app. Quit still asks you to stop or resolve managed servers first. Both options are off until you enable them; Windows startup follows the EXE at its current location, so move it to its intended location before enabling startup.

Build a fresh EXE with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1`.

## App updates

The published Windows app checks the [TogetherServer GitHub Releases](https://github.com/daltonstates/TogetherServer/releases) page when it opens and about every six hours afterward. The small version button checks again on demand. When a newer stable release is available, **Update and restart** downloads its `TogetherServer-win-x64.exe` asset and checks the SHA-256 digest reported by GitHub. Stop any hosted server first; the app refuses to quit for an update while a managed run is active or unresolved. After a verified download, a short-lived copy of the new EXE waits for the old app to exit, replaces it, keeps `TogetherServer.exe.previous` beside it, and reopens the app. Local settings, credentials, and worlds stay in their existing data directory. The update uses the current Windows account and cannot elevate into a protected install directory.

To prepare a stable release, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-github-release.ps1`, review the candidate, then publish a GitHub Release whose `vMAJOR.MINOR.PATCH` tag matches `<Version>` in `TogetherServer.csproj`. Attach the prepared `TogetherServer-win-x64.exe` asset without renaming it. GitHub's [release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release) supplies the version and asset digest. Existing copies that predate the updater need an updater-enabled version installed once by hand; future published versions can update through the app.

## Host a server

1. On first use, **My server** opens the setup form immediately. Choose **Create new** and name the world, or **Use existing** and copy a world found on this PC. An existing world is copied to a separate app-managed save folder. The source is never moved or overwritten. Browse for a world folder if the scan misses it.
2. Select your installed Valheim Dedicated Server if it was not found automatically. World name and game password stay together in the short setup form. Choose **Start server**; **Save only** is available when you want to finish later. The game server is installed and updated through Steam by you; TogetherServer does not install it or accept game terms.
3. The everyday server card keeps **Start server** or **Stop server** and **Invite friend** on one line. Four small checks show server state, game ports, the Friend-control listener, and whether a Friend has reached it. **Add new server** sits below saved servers. **Cancel** closes setup and discards its unsaved changes, including when you cancel the first server. Additional paths, health actions, and safety settings remain collapsed.
4. Choose **Invite friend** once. TogetherServer creates the server's code when needed, starts its authenticated listener, and copies the code. **Copy again** reuses that same code. **Refresh access** invalidates the previous code and every device credential issued through it for that server; codes and credentials for other saved servers remain valid.

The Host detects an outbound public IPv4 address to fill the game and app addresses. This is an address hint, not a reachability test. **Open on PC** means Windows sees a local listener; **Friend reached** requires a fresh authenticated heartbeat. A Friend on another network must still test the app connection and game join separately. The game's ports and TogetherServer's companion TCP port are separate. TogetherServer does not change firewall, router, or DNS settings. Valheim Crossplay uses its relay, so its game-port card reports relay readiness instead of claiming router forwarding. A custom HTTPS IP endpoint and bind IP are available in **Settings and safety** for local testing or deliberate network setup.

## Host Minecraft Java or Bedrock

Choose **Minecraft Java Edition** or **Minecraft Bedrock Edition** in **My server** setup. TogetherServer searches its own installs, your Desktop, Documents, Downloads, and common Minecraft folders on local drives. It checks Java JARs for a launch manifest, lists each candidate separately, and reads `server.properties` for the world name and port. Choose the correct server if several are found. **Browse server folder** covers other locations. For an existing server, TogetherServer does not rewrite its configuration or world. Some third-party JARs may be detected, but their launch and save behavior has not been verified.

- **Install in the app:** Enter a world name and port, review the linked Minecraft EULA and Microsoft Privacy Statement, check the consent box, then choose **Install Java server** or **Install Bedrock server**. The app fetches the latest official server into a new private folder, never over an existing world. Java also fetches a matching Eclipse Temurin runtime and records `eula=true` only after your explicit consent. The Java JAR and runtime are checked against official metadata hashes and sizes; Bedrock's official download list currently supplies no digest, so the app validates its HTTPS source and ZIP layout. Save setup and choose **Start server**. The installed server binaries are not bundled with TogetherServer.
- **Existing Java:** Choose an installed `java.exe` and a server JAR in its folder. Review Minecraft's terms yourself and set `eula=true` in that folder before Start. [Official Java server download](https://www.minecraft.net/en-us/download/server).
- **Existing Bedrock:** Choose `bedrock_server.exe` in its folder. TogetherServer reads `server-portv6` and `enable-lan-visibility` too, including the default discovery ports when LAN visibility is on. [Official Bedrock server setup](https://learn.microsoft.com/en-us/minecraft/creator/documents/bedrockserver/getting-started?view=minecraft-bedrock-stable) and [properties](https://learn.microsoft.com/en-us/minecraft/creator/documents/bedrockserver/server-properties?view=minecraft-bedrock-stable).

TogetherServer checks for a local Java server-status reply or Bedrock RakNet status reply before showing **Ready**. This proves a local game response, not a Friend join. Local Stop sends the fixed `stop` command to the recorded server's isolated console and waits for exit. Remote Stop and auto shutdown stay unavailable for Minecraft until player coverage and real save/restart behavior have been verified. The current Minecraft evidence uses disposable process fixtures; test the real installed editions, client joins, and recognizable world changes before relying on them for a valued world.

## Join a friend's server

On your own PC, open the same EXE and choose **Join a friend**. Paste an invite and choose **Connect**. Each saved server keeps its own pinned Host certificate, credential, and game-client check; select among saved connections without pausing their heartbeats. After connecting, use the server card to request **Start server**, copy the game address, or request **Stop server** when the Host allows it. The app checks the configured game-client executable; it does not launch the game. Set the installed client path under **Game check** when needed.

Current `TS3` server codes include the address. Older `TS1` and `TS2` per-device invites remain readable until their original expiry; `TS1` also needs the Host IP under **Using an older invite?**.

## Stop and player safety

The owner can always request local Stop. It rechecks the recorded process ID, creation time, and executable path. Valheim receives Ctrl+C; Minecraft receives its fixed `stop` console command. The app waits for exit and never force-kills an unrelated process. A timeout leaves the run recorded for review.

Remote Stop becomes available only for a Ready Valheim server with a restricted `permittedlist.txt` loaded at start. In **Settings and safety**, enter the Valheim Platform User ID shown in F2 for each paired Friend PC and for the owner if the owner plays. While the server is offline, **Create player-only access list** writes the list only in an app-managed new world or imported copy. It never overwrites an existing list. Start the server afterward. Host checks that the list has not changed and contains exactly those IDs, every allowed Friend has a fresh `gameRunning: false` report, and the owner's game is closed when the owner is permitted. Any missing, stale, unknown, or running signal blocks remote Stop; the Host checks again before the stop signal. An owner can still make a local Stop decision. [Valheim's dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) documents `permittedlist.txt` and Ctrl+C stopping.

This safety check covers the enrolled PCs and the exact list, but a closed game process is not proof of a server player count. Auto shutdown remains unavailable. The public route, real Friend join, and recognizable world change after restart still need testing on the intended PCs and network before relying on remote Stop with a valued world. An earlier owner-approved test launched and gracefully stopped an installed Valheim server using a disposable copy of `V1release`; a real client join and recognizable in-world change were not verified.

## Developer checks

The build includes three disposable process fixtures. Isolated checks keep their data under ignored `local-data/` and do not use a real game binary or world:

```powershell
dotnet run --project checks/TogetherServer.Checks/TogetherServer.Checks.csproj -c Release
dotnet run --project checks/TogetherServer.ValheimChecks/TogetherServer.ValheimChecks.csproj -c Release
dotnet run --project checks/TogetherServer.MinecraftChecks/TogetherServer.MinecraftChecks.csproj -c Release
dotnet run --project checks/TogetherServer.MinecraftSetupChecks/TogetherServer.MinecraftSetupChecks.csproj -c Release
dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File checks/served-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File checks/desktop-smoke.ps1
```

The Host lifecycle uses reviewed built-in game drivers. Valheim, Minecraft Java, and Minecraft Bedrock appear in setup; the synthetic driver exists only for isolated checks. See [adding a game](docs/07-ADDING-A-GAME.md) for the process, port, readiness, stop, and security contract.

See [implementation status](docs/06-IMPLEMENTATION-STATUS.md) for the latest test evidence and remaining real-world acceptance checks.
