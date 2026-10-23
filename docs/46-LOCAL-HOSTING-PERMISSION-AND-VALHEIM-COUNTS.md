# Local hosting permission and Valheim count recovery

Owner report, 2026-10-23: preparing another PC's Valheim world through
**Host locally** fails despite successful hosting of a different local server.
The other PC also intermittently reports an unavailable player count, blocking
Friend Stop.

## Observed hosting blocker

Read-only diagnostics from the existing app confirmed a secure connected
Friend session with both apps reporting 0.3.3.5. A fresh permission lookup
returned `SharingPermissionUnknown`. Its protected cache had a matching signed
roster, owner pin and revision floor. The enrolled, non-revoked member had no
Receive or EligibleHost grant. No source group had been approved locally, no
receive consent was recorded and no local continuation had been prepared.

The signed denial was hidden behind the first-group approval check. A roster
without Receive permission intentionally does not approve its group, so retrying
could never change that result. Local game installation, capacity, port and
world preparation were not the observed blocker.

`FriendLink.LocalHosting` now verifies the roster, exact floor, owner, local
signing identity and history before reporting a missing hosting grant as
`HostingPermissionRequired`. The message identifies the original Host's
**Allow friends to host this server** setting. A positive result still requires
local group approval. No grant, group, consent or world is automatically changed.
An unavailable refresh without verified cached permission now reports
`RosterUnavailable`; valid signed offline grants and denials remain available.

On the original Host, enable **Backups > Sharing & hosting > Allow friends to
host this server**, keeping this PC approved and assigned. Ordinary remote
Start/Stop access does not grant shared-save hosting. A completed shared save
and all later preparation checks are still required. The diagnosis does not
establish that those later checks will pass.

## Valheim query recovery

The other PC's server was Offline during inspection, so its intermittent count
failure was not reproduced. Source inspection found that each local A2S query,
including the final serialized Stop recheck, gave a silent UDP exchange only
one attempt. The local query now retries silence once on the same socket,
retaining any received Steam challenge. One challenge response and one retry
bound the query to three exchanges. External game-endpoint probes retain their
existing single-attempt behavior.

Invalid count replies remain Unknown. Mandatory A2S_INFO fields must be complete
before a count is accepted. The owned-log reader captures a fixed bounded byte
window, requires complete lines and needs an occupancy baseline when reading
only a tail. After the last silent query, the non-Crossplay fallback reads the
log again rather than reusing its pre-query count. A new pending or completed
connection during that wait therefore changes the final evidence. Query silence,
incomplete logs, historical counts and Crossplay's unaccepted log fallback never
become zero. Remote and automatic Stop still require a fresh trusted zero inside
the lifecycle gate.

The challenge exchange follows Valve's [developer announcement](https://steamcommunity.com/discussions/forum/14/2974028351344359625/).
It does not establish real Valheim reliability or a particular saved-world result.

## Validation and remaining checks

The integrated code-only gate passed all 68 source/synthetic groups, locked
dependencies, pinned tools, matching versions, TypeScript, lint, solution
compilation with zero warnings/errors, whitespace and diagnostic isolation.
This working tree also contains two separate pending connection-check groups;
their pre-existing changes are preserved and excluded from this slice's commit.
The gate's existing symbolic-link subcase skips because this account cannot
create symbolic links. The getting-started corruption fixture emits its expected
diagnostics and passes.

The focused JSDOM run passed all 111 tests in `FriendLocalHosting.test.tsx` and
`LocalHostingGuide.test.tsx`. A read-only review after the full gate found the
pre-query fallback race described above. After correcting it, final solution
compilation passed with zero warnings/errors and all four focused source groups
passed: Valheim query recovery (including a join during the query), count
presentation, permission before preparation and local shared-hosting preparation.

Ignored validation logs are `local-data/local-hosting-valheim-code-only-20261023.log`,
`local-data/local-hosting-valheim-final-build-20261023.log` and
`local-data/local-hosting-valheim-final-checks-20261023.log`.

No app, game, fixture, browser, installer or native dialog is launched for these
checks. The existing app and real worlds stay in place. Real laptop
zero/positive/disconnect transitions, a final fresh-zero remote Stop, graceful
save/restart, separate-PC transfer and native rendering remain unverified.
Minecraft remains fixture-tested. No release, network-policy change or running
app replacement is part of this slice.
