# Game setup and limitations

[All documentation](../README.md) · [Hosting](hosting.md) · [Backups](backups-and-sharing.md)

A built-in driver means the app has reviewed launch, status, and Stop code.
It does not itself prove that a real client can join or that a world survives
a saved restart. Minecraft remains fixture-tested; Factorio and Terraria
remain previews. Record real-game and network acceptance separately for the
installation you intend to use.

## Valheim

Choose **Valheim**, create a new world or review and confirm a copy of an
existing one, then select your installed Valheim Dedicated Server and a game
password. Steam installation, updates, and terms remain your actions.

The driver uses local query evidence and, for a non-Crossplay query with no
reply, may use a complete occupancy sequence from its own exact-run log.
Malformed replies, incomplete logs, and a silent Crossplay query keep the
count unavailable. Stop uses a graceful Ctrl+C-equivalent signal against the
exact managed process.

The Steam game route normally uses the selected UDP port and the next port
(defaults 2456–2457). Crossplay uses its game relay. Neither supplies the
TogetherServer Friend-control route. Test real player transitions, a Friend
join, graceful Stop, and a recognizable saved change after restart before
relying on timers or save protection for a valued world.

## Minecraft Java

Choose **Minecraft Java** and either reuse a valid TogetherServer-installed
official server or choose **Install a new official server**. Read the linked
terms and explicitly consent before starting an in-app install. The app uses
a new private folder and installs a matching Eclipse Temurin runtime.

Java accepts only the official vanilla `server.jar` with TogetherServer's
install provenance and a matching recorded hash. It checks again before
Start. A renamed JAR, launch manifest, arbitrary download, Paper, or other
modded server is not accepted. **Browse server folder** does not bypass that
requirement. Use a fresh official in-app install for an unrecognized folder.

The local Java status reply supplies readiness and online/max counts. Stop
sends the fixed `stop` command to the exact game process. Console output is
captured for the log viewer; capture failure does not authorize or prevent
lifecycle actions. Test the real installed server, intended client version,
join, player transitions, and saved restart separately.

## Minecraft Bedrock

Choose **Minecraft Bedrock**. Select an existing `bedrock_server.exe` folder
or explicitly consent to **Install a new official server** in a fresh private
folder. Setup reads `server.properties`, including the world, game port,
IPv6 port, and LAN visibility. It does not rewrite an existing world during
discovery. Java and Bedrock are separate server editions.

The local RakNet reply supplies readiness and counts. Stop uses the fixed
`stop` console command against the exact managed process. A successful local
reply is not a Friend join or save-integrity result. Real-game acceptance,
including intended routes and saved restart, remains open.

## Factorio preview

Select an owner-installed `factorio.exe`, review an existing save `.zip`, then
choose **Confirm copy**. TogetherServer hosts a verified per-server copy and
preserves the original. It does not download Factorio or accept its terms.

The driver declares a UDP game port and a separate TCP RCON port. It uses
authenticated RCON locally for fixed player-count queries and `/quit`; the
password is Windows-protected. **Never forward the RCON port.** Friend inputs
cannot contain RCON commands or receive the password.

Fixture checks cover the managed copy and guarded lifecycle. Real client join,
player transitions, save/restart/restore, and network routes remain acceptance
work. Automatic crash restart is unavailable. Fresh-world-only development
staging hides Factorio because that mode disallows existing-save import.

## Terraria preview

Select an owner-installed `TerrariaServer.exe`, review an existing `.wld`,
and choose **Confirm copy**. The original stays intact. The driver launches
the managed copy on the selected TCP port and uses fixed `exit` for local Stop.

A local listener reports **Listening**. The driver cannot yet prove readiness
or a trusted player count, so Friend Stop, automatic Stop, and automatic crash
restart remain unavailable. Real binary, client join, and saved-restart checks
remain open. Terraria is unavailable in fresh-world-only development staging.

## Settings and add-ons

Open **Settings & files** on the selected server. **Game settings** holds
reviewed simple settings and player lists. **Server files** contains **Files**
for the raw supported text editor, plus **Add-ons** and **Setup backups**.
Changes require the [maintenance and checkpoint flow](hosting.md#maintain-settings-and-files).

| Game | Reviewed options |
| --- | --- |
| Valheim | Existing admin, ban, and permit lists. No reviewed mod-loader import. |
| Minecraft Java | Supported `server.properties` choices and player lists. Official vanilla JAR only. |
| Minecraft Bedrock | Supported properties/player lists and local `.mcpack` behavior/resource packs copied into the selected world. External shared packs can prevent a complete setup backup. |
| Factorio | Per-server settings, owner-selected local mod ZIPs, an enabled list, version checks, and Undo. |
| Terraria | Per-server settings. No reviewed mod import. |

Simple controls do not accept arbitrary properties, paths, commands,
online-mode changes, or world/port substitutions. A detected game update
requires owner review before Start. Displayed versions and add-on inventories
do not establish real add-on loading or client compatibility; test them in
the actual game.

## Custom game scripts

Choose **Custom game** for an advanced local-owner profile. Set the game and
server names, existing working/save directory, and declared TCP/UDP ports,
then supply three reviewed PowerShell actions:

| Action | Contract |
| --- | --- |
| Start | Launch the server and keep the Start PowerShell wrapper alive until it exits. |
| Status and players | Finish within four seconds and output exactly one JSON object with `state` (`Ready`, `Starting`, or `Failed`) and optional `detail`, `onlinePlayers`, `maxPlayers`, and `players`. |
| Stop | Ask the game to save and exit, finish the action within 15 seconds, and make the tracked Start wrapper exit within 90 seconds. |

The app supplies `TOGETHERSERVER_ACTION`, `TOGETHERSERVER_PROFILE_ID`,
`TOGETHERSERVER_WORLD_ID`, `TOGETHERSERVER_WORKING_DIRECTORY`,
`TOGETHERSERVER_GAME_PORT`, and `TOGETHERSERVER_MANAGED_PID`. Contract v2 also
supplies `TOGETHERSERVER_CONTRACT_VERSION`, `TOGETHERSERVER_PROBE_ID`, and
`TOGETHERSERVER_OPERATION_ID`. Status JSON must echo these as `contractVersion`,
`probeId`, and `operationId`, with an exact `onlinePlayers` count. Follow the
[driver contract](../07-ADDING-A-GAME.md) and built-in setup checks.

Scripts run with your Windows account's access and are protected at rest for
that account. Use only code you wrote or reviewed. Friends submit saved-profile
actions; they never supply scripts, paths, arguments, or environment values.

Counts stay display-only until the owner completes the exact configuration's
live test: valid v2 probes, zero → positive → zero, the final-zero race check,
graceful wrapper exit, restart of the same world, a rejoin, and explicit
recognizable-save confirmation. Matching protected acceptance can enable
guarded Friend Stop, Restart, replacement, and idle shutdown. Changing scripts,
world, working/save directory, ports, or contract invalidates it. This remains
owner-tested Custom control rather than official support for that game.

For proposed built-in games and evaluation criteria, see the
[game support catalog](../08-GAME-SUPPORT-CATALOG.md).
