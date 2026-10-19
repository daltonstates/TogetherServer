# Plain-language wording audit

Date: 2026-10-19

The owner requested a full audit of app wording because ordinary screens had
become too technical and verbose. This delivery simplifies visible copy across
Host and Join, including less common error, recovery, sharing, and setup states.

## Wording rules

- Say what happened and what the person can do next. Use short sentences.
- Use the words on the screen: server, server code, backup, shared save, and PC.
- Keep permission names and important consequences explicit. Distinguish an
  access end date from saved connection access, and a world backup from a
  complete setup backup.
- Give distinct controls distinct names. Ordinary errors show a useful message;
  structured error codes and decoder details remain available to app logic.
- Keep technical field names when needed to finish an advanced task, such as
  TCP/UDP ports, RCON, certificate fingerprints, and Custom script contracts.
- Describe only what the app has checked. A running process, open port, verified
  file copy, or elapsed timer does not prove a game join, saved progress, or a
  completed shutdown. Preview and simulation-only limits remain visible.

## Completed coverage

| Area | Copy reviewed and simplified |
| --- | --- |
| Main app and navigation | Host/Join actions, server tools, invitations, confirmations, access editors, updates, loading, and connection errors |
| Setup and settings | First use, game/world/server selection, support limits, imports, profiles, app/Windows preferences, shutdown settings, advanced controls, files, and add-ons |
| Playing and connections | Join guides, launch actions, game requirements, player counts and timestamps, count help, timer details, connection checks, and private connection details |
| Sharing and hosting | Receive and forwarding permissions, local consent, shared hosting setup, save histories, older recovery flow, solo recovery, transfers, peer connections, and chat |
| Backups and recovery | Naming/pinning, retention previews, verify/copy/restore/test actions, complete setup backups, safe restart, startup recovery, and damaged app data |
| Supporting tools | Attention/history, sessions and weekly summaries, diagnostics, recorded game tests, private live-save tests, and test-copy instructions |
| Windows and returned messages | Installer setup, native window errors, Host status/action messages, port checks, diagnostic descriptions, and live-save/connection test descriptions |

Examples:

| Before | After |
| --- | --- |
| Connection Doctor | Connection help |
| Save with checkpoint | Back up and save |
| Prepare rehearsal | Prepare connection test |
| Guarded Stop | Stop when empty |
| Recent authenticated heartbeat | Recently connected |
| Friend extension increment | Minutes added per request |
| Recovered app session | App reopened after an interruption |

Existing component and app-flow assertions track the new copy. Browser and
render-review selectors were updated without changing renderer case/variant
IDs. Their interactive journeys have not been executed for this delivery.

## Review corrections

Integration review corrected wording that could overstate game readiness,
confirmed process absence, or an active player check. It also kept local owner
Stop separate from Friend/automatic Stop, described saved port overlap without
claiming a port was currently occupied, directed Bedrock pack review through
Add-ons, and gave certificate and notification controls distinct labels.

No lifecycle, permission, pairing, network, save-storage, or API schema changes
are intended. The API error formatter now omits internal code prefixes and
decoder details from ordinary user messages while retaining structured codes,
HTTP status, and error causes for app logic.

## Validation and acceptance

| Check | Result |
| --- | --- |
| Full JSDOM UI suite, `npm test -- --maxWorkers=4` | PASS: 1,208 tests in 97 files, no failures or pending tests |
| Production UI bundle, `npm run build --ignore-scripts` | PASS: TypeScript and Vite; existing non-blocking chunk-size warning remains |
| `scripts/verify-code-only.ps1`, rerun after the final source changes and UI bundle | PASS: pinned tools, locked installs/restores, typecheck, lint, compilation with zero warnings/errors, 54 source/synthetic groups, whitespace, and desktop diagnostic isolation |
| Updated review harnesses | PASS: syntax checks for four JavaScript modules and the served-smoke PowerShell script; journeys not executed |

Earlier UI runs exposed stale wording assertions and ambiguous shortened labels;
the first full run had 73 failures. These were corrected while retaining the
permission, wrong-server, count, consent, and save-safety assertions. The final
full run passes all 1,208 tests.

The source gate reports one internal skip: synthetic symbolic-link cases could
not create links under this test account. Its process, fixture, browser,
listener, installer, and real-game journeys remain explicitly skipped.

Desktop/browser/installer smoke, update handoff, real games, Friend-PC/network
acceptance, and rendered viewport review are not run on the owner's active
desktop. They need an approved separate PC or unattended Windows session.
Production settings, credentials, worlds, and the running app are untouched.

## Production build handoff

Requested after the wording cleanup was committed. Built from
`76a4cb50330109ca9edf59d13efe2ac69efe20a1` with a fresh production UI bundle,
locked application restore, and self-contained Windows x64 single-file publish.

- EXE: `local-data/plain-language-production-20261019-76a4cb5/TogetherServer.exe`
- File version: `0.3.2.1`
- Product version: `0.3.2.1+76a4cb50330109ca9edf59d13efe2ac69efe20a1`
- Size: 66,029,530 bytes
- SHA-256: `ff2c057db6877db568a695cced64f24d69d063f1ae8ab9a72a26f7aa77c72d92`
- Authenticode: `NotSigned`

Static package verification passed: Windows GUI subsystem, bundled assembly
identical to the current build, and all seven current UI assets embedded
byte-for-byte. The verifier reads the EXE without loading or launching it.
`package-evidence.json`, `build-info.json`, and `TogetherServer.exe.sha256` sit
beside the executable. Binaries and local evidence remain outside Git.

The EXE was not launched or installed, and the running production app was not
replaced. Native, game, and Friend-network acceptance remains unverified.
