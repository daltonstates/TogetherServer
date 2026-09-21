# Small v1 architecture

```text
Friend PC                                      Owner PC
TogetherServer.exe (Friend mode)              TogetherServer.exe (Host mode)
  React UI in native window                      React UI in native window
  Valheim.exe process check                      settings + friend pairing
  outbound HTTPS heartbeat/start/stop  ----->   small HTTPS companion API
                                                process supervisor + game registry
                                                       |
                                                built-in Valheim driver
                                                       |
                                                valheim_server.exe
                                                chosen world/save folder
```

This is **one codebase and one TogetherServer process per PC**. The game client and Valheim dedicated server remain their own processes. There is no separate TogetherServer web server, worker agent, Docker runtime, PostgreSQL server, or cloud relay. Development may use frontend tooling, but a published app must bundle the built React assets and run without Node installed.

The borderless native window supplies app-styled drag, resize, minimize, maximize/restore, and close controls. It embeds the bundled React UI with WebView2 and talks to the same process's loopback API. WebView2 may start its normal renderer child processes; there is still only one TogetherServer app process and no second web-server process. The Evergreen WebView2 Runtime is a shared Windows component; the app shows a native setup message if it is absent.

The optional per-user Windows Run entry starts this same EXE at sign-in with `--startup`, which opens it in the tray. Close to tray hides the window while the Host supervisor, Friend heartbeat, and loopback GUI continue in the same process. Tray Quit uses the existing guarded local quit action, so an active or unresolved managed server still blocks process exit.

## Host internals

The Host and Friend capabilities may run concurrently in the same process. My server and Join a friend select which local page is visible; they do not start or stop a capability. A configured Host listener starts from saved owner settings even when the app reopens on Join a friend. Quit remains blocked by any managed game run from either page.

- A local loopback GUI listener serves bundled React files and local-owner API actions. It must not become the public management interface.
- An optional HTTPS companion listener accepts only pairing, authenticated heartbeat, status, Start, and Stop requests. It is off by default and cannot start without pairing and TLS configuration. The owner can start or stop it immediately in the same process; the local GUI remains on loopback. It is distinct from Valheim's game port.
- `HostManager` owns shared serialization, process identity, one-writer world rules, concurrency, and remote authorization. It dispatches only to a registered `IGameServerDriver`. Each built-in driver owns its executable validation, fixed launch arguments, declared TCP/UDP ports, readiness evidence, public join-address shape, and graceful stop. Friend requests contain only a saved profile ID and typed action; they never select a driver, executable, script, path, argument, environment key, or shell expression.
- Serialize lifecycle actions with one in-process gate and durable state. On app restart, verify the recorded PID, start time, executable path, and managed world before reattaching. If identity cannot be proven, show Unknown and refuse another start for that world until the owner resolves it. Never kill by process name alone.
- Local settings and pairing metadata live under the user's `%LOCALAPPDATA%\TogetherServer` directory. A small atomically replaced JSON store is enough for v1's single Host process; secrets must be protected with Windows facilities, and only credential hashes should be stored on Host. Do not store worlds under the source checkout.
- `maxConcurrentServers` counts managed game server processes. A world/save path and every port declared by its driver can have only one managed writer. Check actual local port binding before a start; a configured count is not a physical-capacity guarantee.
- Port diagnostics read Windows' active TCP and UDP listener tables. They can prove that a declared port is open on this PC. Only a fresh authenticated Friend heartbeat proves that the control listener was reached from a Friend PC, and only a real game-client join proves the game route.

## Friend internals

- The same executable runs in Friend mode and serves its React UI locally. It stores only its own pairing material, protected on that Windows account.
- It monitors the configured Valheim **game client** process using a verified executable path, sends an outbound heartbeat at a bounded interval, and displays the last Host response with a freshness timestamp. It must stay running when the game closes so it can report `false`.
- The Friend app does not receive or run host scripts. It sends typed Start/Stop requests and displays actual Host results. An unreachable Host is Disconnected/Unknown; it is not assumed Disabled or Offline.

## Public-IP path

V1 uses the owner's publicly reachable IP address and a separately configured TCP control port; no VPN is required. Before enabling remote control, prove that the WAN address is routable and that an actual Friend on another network can connect. A router port forward and host firewall rule may be needed; the app must explain them but never create them automatically. If the ISP uses shared-address NAT or inbound filtering, direct public-IP control cannot be promised.

The Host interface stays on loopback. For the Friend API, prefer a Host-generated TLS certificate whose fingerprint is included in a manually shared pairing invite and pinned by the Friend app. This allows an IP-address endpoint without DNS. The Friend must refuse a certificate mismatch rather than silently bypass validation. The implementation may use an equally well-tested publicly trusted IP certificate if automated renewal and owner approval are available; this is a design choice to verify during implementation. Per-device credentials and public TLS details are in [02-NETWORK-AND-SECURITY.md](02-NETWORK-AND-SECURITY.md).

## Scope discipline

Keep the code close to these actual boundaries: GUI, settings/pairing, process supervisor, built-in game drivers, and companion HTTP API. Avoid schedulers, outboxes, migrations, generic plugin systems, multi-node abstractions, or cloud/provider code in v1. A later game follows [the built-in driver contract](07-ADDING-A-GAME.md) and can ship only with its own focused lifecycle and safety evidence.
