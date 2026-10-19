# Per-PC prerelease updates

## Authorized behavior

On 2026-10-18 the owner requested a checkbox for prerelease enrollment and kept
the existing explicit **Update and restart** installation flow. Preview builds
use the normal production installation, data, pairings, worlds and servers.
The checkbox is independent of the isolated development/staging app.

**App Settings > App updates > Include prerelease versions** defaults off on
new and existing PCs. It persists in the existing `update-ui.json` beside
reminders and release notes. Changing a reminder or receiving notes preserves
enrollment. Each PC opts in independently; Host and Join use the same setting.
The UI shows save progress and errors and reloads canonical update state.
Changing the checkbox performs a metadata check, not a download or installation.

## Selection and safety

- Stable PCs keep the fixed GitHub `/releases/latest` endpoint and reject draft
  and prerelease metadata.
- Opted-in PCs use the fixed `/releases?per_page=100` endpoint and choose the
  highest eligible numeric version among the 100 most recently created releases,
  including stable releases and previews. Feed order is not version order.
- Both paths retain the exact repository/asset URL, uploaded state, size, digest
  and numeric tag validation. Metadata has a bounded response size (1 MiB stable,
  4 MiB preview). Drafts, invalid entries and suffix-bearing tags are ineligible.
- The available version is explicitly identified when it is a prerelease.
- Changing enrollment invalidates the cached candidate, prepared download
  reference, preparation display and automatic-check cache before rechecking.
  A save failure preserves the previous choice. A failed metadata check keeps
  the saved choice but offers no newly prepared update.
- A newer installed preview is never downgraded when its checkbox is cleared.
  It receives another update once a higher eligible stable version is available.
- Download/version/hash checks, optional same-publisher verification, the state
  checkpoint, previous-EXE recovery and active/unresolved-server blockers remain
  the existing manual installation path. Staging updates remain disabled.
- `PUT /api/local/update/preferences` accepts exactly one boolean
  `includePrereleases`, rejects unknown/duplicate fields and oversized input,
  requires the owner-local header, rejects query parameters, and shares the
  existing mode/update gate for the short save. An update check or download
  already holding the updater gate returns a retryable conflict instead of
  waiting. The subsequent GitHub refresh runs outside the mode gate so it
  cannot delay Host Stop or other guarded controls. Both Host and Join may
  change their local setting.

No companion protocol or storage schema change is introduced by this slice.
It adds no listener, credential exchange, network setting, background installer
or world operation. Compatibility with currently released Host/Friend apps and
their production data remains a requirement for each future preview candidate;
this checkbox alone does not establish that acceptance.

## Publishing procedure

1. Obtain explicit owner authorization before creating tags or releases.
2. Prepare and verify a normal production artifact with a higher numeric
   `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH.REVISION` tag. Retain the normal
   asset names and checksums. The first authorized preview is `v0.3.2.1`;
   its fourth numeric component is retained throughout update verification.
3. Publish the GitHub release with **This is a pre-release** selected. Only
   enrolled PCs with a compatible updater are offered it. The published 0.3.2
   updater accepts only three-part tags, so 0.3.2.1 needs one direct installation
   even if that release is later promoted to stable. This installation also
   supplies the enrollment checkbox for subsequent previews.
4. Give each subsequent preview a higher numeric version. Never replace tested
   assets under an existing tag to deliver another build.
5. When the exact candidate passes acceptance, clear the prerelease designation
   and mark that release latest without changing its assets. Stable PCs with a
   compatible updater are then offered those same tested bytes. Preview PCs
   already running them need no reinstall just because the release was promoted.

The [0.3.2.1 prerelease record](31-0.3.2.1-PRERELEASE.md) tracks the separately
authorized publication, four-part version support and exact-candidate acceptance.

## Validation and remaining acceptance

Implementation and focus-safe source verification are complete on 2026-10-18.

| Check | Result |
| --- | --- |
| Focused Settings/update/app-flow JSDOM checks | PASS 126 tests across three files. |
| Complete JSDOM suite | PASS 1,115 tests across 91 files. |
| UI TypeScript, ESLint and production bundle | PASS. Existing Vite warning for a JavaScript chunk over 500 kB remains. |
| `scripts/verify-code-only.ps1` | PASS locked restore, complete solution compilation and all 47 source/synthetic groups; zero .NET warnings/errors. |
| Focused .NET whitespace verification | PASS for all five changed C# files. |
| Git whitespace and desktop diagnostic isolation | PASS. |

The existing symbolic-link source subcase is SKIPPED because this Windows
account cannot create symbolic links. This is not a symlink acceptance pass.
The final solution compilation embeds the freshly built UI assets; it does
not launch, publish, install or replace an app.

Initial checks found and corrected two test setup errors: an awaited byte-array
comparison retained a span across its await, and the restart check reopened an
exclusive synthetic `LocalData` store before disposing the first instance.
The final formatting check passed after object-initializer whitespace fixes.
Read-only review also led to separating GitHub refresh from the mode gate and
preserving the last known prerelease designation after a failed refresh; both
behaviors have passing synthetic coverage.

Evidence is in the ignored `local-data/prerelease-ui-focused.log`,
`prerelease-ui-all.log`, `prerelease-ui-build.log`,
`prerelease-code-only-verified.log` and `prerelease-format-verified.log`.

The new source/synthetic checks cover enrollment persistence and default-off
migration, per-device separation, fixed input validation, failed save, stable vs
preview selection, feed order, promotion, no downgrade or enrollment side effect,
trust constraints, a verified prepared preview being invalidated on opt-out,
digest/publisher rejection, disabled staging and metadata failure. Payload bytes
come from a built managed PE and are never executed.

The following paragraphs record the original code-only feature delivery. The
separately authorized [0.3.2.1 prerelease](31-0.3.2.1-PRERELEASE.md) is now
published and records its later hosted browser/native, compatibility, update
and installer acceptance. Live promotion of that preview to stable has not
been requested or performed.

No dependency, app-version, companion-protocol or storage-schema bump was made
in that initial feature delivery.
The running production app and its data were not used for validation. No tag or
GitHub release was created, and no app was installed or replaced.

Rendered/native viewport review, real installed-app update/relaunch and live
GitHub prerelease-to-stable acceptance remain pending on a separate test PC or
explicitly approved unattended Windows session. Mixed-version Host/Friend
network and real-game/save acceptance are not established by synthetic checks.
No full release gate, process runner, desktop/installer smoke, browser automation,
production app/data/world access, release/tag creation or installation is
authorized as part of the local code-only verification.
