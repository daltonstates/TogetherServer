# Developer guide

[Documentation index](../README.md) · [Testing](testing.md) · [Releases](releases.md)

TogetherServer is one Windows-first .NET 10 app with a bundled React/TypeScript
interface in WebView2. Dedicated servers remain external game processes. The
same executable hosts local servers and connects to Friends; there is no
separate production web tier, Node runtime, database service, or cloud relay.

Before changing behavior, read [AGENTS.md](../../AGENTS.md) and the core
[product and engineering rules](../README.md#product-and-engineering-rules).
Preserve unrelated changes, production data, and the owner's foreground app.

## Source layout

| Path | Contents |
| --- | --- |
| [src/TogetherServer](../../src/TogetherServer/) | Windows shell, loopback UI/API, companion HTTPS, lifecycle, drivers, local state, backups, sharing, and updates. |
| [ui/src](../../ui/src/) | React workspaces, setup flows, typed contracts, and component tests. |
| [src/*Fixture](../../src/) | Disposable synthetic game processes for approved isolated testing. |
| [checks](../../checks/) | Source/synthetic checks, process and remote journeys, browser/native/installer checks. Read each runner's scope before using it. |
| [contracts](../../contracts/) | Shared checked-in contract examples used by .NET and TypeScript. |
| [scripts](../../scripts/) | Build, code-only verification, isolated packages, and guarded release tools. |
| [installer](../../installer/) | Per-user Windows installer definition. |
| [.github/workflows](../../.github/workflows/) | Hosted Windows validation and dedicated visual/release workflows. |
| [docs](../README.md) | User guides, contributor workflows, design rules, and delivery evidence. |
| [agent-prompts](../../agent-prompts/) | Bounded historical worker handoffs; confirm their scope against current code and rules. |
| `local-data/` | Ignored build outputs, disposable test data, and local evidence. Keep worlds, secrets, binaries, and production logs out of Git. |

## Build

Use the exact .NET SDK in [global.json](../../global.json) and Node version
in [.node-version](../../.node-version). Locked .NET and npm dependency files
are committed. Run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

The script builds the UI, compiles fixtures without running them, and publishes
the bundled Windows x64 app. The normal output is
`local-data\release\TogetherServer.exe`. Building does not launch, install,
or replace the running app. This output uses the normal production identity
when opened. Use an isolated package for development beside production.

For routine source validation on the owner's desktop, run the
[code-only gate](testing.md#on-the-owners-active-desktop). Documentation-only
changes need link, content, and whitespace checks rather than a game or release
test run.

## Develop beside the stable app

Prepare a separate development package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-staging-package.ps1 -Build
```

It is written to `local-data\staging-package`. The executable is named
**TogetherServer DEVELOPMENT.exe**; separate DEVELOPMENT Host and Friend
launchers are included. The filename selects staging even when opened directly.
The window, orange banner, and D icon identify it, and the banner shows ports.
Prepare the package without launching it on the owner's active desktop during
automated work. Runtime testing needs the approved separate session described
in [Testing](testing.md).

| Setting | Normal app | Development/staging |
| --- | --- | --- |
| Data root | `%LOCALAPPDATA%\TogetherServer` | `%LOCALAPPDATA%\TogetherServer-Staging` |
| Loopback UI port | 5127 | 5128 |
| Friend-control TCP port | 5131 | 5132 |
| Sign-in startup / release updater | Available by owner choice | Disabled |
| Credentials, profiles, runs, worlds | Normal saved data | Separate persistent development data |

The first staging root must be empty and cannot equal, contain, or sit inside
the production root. Staging marks it permanently, accepts only save paths
inside it, creates separate Valheim worlds, and uses separate fresh Minecraft
installs. Existing-world imports, Factorio/Terraria imports, and unrestricted
Custom scripts are disabled. Do not copy production profiles, credentials,
settings, runs, or worlds into it.

Game defaults are separate too: development Valheim uses 2458–2459, Java 25566,
and Bedrock 19134–19135. A real Friend test needs its own owner-approved route
to the staging Friend port and separate game ports. Staging isolation does
not prove focus safety or Internet reachability.

## Rehearse and review

The development **Network** area has **Open rehearsal > Prepare rehearsal**.
It prepares synthetic saves and a non-runnable Connection rehearsal server.
Pair a development Friend normally, grant Receive, and enable **Allow saves on
this PC**. **Run rehearsal** checks pinned access, signed chat, transfer hashes,
and a signed receipt. Its network scope labels distinguish loopback from
owner-reported LAN or separate-network use; it never certifies a real game.

Use the [local pairing lab](../LOCAL-PAIRING-LAB.md) for a disposable three-app
rehearsal and the [development demo record](../31-DEVELOPMENT-DEMO.md) for UI
scenarios. These launch app processes and require approved unattended testing.
For a real connection test, use the [two-PC checklist](../TWO-PC-CONNECTION-TEST.md).

## Contribute a bounded change

Keep public Friend operations fixed and scoped to saved profiles. New games
follow the [driver contract](../07-ADDING-A-GAME.md). Keep configuration, files,
backup/restore, and owner-only mutations behind the local loopback boundary.
Unknown counts never become zero; display logs never grant lifecycle authority.

Update the relevant user guide when behavior changes. Put implementation
details, exact validation results, remaining checks, and acceptance limits in
the slice's delivery record, linked from the [documentation index](../README.md).
Commit completed authorized work after appropriate validation. Tags and GitHub
releases require explicit owner authorization.
