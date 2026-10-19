# Quiet player checks

Owner request: routine occupied, pending and briefly unavailable player counts must not produce a stream of warnings, loading text or notifications. Only problems needing attention should interrupt the server workflow.

## Delivered behavior

- Stop remains disabled for positive, missing, stale or untrusted counts. Its reason stays available on demand in the button tooltip and access details. Routine Stop, Restart and Add time blockers no longer appear as standalone notices.
- Background refreshes keep a current count visible, or display a neutral dash when no current count is available. They do not animate the refresh button, announce every observation, or create a checking message in the directory, command header, lifecycle summary or connection summary. Explicit Refresh still shows progress in its own control.
- A repeated count problem has one dismissible troubleshooting note in Players, with an Attention destination. Recovery removes that note. Count-only Unknown observations do not claim a process identity failure or server readiness.
- Count changes and automatic countdown start/cancel events remain in activity history without notification badges, popup items or native alerts. The native recipient filters the same fixed event kinds from older paired Hosts. Explicit timer actions and actual automatic Stop results remain eligible for notifications.
- Actual access, identity, startup, Stop, save and confirmed port problems keep their feedback. Count-only evidence cannot suppress an unrelated action result. Freshness clocks advance when responses arrive so a newly received count does not briefly appear to be from the future.

No count authority, retry timing, lifecycle lock, Stop guard or countdown cancellation behavior changed. Unknown still cancels the active countdown. Saved added time remains intact where the existing lifecycle allows it.

## Demo and validation

The isolated fake-data demo includes Friend servers with players online and a background count check, plus initial/repeated count lookup scenes. It uses the existing inert transport and separate demo runtime; it never probes, changes or stops the running production app.

Validation on 2026-10-19:

- Complete JSDOM suite: **1,207 tests across 97 files passed**. After the final progress-text and dismissal fixes, all **30 targeted tests across three files passed**, including the added retry/dismissal regression. Occupied/missing counts disable Stop, new zero enables it only through existing Host permission flags, manual refresh has local progress, count history stays available, and unrelated explicit action failures stay visible.
- `scripts/verify-code-only.ps1` passed pinned tools, locked dependencies/restores, TypeScript, lint, solution compilation, all 54 source/synthetic groups, whitespace and diagnostic isolation. After the older-Host recipient filter was added, the final solution rebuild passed with **zero warnings/errors**, and all **54 source/synthetic groups passed again**. Synthetic cancellation checks retain added credits, remove unsafe deadlines and suppress duplicate history. Existing source-link subcases were skipped because this account cannot create symbolic links.
- Final UI TypeScript/bundle and lint passed. The existing main-chunk size advisory remains. Demo package PowerShell syntax passed.
- The checks caught missing current time at response receipt, a leftover unused notification variable, and older tests expecting live checking announcements. Those were fixed before the passing runs. Review also caught a duplicate Restart notice and overly broad suppression of unrelated failures; focused regressions now cover those boundaries.

Ignored local logs: `local-data/quiet-count-ui-all.log`, `quiet-count-final-focused.log`, `quiet-count-ui-lint.log`, `quiet-count-ui-build.log`, `quiet-count-code-only.log`, `quiet-count-final-build.log`, and `quiet-count-final-source.log`.

No app, game, fixture, browser, native dialog or listener was launched. Source/JSDOM evidence does not prove native viewport, real game, Friend-PC, public network or save acceptance; those checks remain for a separate approved test session. The worker used the configured inherited workhorse for bounded backend changes and a read-only review; shared builds, integration and commits stayed with the coordinator.
