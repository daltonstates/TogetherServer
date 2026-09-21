# Product scope: TogetherServer v1

## Goal

Let the owner host a Valheim dedicated server on a Windows PC and let a small, known group of friends see its status and request start/stop from their own Windows PCs. Valheim is the first supported game; later games can be added as reviewed built-in game drivers with fixed lifecycle actions. Keep the program understandable: one installable app with Host and Friend modes, a bundled GUI, approved actions, and local settings. No cloud control plane or separate database service.

## People and modes

- **Owner / Host mode:** sets up a world and game password, starts or stops the server, and invites Friends. Installed paths, ports, multiple servers, and remote Stop safety live in secondary settings. The Host app supervises game processes and shows honest status and errors.
- **Friend mode:** each friend runs the same app on the PC used to play Valheim. It pairs to one saved server using that server's current invite code, receives a distinct device credential, sends an authenticated heartbeat with whether that PC's Valheim game is running, displays Host/server reachability, and requests only actions the owner grants.
- **Owner playing locally:** Host mode must include the owner's own game-running signal in idle decisions. Running the Host app must not imply the owner's Valheim game client is open.
- **Owner joining another Host:** Hosting and Friend connection are concurrent capabilities in one app process. Opening a Friend connection must not stop the owner's managed server, disable its companion listener, or interrupt existing paired Friends. The visible My server and Join a friend pages are navigation, not mutually exclusive runtime roles.
- V1 requires every potential player to use the companion app. Console players, unpaired players, and multiple devices per person need explicit support before they can participate in automatic idle shutdown decisions.

## V1 user flow

1. In My server, the owner chooses a new world or a copied existing world, enters a game password, then saves and starts. The app finds an installed Valheim Dedicated Server when it can; Steam installation and game terms remain the owner's actions. The original world is not changed or deleted.
2. The owner creates one current invite code for a saved server and copies it privately to Friend PCs. This deliberate action enables the companion listener immediately. The app does not send messages to friends automatically. Refreshing the code revokes the old code and every paired credential for that server.
3. A Friend pastes the server code. It includes the Host IP, control port, server identity, pairing secret, and full Host TLS fingerprint. The Friend app verifies the pinned identity and saves its own revocable device credential. The GUI shows Connected, Disabled, Revoked, or Unknown/Disconnected distinctly.
4. A permitted friend may request Start. The Host serializes requests, checks configured maximum concurrency and port/world conflicts, starts only the approved local Valheim server program, and reports Starting until it observes real readiness. A duplicate request cannot launch a second process for the same world.
5. A permitted friend may request Stop when the Valheim permitted-player list matches exactly the owner and paired Friend IDs, was loaded at server start, and every allowed PC reports its game closed. A true, missing, stale, or unknown report denies remote Stop. The Host performs a graceful game stop and reports its actual result. The owner can request local Stop independently.
6. The owner can turn **remote controls off** without stopping a running game. Host rejects new remote Start/Stop immediately, keeps an authenticated read-only heartbeat/status channel so connected Friend apps can display the disabled notice, and shows it after an offline Friend reconnects. Revoking a Friend device invalidates its credential and is shown as Revoked on its next request.

## Auto shutdown

- The owner sets an idle duration in minutes and can disable auto shutdown. Default it to **off** until real Valheim and companion coverage checks pass.
- A Friend app sends `gameRunning` from the local Valheim game client process; the Host checks the owner's local client separately. These are *client intent signals*, not server player counts.
- The idle timer may advance only while **every allowed player's required companion** has a fresh authenticated `false` report, the owner local client is closed, and no other available reliable source reports a player. A `true`, stale, missing, revoked-without-replacement, or uncertain report pauses/resets the timer. Do a final fresh recheck before stopping.
- The app must clearly explain that this rule depends on all players being enrolled and running the companion. Before enabling it, restrict the server to the paired players with Valheim's supported permitted-player list, or prove an equivalent reliable server-side player signal. If that condition cannot be verified for a server profile, show Auto shutdown unavailable rather than assume nobody is playing.
- Never force-kill a real world as a normal idle action. Surface timeout/failure and leave the process/world state Unknown when a graceful stop cannot be verified.

## Settings and honest status

- `maxConcurrentServers` is an owner-set positive count, default 1. It limits only TogetherServer-managed processes. Each running world needs a distinct save location and game ports.
- Configure idle minutes, game executable path, world/profile, server name, port, remote-control toggle, and per-friend Start/Stop permissions. Save secret material through Windows-protected storage, outside the repo.
- Distinguish Process running, Server ready, local game ports, the local Friend-control listener, an authenticated Friend heartbeat, and Friend game running. A local listener or process is not a public route or game join test.
- A saved Friend Host endpoint may be checked from Friend mode; it must display Unknown when the public route or Host cannot be verified. Host mode may show the latest authenticated heartbeat for each paired Friend.

## Out of scope for v1

Commercial or cloud servers, billing, a public web dashboard, a plugin system, provider provisioning, automatic DNS/router/firewall changes, mobile or console companions, and guaranteeing physical server capacity. A future game driver must identify the actual server process, declare its ports and readiness evidence, and prove safe Stop behavior before remote Stop or automatic shutdown is enabled for that profile. No Friend request can supply a script or command. The Valheim friend join/save/restart gate remains required.
