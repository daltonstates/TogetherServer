# Small v1 architecture

```text
Friend PC                                      Owner PC
TogetherServer.exe (Friend mode)              TogetherServer.exe (Host mode)
  React UI in native window                      React UI in native window
  saved Host connection                          settings + friend pairing
  outbound HTTPS heartbeat/start/stop  ----->   small HTTPS companion API
                                                process supervisor + game registry
                                                       |
                                                built-in game driver
                                                       |
                                                Valheim, Minecraft Java or Bedrock
                                                chosen world/save folder
```

This is **one codebase and one TogetherServer process per PC**. Game clients and dedicated servers remain their own processes. There is no separate TogetherServer web server, worker agent, Docker runtime, PostgreSQL server, or cloud relay. Development may use frontend tooling, but a published app must bundle the built React assets and run without Node installed.

The borderless native window supplies app-styled drag, resize, minimize, maximize/restore, and close controls. It embeds the bundled React UI with WebView2 and talks to the same process's loopback API. WebView2 may start its normal renderer child processes; there is still only one TogetherServer app process and no second web-server process. The Evergreen WebView2 Runtime is a shared Windows component; the app shows a native setup message if it is absent.

The optional per-user Windows Run entry starts this same EXE at sign-in with `--startup`, which opens it in the tray. Close to tray hides the window while the Host supervisor, Friend heartbeat, and loopback GUI continue in the same process. Tray Quit uses the existing guarded local quit action, so an active or unresolved managed server still blocks process exit.

## Host internals

The Host and Friend capabilities may run concurrently in the same process. Host and Join select which local page is visible; they do not start or stop a capability. A configured Host listener starts from saved owner settings even when the app reopens on Join. Quit remains blocked by any managed game run from either page.

- A local loopback GUI listener serves bundled React files and local-owner API actions. It must not become the public management interface.
- An optional HTTPS companion listener accepts only pairing, authenticated heartbeat, status, Start, and Stop requests. It is off by default and cannot start without pairing and TLS configuration. The owner can start or stop it immediately in the same process; the local GUI remains on loopback. Its port is distinct from each game's ports.
- `HostManager` owns shared serialization, process identity, save-folder ownership, concurrency, and lifecycle policy. Pairing metadata records both the server code that issued each device credential, an explicit owner-managed set of assigned profile IDs, global Start/Stop defaults, and per-server permission exceptions. The companion API exposes only assigned profiles and checks the effective permission for that exact profile before dispatching through `HostManager` to a registered `IGameServerDriver`. Each built-in driver owns its executable validation, fixed launch arguments, declared TCP/UDP ports, readiness evidence, local online-player count, public join-address shape, and graceful stop. The Valheim driver prefers A2S_INFO and, only for a non-Crossplay no-reply case, may derive occupancy from the complete event sequence in its TogetherServer-owned run log. Malformed replies and incomplete log sequences fail closed. Friend requests contain only an assigned saved profile ID and typed action; they never select a driver, executable, script, path, argument, environment key, or shell expression. A run records its declared ports for conflict checks after restart.
- Serialize lifecycle actions with one in-process gate and durable state. On app restart, verify the recorded PID, start time, executable path, and managed world before reattaching. If identity cannot be proven, show Unknown and refuse another start for that world until the owner resolves it. Never kill by process name alone.
- Local settings and pairing metadata live under the user's `%LOCALAPPDATA%\TogetherServer` directory. A small atomically replaced JSON store is enough for v1's single Host process; secrets must be protected with Windows facilities, and only credential hashes should be stored on Host. Do not store worlds under the source checkout.
- Minecraft setup scans bounded common folders and saved paths for Bedrock executables and manifest-bearing Java server JARs. The owner-only install action fetches official metadata and fresh Java or Bedrock files into new private folders after explicit in-app terms consent. Java uses Mojang's checksum and a matching hashed Temurin runtime; Bedrock uses the official download link and guarded ZIP extraction. Neither discovery nor installation modifies an existing server folder.
- `maxConcurrentServers` counts managed game server processes. A world/save path and every normalized protocol/address-family/port declared by its driver can have only one managed writer. A named port conflict is reported before the generic concurrency limit. Check actual local port binding before a start; a configured count is not a physical-capacity guarantee.
- Port-conflict replacement is a separate fixed remote action, never an effect of ordinary Start. It requires Start permission on the requested assigned profile but deliberately does not require assignment or Stop permission for the conflicting profile. Under the same lifecycle gate, `HostManager` rejects Host-extended countdowns, requires each conflict to be Ready at zero players, repeats that driver count immediately before graceful Stop, and starts the requested profile only after conflicts exit. The response may disclose the conflicting saved name and shared ports, but not its join address.
- Port diagnostics read Windows' active TCP and UDP listener tables. They can prove that a declared port is open on this PC. Only a fresh authenticated Friend heartbeat proves that the control listener was reached from a Friend PC, and only a real game-client join proves the game route.

## Friend internals

- The same executable runs in Friend mode and serves its React UI locally. It stores only its own pairing material, protected on that Windows account.
- Each saved Friend connection keeps its own credential and heartbeat sequence. A credential initially sees only the server whose code created it, then may see zero, one, or several servers explicitly assigned by the Host. It sends an outbound heartbeat about every five seconds and displays the selected Host's last response, assigned server-reported online-player counts, any Host-owned automatic-shutdown deadline or blocker reason, and freshness timestamp. The Host supervises and polls its managed servers about every three seconds. Both UIs render the Host-owned deadline locally each second; Friend clocks never decide when Stop runs. No game-client path or `gameRunning` report is stored or sent.
- The Friend app does not receive or run host scripts. It sends typed Start/Stop requests and displays actual Host results. An unreachable Host is Disconnected/Unknown; it is not assumed Disabled or Offline.

## Public-IP path

V1 uses the owner's publicly reachable IP address and a separately configured TCP control port; no VPN is required. Before enabling remote control, prove that the WAN address is routable and that an actual Friend on another network can connect. A router port forward and host firewall rule may be needed; the app must explain them but never create them automatically. If the ISP uses shared-address NAT or inbound filtering, direct public-IP control cannot be promised.

The Host interface stays on loopback. For the Friend API, prefer a Host-generated TLS certificate whose fingerprint is included in a manually shared pairing invite and pinned by the Friend app. This allows an IP-address endpoint without DNS. The Friend must refuse a certificate mismatch rather than silently bypass validation. The implementation may use an equally well-tested publicly trusted IP certificate if automated renewal and owner approval are available; this is a design choice to verify during implementation. Per-device credentials and public TLS details are in [02-NETWORK-AND-SECURITY.md](02-NETWORK-AND-SECURITY.md).

## Scope discipline

Keep the code close to these actual boundaries: GUI, settings/pairing, process supervisor, built-in game drivers, and companion HTTP API. Avoid schedulers, outboxes, migrations, generic plugin systems, multi-node abstractions, or cloud/provider code in v1. A later game follows [the built-in driver contract](07-ADDING-A-GAME.md) and can ship only with its own focused lifecycle and safety evidence.
