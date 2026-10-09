# QoL and UX runtime validation

The owner authorized additional validation on 2026-10-09, conditional on preserving the currently running version. The owner explicitly selected a separate Windows CI runner. Interactive tests run only on the dedicated GitHub-hosted Windows runner with the existing explicit opt-in; the owner's active desktop uses source inspection, compilation and code-only checks.

Implementation baseline: `390da9a6c7cc0d52195e7b04fed8a7b0bed2196d`. The original 100-item implementation record and its 583 UI / 16 safe backend check results remain in [the delivery ledger](15-QOL-UX-DELIVERY.md). Implementation completion is separate from runtime and external acceptance.

## Isolation and authority

- Validation branch: `codex/qol-ux-validation-20261009`, [draft PR 1](https://github.com/daltonstates/TogetherServer/pull/1). Main, tags, GitHub Releases, deployment and the owner's installed/running application are unchanged.
- This public repository uses the existing standard `windows-latest` runner. No paid larger runner or cloud machine is created. [GitHub's runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners?productId=actions&versionId=free-pro-team%40latest) describes the public-repository standard-runner allowance.
- A read-only production baseline records the executable hash/version/PID and listener metadata in ignored local evidence. World data, credentials and production logs are not read by this validation work.
- Packaged checks use unique disposable roots, reviewed synthetic fixtures and instance-owned ports. Check runners observe the OS port tables; app/fixture processes own listeners. No game terms, game download, public firewall/router/DNS change or real-world overwrite is authorized.
- Browser coverage uses pinned Playwright as a build/test dependency and the runner's installed Microsoft Edge. No browser or app is launched on the owner's active desktop. It does not add a runtime dependency to TogetherServer. [Playwright's browser documentation](https://playwright.dev/docs/browsers) describes the installed `msedge` channel.

## Work and evidence

| Work | Current state | Evidence |
| --- | --- | --- |
| Existing complete packaged gate on baseline | Failed at outdated overloaded-method reflection check; fixed for next run | [Run 37886291239](https://github.com/daltonstates/TogetherServer/actions/runs/37886291239) |
| New protected drafts, notification/update guards, backup catalog and scoped probe API journey | Source complete; not executed yet | `checks/TogetherServer.CompanionChecks/QolApiJourney.cs` |
| New main React browser workflows, keyboard, recovery and viewport screenshots | Source syntax and lint passed; not executed yet | `ui/checks/qol-browser-smoke.mjs` |
| Native Quit draft receipt, placement, tray and fixture picker coverage | Source syntax verified; not executed yet | `checks/qol-desktop-smoke.ps1` |
| Expanded release gate and CI evidence retention | Integrated; next CI run pending | `scripts/verify-release.ps1`, `.github/workflows/windows-ci.yml` |
| Real Friend-PC/WAN/game/world-save acceptance | Not performed | Separate owner-installed binaries, test PCs/routes and copied-world acceptance required |

Three subagents and a read-only Codex CLI reviewer use the configured `gpt-6.1-sol` model with max reasoning. File ownership is explicit; compilation, integration, commits and pushes are serialized. Runtime results must be recorded from the exact CI run; authored checks are not passes.

The final validation report will list passes, failures, skips, exact source/candidate identity, screenshot artifacts and the before/after production metadata comparison. Synthetic network/process evidence cannot establish an actual Friend join, installed-game readiness or real save integrity.

## Findings addressed before expanded CI

- The baseline runner passed all 583 UI tests, 113 core groups and the Valheim, Factorio, Terraria, Minecraft, setup, Custom, Shared History and updater checks. It then failed because a name-only reflection lookup of `DesktopWindow.Notify` became ambiguous after the overload was added. The check now resolves both exact reviewed signatures.
- An ordinary protected draft write advanced the revision returned for every absent identity. Two editors read before either saved could therefore conflict despite separate scopes. New schema 2 separates revision allocation from the durable absence floor; schema 1 migration retains its final replay fence. Pruning and tombstone compaction still fence stale saves. Safe regressions cover interleaving, restart, migration, bounds and uncertain-write receipts.
- Pre-index saved Friend connections use the zero GUID. The new scoped chat helpers and draft validators rejected that actual saved identity. Zero is now valid only for chat compose and the exact selected saved link; assignment, room removal, equal-profile peer selection and null Host separation remain enforced.
- Initial notification preference loading omitted the required local-owner header, causing HTTP 403 and an incorrect initial quiet-mode projection. It now uses the guarded local GET helper.
- Canonical Friend states `Not connected` and `Update required` were missing from the tray summary allowlist, preventing newer Host counts from being projected. A data-only regression now checks both states and invalid metadata rejection.
- Required solution formatting was corrected without changing the affected files' behavior. Native title-bar Close checks allow the bounded draft handshake and HTTP shutdown to finish.

CI artifacts contain candidate identity, per-stage outcomes, redacted browser summaries and screenshots only. Disposable state, synthetic credentials, worlds, fixture binaries and protected drafts are excluded from uploads.

## Local follow-up checks

- `scripts/verify-code-only.ps1` passed after correcting three compilation errors in new check sources: the qualified activity-event type and two awaited hash comparisons. Complete solution compilation has zero warnings/errors; all 17 safe feature groups passed, including the new tray group and draft/legacy-chat regressions. The local account's synthetic symbolic-link subcases remain explicitly skipped.
- UI JSDOM: **585/585 passed across 63 files**, including two new legacy compose scope checks. TypeScript, zero-warning lint, locked dependency restores, Git whitespace and desktop diagnostic isolation passed.
- New PowerShell and Node check sources passed syntax checks without executing those scripts. The first solution formatting check found new check-file whitespace; that whitespace was corrected and the affected file was verified again.
- No local packaged app, browser, native window, file picker, game process, console fixture, installer or listener was started.
