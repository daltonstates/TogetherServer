# Seamless world access, server removal and rejoining

Owner follow-up, 2026-10-24: do not show the automatically created shared-world
session as another user; keep existing server permissions; make ordinary access
removal specific to that server rather than a ban; investigate the laptop's
continuing signed-history verification failure. Both PCs run 0.3.3.8 and the
owner believes the laptop used the invitation code again after an older revoke.

Work is isolated from `60236d0` in `TogetherServer-rejoin-0339`. The original
checkout's 29 pending connection-help/local-invite changes are backed up and
remain outside these commits. No production configuration, credentials, logs,
worlds or running application is used for a test.

## Internal world-session access

The additional card was a world-session transport credential. The original
world owner receives guarded Stop for the exact hosted copy; it is separate
from ordinary code-paired credentials and their per-server helper permissions.
Matching display names are not proof that those credentials are interchangeable.

`OwnerPcViews()` excludes those internal records from the owner's normal Friend
PC list. Per-room chat participation controls retain their exact principals so
the owner can still remove or restore a person's access to that room.
Full `Views()`, stored credentials and
companion status remain intact: hiding an internal record cannot remove the
owner's authenticated profiles or change Stop, player-count, controls or
membership gates. No credential is deleted, merged by name or given another
server's helper rights. Ordinary PCs keep their settings.

## Remove access to one server

The ordinary Remove action captures one PC and one server and uses an atomic
loopback-only DELETE route. It accepts no body/query and requires the local
header and Host mode. Removing A preserves B's assignments, permissions,
credentials, renewal state and helper settings. It does not reset defaults or
send a stale replacement assignment list. Existing objects are reauthorized
against current saved scope. A fresh approved server-code pairing may grant
access again; it never reactivates the old removed scope implicitly.

Only the target locally owned roster is fenced for publication. Removing a
local assignment to inherited authority does not rewrite the original owner's
signed proof; valid and damaged inherited authority fixtures retain their exact
roster and protected authority bytes. Internal session IDs are refused by the
ordinary-PC removal route.

Server-originated access management opens on that server; global access opens
on All servers. An explicitly chosen All servers filter survives refreshes.
The UI checks fresh device/server state and pending edits before the scoped
action. The wider credential revoke remains separately named Remove all server
access. It is not a hardware/person blacklist, and a new approved invitation can
still establish access.

## Signed-history investigation boundary

Natural revocation did not reproduce the laptop failure. The final synthetic
group exercises eight variants through actual `FriendLink.PairAsync`, TS3 code
decoding and real `PairingService.Activate` over fake HTTP: old record retained
or deleted, new saved connection or the same KnownRevoked connection, and
legacy standalone snapshots or current signed Host journals. New credentials
and signing keys must retain and catch up the exact original history; old
credentials remain denied. Seven deliberately damaged-history cases retain
signature, parent, fork, root and protected-floor rejection.

Four further composed variants use the actual FriendService and WorldPeerService
with real signed challenge/metadata exchange over fake HTTP. They retain and
reopen the old registry, signer and cursor across revoke/rejoin, and require the
new durable self identity plus actual fresh peer exchange while the old identity
cannot obtain metadata. They cover legacy bootstrap or a preexisting Host journal
and old records retained or deleted. All twelve natural variants and seven
damaged-history variants pass. These source checks still do not establish the
cause on the physical laptop.

The error now adds one fixed readable verification step: this PC's saved
history, the Host's received revisions, joining signed ancestry, or the final
trusted record. It emits no raw exception, path, address, key, signature or
credential. `RosterRejected` and every trust predicate remain unchanged.
This is diagnostic work, not a claimed repair of the owner's unobserved laptop
state. The extended message from a laptop retry is still needed to identify
that failure. No history, trust floor or world data is cleared to hide it.

## Validation and delivery

Focused UI: 12 Friend PC settings flow cases, TypeScript and focused lint pass.
The first source check exposed an unrealistic inherited fixture consisting only
of profile flags. The fixture now creates actual signed Planned authority,
successor proof and protected local binding; preservation assertions remain.
The first full UI run retained four timeouts during concurrent compilation.
A repeat with two workers passes all 1,476 tests in 109 files with unchanged
assertions. App/UI/lockfile versions advance together to 0.3.3.9; dependencies
are unchanged. Full solution compilation has zero warnings/errors. The local
Windows PowerShell 5 wrapper stops on an intentional guide-write-failure stderr
message; the native runner is continued with captured stdout/stderr and checked
exit status. The initial composed fixture also rejected a loopback peer route
and used an inaccurate deleted-credential HTTP status; those test-only setups
now match the real route/authentication contracts. The remaining 69 groups pass
in the full source run, and both final focused groups pass after the fixture
corrections. A full clean hosted source gate remains required below.

Full solution formatting passes after building the required UI assets and
correcting whitespace in the new test fixture. All 48 pure renderer tests pass.
The focused access review pins 12 cases across three desktop/narrow/150-percent
variants (36 planned captures). Its Windows job consumes the candidate's actual
embedded UI bytes, checks every asset before and after capture, and never
rebuilds or launches the candidate.

All process, browser, native, fixture, installer and update execution belongs to
separate explicitly opted-in hosted Windows runners. Local checks are source,
fake/protected synthetic data, JSDOM, compilation and static package inspection.
Real laptop/network/game/save acceptance remains separate and unverified.
