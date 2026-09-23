# Adding a built-in game

TogetherServer has built-in Valheim, Minecraft Java, and Minecraft Bedrock drivers. Minecraft's current launch, status, and stop checks use disposable fixtures; real-game acceptance is still required. Its official-support extension point remains a small, reviewed .NET game driver, not a plugin loader or a public script interface. The separate Custom game manager is an advanced local compatibility path and never counts as built-in support.

## Custom game versus built-in support

Use **Custom game** to try an owner-installed server through protected Start, Status/players, and Stop PowerShell actions. It is appropriate when the owner understands and reviews the scripts and accepts that TogetherServer cannot validate the selected game's launch, readiness, player source, save, or Stop behavior. Friends still submit only a saved profile ID and typed Start/Stop request; they never submit script content. Script-reported player data is display-only, so remote Stop, automatic shutdown, and empty-server replacement fail closed.

Promote a popular custom setup to a built-in driver only after replacing script trust with reviewed typed settings and the game-specific evidence below. Candidate priorities are tracked in [the game support catalog](08-GAME-SUPPORT-CATALOG.md).

## Driver contract

Implement `IGameServerDriver` beside the existing drivers and register it explicitly in `GameServerRegistry`. The driver owns:

- one stable game kind and user-facing name;
- every TCP or UDP port the server needs;
- validation of the owner-saved executable and game-specific settings;
- fixed process launch arguments assembled from validated local settings;
- preparation that must happen before launch;
- a real readiness signal and honest Starting, Ready, Failed, or Unknown detail;
- a validated local online-player count from the game's own status/query protocol, where a missing, malformed, timed-out, or unsupported reply remains Unknown;
- the public game join-address shape, when the game has one; and
- a graceful stop that waits for and reports the actual exit result.

`HostManager` continues to own serialization, recorded PID/start-time/path identity, save-folder ownership, concurrency, port conflicts, durable run state, and the final authorization check. Each run stores the ports declared at Start. Older run records recover them from the unchanged saved profile; an unknown driver or missing profile blocks another Start. Do not copy those rules into a driver.

## Public boundary

A Friend may request Start, Stop, status, or heartbeat only for an already saved profile. Public input must never select an executable, command, script, arguments, environment values, working directory, save path, or driver type. Unknown game kinds fail closed. New game-specific remote actions require their own typed endpoint and permission review.

## UI and data

Add the smallest game-specific setup fields and discovery needed for that game. Keep the normal Start, Stop, Invite, connection checks, and activity states shared. Friend connections are stored per invite and the public profile carries its game kind. Store secrets through `LocalData` protected storage. Keep real saves outside the repository and never mutate an existing save as part of discovery or validation.

The game driver declares local ports so the shared readiness row can distinguish local listeners from public evidence. It also owns player-count parsing so the shared Host/Friend cards can show one consistent count. A local port or count reply is never proof that a router, firewall, relay, or real client route works.

## Required evidence

For each new game, add isolated checks that prove:

1. executable and settings validation fails closed;
2. fixed launch arguments cannot be supplied by a Friend;
3. duplicate Start, world/save ownership, concurrency, and port conflicts still hold;
4. restart reattachment verifies PID, start time, and executable path;
5. readiness comes from a game-specific signal rather than process existence alone;
6. graceful Stop preserves a disposable save across restart and never kills an unrelated process;
7. declared local ports move through Waiting, Opening, Open, Closed, and Unknown honestly;
8. zero, positive, missing, malformed, and timed-out player-count replies are handled, a Friend Stop is allowed only at zero, and the count is queried again immediately before Stop; and
9. the Host and Friend UI remains usable at the required desktop, tablet, and touch phone sizes.

Real support also needs an owner-approved game-client join, real zero/one/disconnect player-count transitions, and a recognizable save/restart check. Remote Stop stays unavailable when a count is Unknown or positive; local Host Stop remains the owner's override. Automatic shutdown stays unavailable until the game's real idle and safe-stop rules are proven.
