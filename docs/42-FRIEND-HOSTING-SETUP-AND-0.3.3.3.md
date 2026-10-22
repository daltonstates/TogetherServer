# Friend hosting setup and 0.3.3.3 prerelease

Owner direction, 2026-10-22: correct the shared-hosting permission shown on a
Friend PC, retain the check list during retries, guide every setup problem,
show the checks without a disclosure, keep the Play action stable after closing
the dialog, and align the removed-access checkbox. Publish `v0.3.3.3` as a
GitHub prerelease after implementation and validation.

## Delivered interface

- Host locally keeps all five checks visible from its first read. Each check
  has its own icon, a status icon and text, and status color. The description
  and results have more padding.
- A retry retains the last results and check time. The displayed age updates
  each second; an interrupted check labels the old results and blocks Start
  until a valid recheck. A late response cannot revive an expired operation.
- The dialog reads its full status while open; the lightweight permission poll
  runs while it is closed. An unavailable sharing lookup keeps the action
  available for recovery. Verified permission withdrawal still cancels pending
  work and blocks hosting.
- A five-step guide selects the first unresolved check. Every check opens its
  relevant guide step and brings its heading into view. Automatic checks do
  not move keyboard focus. The guide covers owner approval and sharing, installed
  server files and terms, missing or conflicting saves, verification failures,
  game ports and incoming connections. Actions use fixed existing callbacks.
  Installation, terms, network changes and Start remain deliberate user actions.
- Show removed access keeps its checkbox and wrapping text on one clickable
  row. Its component rule now takes precedence over the shared sidebar label
  grid.

## Sharing repair

The backend investigation identified two related problems: unavailable or
unverified cached membership was presented as an owner permission denial, and
legacy standalone sharing snapshots could diverge from the retained signed
history.

New publication anchors the original signed snapshot immediately and appends
later changes as exact-parent schema-3 revisions. Existing legacy splits may
retain a strictly newer schema-2 snapshot only while the journal head is still
legacy, with the same signed owner, profile and group and both counters strictly
increasing. Exact previously trusted snapshots remain retained. After schema 3,
standalone replacements cannot advance the journal.

The companion roster read can resume after a bounded exact-hash header rather
than treating one PC's journal length as the other PC's offset. Legacy peers
retain the offset fallback. Peer metadata uses protected revision-to-position
lookup so retained legacy revision gaps cannot skip a later entry. Bounded
catch-up verifies complete pages before appending; wrong roots, invalid
signatures, equal-counter forks and schema-3 parent gaps still fail closed.

A full hosting-status retry refreshes pinned signed permission when connected.
When disconnected, a verified signed grant remains available without network
access; a missing, damaged or denied cache can retry the saved pinned Host.
Lightweight access polling retains its signed offline-grant behavior. Known
revocation, owner expiry and approval remain explicit blockers. Missing or
unverified sharing records are Unknown or need review, rather than claiming
that the owner turned sharing off.

A genuine rollback, damaged signature, revocation or conflicting history must
remain blocked; no floor is lowered to make a check pass.

## Validation checkpoint

The first focused component run passed 130 tests in FriendLocalHosting,
LocalHostingGuide, ServerDirectory and HostingMove. The final full UI suite
passes **1,410 tests in 108 files**. The first full UI run found
two demo expectations that required the selected file field to disappear; the
updated assertions require the chosen file to remain visible instead. A
guide-copy correction passed its focused rerun. A composed demo click now
waits for the actual enabled action after changing server selection.
The final explicit-guide focus polish passes **136 focused tests in four files**;
lint, TypeScript and the rebuilt production UI bundle also pass. That check
requires explicit row selection to focus and reveal its guide heading while
automatic selection and polling preserve focus.

The first source gate compiled with zero warnings/errors and passed 60 of 63
groups. Its three failures were retained: two fixtures still targeted old
journal/storage assumptions, and the new fresh-status Retry check required
the disconnected refresh path. After correction, an incremental full solution
build has zero warnings/errors and all five focused groups pass: server hosting
policy, legacy bootstrap/floors, permission before preparation, fixed hosting
Start/repair, and offline-owner peer catch-up. The next integrated run exposed
two offline regressions: a first incoming listener and a completed hosting move
attempted unnecessary network refresh. The final change preserves an already
verified offline grant. All three focused permission, preparation and exact
hosting-move groups pass without weakening their existing assertions. The final
integrated source run passes **63 of 63 groups**, with one synthetic symbolic-link
subcase skipped because this Windows account cannot create symbolic links.

The focused hosted renderer contains 20 cases across desktop, narrow and narrow
150% text: 60 plans. Its 34 pure fixture tests pass. New cases cover permission
unknown versus denied, all guide checks, a reopened action, preserved failed
retry results, successful recheck, and checkbox/text bounds and click behavior.

The immutable packaged candidate, hosted rendering, installer checks and
publication are pending at this checkpoint.
The production UI bundle and TypeScript check pass; Vite retains its existing
large-chunk advisory. Whitespace and desktop diagnostic isolation pass.
The normal .NET app build embeds that UI with zero warnings/errors.
No app, game, console fixture, native dialog, installer or browser automation
has been launched on the owner's active desktop.

The native window smoke's initial wait was shorter than the product's supported
startup deadline: approximately 10 seconds from process launch versus 15 seconds
after Window.Shown. The test now uses a 25-second initial wall-clock deadline
and retains the last window lifecycle fields. A terminal load failure still
fails immediately. Later chrome, taskbar, tray and geometry assertions retain
their existing scope and waits. Runtime verification is hosted-only.

## Acceptance boundaries

Source/JSDOM checks, synthetic hosted processes and rendered fixtures are
separate evidence. They do not establish a physical Friend-PC connection, WAN
reachability, a real game join, recognizable saved progress or game restart
integrity. Those remain external acceptance. Minecraft remains fixture-tested.
Release assets remain unsigned unless a validated signature is explicitly
recorded. Keep source, workflow/run/artifact identity and exact asset hashes
with the final publication record; never rebuild frozen release bytes to
complete a later check.
