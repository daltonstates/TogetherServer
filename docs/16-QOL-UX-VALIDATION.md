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
| New protected drafts, notification/update guards, backup catalog and scoped probe API journey | Passed on three successive isolated candidates | Runs 37891050308, 37894019360 and 37895269849; `checks/TogetherServer.CompanionChecks/QolApiJourney.cs` |
| New main React browser workflows, keyboard, recovery and viewport screenshots | First two complete journeys passed; setup stopped at a field lookup; later journeys pending | Run 37895269849; `ui/checks/qol-browser-smoke.mjs` |
| Native Quit draft receipt, placement, tray and fixture picker coverage | Native WebView rendered; debugger attach blocked further coverage; no completed native journey claimed | Run 37895269849; `checks/qol-desktop-smoke.ps1` |
| Expanded release gate and CI evidence retention | One immutable build shared with four separate Windows verification runners | `scripts/verify-release.ps1`, `.github/workflows/windows-ci.yml` |
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

## First expanded run and parallel follow-up

[Run 37889095349](https://github.com/daltonstates/TogetherServer/actions/runs/37889095349), branch head `78033880f1de690c22ba9c4e67a9c81f9ecca10a`, failed before the game and packaged lanes: **585 UI tests passed; 111 core groups passed and two failed**. `WorldLoadRehearsalChecks` reported a second trial Stop failure; `LiveSaveActionChecks` reported an interrupted signed copy not reviewable. Their generic failure messages did not establish a product cause. Those product paths were unchanged between the successful baseline and this run. The new checks now retain typed action/state diagnostics; the live-save overlap uses the existing source-scan hook instead of a timing sleep, and the crash case must prove its injected crash point was reached.

The failed candidate was `0.3.0+fa06ad4fe7d2c5b71d6efc21d7c656f12480ce4c`, SHA-256 `49E125AD53AFAE16D7313623EE89100EF027A3537B99AD97998EDF6D71B0DED9` (unsigned). The merge revision is the exact checkout built by GitHub Actions, distinct from the PR head. The redacted per-stage artifact was retained; no later stage is counted as passed.

Read-only cross-review also corrected the new journey's exact synthetic Valheim launch contract, scoped an ambiguous session selector, used the real unauthorized-log button label, selected a saved server at a visible wide viewport, and strengthened cancelled Restore with distinct current DB/FWL bytes. Both native draft cases now hold autosave until the native flush request, making success and storage rejection causally test the Quit handshake.

The next CI run builds one candidate once, then downloads those same bytes and fixture outputs onto **two separate standard Windows runners**. `Existing` runs the source/game/established package/desktop/update gate; `Qol` runs the new packaged API, bundled browser and native checks. Jobs have independent roots and outputs, and a failure in one does not cancel the other. Builds and package creation remain serial; all modes still require explicit interactive-test opt-in and owner-approved separate Windows environments. Default `Suite=All` retains the complete serial gate. Exact hashes and source identities from both evidence reports must agree before combined acceptance.

## First parallel run

[Run 37891050308](https://github.com/daltonstates/TogetherServer/actions/runs/37891050308), head `561be1d264c2c98e423d693677a328a8eeb77f99`, used one candidate `0.3.0+5dfaf3efac8c7c7fd9872c506fef0b47560edaeb`, SHA-256 `41F3A9B123B290EBAAC4EEF0715F3DC28C9CCF75DD722FA097A2D1930D2B19B4` (unsigned).

- `Qol`: **all 17 safe groups and the packaged API journey passed**, including protected editor/Host/Friend draft scopes and CAS, notification choices, update-disabled guards, independently measured backup evidence, selected-connection/run probe rejection, durable restart and the reconstructed legacy zero-ID connection. The later browser check failed when Alt+1 from Settings changed Friend mode to Host but stayed on Settings. Native checks were not reached.
- `Existing`: **585 UI tests, all 113 core groups, all game/setup/Shared History/update suites, Core remote journey and packaged world-load rehearsal passed**. The two earlier core failures did not recur. Shared Worlds then failed because the fixture requested a loopback candidate address; the production takeover proof correctly rejects loopback. Later stages were not reached.
- Fixes for the next candidate: guarded cross-mode navigation explicitly opens the requested Host/Join workspace and rejects superseded results; automatic public-IP detection waits for a known production Host instance and aborts/rejects stale results; Host shared-save reads abort on cleanup. The browser favicon is bundled. Expected transition denials must be identified by exact typed role codes and a recorded actual mode transition; unrelated HTTP errors remain failures.
- Shared Worlds and override-off fixture candidates now use an **actually assigned RFC1918 address on the isolated runner**, an explicit private bind and Advanced address route, plus exact signed endpoint/listener-owner assertions. No address is invented, no public/wildcard listener is enabled, and no firewall/router/DNS setting is changed. The helper refuses a runner without an eligible assigned private address.
- Post-fix local UI bundle/typecheck and **585/585 JSDOM tests passed again**. Complete solution compilation has zero warnings/errors. Runtime fixes await the next CI run.
- Read-only interim production comparisons matched the baseline process identity, executable version/hash and listeners. No local runtime check was launched.

The next run separates `Existing`, `QolApi`, `Browser` and `Desktop` onto four independent Windows runners using the same single build artifact. Each has its own evidence report and disposable roots. Native Quit/recovery/placement/picker coverage can therefore run while browser and established journeys are checked. `All` and `Qol` retain serial combined modes; every interactive mode keeps the foreground-safety guard. No new runner class, paid service or production listener is introduced.

## First four-lane run

[Run 37894019360](https://github.com/daltonstates/TogetherServer/actions/runs/37894019360), head `3e23ae241c66c8cddf35b17a5f0ccea18299cf7a`, uses candidate `0.3.0+5f5e9dd5e12f4b0c792bb5812e1613032d28ea9a`, SHA-256 `408CCE48B717BAF711ECF97BBE503B8508A6C78556CEC0B98E0BED46687FE28C`.

- `QolApi` passed completely again.
- `Browser` verified the previously failing Host shortcut and command-palette navigation. It then timed out at the exact Theme label on the visibly rendered Appearance controls. The artifact contains **zero unexpected HTTP failures or browser errors**; seven exact `FriendMode` denials were correlated with the real Host-to-Join transition, retained separately and deduplicated from Chromium's corresponding console messages. Static Appearance control names are now explicit; select lookups are being checked against their actual labels.
- `Desktop` stopped before launching an app: PowerShell 7 rejects catching its `HttpResponseException` after the base `HttpRequestException`. The redundant derived catch is removed. Windows PowerShell 5 source parsing had not resolved that PowerShell 7 type hierarchy; this failure is not recorded as native runtime coverage.
- At this checkpoint, `Existing` remained running. Its later result is recorded below; no pending stage was counted as passed.

Follow-up commits use a per-source-revision CI concurrency group so correcting browser/native checks does not cancel an older ongoing isolated acceptance job. Every run still builds one immutable candidate; final combined acceptance requires all lanes on the same candidate, with older results retained as diagnostic evidence.

## Second four-lane run and next corrections

[Run 37895269849](https://github.com/daltonstates/TogetherServer/actions/runs/37895269849), head `af18f1b59e971691a4eb6f17751a947989491666`, built candidate `0.3.0+88e576dc19ee75c004f701515c4c9821f75e0f39`, SHA-256 `B2C33C78A05F3ED69C5F29364CE14F0FA878C6FF13B8402DD86C4842E1370E09`.

- `QolApi` passed completely again.
- `Browser` passed keyboard/command-palette/appearance preferences and saved-server search, per-server tabs, favorites and durable ordering. Setup reached the actual reused world step, then timed out because an exact bare field lookup did not include the implicit label's helper text. The corrected lookup still independently asserts both secret/world fields are empty after reuse. Downstream guided-setting and raw-file lookups now match their real controls.
- Artifact review confirmed wide and narrow 150-percent text rendered, but the narrow header clipped controls: an older, more specific responsive rule showed the superseded mode switch beside the workspace navigation. The obsolete switch now stays hidden at every width, the compact Commands icon retains a name, and screenshot checks verify all three named header controls have visible dimensions within the viewport. Document-width checks alone had missed this defect.
- `Desktop` launched the native app and rendered its WebView, but CDP refused the debugging connection. [Microsoft documents](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security#for-an-elevated-host-app-use-appropriate-override-flags) that elevated WebView hosts ignore environment overrides and honor flags passed through the API. A narrow staging-only option now accepts only canonical high ports 49152–65535 and generates fixed loopback debugging arguments through that API. Production and invalid values return no arguments; pure regressions cover the boundary and malformed values. The disposable CI child receives the opt-in; no parent environment, policy, registry or token is changed. Before attaching, the native check validates the loopback port's exact WebView browser identity, process ancestry and isolated staging profile.
- The first four-lane run's `Existing` job eventually passed shared-save takeover/lineage and owner-override resolution, then failed after 43 Companion groups at a compound retained-log assertion following restart. Source review found a possible canonical-status race: terminal-operation polling can return before refreshed Host capabilities. The check now waits for canonical status and reports typed outcomes; the original failed conjunct remains unconfirmed. Product log-access authority is unchanged. The second run passed all **46 Companion groups**, including the original retained-log assertion, plus solution formatting. Its served smoke then found three raw backup checkboxes bypassing the shared control component; they now use the shared `Input` with the same attributes, labels and confirmations. The source guard is unchanged.
- Code-only compilation, all 17 pure/synthetic feature groups, **585/585 JSDOM tests**, TypeScript, lint and source syntax passed after the staging option and header fix. All local runtime/native/process/browser checks remain skipped. Browser mode-transition correlation now requires a snapshot requested after the confirmed mode response, tracks body-ready order, rejects stale prior-mode inference and records fixed causal diagnostics. Strict unexpected HTTP/JavaScript failures remain enforced.
- After the checkbox correction, all **11 existing backup catalog tests** passed and the UI bundle/typecheck passed. Source inspection identified one stale served-text expectation for the former UTC-input label; it now checks the implemented local-time input plus the retained exact UTC preview. The unchanged shared-control guard and all reviewed data-only served-bundle assertions passed locally without starting the served smoke, a listener or an app. Focused lint and check-source parsing also passed.

The second `Existing` job stopped at the shared-control guard; its later staging/desktop/update handoff checks and later new browser/native workflows remain pending. Older partial passes are retained as diagnostic evidence, not combined candidate acceptance.
