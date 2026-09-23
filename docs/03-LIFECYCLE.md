# Process, heartbeat, and idle rules

## Server states

Display `Offline`, `Starting`, `Ready`, `Stopping`, `Failed`, and `Unknown` based on observed behavior. A started process is not automatically Ready. A failed probe is not proof the process stopped. Do not report a join address as verified until real game networking has been checked.

### Start

1. Authorize the local owner or authenticated Friend action. A Friend credential must currently be assigned to the requested saved server profile and have permission for the typed action. Reject remote requests while controls are disabled.
2. Serialize starts/stops in the Host process. Recheck one writer per world, configured driver-declared protocol/address-family/port conflicts, maximum concurrent managed servers, executable identity, save path, and available local ports immediately before launch. Report a named managed port conflict before the generic maximum so the initiator sees the actionable cause. Setup warnings and suggested non-overlapping configured ports are advisory; this gate is authoritative.
3. Record an operation ID and its intended world/profile before launching. A repeated request with the same idempotency key returns the same result and never creates another server process.
4. Start only the owner-approved installed server selected by a saved built-in game profile, or the saved Custom profile's Windows-protected Host script. Built-ins use validated fixed arguments and an explicit world/save location. A custom Start action is local owner code and must remain alive as the tracked wrapper until its server exits. Do not modify installed game files or accept game terms. Capture logs without storing passwords or personal identifiers in Git.
5. Show Starting until an actual readiness signal is observed. A fixture script or process-exists result is labeled Fixture/Process running; only a real client join can certify Join verified.

An ordinary Start conflict does not stop anything. A paired device with Start permission for the requested assigned profile may separately confirm **replace empty port conflict**. Assignment or Stop permission for the conflicting profile is intentionally not required, but the response exposes only its saved name and shared ports. The Host denies replacement if remote controls are off, process identity is uncertain, readiness/count is unavailable, any player is online, or a Host-added countdown extension is active. It then repeats exact zero inside the lifecycle gate, gracefully stops each conflict, and launches the requested profile only after those exits are confirmed.

### Stop

1. Remote Stop is denied unless the Ready built-in game server returns a fresh online-player count of exactly zero. Host asks the same driver again inside the lifecycle gate immediately before signaling Stop. For non-Crossplay Valheim, a query timeout may use a complete TogetherServer-owned run-log sequence; malformed query data or partial/contradictory log events remain Unknown. A positive, missing, unknown, or changed result sends no stop signal. The owner can request local Stop independently, including while players are online. A successful remote request never means the game already saved.
2. Ask Valheim to exit gracefully, wait for actual process exit and save stabilization, then mark Offline. The [official Valheim guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) says to stop its Windows dedicated server with Ctrl+C rather than closing its window.
3. If the graceful stop times out, report Failed/Unknown and preserve the world. Do not automatically force-kill. Any owner-approved force action must warn that the save may be inconsistent.
4. A restart is a verified stop followed by a new start against the same saved world. Test a recognizable in-world change surviving it before declaring real Valheim support.

Minecraft Java and Bedrock use a fixed `stop` console command sent only after exact process and isolated-console checks. Their local status replies include player counts, so Friend and automatic Stop use the same two-query zero-player gate. Disposable stop markers do not establish real save integrity.

A Custom profile may show the count and player names returned by its bounded Status/players script, but that data is not an occupancy authority. Friend Stop, automatic shutdown, and empty-server replacement remain unavailable for Custom profiles even when the script reports zero. The local owner may run the protected Stop script; TogetherServer requires that action to finish and the tracked Start wrapper to exit, never force-kills the game, and does not claim the game's world was saved.

### Host app restart

Persist enough identity to check whether a previously managed server process still exists. Reattach only when PID, process creation time, executable path, and world/profile identity agree. If uncertain, show Unknown, keep the world blocked from another start, and require owner reconciliation. Never search for a name and kill the first matching process.

## Companion heartbeat

- Friend mode sends an authenticated outbound heartbeat and status request about every five seconds with device ID, version, and monotonic sequence. The Host uses **its receipt time** for freshness, not the Friend PC's clock. It does not send a local game-process indicator, and Friend presence does not participate in automatic shutdown.
- A heartbeat older than roughly 45 seconds is Stale/Unknown. Retries are bounded. Host reachability and game-running status are separate fields in the GUI.
- Minimizing the Host window must not stop Host supervision or its roughly three-second managed-server polling loop.
- If a Friend app is revoked or missing, its connection status is Unknown. Server occupancy remains a separate driver-reported value; do not infer it from companion reachability.
- The heartbeat response includes public Host status and the remote-controls flag. This is enough for a connected Friend app to show an enable/disable notice without a second push service.

## Idle timer

Auto shutdown is off by default. When the owner enables it, a Ready server's first exact zero-player observation starts its own idle window. Host publishes the resulting UTC deadline to its local snapshot and to assigned Friends; both UIs render the countdown from that same deadline. Without a deadline, both UIs show whether the feature is off, occupancy is positive, or the count is Unknown. Any positive or Unknown count cancels the countdown, and a later exact zero starts a full new window. The Host may extend the active deadline by an entered number of minutes; that extension is discarded if the countdown is canceled. Restarting the Host app also starts a fresh window because time without observation cannot count as verified idle time.

When the deadline expires, Host serializes the action with every other Start/Stop, rechecks exact process identity, and queries the same game driver again. It invokes only the existing graceful Stop when the fresh result is still Ready with exactly zero players. A changed or Unknown result sends no signal. A failed or timed-out graceful Stop remains recorded for review and is not force-killed.

Process presence alone does not prove a player joined, and a fixture reply or synthetic log does not certify a real game's count. Before relying on the feature for a valued world, verify real zero/one/disconnect transitions, the idle window, graceful exit, and a recognizable saved-world change after restart. Query silence by itself is never a zero-player pass; the Valheim fallback requires a complete event history from the exact managed run log.
