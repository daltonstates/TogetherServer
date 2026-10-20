# Testing and acceptance

[Developer guide](README.md) · [Acceptance requirements](../04-IMPLEMENTATION-AND-ACCEPTANCE.md)

Choose checks for the surface changed. A passing source check, packaged
journey, rendered screen, real network connection, and game save test answer
different questions. Record passes, failures, and skips separately.

## On the owner's active desktop

Automated work must preserve the foreground window and keyboard focus. Use:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-code-only.ps1
```

This checks pinned tool versions, application/UI version alignment, locked
dependency installs/restores, TypeScript, lint, solution compilation, independent
source/synthetic/fake-adapter groups, whitespace, and diagnostic isolation.
It does not launch the app, game/console fixtures, listeners, browser journeys,
native dialogs, installer, or update handoff. It is not the full UI test suite
or a production UI bundle check.

Pure component checks and static source checks can supplement it when relevant.
Read the runner first. The separate `TogetherServer.FeatureChecks` entry point
uses synthetic files, protected state, pure projections, and injected fake
adapters; the legacy process runner has a different scope.

Do not run full release verification, direct process-check runners, desktop
smoke, installer smoke, or automated app/browser launches on the owner's active
desktop. Hidden windows, tray mode, a headless label, and `-SkipDesktop` do not
establish safety: console creation, attachment, graceful commands, and relaunch
can still take focus.

## On an approved separate Windows session

Use a separate test PC or dedicated unattended Windows session with explicit
owner approval. Only there, the full build-and-verify command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-release.ps1 -Build -AllowInteractiveTests
```

For an existing immutable candidate, supply its exact app path instead of
`-Build`. Suite selection is available in the script; serialize shared builds,
package outputs, integration, and commits. `-AllowInteractiveTests` records
the approval requirement; setting it is not permission to use the active desktop.
Hosted Windows CI opts in explicitly on its separate runner.

The release gate checks locked builds and candidate identity, source and game
fixtures, packaged journeys, the bundled UI, and applicable native/update
surfaces. The **Core remote journey** requires separate packaged Host/Friend
processes, pinned authentication, remote Start, positive-player Stop denial,
fresh-zero graceful Stop, listener closure, and saved-access reconnection after
both apps restart. These remain loopback/fixture results.

Installer smoke uses a disposable per-user destination and data root, refuses
an existing installed app, verifies the exact candidate bytes, and checks
preferences, shortcuts, startup, launch, and uninstall while retaining data.
Release preparation invokes it against the same candidate.

If a test takes focus or the owner reports interference, stop that exact test
runner and its identified disposable processes. Preserve production and
unrelated processes, and record the remaining checks as unfinished.

## What each result means

| Evidence | Establishes | Still needs separate acceptance |
| --- | --- | --- |
| Source/synthetic or component checks | Exercised parser, permission, state, file, fake-adapter, and rendering logic. | Installed games, native behavior, real routes, and real saves. |
| Packaged loopback journey | Exact packaged processes and protocols exercised locally. | Separate-PC/WAN access and an actual game client join. |
| Rendered/native review | The inspected screens, sizes, input, and Windows behavior. | Network reachability and game/save integrity. |
| Outside TCP result | Reachability of the configured control TCP port at that time. | Pinned authenticated pairing and game-port reachability. |
| Authenticated Friend heartbeat | An authorized PC reached that Host. | Whether it was on another network and whether a game join succeeded. |
| Verified transfer or restore manifest | The copied bytes match the completed signed/hash evidence. | Whether the real game loads and preserves recognizable changes. |
| Owner-reported game test | The explicit checks that person performed for the recorded setup. | Any other game, installation, route, binary, world, or configuration. |

## Real-game and Friend-network acceptance

Use owner-installed, owner-approved games and disposable worlds. Keep valued
worlds, credentials, and production logs out of test data and Git. Do not
accept terms, download a terms-gated binary, change public firewall/router/DNS,
or overwrite real worlds without explicit owner authorization.

For each intended game and route, record real readiness, zero/one/disconnect
counts where supported, a separate Friend-PC connection, actual game join,
graceful Stop, recognizable saved change, restart, and relevant recovery/restore
checks. Keep automatic shutdown off for a valued world until its real count,
idle-window, Stop, and save behavior have passed.

Minecraft remains fixture-tested until real owner-installed acceptance is
recorded. Factorio and Terraria remain previews. Terraria lacks trusted
occupancy and cannot use Friend/automatic Stop. Custom acceptance is bound to
the exact owner-tested v2 configuration. Live-save publication requires its
separate six-stage exact-installation acceptance; fixture success cannot enable it.

## Record evidence where it belongs

Use [implementation and acceptance](../04-IMPLEMENTATION-AND-ACCEPTANCE.md)
for required gates and the relevant [delivery record](../README.md#feature-and-delivery-records)
for measured results. Record source revision, exact commands, candidate
version/hash/signature, run/job/artifact identities, actual pass/fail/skip
counts, and open external checks. Keep earlier failures visible when later
checks supersede them. Do not copy transient build paths or raw test logs into
the front-page README.

The [historical firewall cleanup tool](../11-FIREWALL-CHECK-RULE-CLEANUP.md)
is a separate owner-reviewed operation; its pure mock test does not query the
desktop firewall, while its normal dry run does.
