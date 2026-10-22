# TogetherServer documentation

Start with a task guide below. These cover stable 0.3.3 and current source;
use a release's notes to check its included features and known limits.

## Using TogetherServer

| Task | Guide |
| --- | --- |
| Install, finish setup, update, or use the tray | [Getting started](guides/getting-started.md) |
| Create a server, manage access, or understand Stop and timers | [Hosting](guides/hosting.md) |
| Connect to a Host, check requirements, open a game, or chat | [Joining](guides/joining.md) |
| Choose a route, check ports, or diagnose a failed connection | [Connection help](guides/connection-help.md) |
| Protect a world, receive saves, recover a copy, or move hosting | [Backups and shared hosting](guides/backups-and-sharing.md) |
| Set up a specific game, add-ons, or Custom scripts | [Games](guides/games.md) |

## Developing and testing

| Task | Guide |
| --- | --- |
| Find the code, build, or prepare an isolated development app | [Developer guide](development/README.md) |
| Choose safe checks and understand acceptance limits | [Testing and acceptance](development/testing.md) |
| Package a candidate and understand update verification | [Releases and updates](development/releases.md) |
| Implement another built-in game driver | [Adding a game](07-ADDING-A-GAME.md) |
| Review the evaluated game backlog | [Game support catalog](08-GAME-SUPPORT-CATALOG.md) |
| Rehearse pairing with synthetic local instances | [Local pairing lab](LOCAL-PAIRING-LAB.md) |
| Test a real Host/Friend connection on two PCs | [Two-PC connection test](TWO-PC-CONNECTION-TEST.md) |
| Inspect UI prototypes and their coverage | [Mockups](mockups/README.md) and [coverage](mockups/COVERAGE.md) |

