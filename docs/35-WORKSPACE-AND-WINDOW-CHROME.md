# Workspace cleanup and window chrome

2026-10-19: the desktop sidebar and content now extend from the header to the
status footer. Content padding lives inside its scroll pane. The divider keeps
its accessible drag target while sharing the sidebar surface. A shared themed
scrollbar styles the main pane, sidebar, dialogs and nested readers, removes
Chromium's native arrow buttons and respects forced colors.

Native caption controls use centered 12-DIP strokes at the current monitor DPI.
The custom frame retains Windows' caption, system-menu, minimize, maximize and
resize styles while removing their visual frame through `WM_NCCALCSIZE`.
Windows handles taskbar minimize/restore; explicit app reveal uses `SC_RESTORE`
so a minimized maximized window returns to its prior state. Monitor work-area
bounds apply through `MINMAXINFO`, after WinForms supplies its track constraints.
During `WM_NCCALCSIZE`, native `IsZoomed` and the proposed destination monitor
keep the maximized visible client inside that monitor's working area. Windows
may retain an invisible resize frame outside the client; content and caption
controls stay clear of it without assuming a fixed frame size or DPI.
Close-to-tray changes the taskbar flag before hiding, because WinForms handle
recreation can otherwise re-show a maximized window.

## Local artifacts

The latest requested build is kept under ignored `local-data/latest-build`.
The active portable build in `local-data/sidebar-production-20261019-c585856`
is protected while it is running. Current release evidence, registered
worktrees, possible owner state and all installed/production/staging/demo data
are protected. Seventeen older worktrees have uncommitted changes; none was
discarded.

Automatic approval rejected permanent artifact deletion with `blocked by
policy`. Obsolete generated artifacts are instead moved reversibly to
`G:\repo\TogetherServer-artifact-archive-20261019`, outside the checkout, with
source/destination manifests. This cleans the checkout without claiming freed
disk space. Root containment, process paths, registered worktrees, protected
paths and reparse points are checked before moving each reviewed root.
The cleanup moved 441 obsolete entries totaling 27,235,001,610 bytes (25.36 GiB),
including old build/test output, helper files and intact prior release records.
All 17 dirty registered worktrees remain in place. Fresh validation evidence is
kept in the archive's `window-chrome-review` folder.
The nested `ui-refactor/ui-before-worktree` and possible owner-session captures
in `ui-refactor/visual` remain in place; only audited generated siblings moved.
The final source-check run's fresh 5,428 synthetic files (63,902,913 bytes) are
also archived separately after the check completed.

## Validation

- PASS: all 1,244 UI tests in 99 files and 26 pure renderer fixture checks.
- PASS: the serialized code-only gate, including locked dependencies,
  TypeScript, lint, solution compilation with zero warnings/errors and all 57
  source/synthetic groups at `cf6cd30`. Its existing symbolic-link subcase is
  skipped because this Windows account cannot create symbolic links. The
  subsequent native-only hide ordering fix also passed solution compilation
  with zero warnings/errors and the complete hosted native check below.
- PASS: the exact hosted candidate is retained as
  `local-data/latest-build/TogetherServer.exe` (66,071,364 bytes), source
  `12d8cf94bca016405813242124f73d1533f5d4f3`, product version
  `0.3.2.1+12d8cf94bca016405813242124f73d1533f5d4f3`, SHA-256
  `40b509f7b146e7d4d4f90fa87e29e0f65cc53ba5fa76b3bc64100144351db8fb`.
  Static bundle inspection confirms the WebView loader, all seven current UI
  assets and a Windows x64 GUI executable. Its bundled assembly differs from
  the local CI-mode compilation only in 72 bytes of compiler/PDB identity;
  all executable content and other metadata match. The record reports that
  distinction rather than claiming byte-identical assemblies. Authenticode
  reports `NotSigned`. The executable was not launched or installed locally;
  the owner's original process and start time are unchanged. Its superseded
  provisional build and evidence were archived intact.
