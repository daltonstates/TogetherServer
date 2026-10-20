# Lightweight local pairing and shared-save lab

2026-10-20. The owner permits focus changes for this lab if test windows stay
off the main display, preferring the left monitor with VS Code. The read-only
monitor survey found VS Code on the left 1920x1080 display. This implementation
uses no test windows at all; it does not widen permission for desktop smoke,
installer journeys, real game processes, or the full release gate.

## Run

From the repository, run:

```powershell
powershell -NoProfile -File scripts/run-local-pairing-lab.ps1 -Run -Ux
```

This is the owner's requested automated local UX test. Two copies of the actual
app use separate disposable data and one contained headless Chromium browser
with separate browser contexts. The runner clicks through both bundled interfaces:
first-use setup, server-code pairing and approval, sharing permissions and local
consent, chat and mute, safe fixture Stop and verified transfer, actionable
connection repair, guided hosting, signed new-Host discovery and Connect, a narrow
layout, and final local Stop. App responses are never mocked or substituted. It
requires no VM, second PC, app installation, or manual app clicks.

The launcher selects the single assigned private IPv4 address. Use
`-LanAddress <assigned-private-IPv4>` if more than one exists. It reads existing
firewall permission and refuses before launch if a native permission prompt
cannot be excluded; it never creates or changes a rule. Real pinned HTTPS uses
that one address on this PC. This exercises both ends of the protocol, not a
packet crossing a physical network or an outside connection.

Without `-Run`, the script only describes the lab. The launcher builds the current
UI, guarded app, harness and pipe-only fixture. It never passes a lab flag to an
old production EXE, which could ignore it and allocate its ordinary console.
Existing pinned tools, locked dependencies, and the pinned Playwright Chromium
headless shell are prerequisites. There is no headed-browser fallback. Audio is
muted by the browser even while its UI sound preferences are exercised.

Optional protocol-focused modes retain one Host and two Friends: omit `-Ux` for
the loopback blocker rehearsal, or pass `-LanAddress` without `-Ux` for the
same-PC LAN-address API journey. These are separate from the two-interface UX run.
Reports are written under ignored `local-data/local-pairing-lab/run-*/report.json`.
They contain stage outcomes, process/run IDs, timings, resource samples, cleanup
and window observations. UX results and masked screenshots are in `ux/` beside
the report. Raw app output, invite codes, credentials and passwords are not copied
into the report. Private protected test state stays in the disposable run folder.

## Boundaries

- `--local-pairing-lab` requires explicit staging, one Host/Friend mode, a local
  port, and disposable roots beneath this repository's lab folder. Mixed native,
  demo, startup, update and installer arguments fail before native dispatch.
- Lab startup skips console preparation, WebView, tray and power requests. It
  disables Windows desktop crash-restart registration and native error dialogs.
  The API allowlist excludes shell/file opening, browsing, installation,
  discovery, external route checks and arbitrary endpoint recovery.
- Only the pipe-only fixture driver is registered. The canonical fixture EXE,
  DLL, dependency manifest and runtime manifest are hash-bound in the lab marker
  and revalidated before a run. Linked or outside save/binary paths are refused.
- Incoming companion binds and pinned outgoing requests allow loopback and,
  only when explicitly selected, one canonical RFC1918 IPv4 address currently
  assigned to an active local adapter. An entire subnet, another PC, public IP,
  hostname, redirect or proxy is never admitted. The address is bound into the
  disposable marker and rechecked before selected-address traffic. General
  Internet clients refuse requests. Production transport defaults are unchanged.
- Native window observation samples top-level windows belonging to every Windows
  Job member every 250 ms, including Node, browser and fixture descendants. Any
  observed visible window fails the lab and stops its contained processes.
  This combines sampled evidence with explicit windowless startup; it does not
  claim continuous observation of every Windows system window.