Local process, game, browser, native-window, installer, and full release checks
require an explicitly approved separate PC or unattended Windows session.
Use the [code-only gate](development/testing.md#on-the-owners-active-desktop)
on the owner's active desktop.

## Product and engineering rules

Read these before changing behavior. Current owner instructions and
[AGENTS.md](../AGENTS.md) apply first. Feature delivery records explain later
changes; older chronological notes are historical evidence.

| Document | Covers |
| --- | --- |
| [Product](00-PRODUCT.md) | People, main flows, features, and scope. |
| [Architecture](01-ARCHITECTURE.md) | One-app design, services, storage, and driver boundaries. |
| [Network and security](02-NETWORK-AND-SECURITY.md) | TLS pinning, pairing, permissions, routes, and local-only controls. |
| [Lifecycle](03-LIFECYCLE.md) | Process identity, readiness, Stop, recovery, and idle shutdown. |
| [Implementation and acceptance](04-IMPLEMENTATION-AND-ACCEPTANCE.md) | Required checks and the limits of each kind of evidence. |
| [Source notes](05-SOURCE-NOTES.md) | Upstream references used during implementation. |
| [Implementation history](06-IMPLEMENTATION-STATUS.md) | Chronological checkpoints; older results may be superseded. |

## Feature and delivery records

These retain implementation detail, exact evidence, failures, skips, and open
acceptance work. Existing filenames stay stable so source references and older
handoffs continue to resolve. Some numbers are shared by unrelated records;
use the document title to identify the topic.

### Shared worlds, backups, and hosting moves

- [Shared-world audit](12-SHARED-WORLD-AUDIT.md)
- [Four-feature delivery: rehearsal, overview, world-load tests, live saves](13-FOUR-FEATURE-DELIVERY.md)
- [Earlier live-save adapter acceptance](10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md)
- [Peer shared worlds](26-PEER-SHARED-WORLDS-DELIVERY.md)
- [World protection controls](27-WORLD-PROTECTION-UX.md)
- [Simple shared hosting](30-SIMPLE-SHARED-HOSTING.md)
- [Paired-PC hosting moves](31-PAIRED-PC-HOSTING-MOVES.md)
- [Guided setup and shared hosting](35-GUIDED-SETUP-AND-SHARED-HOSTING.md)
- [Single-setting shared hosting and automatic latest saves](36-SHARED-HOSTING-SIMPLIFICATION.md)

### Access, setup, and updates

- [Removed PCs and saved Host deletion](25-REVOKED-PROFILES-AND-DELETION.md)
- [Profile names](28-PROFILE-NAMES.md)
- [Required feature setup](29-REQUIRED-FEATURE-SETUP.md)
- [Prerelease updates](30-PRERELEASE-UPDATES.md)
- [Historical firewall-check rule cleanup](11-FIREWALL-CHECK-RULE-CLEANUP.md)

### Interface and usability

- [Six-feature delivery: requirements, settings, backup pins, client launch, notices, summaries](14-SIX-FEATURE-DELIVERY.md)
- [Quality-of-life delivery](15-QOL-UX-DELIVERY.md) and [validation](16-QOL-UX-VALIDATION.md)
- [UI structure and flow](17-UI-STRUCTURE-AND-FLOW.md)
- [Core UX](19-CORE-UX-DELIVERY.md)
- [Settings flow](21-SETTINGS-FLOW-DELIVERY.md)
- [UI redesign](21-UI-REDESIGN-DELIVERY.md) and [coverage](22-UI-REDESIGN-COVERAGE.md)
- [Settings and recovery notices](23-SETTINGS-AND-RECOVERY-FIX.md)
- [UI layering](24-UI-LAYERING-FIX.md)
- [UI polish review](28-UI-POLISH-REVIEW.md)
- [Development demo](31-DEVELOPMENT-DEMO.md)
- [Server management redesign](32-SERVER-MANAGEMENT-REDESIGN.md)
- [Sidebar layout](32-SIDEBAR-LAYOUT.md)
- [Quiet player checks](33-QUIET-PLAYER-CHECKS.md)
- [Plain-language audit](34-PLAIN-LANGUAGE-AUDIT.md)
- [Workspace and window controls](35-WORKSPACE-AND-WINDOW-CHROME.md)
- [Documentation refresh](37-DOCUMENTATION-REFRESH.md)
- [Friend hosting checks and guided fixes](42-FRIEND-HOSTING-SETUP-AND-0.3.3.3.md)
- [Valheim shared-save publication and backup retry](43-VALHEIM-SHARED-SAVE-PUBLICATION-AND-0.3.3.4.md)

## Release evidence

| Release | Record |
| --- | --- |
| 0.3.1 | [Release delivery](18-0.3.1-RELEASE.md) |
| 0.3.2 | [Release delivery](20-0.3.2-RELEASE.md) |
| 0.3.2.1 preview | [Prerelease delivery](31-0.3.2.1-PRERELEASE.md) |
| 0.3.2.2 preview | [Prerelease delivery](36-0.3.2.2-PRERELEASE.md) |
| 0.3.3 | [Stable release delivery and evidence](38-0.3.3-RELEASE.md) |
| 0.3.3.1 preview | [Prerelease delivery](39-0.3.3.1-PRERELEASE.md) |
| 0.3.3.2 preview | [Prerelease delivery](41-0.3.3.2-PRERELEASE.md) |
| 0.3.3.3 preview | [Friend hosting fixes and prerelease delivery](42-FRIEND-HOSTING-SETUP-AND-0.3.3.3.md) |
| 0.3.3.4 preview | [Valheim publication fix and prerelease delivery](43-VALHEIM-SHARED-SAVE-PUBLICATION-AND-0.3.3.4.md) |

Public downloads and release notes are on
[GitHub Releases](https://github.com/daltonstates/TogetherServer/releases).

## Keeping these docs organized

Keep the root README focused on the product, downloads, first use, and links.
Put user instructions in `guides/` and contributor workflows in `development/`.
Keep validation logs, artifact paths, hashes, and unfinished acceptance in the
relevant delivery record. Link new records here by topic. Update the task guide
when behavior changes instead of adding another feature announcement to the
README. The [new-chat handoff](../START_NEW_CHAT.md) points to this structure.
