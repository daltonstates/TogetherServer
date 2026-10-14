# Current profiles and revoked-PC deletion

Delivered 2026-10-13. Backend and secondary UI workers used the configured `gpt-6.1-sol`; integration, checks and commit remain coordinator-owned.

## Behavior

Friend access defaults to Current PCs. Available PCs with a recent authenticated heartbeat lead, followed by available PCs with Unknown connection and other current records. Revoked PCs stay out of ordinary search/status filters and totals. Show removed PCs explicitly reveals compact removed-PC cards with Delete PC. Confirmation precedes deletion; cancellation makes no request, and failure retains the card with retryable feedback. Advanced Host settings no longer advertises removed PCs or their count.

Saved Hosts default to current connections and put Connected/Disabled before other current states and revoked entries. Show removed Hosts is inside the picker and exposes deletion of the exact saved connection using the existing Forget endpoint. A hidden selected revoked Host shows the Join form instead of its identity, cached servers, status or activity. Explicit revocation learned over the pinned heartbeat transport survives protected-state reload; generic denial or network failure never infers it.

Host and Friend server pickers, All servers, permission/assignment selectors, diagnostics and rehearsal selectors put active profiles first. Active display states are Ready, Starting, Stopping, Listening, Process running and World copy in progress. Favorites and saved order apply within the groups; selection and persisted server settings remain unchanged. All servers retains attention sorting within each group and displays inactive-server warnings. Sharing-member lists omit revoked entries, including pending local revocations in active-PC totals, and put unexpired grants before expired grants.

## Deletion boundary

The loopback Host-owner DELETE accepts only the device ID. Active/unknown devices, Friend mode, query/body input and missing local authorization are denied. Pairing mutation serializes with shared-world authority, removes renewal/heartbeat/pairing data, invalidates held objects and continues denying old credentials after restart. Other PCs remain authorized. Signed original-owner roster changes retain the publication fence until existing repair verifies them.

Deletion removes the saved access record, not immutable signed lineage or private audit evidence. Successor/damaged-authority membership refuses deletion for original-owner review. The interface keeps those revoked records hidden by default. A Host that deletes a record before the Friend ever receives explicit revocation leaves a generic credential denial on that Friend; the app cannot safely label that denial revoked. Action denials remain transient until the next heartbeat records revocation. These are deliberate trust boundaries.

## Verification

- PASS full JSDOM UI suite: 970 tests across 81 files, including default/reveal/search/count behavior, active ordering, preserved selection, cancellation, exact deletion, failure retention, busy controls and pending signed-roster counts.
- PASS `scripts/verify-code-only.ps1`: pinned tools, locked install/restore, TypeScript, lint, solution compilation with zero warnings/errors, all 22 source/synthetic groups, whitespace and desktop diagnostic isolation. New groups cover revoked-PC deletion and remembered Friend revocation using synthetic protected files and fake HTTP.
- Existing synthetic source-link subcases were SKIPPED because the test account cannot create symbolic links; the enclosing import-preview group passed its remaining cases.
- Initial validation found two shell assertions omitting the helper's signal argument and the existing mutation helper's missing DELETE type. These were corrected; the original failed code-only log remains under ignored `local-data/revoked-access-20261013/code-only.log`.
- PASS production UI bundle and final app build with zero .NET warnings/errors. All four current UI assets were verified embedded byte-for-byte in the compiled app. Vite retains its existing non-blocking JavaScript chunk-size warning.
- SKIPPED native window/browser/viewport, packaged HTTP/process/installer, real Friend-PC/network, game join and save/restart checks. No app, fixture, game, listener or native dialog was launched. Those checks require an explicitly approved separate PC or unattended Windows session; code-only evidence does not certify them.

Safe verification logs are in ignored `local-data/revoked-access-20261013`. No production data or installation, terms, credentials, public route, release or tag is changed by this slice.

## Local executable handoff

Source commit: `ed70143ec31655ae8587942d975c07484d2013d3`. A fresh self-contained production-named build is in `local-data/revoked-access-20261013/app/TogetherServer.exe`, with its adjacent SHA-256 file and `artifact-evidence.json` in the parent folder. Numeric PE version is `0.3.2.0`; informational version is `0.3.2+ed70143ec31655ae8587942d975c07484d2013d3`. The file is 65,698,576 bytes and Authenticode reports NotSigned.

SHA-256: `bbe2b97d8f4ea67c631c93047b2dc464a259ff6c1e2eb42e42cf6e31cde7fa87`. The publish succeeded and its compiled assembly includes all four current UI assets byte-for-byte. The executable was not launched or installed; native/HTTP/Friend-PC acceptance remains skipped as above. No version tag or public release was created.