- Each fresh app enters an unnamed Windows Job before any API command. Children
  inherit membership, no breakaway is enabled, and closing the harness kills
  remaining members. Cleanup checks zero active members. The optional protocol
  fault stage leaves a fixture out of normal tracking, terminates its exact Host,
  verifies the child is alive, then proves job cleanup removes it. See
  [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
- Fixture health remains `Process running`, with unknown occupancy. Local owner
  named-pipe Stop is used; no trusted player count, real readiness, real save
  completion, game join or game-world integrity is fabricated.

## Automated local UX acceptance

PASS the complete launcher on 2026-10-20, run
`71a9b253-c3f2-41d6-95b7-edf9250b0d65`: **eight UX stages passed**, plus
isolated startup and cleanup; zero failed or skipped stages. Evidence:
`local-data/local-pairing-lab/run-71a9b253c3f241d695b7edf9250b0d65/` and
`local-data/automated-local-ux-accepted-launcher.log`.

The UI verified one successful guided Start request after the disabled-listener
repair and no duplicate Start from polling or Connect. The original app learned
the exact authorized signed world root, candidate, branch, managed run and route;
its explicit Connect selected that session and authenticated to the exact new
profile/endpoint. Both chat directions delivered, sound preferences were toggled
on and off, and candidate Stop completed. There were no JavaScript errors,
failed HTTP requests, external browser requests, downloads or native dialogs.
One aborted read-only peer poll during navigation is recorded separately.

The run took **41.318 seconds**, excluding compilation. Peak summed working set
for every job member was **1,116,016,640 bytes (1,064.32 MiB)**, with at most
12 processes including browser helpers. This includes shared pages more than once
and excludes build tools and the harness; it is an observed run, not a memory
cap. All 125 window observations saw zero visible owned windows. Final job
membership and tracked working set were zero.

The rehearsal exposed and fixed actual first-use defects:

- An explicit incoming-connections choice can now be saved before the first
  invite/candidate. The real listener stays Idle until its independent signed
  membership/pairing and TLS gates pass. Full-settings false-to-true changes still
  require pairing; later validated saves preserve the explicit choice. Remote
  Start/Stop permission remains a separate gate.
- Guided hosting creates its TLS identity and exact signed copy before requiring
  an active owned listener. The explicit hosting instruction registers its signed
  group before listener activation; failures preserve the save and remove Start
  readiness. Incoming-connection consent and route/firewall settings stay under
  owner control.
- The first managed run advertises its locally signed branch/session before its
  first post-Stop backup. Received payload sharing keeps its existing grants.
  The original Host overview now shows that report and an explicit Connect
  action. A fixture remains Starting/Preparing, never game-ready.
- Host PC settings show save failures and success beside their controls.

File-only policy/preparation regressions, 62 source/synthetic groups, 138 focused
UI tests across AppFlows and SharedHostNotice, lint, TypeScript/UI bundling, and
current app/harness/fixture builds passed. Logs include
`local-data/automated-local-ux-source-checks-7.log`,
`local-data/automated-local-ux-final-ui-tests.log` and
`local-data/automated-local-ux-final-lint.log`.
The final launcher builds reported zero .NET warnings/errors; the existing UI
large-chunk warning remains. Earlier failed rehearsals and their cleanup reports
were retained. Representative real chat, connection-repair, new-Host and narrow
screens were inspected; this is not a review of every app screen.

This accepts the local two-ended UX and real pinned transport between isolated
processes on one PC. It does not accept a physical second-PC/network path, WAN
reachability, native installer/window behavior, real-game readiness/join, actual
Valheim password reveal/change, or loadable world/save/restart integrity. The
fixture contains only synthetic bytes and reports unknown players.

## Earlier loopback protocol validation

The measurements and two skips below belong to the original loopback-only
rehearsal. They do not describe the larger browser UX run or accept its later
hosting/Connect path.

- PASS solution compilation, zero warnings/errors, and 62 source/synthetic groups.
  Logs: `local-data/local-pairing-lab-ready-build.log` and
  `local-data/local-pairing-lab-ready-policies.log`.
- PASS the launcher end to end, run `dda7fdc5-fd55-4afd-9a49-7eac3f565db7`:
  **10 stages passed, zero failed, two skipped**. Report:
  `local-data/local-pairing-lab/run-dda7fdc5fd554afd9a497eac3f565db7/report.json`.
  Log: `local-data/local-pairing-lab-final-run.log`.
  The same run folder's `verified-inputs.json` retains report, app assembly,
  apphost and composite fixture hashes for this accepted execution.
- The passing stages cover independent app/listener identities and paired
  credentials, separate receive/hosting grants and local consent, signed chat
  synchronization/removal, two exact copied payloads with signed receipts,
  independent password-permission grant/revoke, the guided connection blocker
  without launching, explicit local-copy hosting/pipe Stop, lost-response cleanup,
  and final cleanup. Ordinary loopback route refusal is preserved.
- The rehearsal took **6.835 seconds** excluding compilation. Peak summed
  working set for tracked apps/fixtures was **402,481,152 bytes (383.84 MiB)**,
  with at most four simultaneously tracked processes. Build tools, the harness,
  shared-page accounting and an intentionally untracked cleanup-fault child are
  outside that memory figure. This is one measured run, not a hard resource cap.
  Retained data for that run was approximately 121 KB, excluding existing build
  outputs/tools. The 24 window samples observed zero visible owned windows;
  cleanup reported zero active job members and zero remaining tracked memory.
- Initial failed rehearsals were retained: premature listener enablement,
  missing explicit password scope, and process-exit waits that incorrectly waited
  for a child-held redirected log pipe. The fixture-only sequence was corrected;
  cleanup now checks process termination independently from redirected EOF.
- SKIP actual Valheim password reveal/change: the fixture has no Valheim password
  source. Permission isolation and unsupported-source refusal are checked; source
  tests cover the protected real password path separately.
- SKIP successful guided Start with new-Host announcement/Connect: ordinary peer
  routing refuses loopback as an incoming Host route. The lab tests its actionable
  blocker and hosts a separate synthetic copy through the explicit local action.
  Real permitted two-PC routing, rendered UI, native installer, game join,
  recognizable save/load/Stop/restart, and WAN acceptance remain separate.

The production app is outside the job and lab data roots. Its process identity,
executable path/hash and continued running state are checked separately; the lab
does not deploy or replace it. Workers used the configured gpt-6.1-sol model for
bounded scenario implementation and read-only safety review; integration,
compilation, execution and final evidence remain coordinator-owned.
