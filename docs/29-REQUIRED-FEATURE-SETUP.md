# Required setup after installation and updates

## Delivered flow

The first app opening after installation, a portable replacement or an in-app
update checks required setup before showing the normal workspace. The first step
is **Your name**, using the same profile editor as App Settings. A missing name
must be saved before continuing. An existing valid name already satisfies the
requirement, so an upgrade does not ask the person to enter it again. Local
profile edits now require 1–80 characters and cannot clear the name.

The installer keeps its optional Host/Join and Windows preferences. Its setup
copy explains that the app will guide required setup when it opens. No name is
passed in a command line or installer log. Choosing not to launch the app from
the installer defers required setup to the first manual opening.

Setup keeps ordinary workspace navigation, shortcuts, notifications and commands
behind the current requirements. Loading or failed verification does not count
as completion. Retry preserves the name draft, and a successful completion is
followed by another authoritative setup read before revealing the workspace.

Existing hosting, observation and Friend polling continue in the background.
The setup page retains explicit existing-server graceful Stop and local recovery
tools; it does not expose a general skip or Start path. Native window controls
remain independent of the web workspace. No server is started or stopped merely
because required setup appears.
If the app last opened on Join, **Review hosting on this PC** selects the local
Host state within the same setup page so those Stop controls are reachable. It
preserves the unfinished name and keeps ordinary workspace navigation gated.

## Reuse for later requirements

`RequiredFeatureSetupService` owns a small fixed list of definitions, each with a
stable ID, positive revision, title, description and a current configuration
predicate. Completed revisions live in the local `required-setup.json`, included
in existing update checkpoints. Progress is per feature, not the app version.

A configuration requirement may be satisfied by existing valid data. A required
review can instead set `RequiresAcknowledgement` so that configuration and an
acknowledged current revision are both necessary. Increasing that requirement's
revision reopens only that review; adding another definition leaves completed
requirements intact. Saved progress never overrides missing configuration.
Damaged progress is quarantined without blocking unrelated game lifecycle.

To add a required feature:

1. Add a fixed `RequiredFeatureDefinition` with its own ID/revision and a read-only
   predicate over the feature's saved settings. Request explicit acknowledgement
   only when the person must review something even if their settings are valid.
2. Register the matching revision in `appRequiredFeatures` and supply its editor
   through the reusable `RequiredFeatureSetup` step renderer. Save through the
   feature's existing owner API, then call the provided completion callback.
3. Cover missing/existing data, completion failure, restart, later revision and
   unknown/missing step behavior. Setup must never introduce a permission grant,
   public listener, game launch or network-policy change by acknowledgement.

An unknown, missing or mismatched renderer/requirement remains a visible retryable
setup error. Browser local storage cannot acknowledge a feature or bypass setup.

## Local contracts and boundaries

- `GET /api/local/required-setup` returns a bounded `features` list of
  `{ id, revision, title, description, completed }`.
- `POST /api/local/required-setup/{id}/complete` accepts exactly `{ revision }`.
  Only a registered ID at its exact current revision can complete, and its
  current saved configuration is checked before progress is written.
- Reads require the exact local header, loopback peer and matching local Host
  and port, with no query or body. Completion also requires same-origin JSON,
  at most 256 bytes, and a current update/shutdown check under the mode gate.
  Both Host and Join use the same owner-local setup flow.
- Profile data remains Windows-protected. The progress file contains only fixed
  feature IDs and revisions. Neither is new remote lifecycle authority.
- Older paired apps retain their existing optional-name protocol compatibility;
  required setup applies to the updated app on this PC. Background transport is
  not disabled while a local person completes setup.

## Validation and remaining checks

Final serialized validation on 2026-10-18:

| Check | Result |
| --- | --- |
| Code-only gate | PASS: pinned tools, locked installs/restores, typecheck/lint, solution compilation, all 44 source/synthetic groups and static checks. |
| Final UI regression | PASS: 1,102 JSDOM tests across 91 files, including setup while last on Join and access to local Stop controls. |
| Final UI lint, typecheck and production bundle | PASS. Existing Vite warning for a JavaScript chunk over 500 kB remains. |
| Final app compilation with current bundle | PASS: zero warnings/errors. |
| Read-only review and whitespace | PASS: no actionable findings in the reviewed registry, routes, profile editor and setup flow. |
| Symbolic-link subcases | SKIPPED: this Windows account cannot create symbolic links. |

Logs remain under ignored `local-data/required-setup-20261018/`.

Source checks cover first installation and old-version data, existing-name
inference, persisted and later revisions, new features, invalid configuration,
damaged/write-failed progress, exact local inputs and operation-gate rechecks.
Component and shell checks cover required setup, saving, retries and navigation.

An early focused AppFlows run had four old expectations for an empty profile or
pre-setup loading/navigation timing. They were corrected and passed targeted
checks before the full suite. The first complete suite passed 1,101 tests; final
review added the Join-to-local-hosting safety case, and the final complete suite
passed all 1,102 tests. No failed or skipped interactive acceptance was relabeled
as a pass.

Installer, native/rendered UI, packaged update/relaunch and real Friend-PC/game
acceptance require an approved separate PC or unattended Windows session and
remain unverified on the owner's active desktop. No running production app,
real world, credential, public listener or network policy is changed by testing.

## Production artifact handoff (2026-10-18)

Published the self-contained Windows x64 `TogetherServer.exe` from
`be4b92ff5ad2244fede4cf78080f249c668d0346` to ignored
`local-data/profile-setup-production-20261018-be4b92f/`.
The executable is 65,961,820 bytes, file version `0.3.2.0`, and unsigned.
Its SHA-256 is
`a3faad1040262f57dd543ee5e50a6cc8897ee20717d0b2065024a0c7c699d214`.
Adjacent checksum, publish log and JSON evidence record the handoff.

Release publication passed. Static inspection confirmed a Windows x64 GUI
executable and all four current UI assets embedded byte-for-byte in its publish
input assembly. The app was not loaded, launched, installed or copied over the
running app.

The profile/setup changes retain companion protocol 3 and the existing storage
schemas. Pairing, heartbeat and status names are optional added fields; older
compatible peers ignore unknown fields, and updated peers accept absent names
with existing PC/Host labels as fallback. Required setup stays local to the
updated app. Builds outside the existing protocol compatibility range still
require an update. Mixed-version real-PC acceptance remains unverified.
