# Host locally entry and Play actions

Owner correction, 2026-10-21: show a clear Friend **Host locally** action before
hosting checks. Clicking it opens the guided checks and the network warning;
the app helps resolve missing setup and starts only after confirmation. Remove
the explanatory text between **Open game** and **Stop server** and give those
buttons the same dimensions.

## Change

- **Host locally** has a visible label beside the ordinary server actions.
  It remains available when a permission lookup is pending or unavailable,
  allowing the user to open the guided recovery flow. A confirmed denial hides
  it; revoked, expired and pending-approval access stays blocked.
- A small local-only permission lookup refreshes signed membership and checks
  the existing identity, grant, roster, floor and owner-access gates. It never
  selects or verifies a save, discovers server files, enters the lifecycle gate
  or checks ports/routes. Signed offline grants remain usable when the original
  Host cannot be reached. No permission or incoming listener is enabled here.
- Full hosting checks run after the click and while the dialog remains open.
  Missing server installation, terms, save, connection and port problems have
  the existing guided next step. The network confirmation still authorizes one
  bounded prepare/Start sequence; reads, retries, stale replies and permission
  withdrawal never recreate a consumed Start intent.
- Host-decision diagnostics are in Connection help instead of preceding Play.
  Open game and Stop server share a 12-rem width and 44-pixel minimum height.
  The Steam helper sentence is removed; explicit launch errors and results stay
  visible. Game launch and remote Stop permissions are unchanged.

## Verification checkpoint

Source checks passed all 63 groups, including the new permission-only group.
The complete solution compiled with zero warnings/errors. One existing
symbolic-link subcase remains skipped because this Windows account cannot
create the link. Only synthetic files and fake HTTP/adapters were used.

The initial focused interface run passed 116 cases. The full 1,347-case run
passed 1,338 cases and failed nine: shell fixture responses did not yet describe
the new permission endpoint, shell selection assumed the previously delayed
button, and unchanged shell/recovery cases hit their existing deadlines during
the concurrent build. Fixtures now supply explicit denied/allowed permission
responses, wait for the selected section before clicking, and await a confirmed
denial before asserting that hosting is hidden. Serial followups passed all
141 cases in the two affected files except those five fixture/selection cases;
the corrected five cases and a new pending-denial/cancel regression then all
passed. All 1,348 UI cases passed across these runs. Deadlines and safety
assertions are unchanged. Lint, TypeScript and production bundling pass; the
existing large-chunk advisory remains.

All 31 pure renderer checks pass. The hosted visual inventory now covers 26
desktop/narrow cases, including the visible entry before preparation, entry
with no server installed, and measured equality of Open game/Stop server.
Hosted Windows rendering, actual permission-GET/packaged handoffs and the exact
production EXE are pending. No app, fixture, listener, game, browser, installer
or native dialog has been launched on the owner's active desktop.

The published `v0.3.3.1` assets remain immutable. This correction has not been
published as another GitHub release. Physical Friend PCs, outside routing,
real game joins and real save/load/Stop/restart remain external acceptance.
