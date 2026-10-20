# Guided setup, clearer access, and shared hosting

2026-10-20 delivery. This record covers the notification/settings/access fixes,
installer guidance, simpler shared hosting, separate game-password access, and
chat presentation/sounds requested together. The running production app and
owner's active desktop are outside local automated testing.

## App navigation and setup

- The notification popover owns its padding and gaps. Tooltips close on activation
  and stay hidden over an open disclosure.
- Context already supplied by a selected server, the This PC settings disclosure,
  or the App Settings page is not repeated as a sidebar heading. Navigation keeps
  its accessible labels.
- App Settings is one continuous page. The sidebar tracks the visible section and
  explicit selections scroll to that section. Desktop content and narrow-screen
  workspace scrolling use the same section logic. Passive observations and polling
  do not issue scroll requests. Reduced-motion settings skip smooth motion.
- Friend PC cards show every action, including separate game-password access.
  Assigned grants are highlighted; unavailable grants remain readable. Partial
  assignment counts, revoked access, unknown state, and expired helper projections
  keep their existing conservative meanings.
- Installer Choose/Host/Join choices now persist a first-use guide request even
  when opening the app is deferred. A new portable data directory also offers
  guidance. An existing installation is not treated as new just because its
  server list is empty. Ordinary upgrades do not reset optional guidance.
- Required profile setup still comes first. Optional guidance does not authorize
  Start/Stop or alter runtime supervision. Its local JSON/read/write failures do
  not block safe Stop. GET/PUT `/api/local/getting-started` accept only their fixed
  owner-local shapes; they have no public companion counterpart.
- Guides in the top bar and command palette reopen Host/Join walkthroughs. Steps
  highlight actual app controls, preserve app interaction, and pause for native
  workflow dialogs. Optional local progress contains only a guide kind and step.

## Game preparation and connection checks

The guide reuses existing discovery, reviewed installers, validation, local port
diagnostics, and Connection Doctor rather than inferring readiness from a file.

| Game or route | Checks and owner action |
| --- | --- |
| Valheim | Discover the installed dedicated server in Steam libraries or browse to it. Steam's Tools installer is sufficient; SteamCMD is optional. The owner handles installation and terms. |
| Minecraft Java | The guided install validates official metadata/JAR provenance and prepares a matching managed Java runtime after explicit terms consent. An externally discovered runtime's compatibility is not claimed from file presence. |
| Minecraft Bedrock | Choose a prepared server or explicitly install official files. Java and SteamCMD are not prerequisites. Game-specific Windows requirements remain the provider's requirements. |
| Local ports | Existing saved-profile validation and Start gates check declared ports and conflicts. A local listener is local evidence. |
| App access | Review the selected route and separate HTTPS TCP port. The optional outside TCP check is requested deliberately and checks only the saved route. |
| Friend/game acceptance | Fresh pinned app access, a game-port response, an actual game join, and recognizable save/restart evidence remain distinct. No guide completion records a fabricated pass. |

