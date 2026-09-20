# Process, heartbeat, and idle rules

## Server states

Display `Offline`, `Starting`, `Ready`, `Stopping`, `Failed`, and `Unknown` based on observed behavior. A started process is not automatically Ready. A failed probe is not proof the process stopped. Do not report a join address as verified until real game networking has been checked.

### Start

1. Authorize the local owner or authenticated Friend action. Reject remote requests while controls are disabled.
2. Serialize starts/stops in the Host process. Recheck maximum concurrent managed servers, one writer per world, configured game-port conflicts, executable identity, save path, and available local ports immediately before launch.
3. Record an operation ID and its intended world/profile before launching. A repeated request with the same idempotency key returns the same result and never creates another server process.
4. Start only the owner-approved installed Valheim Dedicated Server with validated arguments and an explicit world/save location. Do not modify Valve/Iron Gate's installed script in place. Capture logs without storing passwords or personal identifiers in Git.
5. Show Starting until an actual readiness signal is observed. A fixture script or process-exists result is labeled Fixture/Process running; only a real client join can certify Join verified.

### Stop

1. Remote Stop is denied if any required companion heartbeat is fresh `gameRunning: true` or Unknown, unless the owner performs an explicit local override. A successful remote request never means the game already saved.
2. Ask Valheim to exit gracefully, wait for actual process exit and save stabilization, then mark Offline. The [official Valheim guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) says to stop its Windows dedicated server with Ctrl+C rather than closing its window.
3. If the graceful stop times out, report Failed/Unknown and preserve the world. Do not automatically force-kill. Any owner-approved force action must warn that the save may be inconsistent.
4. A restart is a verified stop followed by a new start against the same saved world. Test a recognizable in-world change surviving it before declaring real Valheim support.

### Host app restart

Persist enough identity to check whether a previously managed server process still exists. Reattach only when PID, process creation time, executable path, and world/profile identity agree. If uncertain, show Unknown, keep the world blocked from another start, and require owner reconciliation. Never search for a name and kill the first matching process.

## Companion heartbeat

- Friend mode runs while the game is closed. It sends an authenticated outbound heartbeat about every 15 seconds with device ID, version, monotonic sequence, and a boolean for whether that PC's verified Valheim game client executable is running. The Host uses **its receipt time** for freshness, not the Friend PC's clock.
- A heartbeat older than roughly 45 seconds is Stale/Unknown. Retries are bounded. Host reachability and game-running status are separate fields in the GUI.
- Host mode performs the same local Valheim-client check for the owner's PC. Minimizing the Host window must not stop this check while the Host app is running.
- If a Friend app is revoked or a required device is missing, its status is Unknown for idle decisions until the owner explicitly changes the allowed-player set. Do not assume offline equals not playing.
- The heartbeat response includes public Host status and the remote-controls flag. This is enough for a connected Friend app to show an enable/disable notice without a second push service.

## Idle timer

Auto shutdown is off by default. The owner chooses a number of minutes and explicitly enables it after every allowed player has a paired companion and unpaired game access is excluded through Valheim's permitted-player list or an equivalent verified server-side player signal. The timer starts only after startup grace, while every required Friend and the owner have fresh `gameRunning: false` observations and no reliable game signal contradicts them. Any `true` or Unknown cancels/pauses the countdown. Immediately before stopping, re-read all statuses under the same lifecycle gate and confirm the server is still the managed process/world.

Process presence alone does not prove a player joined; the companion rule works only because v1 requires each possible player PC to run the app. If the host cannot maintain that coverage, show Auto shutdown unavailable or leave it off. Do not turn a synthetic fixture, silent process, or timed-out ping into a player-count pass.
