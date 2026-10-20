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
- NOT RUN locally: app/browser/native/process/installer journeys. Installer smoke
  source expectations are updated but execution requires the approved isolated
  Windows runner/session. Real Friend-PC/network, actual game join,
  recognizable save/load/Stop/restart, and mixed-version acceptance remain external.

Workers use the configured `gpt-6.1-sol` model. The coordinator owns integration,
shared builds, packaging, validation, commits, and the remaining acceptance record.

## Production verification build

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
