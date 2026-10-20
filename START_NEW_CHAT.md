# TogetherServer handoff

Continue in `G:\repo\TogetherServer`. Treat the live checkout and current chat
as authoritative. Preserve unrelated work and production data.

## Start here

1. Run `git status --short` and read [AGENTS.md](AGENTS.md).
2. Read [README.md](README.md) and the core rules:
   [Product](docs/00-PRODUCT.md), [Architecture](docs/01-ARCHITECTURE.md),
   [Network and security](docs/02-NETWORK-AND-SECURITY.md),
   [Lifecycle](docs/03-LIFECYCLE.md), and
   [Implementation and acceptance](docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md).
3. Use the [documentation index](docs/README.md) to find the task guide and
   relevant feature/release delivery record. Chronological implementation
   history and old worker prompts may be superseded; verify against code.

## Current product boundaries

One Windows-first .NET 10 app bundles its React/TypeScript UI. Host and Join
work concurrently. Built-in drivers cover Valheim, Minecraft Java/Bedrock,
Factorio preview, and Terraria preview; Custom scripts remain an advanced
local-owner option. Minecraft is fixture-tested until real installed-game
acceptance is recorded. Terraria lacks trusted counts, so Friend/automatic
Stop stay blocked. Factorio/Terraria crash restart is unavailable.

The management UI stays on loopback. Friend access is deliberately enabled
with a server code and valid TLS/pairing state. Current TS3 server codes persist
until replaced, initially scope access to their server, and issue separate
revocable PC credentials. Owner assignments, per-server permissions, access
deadlines, and maintenance are checked server-side. Friends never submit
scripts, paths, executables, raw arguments, or arbitrary settings.

Track game processes by PID, creation time, and executable path. Remote Stop,
Restart, empty-port replacement, and automatic shutdown require the reviewed
trusted zero-player rules and a fresh in-gate recheck before graceful Stop.
Unknown is never zero. Friend app presence never controls idle shutdown.
Local owner Stop still verifies exact identity. Uncertain runs block another
writer; never kill a process by name alone.

Shared worlds use signed membership, separate Receive/hosting/forwarding grants,
local receive consent, and verified copies. Planned moves need recipient
acceptance and keep destination Start manual. Eligible solo recovery has no
Host-loss wait, vote, or age cutoff and creates a separate history. Reachable
peers can sync signed information while the owner is offline; there is no cloud
relay or automatic save merge. New chat delivery still needs its Host route.
Real live-save publication needs exact-installation six-stage acceptance.

## Build and verification

Read versions from `src/TogetherServer/TogetherServer.csproj`, `ui/package.json`,
`global.json`, and `.node-version`; do not rely on a handoff's version snapshot.
The [developer guide](docs/development/README.md) covers source layout, bundled
builds, and isolated staging. Preview enrollment uses production data and is
separate from the DEVELOPMENT/staging app.

On the owner's active desktop, use only focus-safe source checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-code-only.ps1
```

Do not launch automated app, process/console fixture, browser, native desktop,
installer, or full release tests there. Hidden/tray/headless modes and
`-SkipDesktop` do not prove focus safety. Interactive gates require explicit
owner approval for a separate test PC or dedicated unattended Windows session,
plus the guarded switch. Follow [testing and acceptance](docs/development/testing.md).

## Updates and delivery

Updates require the user's **Update and restart** action, verified release
hashes/version, a valid same-publisher signature when the installed app is
signed, and a bounded verified local-state checkpoint. Unsigned installations
may use GitHub digest verification and must be labeled honestly. Staging never
consumes release updates. See [release procedures](docs/development/releases.md).

For each authorized slice, update its delivery record with source/candidate
identity, actual commands/results, failures/skips, and open acceptance. Commit
completed validated work. Tags, publication, and GitHub releases require explicit
owner permission. Do not infer game/network acceptance from fixtures, hashes,
a local listener, or a heartbeat.

Never accept game terms, download terms-gated binaries, spend money, request
real credentials, change public firewall/router/DNS, or overwrite/delete real
worlds without explicit owner authorization. Do not send invites to other
people from tools without explicit instructions.