- PASS: [hosted browser review](https://github.com/daltonstates/TogetherServer/actions/runs/39317405356)
  at `12d8cf94bca016405813242124f73d1533f5d4f3`: 58/58 plans, 201 screenshots,
  zero failures/layout findings/skips/missing images, all 140 recorded
  scrollbars styled and 49 vertical coverage records complete. Fourteen
  representative original PNGs were visually inspected without findings in
  the earlier byte-identical UI bundle; three PNGs from `39316261016` were also
  inspected. The final download's source/hash, summary and PNG inventory match.
  Desktop/tablet/narrow, short and wide windows, compact density, light/dark
  themes and 150% text are covered. The local UI index matches hosted SHA-256
  `d7fae879483afe918d2d019a2789b8f45aba909476f645fcc334502f29174e44`.
  Artifact `12218936366` ZIP SHA-256 is
  `cb445c78ee5fd961fdbe0c6f4a8c80b9ed1b82e58718ca0a20980ff3502ed169`.
- The first browser run found seven instances of the same inherited 14px
  Settings margin. The scoped reset removes them. The compact-density inventory
  assertion was updated from seven to eight variants before its final pass.
- PASS: [hosted native Windows review](https://github.com/daltonstates/TogetherServer/actions/runs/39317405415)
  at `12d8cf94bca016405813242124f73d1533f5d4f3`: 10 passes, zero skips, required
  scope complete, at the runner's 96 DPI. Actual shell clicks minimize and
  restore normal/maximized windows; caption minimize, ShowAsync, single-instance
  reveal, final normal bounds and both real tray hide/reveal cases pass.
  Normal/maximized native caption PNGs were visually inspected. Artifact
  `12218607630` ZIP SHA-256 is
  `76957ff1825090060dcad9075d98ad8ffff3e7cafffa0032044b01bfb285e225`;
  its tested EXE hash equals the retained candidate above.
  The smoke distinguishes the actual
  visible client from Windows' invisible maximized frame, requires exact
  normal restore bounds and records native state/geometry before and after
  restoration. Required taskbar checks run before optional tray discovery;
  unavailable runner tray UI is reported as a skip. A discovered icon that
  loses state or bounds remains a failure. Native automation is guarded before
  path resolution or desktop access; local dry-run and PowerShell parsing
  passed without launching the app.
  Hosted testing exposed and fixed both the inherited Settings margin and the
  maximized caption clipping. Focused source cases cover native structure
  layout, multiple DPIs, monitor/taskbar edges and invalid geometry.
- No local app, game, browser or native-input automation is run on the owner's
  active desktop. Browser fixtures do not establish native WebView, game, save
  or Friend-network acceptance. Native glyph geometry across several DPIs is
  source evidence; an actual runner exercises only its current desktop DPI.
  Mixed-DPI/multiple-monitor transitions and real game/save/Friend-network
  acceptance were not exercised by this slice.

## Titlebar double-click follow-up

Double-clicking the empty titlebar, title text or app mark now maximizes the
window; double-clicking again restores its last normal position and size. The
caption handles the second left-button press before entering the native drag
loop, which consumes the mouse-up where WinForms raises `DoubleClick`. A single
press still uses the existing drag behavior; caption buttons keep their own
actions.

PASS: local app compilation with zero warnings/errors, PowerShell parsing,
dry-run and the foreground guard before any desktop/path access. No local app,
browser or native-input test was launched. The unchanged UI bundle retains
index SHA-256 `d7fae879483afe918d2d019a2789b8f45aba909476f645fcc334502f29174e44`;
the automatic [hosted UI regression](https://github.com/daltonstates/TogetherServer/actions/runs/39330394047)
also passed.

PASS: [hosted Windows native review](https://github.com/daltonstates/TogetherServer/actions/runs/39330394059)
at `adaad14151ea3809a3f95108525c8221812bc482`: all 14 checks, zero skips at
96 DPI. Physical mouse double-clicks cover all three titlebar regions and a
second moved/resized normal window. The first restores exactly to
`[40,30,840,630]`, the updated one to `[60,40,820,620]`; maximized visible
clients fill `[0,0,1024,720]`. Existing normal/maximized taskbar, caption,
ShowAsync, single-instance, tray and final normal recovery checks also pass.
Artifact `12224716036` ZIP SHA-256 is
`e2d071c0400ba54470264a8ca952908647d8cba42084e6e01c8371ad80b881c2`.

The latest handoff is now `local-data/latest-build-titlebar/TogetherServer.exe`,
the exact tested 66,071,341-byte EXE, version
`0.3.2.1+adaad14151ea3809a3f95108525c8221812bc482`, SHA-256
`d2c0cd82e12a5a27725362775e81a34f6a21f2ab453f840753d9f0501ed6a4f5`.
Static inspection verified the bundled code against the local CI-mode build
with compiler/PDB identity fields recorded separately, all seven current UI
assets, the native WebView loader and x64 GUI header. It remains unsigned and
was not launched or installed locally. Build identity, package evidence and
all native results accompany the EXE; temporary extraction is outside the
checkout under `%TEMP%/togetherserver-titlebar-39330394059`.

The owner's running copy in `local-data/latest-build` was left byte-identical
and running. Exit that copy before opening the new handoff. Actual mixed-DPI
and multiple-monitor transitions remain outside the recorded native evidence.
