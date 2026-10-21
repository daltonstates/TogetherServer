# TogetherServer

Host a game server on your Windows PC and let friends join, check its status,
and use the controls you allow. Everyone uses the same app. You can host your
own server and connect to a friend's at the same time.

TogetherServer brings server setup, player counts, backups, shared saves, and
chat into one Windows interface. Game servers run locally on the Host PC.

[Download stable](https://github.com/daltonstates/TogetherServer/releases/latest)
| [Preview releases](https://github.com/daltonstates/TogetherServer/releases)
| [Read the guides](docs/README.md)

## Get started

1. Download `TogetherServer-Setup-win-x64.exe` from a release and install it.
   A portable `TogetherServer-win-x64.exe` is also available. Builds are
   currently unsigned, so Windows may show **Unknown Publisher**.
2. Open the app and enter your name. In the welcome guide, choose
   **Host on this PC** or **Join a friend**. Reopen either task through **Guides**.
3. To host, choose a game, prepare a new world or review a copy of an existing
   save, select the server app, and choose **Save and start**. Use
   **Invite friends** to share that server's code privately.
4. To join, paste your friend's server code and choose **Connect**. Use the
   server card's connection details to join in the game.

Keep TogetherServer running while hosting or using Friend controls. The Host
needs a reachable connection for the Friend app, and the game has its own
connection requirements. See [connection help](docs/guides/connection-help.md).

Windows x64 and the Microsoft Edge WebView2 Runtime are required. You don't
need Node, the .NET SDK, Docker, or a separate web server to use the app.

## What you can do

| Area | Features |
| --- | --- |
| Hosting | Guided setup, multiple saved servers, Start/Stop, player counts, and a comparison of your servers. |
| Friend access | Server codes, approval, access deadlines, separate permissions for each PC and server, and immediate revocation. |
| World protection | Named and pinned backups, verified vault copies, restore checks, and a guide for testing a copy in the game. |
| Shared hosting | Turn on one server setting, then approved friends use Host locally to continue the latest verified save. |
| Server management | Logs, session history, reviewed settings and player lists, maintenance, and supported local add-on imports. |
| Playing together | Game requirements, connection details, explicit Open game actions, server chat, and pinned notices. |
| Windows app | Tray and sign-in options, notifications, resizable sidebars, and updates you choose when to install. |

For the features included in
[stable 0.3.3](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3),
check its release notes. This guide also covers current source changes that
have not been released. Installation and updates are covered
in [getting started](docs/guides/getting-started.md).

## Games and current limits

| Game | Current status |
| --- | --- |
| Valheim | Built-in driver. Real Friend-network, join, and save/restart checks remain separate acceptance requirements. |
| Minecraft Java | Built-in preview, fixture-tested. Official vanilla server installs only; real-game acceptance remains open. |
| Minecraft Bedrock | Built-in preview, fixture-tested. Real-game acceptance remains open. |
| Factorio | Preview using a managed copy of an owner-selected save. Real-game acceptance remains open. |
| Terraria | Preview using a managed world copy. Player count is unavailable, so Friend and automatic Stop stay blocked. |
| Custom game | Advanced local PowerShell scripts. Guarded remote Stop needs testing of that exact setup by the owner. |

See [game setup and limitations](docs/guides/games.md) for installation paths,
add-on support, and the checks still needed for each game. Game binaries are
separate from TogetherServer; you handle their installation and terms.

## Before relying on it

- **Friend Stop and automatic shutdown require a fresh zero-player count.**
  Positive or unknown counts block them. Automatic shutdown starts off, and
  TogetherServer checks the count again immediately before stopping.
- **The owner controls access.** The local management interface stays on
  loopback. Friend connections use pinned HTTPS and separate revocable access
  for each PC. TogetherServer never changes your firewall, router, or DNS.
- **A verified copy still needs a game test.** Hash checks and synthetic tests
  don't prove that a real world loads or survives a saved restart. Live save
  sharing stays gated until the exact installed setup passes its checks.
- **Shared hosting follows the latest verified progress.** Approved friends
  keep and forward completed saves automatically. Host locally checks the save,
  game and connection before starting after your confirmation. New transfers
  need a reachable approved PC; different progress stays protected for review.

## Find the right guide

| I want to... | Read |
| --- | --- |
| Install, update, or change app preferences | [Getting started](docs/guides/getting-started.md) |
| Set up a server and manage Friend access | [Hosting](docs/guides/hosting.md) |
| Connect, check requirements, or use chat | [Joining](docs/guides/joining.md) |
| Fix a connection or understand port checks | [Connection help](docs/guides/connection-help.md) |
| Back up, restore, share saves, or move hosting | [Backups and shared hosting](docs/guides/backups-and-sharing.md) |
| Build or work on TogetherServer | [Developer guide](docs/development/README.md) |
| Find design decisions and validation records | [Documentation index](docs/README.md) |

## Build from source

Use the .NET SDK pinned in [global.json](global.json) and Node version pinned
in [.node-version](.node-version). From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

The output is `local-data\release\TogetherServer.exe`. The
[developer guide](docs/development/README.md) covers the source layout, an
isolated development app, and verification that preserves the owner's desktop.
