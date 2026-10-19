# Simple shared hosting delivery

Owner request, 2026-10-18: sharing should be one permission and a local-hosting
action beside Start, with setup and save checks handled by the app. Delivery
continued on 2026-10-19.

## Everyday flow

- Host: open the selected world's Sharing & hosting page and set **Can host
  this save** for an approved PC. This grants Receive and EligibleHost together
  and enables the required post-Stop backup/save sharing settings. Other grants,
  retention/reserve settings, crash recovery and listener policy are preserved.
- Friend: **Host on this PC**, beside Start, opens one preparation dialog. The
  Friend consents to keeping the save and to preserving separate progress if
  another Host is running. Preparation finds a matching installed server, pulls
  or reuses a completed save, verifies signed history/file hashes, selects a
  driver-declared local port plan, creates a fresh managed local world and
  finishes existing setup checks. It never starts a game.
- **Start server on this PC** is a separate explicit action. Later Host Start
  and Resume actions use the same warned, signed separate-copy Start gate.
  Existing local progress is reused and never replaced by a newer source save.
- Missing software and matching Minecraft/add-on setup appear inside the same
  dialog. The exact prepared branch is selected automatically and known setup
  values are filled in. Valheim offers the existing fixed Steam installation
  link. Terms, installation and unsupported/mismatched setup remain explicit
  owner actions. No executable is downloaded by preparation.
- A configured public connection can receive the existing read-only outside
  IPv4/TCP check during explicit preparation. Listener off, loopback/private
  routes, address uncertainty, closed TCP and inconclusive checks remain
  distinct. Opening/status polling never probes the outside checker. Local
  hosting remains possible without an incoming connection; inviting others
  still requires deliberately configuring this PC's connection.

Copy management, forwarding, legacy recovery voting, planned transfers and
history remain available under advanced sharing. The ordinary sharing page
leads with the hosting permission/action. Closed Friend advanced details do not
mount their old polling workspace.

## Safety and implementation boundaries

The owner mutation validates canonical access, assignment, original-owner/source
binding and recovery/restore/handoff restrictions under the existing lifecycle
and shared-world gates. It merges prerequisites into current settings. Grant
withdrawal signs the current roster even while payload sharing is paused;
pending enrollment remains explicit. Settings and protected pairing snapshots
are separate durable files: an interrupted grant save may leave backup/sharing
enabled, but never authorizes a new unsigned member.

Local hosting permission comes from the verified signed roster, not remote
Start permission or a remembered capability string. It works from an approved
local copy after an offline app restart. Explicit revocation and signed owner
access deadlines fail closed; remote disconnection and renewable credential
expiry do not by themselves revoke a signed local hosting grant. Offline PCs
cannot learn a new revocation until authenticated metadata reaches them.

Passive hosting status reads bounded signed metadata and protected preparation
state. It does not repeatedly hash world payloads. Explicit Prepare and Start
perform current verification. UI request generations, cancellation and bounded
status-read freshness prevent old connections or stalled reads from leaving
Start actionable. Multiple local histories require a deliberate history choice;
hash equality or timestamps never silently merge competing progress.

The preparation endpoint accepts only consent/warning choices, an optional local
installed file and optional new game password. The Host Start wrapper accepts
only the exact branch hash and warning choice for a saved local profile. Both
retain loopback/header/mode/update/body gates and expose no remote command,
script, arbitrary probe target or world path. Valheim receives a new protected
local password rather than a copied Host secret.

No public listener is enabled, and no firewall/router/DNS configuration changes.
Outside TCP evidence is bound to the unchanged configured endpoint, port, bind,
listener ownership and certificate. A historical second-PC proof cannot mask a
stopped/changed current listener. Neither result certifies game reachability,
game load, a particular Friend join, saved progress or restart integrity.

## Verification

Final serialized validation on 2026-10-19:

| Check | Result |
| --- | --- |
| `scripts/verify-code-only.ps1` | PASS: pinned tools, locked dependency install/restore, typecheck, lint, solution compilation, 51 source/synthetic groups, whitespace and desktop diagnostic isolation |
| Complete JSDOM suite | PASS: 1,151 tests in 92 files |
| Final guided flow, exact manual setup and shell regressions | PASS: 125 tests in three files after adding the direct connection-fix action |
| Production React bundle | PASS: existing advisory for a JavaScript chunk over 500 kB |
| Normal app compilation embedding the current UI | PASS: zero warnings/errors |

One symbolic-link source subcase is SKIPPED because this account cannot create
that link. Source checks use disposable files, protected state and adapters that
refuse process launches; route checks use injected fake HTTP/probe adapters. UI
checks use JSDOM. No app, game, fixture, browser, listener or native-dialog
journey was launched. The running production app, real game installations/worlds
and network policy were untouched.

Initial compilation exposed a manifest-size integer-pattern mismatch, a
nullable signature call and a missing test clock argument; all were corrected.
A new process-safety assertion initially counted Offline display cards as runs.
It now checks durable managed runs, and the service also excludes Offline cards
from active-history selection. A failed explicit preparation now persists its
withdrawn readiness so later passive reads cannot revive that known failed
check. These regressions pass in the final source gate.

Evidence logs are ignored local files:
`local-data/local-hosting-code-only-final.log`,
`local-data/local-hosting-ui-all.log`,
`local-data/local-hosting-ui-final-focused.log`,
`local-data/local-hosting-ui-build.log`, and
`local-data/local-hosting-bundled-app-build.log`.

Rendered/native viewport, packaged process journeys, separate-PC transfer,
actual incoming routes and real game load/change/Stop/restart remain unverified.
They require an approved separate PC or unattended Windows session. No real
installation's live-save acceptance changes in this slice.
