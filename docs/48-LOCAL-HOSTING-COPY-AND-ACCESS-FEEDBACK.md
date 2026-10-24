# Local hosting copy preparation and access feedback

Date: 2026-10-23. Source change following the 0.3.3.6 preview; no tag or release
is created by this repair.

## Report and findings

A Friend reported the guide sentence "An interrupted transfer or a signed save
announcement is not a complete verified local copy" after opening Host locally.
Another PC did not show the hosting button. Both apps were reported current.

That sentence was static guide text, not a returned transfer error. Passive
hosting status used `SharedSaveRequired` whenever a local hosting branch had
not been restored, including when received files already existed. Rechecking
status does not prepare a hosting branch, so following that advice could keep
showing the same apparent missing-save problem.

The launcher correctly required a current verified hosting grant, but pending,
failed, unknown and denied permission lookups all hid it without an explanation.
This made transport or history-check failures look like a missing feature.
These source findings do not establish why either physical PC's transfer or
permission lookup failed.

## Changes

Passive unprepared-copy status uses `LocalHostingPreparationRequired`, and its
save check stays Unknown. The dialog explains the explicit preparation action
before treating a missing source or failed transfer as a blocker. It does not
label signed metadata, a complete but unchecked payload, or a partial download
as a verified hosting copy. Actual preparation still verifies payload hashes,
signed ancestry, current access, local setup, ports and the hosting route.

The Friend UI exposes hosting-access progress and results with a read-only
retry, while keeping Host locally unavailable without a verified grant. Retry
does not prepare a copy, enable a listener or start a server. Scope changes and
late replies cannot reuse another connection's authorization.

Consent, owner withdrawals, conflicting history protection, network confirmation
and the explicit hosting instruction retain their existing enforcement.
Incomplete payloads cannot reach a prepared branch or Start.

## Validation and remaining acceptance

File-only regressions cover complete, missing and
partial payloads under a signed announcement; repeated status reads must leave
profiles, runs, branches and listener policy unchanged. Only explicit
preparation of the complete payload may create a checked hosting copy.

PASS: **279 UI tests in four files** (`FriendLocalHosting`, `LocalHostingGuide`,
`FriendPlayFlow` and `AppFlows`). These cover permission lookup failures,
read-only retry, timeout and late replies, profile/connection changes, denied
and blocked access, unprepared-copy help, exact save failure messages and the
existing single-confirmation preparation/Start flow. The three focused source
groups for permission, preparation and fixed Start/repair actions also pass.

The first code-only gate invocation stopped at an expected getting-started
write-failure diagnostic because Windows PowerShell treated redirected native
stderr as a terminating error. The retry uses native stream redirection for
the log; no test expectation or product code was changed for this issue.
PASS: `scripts/verify-code-only.ps1`, including all **66 source/synthetic
groups**, pinned Node 24.14.1/.NET SDK 10.0.301, locked dependencies,
TypeScript, lint, solution compilation with zero warnings/errors, whitespace
and desktop diagnostic isolation. The existing synthetic symbolic-link
subcases skip because this test account cannot create those links; their
containing group passes. Expected getting-started read/write-failure
diagnostics are retained in the successful gate log.

Validation ran in an isolated worktree based on `793beee`; unrelated
connection-help and local-invite work in the original workspace is preserved.
Ignored logs are in `local-data/hosting-copy-feedback/` in that worktree.

PASS: production UI bundle and TypeScript build. Vite retains the existing
advisory for a JavaScript chunk larger than 500 kB. A portable test package is
prepared separately from any public release and its identity is recorded below
after static inspection.

Native rendering, actual Host/Friend PCs and the selected
network route, receipt confirmation, real game join and saved restart remain
unverified. No app, game, browser, console fixture or installer may be launched
on the owner's active desktop for this change.

## Portable test build

PASS: Release single-file publish and static package inspection. The EXE is a
Windows GUI binary containing the exact current assembly and all seven current
UI assets. ProductVersion binds it to the committed source below. No app or
game was launched to build or inspect it.

| Field | Value |
| --- | --- |
| Source | `b7018f744020a3c34ddd48c66d14f9d77def51cb` |
| Test EXE | `local-data/local-hosting-fix-b7018f7/TogetherServer.exe` in the original workspace |
| File version | `0.3.3.6` |
| Product version | `0.3.3.6+b7018f744020a3c34ddd48c66d14f9d77def51cb` |
| Bytes | `66149437` |
| SHA-256 | `f758e6220e4dbd16ff5d73829e0c341892da1ca0c84dc5900899b3e430cc1cf0` |
| Authenticode | `NotSigned` |

`package-evidence.json` and `SHA256SUMS.txt` accompany the EXE in its ignored
output directory. This is an unreleased test build with the same numeric
version as 0.3.3.6; the path, source suffix and hash distinguish it. It has no
trusted-publisher claim and no tag, installer or release was created.

Use this same test EXE on both PCs. Quit the existing TogetherServer app before
opening it so the old running instance does not handle the launch. This
portable uses the ordinary app's existing local data. Wait until a managed
server has been stopped normally before quitting its app. No installation,
replacement, listener enablement or game launch was performed by this work.
