# Lightweight local pairing and shared-save lab

2026-10-20. The owner permits focus changes for this lab if test windows stay
off the main display, preferring the left monitor with VS Code. The read-only
monitor survey found VS Code on the left 1920x1080 display. This implementation
uses no test windows at all; it does not widen permission for desktop smoke,
installer journeys, real game processes, or the full release gate.

## Run

From the repository, run:

```powershell
powershell -NoProfile -File scripts/run-local-pairing-lab.ps1 -Run
```

Without `-Run`, the script only describes the lab. The launcher builds the current
guarded app, harness and pipe-only fixture. It never passes a lab flag to an old
production EXE, which could ignore that flag and allocate its ordinary console.
Existing pinned tools and locked restored dependencies are prerequisites.

One Host and two Friends use separate disposable data directories and credentials,
real pinned HTTPS on loopback, signed chat, and seeded synthetic save bytes.
Reports are written under ignored `local-data/local-pairing-lab/run-*/report.json`.
They contain stage outcomes, process/run IDs, timings, resource samples, cleanup
and window observations. Raw app output, invite codes, credentials, passwords,
and chat payloads are not copied into the report. Private protected test state
stays in the disposable run folder.

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
- Incoming companion binds and pinned outgoing requests are loopback-only;
  redirects and proxy use are disabled in lab clients. General Internet clients
  refuse requests. Normal production clients and route gates keep their behavior.
- Native window observation samples owned app/fixture top-level windows every
  250 ms. Any observed visible window fails the lab and stops its contained
  processes. This sampled evidence is combined with the explicit no-window code
  path; it does not claim continuous observation of every Windows system window.
- Each fresh app enters an unnamed Windows Job before any API command. Children
  inherit membership, no breakaway is enabled, and closing the harness kills
  remaining members. Cleanup checks zero active members. The fault stage leaves
  a fixture out of normal tracking, terminates its exact Host, verifies the child
  is still alive, then proves job cleanup removes it. See [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
- Fixture health remains `Process running`, with unknown occupancy. Local owner
  named-pipe Stop is used; no trusted player count, real readiness, real save
  completion, game join or game-world integrity is fabricated.

## Validation

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
