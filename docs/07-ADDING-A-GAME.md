# Adding a built-in game

TogetherServer supports Valheim first. Its extension point is a small, reviewed .NET game driver, not a plugin loader or a public script interface.

## Driver contract

Implement `IGameServerDriver` beside the existing drivers and register it explicitly in `GameServerRegistry`. The driver owns:

- one stable game kind and user-facing name;
- every TCP or UDP port the server needs;
- validation of the owner-saved executable and game-specific settings;
- fixed process launch arguments assembled from validated local settings;
- preparation that must happen before launch;
- a real readiness signal and honest Starting, Ready, Failed, or Unknown detail;
- the public game join-address shape, when the game has one; and
- a graceful stop that waits for and reports the actual exit result.

`HostManager` continues to own serialization, recorded PID/start-time/path identity, world ownership, concurrency, port conflicts, durable run state, and the final authorization check. Do not copy those rules into a driver.

## Public boundary

A Friend may request Start, Stop, status, or heartbeat only for an already saved profile. Public input must never select an executable, command, script, arguments, environment values, working directory, save path, or driver type. Unknown game kinds fail closed. New game-specific remote actions require their own typed endpoint and permission review.

## UI and data

Add the smallest game-specific setup fields and discovery needed for that game. Keep the normal Start, Stop, Invite, connection checks, and activity states shared. Store secrets through `LocalData` protected storage. Keep real saves outside the repository and never mutate an existing save as part of discovery or validation.

The game driver declares local ports so the shared readiness row can distinguish local listeners from public evidence. A local port is never proof that a router, firewall, relay, or real client route works.

## Required evidence

Before exposing a new game in the normal setup menu, add isolated checks that prove:

1. executable and settings validation fails closed;
2. fixed launch arguments cannot be supplied by a Friend;
3. duplicate Start, world/save ownership, concurrency, and port conflicts still hold;
4. restart reattachment verifies PID, start time, and executable path;
5. readiness comes from a game-specific signal rather than process existence alone;
6. graceful Stop preserves a disposable save across restart and never kills an unrelated process;
7. declared local ports move through Waiting, Opening, Open, Closed, and Unknown honestly; and
8. the Host and Friend UI remains usable at the required desktop, tablet, and touch phone sizes.

Real support also needs an owner-approved game-client join and recognizable save/restart check. Remote Stop and automatic shutdown stay unavailable until the game's player coverage and safe-stop rules are proven.
