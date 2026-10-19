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
bounds still apply to shell maximize and the app's own maximize control.

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

## Validation

- PASS: all 1,244 UI tests in 99 files and 26 pure renderer fixture checks.
- PASS: the serialized code-only gate, including locked dependencies,
  TypeScript, lint, solution compilation with zero warnings/errors and all 57
  source/synthetic groups. Its existing symbolic-link subcase is skipped when
  this Windows account cannot create symbolic links.
- In progress: refreshed bundled production artifact.
- Pending: dedicated hosted browser review with pane-edge and shared scrollbar
  assertions, and native Windows taskbar/tray/restore smoke. Native automation
  is guarded before path resolution or desktop access; local dry-run and
  PowerShell parsing passed without launching the app.
- No local app, game, browser or native-input automation is run on the owner's
  active desktop. Browser fixtures do not establish native WebView, game, save
  or Friend-network acceptance. Native glyph geometry across several DPIs is
  source evidence; an actual runner exercises only its current desktop DPI.
