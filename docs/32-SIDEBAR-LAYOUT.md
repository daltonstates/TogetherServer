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
- PASS: [final hosted Windows review](https://github.com/daltonstates/TogetherServer/actions/runs/39295264032)
  on source `c3719f38bd319f3c09320f9bc62d6d01eb8f6c5a`: 45/45 viewport plans,
  159 screenshots, zero failures, layout findings, skips, fatal errors, duplicate
  cases or missing images. Seven cases intentionally show unavailable synthetic
  data. Dragging, keyboard bounds/reset, reload persistence, temporary window
  constraints and independent desktop scrolling passed. Sixteen representative
  final screenshots were visually reviewed without findings.
  Initial hosted run 39294068837 attempted all 45 viewport plans with zero layout
  findings but failed 11 cases: seven encountered a missing ordinary Friend
  hosting-status fixture, and four tried to select a closed This PC settings
  disclosure. Run 39294698188 resolved nine failures, leaving two desktop Access
  selectors using the former label. The literal action labels now match the
  current sidebar and have a source-derived regression check. The final menu
  action also restores keyboard focus to the
  bell after opening Attention; all 123 focused navigation/resize/demo tests pass.
  The sidebar-only hosted inventory
  includes Host/Join long names, settings, first-use states, server files,
  Friend access, Attention and updates at desktop/tablet/narrow sizes and 150%
  text, including 1600px-wide and 650px-high desktop windows.
- Native WebView, process/game, real-world save and Friend-PC/network acceptance
  are outside this presentation change and have not been exercised on the
  owner's active desktop.

`ui/checks/qol-browser-smoke.mjs` has updated header-navigation paths and mobile
header assertions. Its older picker/favorite/manual-ordering journeys predate
the unified directory and remain separately unresolved; this delivery does not
claim a pass for that full legacy browser suite.

The immutable final evidence is artifact `12207621578`, ZIP SHA-256
`1f6438e56e457234f4545137786088e0d56db0bbe6231e0bc10e4a6458b6422b`.
Its built index SHA-256
`4312b845fcd05b892d41ef17ba873fad4ad309c1fe296b43ee3712e86624e256`
matches the local production UI bundle. Downloaded evidence and the separate
audit remain under ignored `local-data/sidebar-layout/review-39295264032/` and
`local-data/sidebar-layout/signoff-audit.json`. The hosted manifest is retained
unchanged; visual signoff is recorded here and in the separate audit.
