# TogetherServer repository instructions

## Scope

Build one small Windows-first game hosting application. The same .NET app is installed on the Host PC and every Friend PC, with Host and Friend modes. Bundle a React/TypeScript interface into the app so Node and a separate web server are not runtime dependencies. Valheim, Minecraft Java, and Minecraft Bedrock dedicated servers are external processes controlled by reviewed built-in Host drivers. Minecraft support is fixture-tested until real owner-installed game acceptance is recorded. V1 has no Docker, PostgreSQL, cloud service, billing, Paper/modded servers, or commercial hosting.

Read `README.md`, `docs/00-PRODUCT.md`, `docs/01-ARCHITECTURE.md`, `docs/02-NETWORK-AND-SECURITY.md`, `docs/03-LIFECYCLE.md`, and `docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md` before implementation. The user's current-chat instructions override these files. Preserve data and unrelated changes.

## Delivery

- Work in bounded vertical slices. Start actual code in the first implementation turn; do not stop at a plan or build an orchestration framework.
- Prefer one .NET 10 app with bundled React assets, explicit Host/Friend modes, and simple local durable settings. Use current stable, verified dependency versions and commit lockfiles.
- Keep the public companion API small: authenticated heartbeat, status, and fixed start/stop requests. A remote user must never submit a script, shell command, executable path, world path, or raw arguments.
- Keep the owner's GUI on loopback. Expose the companion API only when the owner deliberately enables it and the configured TLS and pairing requirements are satisfied.
- Give each friend/device a separate revocable credential and explicit permissions. Turning remote controls off is enforced on the Host before the Friend UI receives the notice; heartbeat/status may remain available to communicate the disabled state.
- A missed or stale heartbeat is Unknown for Friend connection status, but Friend app presence never gates automatic shutdown. The game server's reported count is the only occupancy authority: exact zero starts the countdown, while a positive or Unknown count cancels it. Recheck the count inside the lifecycle gate before automatic Stop.
- Track managed game processes by recorded identity, not name alone. Never kill an unrelated process. Gracefully stop and confirm save behavior before claiming real Valheim support.
- Do not claim a fixture or process-exists check proves game readiness, remote reachability, a friend join, world-save integrity, or restart recovery.
- Use focused meaningful tests, then real Windows/process/browser and friend-network tests for the surface changed. Record passes, failures, and skips honestly.

## Hard stops

Do not accept Valheim or other game terms, download a terms-gated game binary, spend money, change public firewall/router/DNS settings, request real credentials, or delete/overwrite real worlds without explicit owner authorization. No public listener is enabled by default. Keep secrets, personal identifiers, worlds, binaries, and production logs out of Git.
