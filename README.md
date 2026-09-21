# TogetherServer

TogetherServer is one Windows app for hosting a Valheim server and connecting to a friend's server. The same EXE runs on every PC. It opens a native window with a bundled interface; Node and the .NET SDK are needed only to build it.

## Open the app

Double-click `local-data\release\TogetherServer.exe`. **My server** and **Join a friend** are pages in the same running app. Switching pages does not stop hosting or a Friend connection. Keep the app open while hosting or playing. Closing it asks you to stop managed servers first.

Build a fresh EXE with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1`.

## Host a server

1. On first use, **My server** opens the setup form immediately. Choose **Create new** and name the world, or **Use existing** and copy a world found on this PC. An existing world is copied to a separate app-managed save folder. The source is never moved or overwritten. Browse for a world folder if the scan misses it.
2. Select your installed Valheim Dedicated Server if it was not found automatically. Enter a game password, then choose **Save and start**. The game server is installed and updated through Steam by you; TogetherServer does not install it or accept game terms.
3. The everyday server card has **Start server** or **Stop server**, **Copy game details**, and **Invite friend**. Additional paths, ports, health checks, and multi-server settings are under **More server options** or **Settings and safety**.
4. To invite Friend PCs, choose **Invite friend**, then **Create code and allow connections**. Each saved server has one current code, which can be copied privately to every Friend PC joining that server. Each PC receives its own revocable credential after connecting. **Refresh code** invalidates the previous code and every credential issued through it for that server; codes and credentials for other saved servers remain valid. The companion HTTPS listener starts immediately when the owner creates the code and is off by default.

The Host detects an outbound public IPv4 address to fill the game and app addresses. This is an address hint, not a reachability test. A Friend on another network must test the app connection and Valheim join separately. Valheim's game UDP ports and TogetherServer's companion TCP port are separate. TogetherServer does not change firewall, router, or DNS settings. A custom HTTPS IP endpoint and bind IP are available in **Settings and safety** for local testing or deliberate network setup.

## Join a friend's server

On your own PC, open the same EXE and choose **Join a friend**. Paste the invite into the single field and choose **Connect**. The app pins the Host certificate and saves a separate credential for this PC. After connecting, use the server card to request **Start server**, copy the game address, or request **Stop server** when the Host allows it. The app checks whether your Valheim game client is running; it does not launch the game. If the client path cannot be found, set it under **Valheim game check**.

Current `TS3` server codes include the address. Older `TS1` and `TS2` per-device invites remain readable until their original expiry; `TS1` also needs the Host IP under **Using an older invite?**.

## Stop and player safety

The owner can always request local Stop. It rechecks the recorded process ID, creation time, and executable path, sends Ctrl+C to the managed Valheim console, waits for exit, and never force-kills an unrelated process. A timeout leaves the run recorded for review.

Remote Stop becomes available only for a Ready Valheim server with a restricted `permittedlist.txt` loaded at start. In **Settings and safety**, enter the Valheim Platform User ID shown in F2 for each paired Friend PC and for the owner if the owner plays. While the server is offline, **Create player-only access list** writes the list only in an app-managed new world or imported copy. It never overwrites an existing list. Start the server afterward. Host checks that the list has not changed and contains exactly those IDs, every allowed Friend has a fresh `gameRunning: false` report, and the owner's game is closed when the owner is permitted. Any missing, stale, unknown, or running signal blocks remote Stop; the Host checks again before the stop signal. An owner can still make a local Stop decision. [Valheim's dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) documents `permittedlist.txt` and Ctrl+C stopping.

This safety check covers the enrolled PCs and the exact list, but a closed game process is not proof of a server player count. Auto shutdown remains unavailable. The public route, real Friend join, and recognizable world change after restart still need testing on the intended PCs and network before relying on remote Stop with a valued world. An earlier owner-approved test launched and gracefully stopped an installed Valheim server using a disposable copy of `V1release`; a real client join and recognizable in-world change were not verified.

## Developer checks

The build includes two disposable process fixtures. Isolated checks keep their data under ignored `local-data/` and do not use a real Valheim binary or world:

```powershell
dotnet run --project checks/TogetherServer.Checks/TogetherServer.Checks.csproj -c Release
dotnet run --project checks/TogetherServer.ValheimChecks/TogetherServer.ValheimChecks.csproj -c Release
dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File checks/served-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File checks/desktop-smoke.ps1
```

See [implementation status](docs/06-IMPLEMENTATION-STATUS.md) for the latest test evidence and remaining real-world acceptance checks.
