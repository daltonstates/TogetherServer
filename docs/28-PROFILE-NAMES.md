# Shared profile names

## Delivered behavior

Set **App Settings → Profile → Your name** once on this PC. Host and Join each
offer **Set your name** or **Edit profile** in their sidebar. The same name is
used for both roles. The app starts with no name and never reads the Windows
account name to fill it in. A blank saved name clears it.

The Host receives the Friend's name during pairing and authenticated heartbeats.
The Friend receives the Host's name after valid pairing and in authenticated
status. Existing paired connections receive edits on subsequent refreshes;
offline PCs receive them after reconnecting. No new code is needed. Approval,
revocation, access deadlines, pinned TLS and all existing permissions still
apply. Another Friend's name is not added to public status or server codes.

Friend PC cards lead with the person's name and retain the PC label underneath.
The saved Host picker and Join caption show the Host's name with the local saved
alias separately. Friend access search accepts the person, PC or assigned server
name. Existing labels remain the fallback when no shared name is available.
Names are self-reported display information, never identity verification.

New chat posts use the current person's name with the existing Host/PC fallback.
Already signed chat entries keep their original author and signature.

The profile editor keeps its unsaved draft across workspace changes, retains it
after failures, offers explicit Save/Discard and retries an unavailable initial
load. Saving temporarily guards navigation through the existing scope checks.

## Storage and transport

- The local name lives in `user-profile.protected` under the current instance's
  existing Windows-protected app data. Development and production remain
  separate. The existing update checkpoint includes this protected top-level file.
- `GET /api/local/profile` returns `{ displayName }`; `PUT /api/local/profile`
  accepts exactly `{ displayName: string }` and returns the typed saved result.
  Both require the exact local header, loopback address and matching local Host
  and port. GET rejects a body; PUT also checks same-origin JSON, bounds input to
  1024 bytes and rechecks update/shutdown availability under the mode gate.
  The profile belongs to both Host and Join; no Host settings mutation is needed.
- Names trim to at most 80 UTF-16 characters. Controls, formatting characters,
  line/paragraph separators and unpaired surrogates are rejected; valid emoji
  pairs are supported. Malformed remote names never enter a displayed profile.
- Additive `profileName` fields extend pairing activation and heartbeats.
  `hostProfileName` extends pairing credentials and companion status. They do
  not widen the API to arbitrary profile/settings mutations.
- Missing older-Friend name fields preserve the last saved Friend name;
  explicit empty strings clear it. New Friends always send the current name or
  an explicit empty string. Missing/null/empty Host names fall back to the local
  Host alias. Protected cached Host names survive restart and transport failure.
- Friend profile names are persisted only when changed, under the existing pairing
  gates and after current authorization and heartbeat replay checks. Name changes
  never rewrite local labels, credentials, assignments or grants.
- Names stay out of activity/audit messages and allowlisted support exports.
  They do not become game-player identities or influence occupancy or lifecycle.

## Validation and remaining acceptance

Focused UI validation: **87 passing JSDOM tests across seven files**, including
editor state/errors, strict names, searching and person/PC/alias fallbacks.

Final serialized verification on 2026-10-18:

| Check | Result |
| --- | --- |
| `scripts/verify-code-only.ps1` | PASS: pinned tools, locked dependencies, TypeScript, lint, solution compilation, all 43 source/synthetic groups and static checks. |
| Complete JSDOM suite | PASS: 1,085 tests across 90 files, including Host and Join profile navigation/save flows. |
| Final UI lint, typecheck and production bundle | PASS. The existing Vite warning for a JavaScript chunk over 500 kB remains. |
| Final solution build after UI bundling | PASS: zero warnings/errors; current UI assets compiled into the app. |
| Final source/synthetic rerun | PASS: all 43 groups, including the profile/cache regression check. |
| Read-only source review and whitespace | PASS; no actionable review findings. |
| Symbolic-link subcases | SKIPPED: this Windows account cannot create symbolic links. |

Logs remain under ignored `local-data/profile-info-20261018/`.

Source/synthetic checks cover protected persistence, exact local inputs, the
registered route's JSON response, pairing, heartbeat authorization/replay,
per-device isolation, fake HTTP exchange, clearing and offline cached names.
The route check executes synthetic request delegates without a server/listener.

The first compilation reported ASP0016 for a route overload that could discard
the save result. The route now uses an explicit typed handler, and its synthetic
registered-delegate check verifies the serialized result.
The initial full UI run passed 1,078 tests and failed five old navigation
expectations that omitted Profile. The updated menu expectations and two new
Host/Join profile flows pass in the final full run. Final review also corrected
a cache ordering change so an unchanged Host name cannot delay persisting the
current assigned-server list; its fake-HTTP regression check passes.

Rendered/native layouts and real separate-PC pairing, refresh, chat and network
acceptance remain unverified. They require an approved separate PC or unattended
Windows session. No app, browser, game, process fixture, listener, installer or
release journey is run on the owner's active desktop for this change. Production
data, running apps, worlds and public network settings are left untouched.
