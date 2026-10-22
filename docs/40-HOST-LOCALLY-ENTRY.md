# Host locally entry and Play actions

Owner correction, 2026-10-21: show a clear Friend **Host locally** action before
hosting checks. Clicking it opens the guided checks and the network warning;
the app helps resolve missing setup and starts only after confirmation. Remove
the explanatory text between **Open game** and **Stop server** and give those
buttons the same dimensions.

## Change

- **Host locally** has a visible label beside the ordinary server actions.
  It remains available when a permission lookup is pending or unavailable,
  allowing the user to open the guided recovery flow. A confirmed denial hides
  it; revoked, expired and pending-approval access stays blocked.
- A small local-only permission lookup refreshes signed membership and checks
  the existing identity, grant, roster, floor and owner-access gates. It never
  selects or verifies a save, discovers server files, enters the lifecycle gate
  or checks ports/routes. Signed offline grants remain usable when the original
  Host cannot be reached. No permission or incoming listener is enabled here.
- Full hosting checks run after the click and while the dialog remains open.
  Missing server installation, terms, save, connection and port problems have
  the existing guided next step. The network confirmation still authorizes one
  bounded prepare/Start sequence; reads, retries, stale replies and permission
  withdrawal never recreate a consumed Start intent.
- Host-decision diagnostics are in Connection help instead of preceding Play.
  Open game and Stop server share a 12-rem width and 44-pixel minimum height.
  The Steam helper sentence is removed; explicit launch errors and results stay
  visible. Game launch and remote Stop permissions are unchanged.

## Verification checkpoint

Source checks passed all 63 groups, including the new permission-only group.
The complete solution compiled with zero warnings/errors. One existing
symbolic-link subcase remains skipped because this Windows account cannot
create the link. Only synthetic files and fake HTTP/adapters were used.

The initial focused interface run passed 116 cases. The full 1,347-case run
passed 1,338 cases and failed nine: shell fixture responses did not yet describe
the new permission endpoint, shell selection assumed the previously delayed
button, and unchanged shell/recovery cases hit their existing deadlines during
the concurrent build. Fixtures now supply explicit denied/allowed permission
responses, wait for the selected section before clicking, and await a confirmed
denial before asserting that hosting is hidden. Serial followups passed 136
of 141 cases in the two affected files; the corrected five fixture/selection
cases and a new pending-denial/cancel regression then all passed. This is
evidence from the original suite and focused followups, not a claim that the
initial full-suite invocation passed. Deadlines and safety assertions are
unchanged. Followup recovery cases also cover typed unavailable
rosters and damaged sharing metadata without hiding the entry or enabling
Start; after a successful retry the action becomes available. The final focused
run passed 119 component/demo cases and all 25 Friend shell cases. The final
local-hosting file passed all 41 cases, including both typed recovery cases.
Lint, TypeScript, focused C# format verification and production bundling pass;
the existing large-chunk advisory remains.

All 31 pure renderer checks pass. The hosted visual inventory now covers 26
desktop/narrow cases, including the visible entry before preparation, entry
with no server installed, and measured equality of Open game/Stop server.
The first hosted review of source `4778d90` passed
25 of 26 views with no layout findings, but the measured desktop Stop button
was 38 pixels high rather than 44. A more specific shared physical/logical
minimum size corrects the inherited styling; the failed view remains failed.
That superseded source passed both packaged eight-stage handoffs, including
the real permission endpoint before the first save.

## Final hosted evidence and production handoff

Source `18f9eebe0f29e84d5c19e92b0420a9d475212541` passed
[Windows run 39699739313, attempt 1](https://github.com/daltonstates/TogetherServer/actions/runs/39699739313).
Both jobs succeeded: renderer `119565795369` and packaged shared hosting
`119565795600`.

- The renderer captured all 26 planned desktop/narrow views with 75 images,
  zero failed views, zero layout findings and zero skipped views. Twenty views
  deliberately include synthetic compatibility/error responses; these are
  recorded intentional cases, not unexpected failures. Both ready-Play views
  measure Open game and Stop server at exactly **192 x 44 pixels**, with equal
  font, padding and physical/logical minimum height, and no Steam helper text.
  The ready Play view, entry before installation, and narrow missing-install
  guidance were also visually inspected. These are bundled-React fixtures.
- The exact packaged candidate completed two ordinary, diagnostics-off,
  eight-stage handoffs, including permission GETs for both Friends before the
  first save, Friend prepare/Start, descendant verification, outbound-only
  peer recovery, returning-owner Start and signed permission withdrawal.
  Each run made one Friend Start and one owner Start, advanced version 2 to 3,
  reported no failed stage and emptied its disposable process job. All recorded
  processes were stopped without unresolved identity checks. No diagnostic
  retry was needed. This remains a same-runner synthetic private-address flow.
- Downloaded ZIPs were checked against GitHub artifact lengths and SHA-256
  digests before extraction. The production EXE matches both independently
  uploaded candidate records and the hash retained after both packaged runs.
  Static PE/bundle inspection verified GUI subsystem 2 and all seven current UI
  assets embedded in the executable. The current UI index SHA-256 matches the
  hosted renderer: `2042b5301a3deef821ce8201fbdf819497eac944628b7da9e628092daaabb3b2`.

| Artifact | ID | ZIP SHA-256 |
| --- | --- | --- |
| Production candidate | `12391933782` | `c8172b94042d0e782be6dfc5868b05943811813fd8b35153d0594c98c2e20b9f` |
| Packaged reports | `12391884203` | `2900b3463b2da93ad26f9ae2cec2393ec6398d06f3582b7c96e820871b250089` |
| Renderer evidence | `12392087333` | `084c21856526724c95ccfe32ede65c9a068edef66b7f09191810630aa2451dce` |

The production file is
`local-data/host-locally-entry-18f9eeb/production/TogetherServer.exe`:

- File version: `0.3.3.1`; product version:
  `0.3.3.1+18f9eebe0f29e84d5c19e92b0420a9d475212541`.
- Length: `66,133,343` bytes; SHA-256:
  `7b31068b484602e2a6facb69c7a3e5ead70e88bcc8ff8c8397824e253aeedcf8`.
- Authenticode: `NotSigned`; no trust/signing claim.
- Local evidence: `local-data/host-locally-entry-18f9eeb/production-verification.json`,
  downloaded reports/renderer manifest, and adjacent `TogetherServer.exe.sha256`.

The local verifier reads PE, bundle and asset bytes without loading the app.
No app, fixture, listener, game, browser, installer or native dialog has been
launched on the owner's active desktop. The current installation was not
replaced or started.

The published `v0.3.3.1` assets remain immutable. This correction has not been
published as another GitHub release. Physical Friend PCs, outside routing,
real game joins and real save/load/Stop/restart remain external acceptance.
