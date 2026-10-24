# TogetherServer documentation

Start with a task guide. These describe current source as well as stable
features; check a release's notes for its included features and known limits.

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
| Rehearse pairing with synthetic instances | [Local pairing lab](LOCAL-PAIRING-LAB.md) |
| Test an actual Host/Friend connection | [Two-PC connection test](TWO-PC-CONNECTION-TEST.md) |
| Review real-game live-save requirements | [Live save acceptance](10-SHARED-WORLD-LIVE-SAVE-ACCEPTANCE.md) |
| Review old check-runner firewall rules manually | [Firewall maintenance](11-FIREWALL-CHECK-RULE-CLEANUP.md) |

Automated process, game, browser, native-window, installer and full release
checks require an explicitly approved separate PC or unattended Windows session.
Use the [code-only gate](development/testing.md#on-the-owners-active-desktop)
on the owner's active desktop.

## Product and engineering rules

Read these before changing behavior. Current owner instructions and
[AGENTS.md](../AGENTS.md) apply first.

| Document | Covers |
| --- | --- |
| [Product](00-PRODUCT.md) | People, main flows, features, and scope. |
| [Architecture](01-ARCHITECTURE.md) | One-app design, services, storage, and driver boundaries. |
| [Network and security](02-NETWORK-AND-SECURITY.md) | TLS pinning, pairing, permissions, routes, and local-only controls. |
| [Lifecycle](03-LIFECYCLE.md) | Process identity, readiness, Stop, recovery, and idle shutdown. |
| [Implementation and acceptance](04-IMPLEMENTATION-AND-ACCEPTANCE.md) | Required checks and the limits of each kind of evidence. |
| [Source notes](05-SOURCE-NOTES.md) | Upstream references used during implementation. |
| [Current status](06-IMPLEMENTATION-STATUS.md) | Current delivery notes, known issues, and open acceptance work. |

## Keeping the repository focused

Keep user instructions in `guides/`, contributor workflows in `development/`,
engineering rules in the numbered specifications, and unresolved work in
[current status](06-IMPLEMENTATION-STATUS.md). Update those files when behavior
changes. Generated packages, screenshots, test data and reports belong under
ignored `local-data/`. Earlier delivery records are available in Git history;
published versions have their own
[release notes](https://github.com/daltonstates/TogetherServer/releases).
