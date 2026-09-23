# Product scope: TogetherServer v1

## Goal

Let the owner host a dedicated game server on a Windows PC and let a small, known group of friends see its status and request approved actions from their own Windows PCs. Valheim was first; Minecraft Java and Bedrock now have built-in local Host drivers with fixture-tested lifecycle paths. Real Minecraft joins and saved-world restart checks remain acceptance gates. Keep the program understandable: one installable app with Host and Friend modes, a bundled GUI, approved actions, and local settings. No cloud control plane or separate database service.

## People and modes

- **Owner / Host mode:** configures a game server and its world, starts or stops it, and invites Friends. Minecraft setup can find an existing server or install a fresh official server after the owner accepts its terms in the app. Valheim setup includes a protected game password. Installed paths, ports, multiple servers, and remote Stop safety live in secondary settings. The Host app supervises game processes and shows honest status and errors.
- **Friend mode:** each friend runs the same app on the PC used to play. Each pasted server code creates a distinct credential that initially grants only that server. The Host may later assign the PC to zero, one, or several saved servers. The app sends authenticated heartbeats, displays only assigned Host servers and their reported online-player counts, and requests only actions the owner grants.
- **Owner playing locally:** the Host sees the same server-reported count as Friends. An optional local game-process indicator may be shown, but it is not the authority for remote Stop.
- **Owner joining another Host:** Hosting and Friend connection are concurrent capabilities in one app process. Opening a Friend connection must not stop the owner's managed server, disable its companion listener, or interrupt existing paired Friends. The visible Host and Join pages are navigation, not mutually exclusive runtime roles.
- V1 requires the companion app for anyone who wants remote controls. Console and unpaired players are still covered by a reliable server count for Stop safety; they do not need a player ID entered in TogetherServer.

## V1 user flow

1. On first use, the owner chooses Host or Join. Host uses a Game, World, Server app, and Review guide. For Valheim, the owner chooses a new world or a copied existing world, enters a game password, then saves and starts. The app finds an installed Valheim Dedicated Server when it can; Steam installation and game terms remain the owner's actions. The original world is not changed or deleted.
2. The owner creates one current invite code for a saved server and copies it privately to Friend PCs. This deliberate action enables the companion listener immediately. The app does not send messages to friends automatically. A credential issued by that code starts with only that server assigned. Refreshing the code revokes the old code and every device credential issued by it, including extra server assignments later placed on those credentials; credentials issued by other server codes remain valid.
3. A Friend pastes the server code. It includes the Host IP, control port, initial server identity, pairing secret, and full Host TLS fingerprint. The Friend app verifies the pinned identity and saves its own revocable device credential. The Host can then reassign that PC to any combination of saved servers through the loopback-only owner GUI. The GUI shows Connected, Disabled, Revoked, or Unknown/Disconnected distinctly.
4. A permitted friend may request Start. The Host serializes requests, checks configured maximum concurrency and port/save-folder conflicts, starts only the saved game server program through its built-in driver, and reports Starting until it observes the driver's local readiness signal. A duplicate request cannot launch a second process for the same save folder.
5. A permitted friend may request Stop only when the Ready game server returns a fresh online-player count of zero. Host asks the same built-in driver for the count again immediately before the graceful stop signal. A positive, missing, malformed, partial, or unknown count denies remote Stop. The owner can request local Stop independently, including while players are online.
6. The owner can turn **remote controls off** without stopping a running game. Host rejects new remote Start/Stop immediately, keeps an authenticated read-only heartbeat/status channel so connected Friend apps can display the disabled notice, and shows it after an offline Friend reconnects. Revoking a Friend device invalidates its credential and is shown as Revoked on its next request.

## Auto shutdown

- The owner sets an idle duration in minutes and can disable auto shutdown. Default it to **off**, and keep the missing real-game acceptance visible before recommending it for a valued world.
- Server-reported player counts are the primary occupancy signal. Valheim prefers its local query; a non-Crossplay no-reply case may use only a complete connection history from TogetherServer's own run log. A malformed query or incomplete log sequence is Unknown, never zero. Host/Friend `gameRunning` checks can only add a blocker; they can never override a positive or unknown server count.
- When enabled, each Ready zero-player server owns a Host-generated shutdown deadline shown as the same live countdown on its Host card and every assigned Friend card. A positive or Unknown count, a running/unknown Host game check, or a running/unknown report from any assigned paired Friend cancels that deadline. When no deadline exists, both cards show the specific timer blocker. A Host app restart starts a fresh idle window rather than counting unobserved time.
- At expiry, Host rechecks the driver's current count under the lifecycle gate and stops only if it is still exactly zero. Fixture coverage is not real-game acceptance; each real built-in game still needs a tested count transition, idle window, graceful stop, and recognizable save/restart result before the owner relies on automatic shutdown for a valued world.
- Never force-kill a real world as a normal idle action. Surface timeout/failure and leave the process/world state Unknown when a graceful stop cannot be verified.

## Settings and honest status

- `maxConcurrentServers` is an owner-set positive count, default 1. It limits only TogetherServer-managed processes. Each running world needs a distinct save location and game ports.
- Configure idle minutes, game executable path, world/profile, server name, port, remote-control toggle, per-friend server assignments, and per-friend Start/Stop permissions. No player IDs are required. Save secret material through Windows-protected storage, outside the repo.
- Distinguish Process running, Server ready, server-reported online players, local game ports, the local Friend-control listener, and an authenticated Friend heartbeat. A local listener, process, or count reply is not a public route or proof of a particular Friend join.
- A saved Friend Host endpoint may be checked from Friend mode; it must display Unknown when the public route or Host cannot be verified. Host mode may show the latest authenticated heartbeat for each paired Friend.

## Out of scope for v1

Commercial or cloud servers, billing, a public web dashboard, a plugin system, provider provisioning, automatic DNS/router/firewall changes, mobile or console companions, and guaranteeing physical server capacity. A future game driver must identify the actual server process, declare its ports and readiness evidence, and prove safe Stop behavior before remote Stop or automatic shutdown is enabled for that profile. No Friend request can supply a script or command. The Valheim friend join/save/restart gate remains required.
