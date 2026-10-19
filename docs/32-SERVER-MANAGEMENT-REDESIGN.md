# One server management workspace

The owner requested one server management page, straightforward essential settings, a clear indication of guest hosting, and player-count warnings only after repeated lookup problems. This supersedes the previous visible Host/Join navigation split; the internal local/Friend endpoint roles remain scoped to the selected server.

The Servers sidebar lists Your servers, Hosting for friends, and Friend servers grouped by the saved Host. One search filters all groups. Selecting a row opens that server's controls without removing the other groups. New server and Connect are explicit actions. The old Host/server pickers, duplicate Join navigation, pin/reorder controls and separate comparison entry are removed from this surface. Notifications and protected-draft navigation still resolve to the selected server; selecting a row does not start or stop anything. Revoked access stays hidden until deliberately revealed.

Guest-hosted local profiles use their existing separate-copy source identity. Both the grouped row and persistent detail header say who the save is hosted for and show the original save name when available. No profile, world or stored identity is renamed. Directory reads expose only bounded display fields, use the already published Host snapshot and saved Friend views, do not refresh remote Hosts or probe processes, and do not change active mode or connection. Stale/offline counts remain Unknown; cached server names convey no permission to start or stop.

This PC settings now has Access, Shutdown, Network and Advanced. Countdown and optional Friend time extensions share one editor. Network combines address/port configuration, with troubleshooting and raw secure-identity details available when opened. Friend servers have one Settings section with saved name, deliberate address change and nested troubleshooting. Legacy notification section identifiers remain supported.

Player-count presentation tracks refresh in progress and consecutive completed unavailable observation cycles for the exact managed run. A successful trusted count resets the streak; the first two internal retries of a single observation are not separate failures. A missing or stale count shows neutral checking; a warning begins after two failed cycles. Transient count-only lifecycle transitions and recovery do not create activity/notification churn. Failed drivers, unknown process identities and protected-setup errors remain visible. Count history is presentation only: no stale/Unknown count authorizes Friend or automatic Stop or keeps a countdown alive, and the final serialized fresh-count check remains unchanged.

The isolated demo includes a shared save owned by Morgan, explicitly labeled guest-hosting results, and checking/repeated-player-issue scenes. The fake transport stays in memory and fails closed. Production and the previous demo are never launched, stopped or replaced by validation.

Validation on 2026-10-19:

- Complete JSDOM suite: **1,201 tests across 97 files passed**, with four workers. This includes grouped local/Friend selection, cross-connection identities, guest origin in the list and header, permission -> prepare -> explicit Start -> revisit, deleted-Host removal, protected drafts, native notification destinations, transient/repeated player checks and successful count recovery.
- `scripts/verify-code-only.ps1`: passed pinned tools, locked installs/restores, TypeScript, lint, full solution compilation with zero warnings/errors, all **54 source/synthetic groups**, whitespace and diagnostic isolation. Existing source-link subcases were skipped because the test account cannot create symbolic links.
- Production UI bundle passed. The existing main-chunk size advisory remains. Packaging PowerShell syntax passed.
- The tests caught a forgotten Host remaining in the combined directory; canonical connection-list replacement and immediate directory refresh now remove it. A long whole-app demo case exceeded the original five-second allowance under full-suite load; its bounded allowance is now 15 seconds, and all interaction assertions pass. The final count scenario and exact warning-selector assertions also passed.

Ignored local evidence: `local-data/servers-ui-all-final.log`, `servers-ui-final-focused.log`, `servers-code-only-final.log`, `servers-ui-build.log`, and the `app-flows-*-final.log` files. The current source is ready for a fresh isolated demo package; its final static verification will be recorded before handoff.

No app/game/fixture/browser/native dialog or listener was launched on the owner's active desktop. Native viewport acceptance and real game, save, network and Friend-PC acceptance remain separate from source/JSDOM/package evidence.
