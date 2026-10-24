# Remote hosting history, progress and original-owner Stop

Owner request, 2026-10-24: repair a same-network Host locally verification
failure; show meaningful progress; let a world's original owner stop its
successor-hosted server when empty; keep one sidebar entry under the original
owner; publish a new GitHub prerelease after validation.

Implementation uses an isolated checkout from `59a3728`. The original checkout's
29 connection-check/local-invite changes remain outside this work. No production
configuration, credentials, real worlds or network policy are changed.

## Signed history

A deterministic fake-HTTP regression reproduces `RosterRejected` when peer
catchup advances the local roster journal to schema 3 while an older Host's
root-history request is in flight. Root fallback now checks the retained signed
legacy interval rather than only the newest head. A previously superseded legacy
snapshot can be skipped only when that interval proves it older. Counter forks,
bad signatures, group/owner changes and missing schema-3 parents remain rejected.
The original failure is retained in ignored evidence. This reproduces a code
path matching the reported error; the owner's physical two-PC state is unobserved.

## Original-owner controls

Peer enrollment retains the exact successor-local profile ID separately from
the original signed world profile. Sharing reads resolve that binding before
loading the current pinned roster, including bounded recovery for an older
single-server authenticated peer connection. Current group, protected floor,
signature, key and membership checks remain required.

Only the proved original owner receives Stop authority for that exact hosted
world. Other enrolled members retain their configured permissions. Revocation,
owner access deadlines, assignment, remote-controls and maintenance checks still
apply. Remote Stop repeats two fresh trusted Ready/exact-zero driver observations
under the Host lifecycle gate immediately before the existing graceful signal.
Positive, Unknown, untrusted or changed-run evidence denies Stop. Local owner
Stop remains independent.

Enrollment and every later credential authorization also resolve the exact
stored signed branch against the pinned owner/profile/group and game/world.
A reused source profile ID cannot grant one world's owner control of another
world. Nonbranch sessions recheck the existing published source binding/current
authority without creating a missing or unreadable owner identity. Withdrawing
future candidate hosting permission preserves the original owner's guarded Stop
for an already-running world.

The hosting PC can deliberately enable remote controls using that verified
owner Stop permission. The setup eligibility check uses effective canonical
permission and only profiles retained by the proposed settings. Existing raw
Start/Stop invite setup remains supported; an unpaired invite still cannot act.
Hosting/network consent alone leaves remote controls off.

## Visible progress and one world entry

The local preparation and Start actions publish display-only progress through a
fixed local-header GET, scoped to the selected connection and world. Polling
does not start, prepare, synchronize or retry anything. Current phases explain
permission, receiving, file verification, latest-save selection, local copy,
game setup, ports, connection and Start. Received/total bytes provide the
transfer percentage; other work uses an indeterminate indicator and elapsed
time. Finishing transfer never claims all preparation completed. Cancellation,
late callbacks and connection changes cannot complete a newer operation.
The UI binds progress to the operation's start, ignores an earlier attempt even
when it updates late, and shows current completed/failed progress while waiting
for the mutation response. A progress result never enables or retries Start.

The passive directory exposes an opaque hash of the verified original
owner/profile/group, the original profile ID and a successor marker. The UI
groups only that signed identity, keeps the original row's name/person, and
marks the current remote Host. Its actual target remains the hosting PC's
connection and profile. Stopped successor connections stay retained for reuse
without leaving another ordinary sidebar row. Unrelated worlds with matching
names or profile IDs remain separate. Display metadata grants no control.

## Validation and acceptance

Coordinator captured the expected pre-fix history regression failure; the
signed-directory and progress source groups passed on that first build.
Focused source, complete code-only, UI, rendered, packaged and release results
are recorded in [the 0.3.3.8 release record](51-0.3.3.8-PRERELEASE.md) as they
complete. Builds, checks and commits are serialized by the coordinator.

Local validation uses pure/protected-synthetic/fake-adapter checks, JSDOM,
compilation and static package reads. Every app, game fixture, browser, console,
native, installer and update test runs only on separate hosted Windows runners.
The owner's active desktop is preserved. Hosted evidence cannot establish a
physical two-PC LAN/WAN transfer, actual game join, valued save integrity or
recognizable progress surviving game restart. Minecraft remains fixture-tested.
