# Page sidebars and adjustable widths

2026-10-19: remove the global Servers / Attention / App Settings rail. The
existing Host, Join and App Settings sidebars now use the left edge of the
workspace. Servers stays reachable in the header, the bell menu opens the full
Attention Center, and the header gear opens App Settings. Existing navigation
guards, required setup and keyboard shortcuts remain in use.

`ResizableWorkspace` owns the three page layouts. Host and Join share an optional
local width preference; App Settings has its own. The default is 268px, with
200–520px bounds and room reserved for the main content. Shrinking a window
temporarily constrains the displayed width without losing the preferred width.
Below 701px, pages stack and hide the divider. Sidebar and content retain their
independent desktop scrolling and existing container layers.

Drag the divider, use Left/Right (Shift moves 40px), Home/End, or double-click to
reset. Its accessible separator exposes the current width, limits and controlled
pane. No new dependency, backend contract or server action is involved.

## Validation

- PASS: 116 focused UI tests across shell navigation/guards, header menus and
  resizing behavior, then all 1,244 UI tests in 99 files. The initial full run
  had one stale demo navigation selector and a 5-second three-page test timeout;
  the navigation path was updated, the round-trip passed independently, and
  its bounded timeout was increased to 10 seconds before the complete pass.
- PASS: 26 pure renderer fixture/inventory checks.
- PASS: code-only gate, including TypeScript, lint, locked dependency restore,
  solution compilation (zero warnings/errors) and all 56 source/synthetic groups.
  The source-link subcase remains skipped because this account cannot create
  symbolic links. No app, browser or game process was launched locally.
- PASS: production UI bundle. Its existing chunk-over-500kB warning remains.
- Pending: final hosted rendered layout/drag/keyboard/persistence review.
  Initial hosted run 39294068837 attempted all 45 viewport plans with zero layout
  findings but failed 11 cases: seven encountered a missing ordinary Friend
  hosting-status fixture, and four tried to select a closed This PC settings
  disclosure. Both harness gaps are corrected; a full hosted rerun is required.
  The sidebar-only hosted inventory
  includes Host/Join long names, settings, first-use states, server files,
  Friend access, Attention and updates at desktop/tablet/narrow sizes and 150%
  text. Exact results will be recorded after those checks complete.
- Native WebView, process/game, real-world save and Friend-PC/network acceptance
  are outside this presentation change and have not been exercised on the
  owner's active desktop.

`ui/checks/qol-browser-smoke.mjs` has updated header-navigation paths and mobile
header assertions. Its older picker/favorite/manual-ordering journeys predate
the unified directory and remain separately unresolved; this delivery does not
claim a pass for that full legacy browser suite.
