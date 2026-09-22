# Process, heartbeat, and idle rules

## Server states

Display `Offline`, `Starting`, `Ready`, `Stopping`, `Failed`, and `Unknown` based on observed behavior. A started process is not automatically Ready. A failed probe is not proof the process stopped. Do not report a join address as verified until real game networking has been checked.

### Start

1. Authorize the local owner or authenticated Friend action. Reject remote requests while controls are disabled.
2. Serialize starts/stops in the Host process. Recheck maximum concurrent managed servers, one writer per world, configured game-port conflicts, executable identity, save path, and available local ports immediately before launch.
3. Record an operation ID and its intended world/profile before launching. A repeated request with the same idempotency key returns the same result and never creates another server process.
4. Start only the owner-approved installed server selected by the saved built-in game profile, with validated fixed arguments and an explicit world/save location. Do not modify installed game files or accept game terms. Capture logs without storing passwords or personal identifiers in Git.
5. Show Starting until an actual readiness signal is observed. A fixture script or process-exists result is labeled Fixture/Process running; only a real client join can certify Join verified.

### Stop

1. Remote Stop is denied unless the Ready built-in game server returns a fresh online-player count of exactly zero. Host queries the same driver again inside the lifecycle gate immediately before signaling Stop. A positive, missing, malformed, timed-out, or changed result sends no stop signal. The owner can request local Stop independently, including while players are online. A successful remote request never means the game already saved.
2. Ask Valheim to exit gracefully, wait for actual process exit and save stabilization, then mark Offline. The [official Valheim guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) says to stop its Windows dedicated server with Ctrl+C rather than closing its window.
3. If the graceful stop times out, report Failed/Unknown and preserve the world. Do not automatically force-kill. Any owner-approved force action must warn that the save may be inconsistent.
4. A restart is a verified stop followed by a new start against the same saved world. Test a recognizable in-world change surviving it before declaring real Valheim support.

Minecraft Java and Bedrock use a fixed `stop` console command sent only after exact process and isolated-console checks. Their local status replies include player counts, so Friend Stop can use the same two-query zero-player gate. Disposable stop markers do not establish real save integrity, and auto shutdown remains unavailable.

### Host app restart

Persist enough identity to check whether a previously managed server process still exists. Reattach only when PID, process creation time, executable path, and world/profile identity agree. If uncertain, show Unknown, keep the world blocked from another start, and require owner reconciliation. Never search for a name and kill the first matching process.

## Companion heartbeat

- Friend mode sends an authenticated outbound heartbeat about every 15 seconds with device ID, version, and monotonic sequence. It may also include an optional local game-process indicator. The Host uses **its receipt time** for freshness, not the Friend PC's clock. This indicator is informational and does not authorize remote Stop.
- A heartbeat older than roughly 45 seconds is Stale/Unknown. Retries are bounded. Host reachability and game-running status are separate fields in the GUI.
- Host mode may show the same optional local game-process indicator for the owner's PC. Minimizing the Host window must not stop Host supervision.
- If a Friend app is revoked or missing, its connection status is Unknown. Server occupancy remains a separate driver-reported value; do not infer it from companion reachability.
- The heartbeat response includes public Host status and the remote-controls flag. This is enough for a connected Friend app to show an enable/disable notice without a second push service.

## Idle timer

Auto shutdown is off by default. Before enabling it for a real game, verify repeated server player-count transitions, the idle window, a final zero-player recheck under the lifecycle gate, graceful exit, and a recognizable saved-world change after restart. Any positive or Unknown count cancels or pauses the countdown. Companion activity indicators may only add a blocker; they can never turn an unknown server count into zero.

Process presence alone does not prove a player joined, and a fixture reply does not certify a real game's count. If the driver cannot obtain a fresh valid count, show Auto shutdown unavailable or leave it off. Do not turn a silent process or timed-out query into a zero-player pass.