Provider references checked for this slice: [Valheim's dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/),
[Minecraft server downloads](https://www.minecraft.net/en-us/download/server),
and [Microsoft's Bedrock setup guide](https://learn.microsoft.com/en-us/minecraft/creator/documents/bedrockserver/getting-started?view=minecraft-bedrock-stable).
The app never changes firewall/router/DNS settings or accepts game terms for an owner.

## Shared hosting and passwords

The owner has one Allow shared hosting switch, followed by Can host this save
for each approved PC. The switch also enables backups after safe Stop while
preserving retention and free-space limits. It changes no per-PC grant or
incoming listener. Save sharing leads with the next useful action; the detailed
live-save installation proof mounts only inside Advanced: game save proof and
review. Connection help and backup settings link to their working editors.

Host on this PC appears beside Start on the Friend overview. An already prepared,
consented copy starts directly. Other cases open the missing-file, terms,
connection, port, storage-consent, or save-history step. One explicit continuation
prepares and starts if its checks pass. Matching owner-installed Minecraft files
use the existing copy-only preparer in a fresh branch folder after explicit terms
consent; downloads are never automatic. An unprepared PC gets working provider
install links and Browse, rather than a dead setup button. Automatic results stay
secondary and distinguish local checks from real-game/network acceptance.

One deliberate hosting request may carry a short-lived start intent through its
own checks. Passive reads, polling, reconnects, or restarting TogetherServer never
create a start intent. Existing receive/hosting grants, local consent, terms,
split-copy acknowledgement, exact save/receipt validation, source fencing,
recorded process identity, and the final lifecycle/port gates remain authoritative.
Cancellation propagates through both lifecycle and shared-membership gates and
is checked again before launch preparation. A failed or canceled Start is never
retried by polling. Move acceptance permits preparation, and a separate explicit
Start instruction can wait for that exact move's final verified save.

The explicit hosting instruction enables the existing signed metadata exchange
for the exact authorized world using an already deliberately enabled pinned
listener. Reachable approved PCs learn its route and managed session through
normal synchronization. The Friend overview shows those reports and the address
automatically, including saved reports while the original Host is unavailable.
Connect obtains/selects the member's own credential only after a click; reports
do not start a game, choose a default, or merge histories. Multiple Hosts remain
an explicit choice, and an unavailable check removes cached connection actions.
Offline delivery and an actual game connection are not claimed.

Game passwords have their own owner-controlled global/per-server permission,
default off. Helper grants and permission presets do not add it. Valheim is the
supported password source; other built-in games do not use its game password.
The stored Host password remains Windows-protected. An authorized Friend fetches
the current value through pinned HTTPS on an explicit Show/Copy action; no
plaintext Friend cache is persisted. Status carries only the permission and an
authorized change-time hint, never the password. Revocation, deadlines,
assignment, current identity, and post-request permission checks still apply.
The UI masks revealed values after 30 seconds, focus/visibility loss, or a change
of Host, assignment, access, or password hint. Copied values cannot be recalled.

## Chat

Chat retains its protected signed history, room permissions, offline drafts,
and idempotent delivery. The UI groups messages and dates, distinguishes the
current author, and animates new arrivals with reduced-motion support. A trusted
local-only `selfAuthorId` identifies own messages without guessing from names.
Public chat-sync payloads do not gain that local field.

Sounds are optional, off initially, and can be muted per room/PC. Quiet
mode suppresses them. Only a new verified incoming remote message after an
established baseline may play a short synthesized sound. Initial history, own
sends, queue echoes, duplicates, backfill, reconnect history, and hidden-tab
history stay quiet. Audio permission/autoplay failures do not interrupt chat.

## Validation and remaining acceptance

- PASS final `scripts/verify-code-only.ps1`: locked tools/dependencies,
  TypeScript/lint, solution compilation with zero warnings/errors, 61
  source/synthetic groups, whitespace and desktop diagnostic isolation. The
  source-link subcase is skipped because this account cannot create symbolic
  links. Logs: `local-data/setup-polish-final-code-only.log`.
- PASS full UI rerun: 1,336 tests across 105 files. Coverage includes continuous
  scroll tracking, guide placement/late replies, separate password permission and
  revocation, quiet chat history, one-click hosting, and canceled late starts.
  Initial broad-run issues with old two-click expectations and a timeout error
  projection were corrected before this final run. The password regression also
  exposed missing durable revocation; only a pinned HTTP 403 with an explicit
  negative Revoked response now records it. Log:
  `local-data/setup-polish-final-ui-tests.log`.
- PASS production UI bundle and the app compilation embedding that bundle, with
  zero .NET warnings/errors. The existing large-chunk warning remains; this
  slice does not restructure the app's bundles.
- PASS renderer syntax and 30 pure fixture tests. The focused review inventory
  has 21 cases and 63 plans across desktop, short desktop, and narrow 150% text,
  with both reduced motion and normal motion. Guide checks include clipping
  ancestors, target hit testing, and coach overlap.
- The first hosted pass exposed a real late directory-selection race that reset
  newer Chat navigation to Play. Navigation tokens now discard that late work;
  two shell regressions failed before the fix and pass afterward. The review
  also led to fixed coach controls with independently scrolling guide text and
  placement below the app header. Old Access labels and an async scroll predicate
  in the harness were corrected: capture now awaits settled, focused geometry
  rather than accepting an unfinished Promise. Pure tests cover false results
  and timeouts; the harness does not force scrolling to conceal failures.
- PASS final hosted Windows rendered review: [run 39369533252](https://github.com/daltonstates/TogetherServer/actions/runs/39369533252),
  job `118454026223`, source `faf0c917dc168f68d6b295b5397d0c2fd9daf26e`.
  All 63 planned case/variant IDs have one result record and one viewport capture.
  The 296 PNGs comprise those 63 viewports, 224 scroll-pane captures, and nine
  guide/scroll interaction captures. No missing plans, duplicate IDs, missing
  capture files, or unlisted PNGs. There are zero failed/skipped cases, layout
  findings, unhandled fixture requests, blocked network requests, failed HTTP
  requests, or JavaScript/console errors. Fifteen cases deliberately exercise
  error or denied states and are accepted as those states, not successful hosting.
- Downloaded and SHA-256-verified hosted artifact `12241031698`:
  `002269a6eb0bb12c95976ea110e94c9fd1f9cf089aa608e82974ed1c35be58ae`.
  Evidence is retained locally under
  `local-data/setup-polish-ci/39369533252/evidence`. Manual inspection of final
  notification, permission-card, continuous-settings, narrow Host/Join guide,
  sharing, missing-server wizard, password, Host notice, and Host/Friend chat
  captures found no further changes required. Automated geometry and interaction
  checks cover all 63 cases; manual inspection is representative, not a claim
  that every one of the 296 images received an independent human review.
- Implementation commits: `443a30a`, `6412c6b`, `44c57ce`, and `faf0c91`.
- At the original delivery, NOT RUN locally: app/browser/native/process/installer journeys. Installer smoke
  source expectations are updated but execution requires the approved isolated
  Windows runner/session. Real Friend-PC/network, actual game join,
  recognizable save/load/Stop/restart, and mixed-version acceptance remain external.

Workers use the configured `gpt-6.1-sol` model. The coordinator owns integration,
shared builds, packaging, validation, commits, and the remaining acceptance record.

## Earlier production verification build

The 2026-10-20 production-mode handoff is version `0.3.2.2`, from clean source
`55ef8ada4a51d29c1c2a6fd7e35cae16c5f89970`. Both files are unsigned and retained
under `local-data/setup-sharing-production-20261020-55ef8ad`:

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer.exe` | 66,115,586 | `7b048c0c3c7367e3fd6d1ac9633b8fafda3f7edb960a64356e2816d04882aa6c` |
| `TogetherServer-Setup-win-x64.exe` | 60,582,099 | `a65618f5cebfe5ad1e4659e5471007ed7e8e5539dd470795edab8a0789c628fd` |

The pinned toolchain rebuilt the UI and published the self-contained Windows x64
single-file app with locked dependencies. Static inspection confirmed Windows
GUI subsystem 2, the exact current managed assembly, all seven current UI assets,
the included runtime configuration, and the native WebView loader. The SDK's
single-file host links its native runtime directly; separate `coreclr.dll` and
`hostpolicy.dll` bundle entries are not required. See the [runtime host source](https://github.com/dotnet/runtime/blob/main/src/native/corehost/apphost/static/CMakeLists.txt).
The existing large JavaScript chunk warning remains.

Inno Setup 6 compiled the installer from those exact app bytes and checked its
version/signature state. The installer was not executed, so an installed-payload
comparison and native setup journey remain unverified. `build-info.json`,
`package-evidence.json`, `runtime-evidence.json`, SHA-256 sidecars and build logs
sit beside the artifacts. Neither artifact was launched or installed; the running
production app's PID, start time, path and executable hash remained unchanged.

This is ready for owner verification, with the limits above. The windowless lab
adds 62 source/synthetic groups and 10 passing process stages with two explicit
skips; see the [local pairing lab](LOCAL-PAIRING-LAB.md). Real Valheim password
reveal/change, successful guided hosting with new-Host announcement/Connect over
a valid two-PC route, real game joins and saved restart, and native installer
acceptance remain pending. Stop hosted servers and quit the existing app before
opening this production build, so the old running process is not reopened instead.

## Optional physical two-PC connection acceptance

The owner has another Windows PC on the home network, but subsequently chose a
fully automated local two-interface UX rehearsal with no manual steps. That local
rehearsal is the current task; physical-PC acceptance remains a separate optional
check. The
[two-PC instructions](TWO-PC-CONNECTION-TEST.md) cover the existing pinned-HTTPS,
signed-chat and verified-transfer rehearsal in both directions, followed by the
actual guided hosting switch and original-PC discovery/Connect. The connection
rehearsal alone does not accept the hosting switch or a game join.

Two opt-in manual launchers open the exact existing portable EXE: the file-only
rehearsal uses separate staging data and UI port `55318`; actual hosting uses
separate normal-mode data and UI port `55319`. This keeps the full hosting run
outside staging's fresh-Valheim-world restriction, which currently refuses a
restored Valheim profile when reopening. The handoff is
`local-data/two-pc-connection-test-20261020-55ef8ad.zip`; it contains no pairing
state, credentials, worlds or logs. It is prepared for the owner to copy and
open on each PC; neither app was launched. Physical cross-PC execution, private
firewall consent if needed, and real-game acceptance are still pending. No
network configuration or production data was changed.

Packaging validation passed: the ZIP contains only the EXE, two manual launchers
and instructions; every extracted entry matches its source file. The EXE retains
SHA-256 `7b048c0c3c7367e3fd6d1ac9633b8fafda3f7edb960a64356e2816d04882aa6c`.
ZIP SHA-256 is `365c71a75facfcce4bc307bac4e3170c4b64532699d088056c2690d2e7d23a73`
(60,015,224 bytes). Read-only review checked the route labels, acceptance limits,
and launcher isolation. This is packaging/source evidence, not a passed manual
launch or cross-PC run. The running app's PID/path/executable hash still match
the production-build baseline. No app code changed for this handoff.

## Automated local two-ended UX acceptance

The owner chose automation on this PC instead of manual physical-PC testing.
`scripts/run-local-pairing-lab.ps1 -Run -Ux` now builds and drives two independent
disposable app instances through their actual bundled UI with a contained,
audio-muted headless browser. It uses real pinned HTTPS on this PC's explicitly
selected assigned private address, signed chat and synthetic fixture saves.
There is no VM, manual clicking, response substitution, production-data access,
or router/firewall change.

PASS final launcher run `71a9b253-c3f2-41d6-95b7-edf9250b0d65`: all eight UX
stages, isolated startup and cleanup passed; no failed or skipped stage. It
covered setup, code pairing/approval, hosting permission and local consent,
bidirectional chat and both sound choices, safe Stop/verified transfer, a genuine
connection remedy, one successful guided Start, exact signed new-Host discovery
and authenticated Connect on the original app, narrow layout and candidate Stop.
The world root, candidate, branch, run, selected Connect request and endpoint
must all match. No JavaScript/HTTP failure or native interaction occurred; one
navigation-canceled GET poll is recorded separately.

The test found and fixed first-use consent/candidate/listener ordering, missing
first-run signed session metadata and the absent original-Host overview notice.
Host settings now show save feedback beside their controls. File-only checks
retain the independent listener material/TLS gates, default-off consent,
permission revocation, current route binding, preserved saves and manual Start.
The fixture still reports Preparing with unknown players rather than game-ready.

This run took 41.318 seconds excluding builds and peaked at 1,064.32 MiB summed
working set for all app/browser/job processes. Shared pages are counted more than
once; build tools and the harness are excluded. All 125 window samples found zero
visible owned windows; cleanup left zero job members. The production app retained
PID `49404`, its original start time, path and executable hash.

See [the local lab record](LOCAL-PAIRING-LAB.md) for exact reports, resource limits,
source/UI checks, earlier failures, and reproducible commands. This advances local
rendered/protocol acceptance. Physical-PC/WAN routing, actual game join and
save/load/restart, real password reveal/change, native window and installer
acceptance remain separate. The earlier portable ZIP remains an optional manual
cross-PC handoff, not the current test requirement.

## Updated production verification build

The updated production-mode handoff includes the first-use hosting fixes above.
It is version `0.3.2.2`, from clean source
`3f5f53e8301df7546d42044d03fe3d02284e2a91`, retained under
`local-data/setup-sharing-production-20261020-3f5f53e`:

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `TogetherServer.exe` | 66,120,267 | `9d466fe5746e9dddf4f0e7af5457d40a0f6c39dfaf191f4a12a41ed08e004f2b` |
| `TogetherServer-Setup-win-x64.exe` | 60,584,761 | `633afe36bfc1a94a71d6155f4fd8d9e6aa5839a055688b3c8b6eee8c27122bc3` |

PASS static GUI subsystem 2, exact bundled current assembly, all seven current UI
assets, self-contained .NET/WindowsDesktop/ASP.NET runtime configuration, and
bundled WebView loader. Inno Setup compiled the installer from the exact app
hash above and checked its version. Both artifacts are unsigned and neither was
launched or installed. `build-info.json`, package/runtime evidence, SHA-256
sidecars, a short readme and build logs sit beside them. The accepted lab run's
`verified-inputs.json` retains the tested input/source/report/screenshot hashes;
its test binaries were built before the source commit and are recorded separately
from these production artifacts.

The running production PID/start time/path/hash still match the baseline. No
production data, app installation, firewall/router rule, tag or GitHub release
was changed. This new build supersedes the earlier handoff for verification;
physical-network, real-game and native installer acceptance remain unverified.

## Expanded unattended acceptance, 2026-10-20

The owner requested full automated coverage and then chose to play a game while
testing continues. Local native UI, installer, keyboard/mouse and real-game
launches are therefore held. Dedicated hosted Windows runners own those desktop
checks; the local lab remains windowless, contained and synthetic.

The expanded local UX run `993ef0e6-e936-4a88-beb3-7fcef61f49bc` passed all ten
stages, with zero failed/skipped stages and zero visible windows in 159 samples.
It adds actual UI hosting-permission withdrawal and local consent/cancel checks
to the eight-stage journey above. The latter found a real initial-read race:
the receiving checkbox is now disabled until this PC's saved consent loads.
All 48 shared-save UI tests pass, including the new pending-read regression.

The API lab now requires signed new-host discovery, authenticated connections
from both other apps, Stop/save transfer, process restart/reconnect, isolated
credential revocation, and a verified final signed receipt. Its first-host
listener setup respects the production permission gate, long peer sync uses a
scoped timeout, and post-job receipt review waits for exact process/file-lock
release rather than assuming an empty job has released every handle. Assigned-
LAN run `4dd1de6b-b346-4aaa-9401-8ce820719772` passed all 18 stages with zero
failed/skipped stages and zero visible windows in 277 samples. Loopback run
`b328e269-e124-4a93-b9ad-d30fcda2aa3b` passed all 11 planned stages with zero
failed/skipped stages and zero visible windows in 33 samples. These local runs
precede the CI source commit; the remote lane rebuilds and verifies its exact
committed candidate separately.

Windows CI now includes the same immutable candidate in loopback, assigned-LAN
and ten-stage UX lanes. Native acceptance exercises the actual OS first-close
notification and Explorer tray Quit. Installer UI acceptance drives each real
Host/Join/Decide-later wizard choice, turns optional tasks on and off, verifies
the visible installed app and persisted guide intent, then checks uninstall
preserves synthetic owner data. These checks are required to pass on the
dedicated runner; source review or compilation does not count as execution.

Hosted execution and the final candidate artifact identity are pending at this
checkpoint. Record their exact run, source revision, hashes and results below
after completion. Real game join/load/save/restart and physical-PC/WAN routing
remain separate from this same-PC synthetic acceptance.

The first full hosted run was `39385522178`, compiled source
`9a99b2df3574c171dd02da9e6afe2dc59cf4de2f`, app SHA-256
`4fee3365429a379a13be5a26d08d238df6c1bb15dc46163141d9900188826b6a`.
It passed 1,339 UI tests, all 15 Existing checks before Core remote, QolApi,
Rehearsal, all four compatibility suites and all 11 loopback lab stages. It
failed and remains failed: older browser/native helpers clicked behind the new
optional guide, the added password test used a launch value rejected by the
synthetic fixture, and the LAN transfer convergence deadline was shorter than
one normal production sync pass. The helpers now use and persist the real
Set up later choice; the fixture starts with its required synthetic value;
only branch-transfer convergence gets a 90-second window, with typed progress
observations and unchanged signed lineage/payload/receipt assertions.

Visual run `39385979932`, verification source `41d9d86`, passed all 63 planned
setup/sharing cases, producing 296 captures with zero failed/skipped cases or
layout findings. Representative notification, permission, continuous-settings,
guide and chat captures were inspected. Product/UI source is identical to the
compiled candidate; this later commit changed only workflow dispatch.

The owner requested careful GitHub usage. Both hosted runs report zero billable
execution time; this is a public repository using standard Windows runners.
Their temporary artifacts total 75.63 MiB. A single pinned continuation reuses
the existing candidate, completed check reports and loopback proof; it runs
only failed/unrun acceptance and the installer. It verifies run/job/artifact,
ZIP and app hashes, and rejects any changed product source. Review artifacts
now expire after one day. Prior failures are retained as failures, not relabeled
successful. The continuation has not yet executed at this checkpoint.

The continuation `39388659351` subsequently failed. Its actual result reports
show ten of twelve remaining Existing checks passed, including all fourteen
Core remote groups (six exercise permission-scoped password reads over pinned
TLS) and both remote rehearsals. The ten-stage rendered two-ended UX and native
window-chrome checks also passed. Continued GitHub steps can show a successful
API conclusion despite a failed underlying outcome; their green step labels
were initially misread. The aggregate rejected the failures correctly. The raw
reports, rather than those labels, are the acceptance record.

Remaining failures were solution whitespace, served smoke during Custom setup,
the Guides button's 40-pixel height, OS notification readiness, and a missing
durable new-host receipt. The old served wrapper lost the terminating exception;
its five-second observation budget was undersized, but a hosted rerun is needed
to establish the fix. The final receipt failure
exposed a product bug: a copy received through a forwarding PC was acknowledged
only to that PC; seeing the same catalog entry later skipped delivery to another
serving peer. Retained copies now retry their original signed receipt only after
checking current membership/consent, signature, lineage and unchanged payload.
Acknowledged delivery suppresses repeat sends; failures have bounded backoff.
Source checks cover lost delivery, restart retry, duplicate suppression, forged
or missing receipts and subsequently damaged payloads. The final lab receipt
assertion remains mandatory.

The Guides target is now at least 44 pixels high. Served smoke uses a bounded
30-second observation budget, operation-specific synthetic Stop signals, exact
process cleanup and safe stage diagnostics. Native acceptance records the actual
Windows notification state before and after one physical click on the exact
owned caption, then waits briefly for readiness; persistent suppression still
fails. No Windows preference or production notification gate is overridden.

These product fixes require a fresh candidate. The bounded
`acceptance-repair.yml` run repeats affected sharing/source/UI checks and attempts
both installer suites even if an independent runtime check fails. Its final
package requires every actual outcome and report to pass. Unchanged historical
passes remain tied to their original source; they are not relabeled as tests of
the repaired executable. All new artifacts expire after one day. The previous
visual archive was hash-verified and saved locally before its GitHub copy was
deleted. The three hosted runs so far report zero billable runner time; this
does not assert anything about unrelated account storage or billing.

The repaired candidate's hosted execution and installer acceptance are pending
at this checkpoint. No local native input, game launch, installation, production
process change or network configuration change was performed.

Before dispatch, the repaired solution compiled with zero warnings/errors;
all source/synthetic feature groups, production UI bundling, solution whitespace,
edited-script parsing and workflow PowerShell/YAML parsing passed locally.
The source runner explicitly skipped symbolic-link cases unavailable to this
test account; those are not recorded as executed. Independent workflow review
checked all step references, inventories, installer independence and the final
actual-outcome package gate. The production process still matched its original
PID, executable path and start time.

Repair run `39392686691` built source
`1b84e8f25943afd71f6ce3041b6159fa519e2ff5`, app SHA-256
`937dc50d965b5a23fd33bb3611cac15723948c9602bedb833d88fa72c67909f7`.
Its actual reports pass all five affected sharing/served/formatting checks,
all 1,339 UI tests, the full browser journeys and all 18 assigned-LAN stages.
The latter includes the formerly missing durable new-host receipt, restart,
both authenticated recipient connections and isolated revocation; all 340
owned-window samples were empty and cleanup left zero job members. The repaired
receipt path is therefore exercised by the exact production-mode candidate.
All six silent installer checks also pass for that run's installer.

The run remains failed. The ten-stage UX stopped while restoring a hosting
grant after its withdrawal assertion had passed; rapid navigation canceled the
Shared saves read while the connection was temporarily unknown. The helper now
waits for the exact grant save, automatic assigned-connection recovery and the
completed Shared saves read before leaving that surface. It retains the
permission/consent, one-Start and exact signed-Connect assertions, with bounded
recovery diagnostics and no HTTP mutation or response substitution.

Actual installer UI stopped because its screenshot helper treated an owned
auxiliary HWND as a dialog. Captures now require the exact native wizard or app
window, retaining the original size and identity limits. All three real choices
still need execution. The native test now establishes the OS state: Windows
returns `QuietTime` before and after verified physical caption input and all
60 readiness polls. Notification delivery and click remain unproven. The next
hosted attempt uses ordinary Windows Settings controls once, then rechecks the
unchanged app gate. Persistent suppression is recorded as BLOCKED while other
native cases continue; every required case must pass for full native success.

The narrowed continuation reuses the immutable repaired EXE and pinned prior
reports. It runs only the unfinished UX/native/wizard work; silent installation
is repeated because the installer wrapper must be rebuilt. It retains that
exact wrapper even if an independent OS prerequisite blocks acceptance, avoiding
another needless rebuild. No product source changed for these helper fixes.

Temporary artifacts from the earlier full run, visual review and failed first
continuation were hash-verified and archived locally before deletion from
GitHub, freeing 82,497,570 bytes. Unrelated uploads were preserved. The current
candidate and repair evidence remain available for the pinned continuation;
all new uploads have one-day retention.

Before that continuation, focused local run
`56a3845f315d4540a27211d9e0ccf8f6` passed all ten actual UX stages against the
exact repaired candidate above. Grant recovery completed automatically with no
manual refresh; the captured connection was Connected, hosting allowed and
local consent intact. The run made one successful Start and verified the exact
signed world/session/branch/address/Connect selection. All 172 owned-window
samples were empty, cleanup left zero job members, and the production process's
PID/start/path/executable hash remained unchanged. This uses the existing guarded
windowless lab, not local native UI or a real game.

Continuation `39398164189` also passed all ten Host/Friend UX stages on hosted
Windows, including automatic grant recovery, one explicit Start and the exact
signed Connect destination. Its installer was bit-identical to the repair run
(`004dcf1cf9431092f85e0a0a2ce34c3e844fd573bfd4b40d61196fd54837269f`)
and passed all six silent installation checks again. Those surfaces need no
further rerun for the unchanged candidate.

The continuation still failed overall. The actual wizard screenshot showed an
enabled Next button exposed through a native provider rather than UIA Button.
The helper now accepts only reviewed native navigation captions/classes inside
the exact owned wizard, rechecking process/session/window identity before input.
The native suite passed tray reopen and the actual file picker assertions, but
forced Node shutdown then hit a Windows libuv assertion; it now disconnects the
owned CDP attachment and requires a natural zero exit before recording PASS.
Neither fix changes application or installer bytes.

One ordinary hosted Windows Settings attempt changed Do not disturb to Off but
did not clear the fresh Windows profile's QuietTime. That OS notification check
remains BLOCKED, never passed. The Settings attempt is not repeated and other
native cases continue independently. The final native gate still requires every
required case, including actual notification delivery and click, to pass.

The remaining-only continuation now reuses pinned app, installer, completed UX
and silent-installer artifacts. It performs no app/compiler/dispatcher build,
browser download, repeated pairing run, silent installer run or duplicate
installer upload. Only native and actual three-choice wizard checks run, with
one-day evidence retention. Both recent completed runs report zero billable
runner milliseconds; storage and unrelated account usage are separate.

Remaining-only run `39400463020` passed all twelve other required native cases,
including the actual file picker with natural Node exit, native Quit/draft flush,
draft rejection/recovery, window restore and Explorer tray-menu Quit. It completed
all independent native work and exact cleanup; notification delivery/click remains
one BLOCKED case because Windows still reported QuietTime. The staging-only
startup case remains an explicit SKIP covered by the separate production gate.
All seventeen retained hosted Host/Friend UX images were also visually reviewed.

The wizard now genuinely advances with its exact native buttons. Its next
failure is the accessible discovery of a required custom setup choice, before
Install is clicked; all navigation and role/preference/task checks remain
mandatory. The next retry runs only actual wizard acceptance, reusing the exact
installer, twelve passed native cases and explicit BLOCKED OS result. It installs
no SDK, Node/browser dependency or compiler and repeats no other runtime suite.
The full native/package gate remains failed while notification delivery is
unproven. App and installer bytes remain unchanged.

The wizard reader now uses Inno's native IAccessible row provider, matching
reviewed checkbox/radio labels, roles, state and geometry. It revalidates the
exact owned row before input and reads the actual checked state afterward.
Bounded diagnostics cover all seven role/Windows/task/Finish labels. The extracted
C# helper compiled against .NET 10 with zero warnings/errors without invoking
native methods; PowerShell parsing, workflow parsing/saved-proof assertions and
independent read-only review passed before dispatch. This is helper validation,
not a substitute for the three hosted installer executions.
