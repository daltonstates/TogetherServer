# Settings flow redesign

Date: 2026-10-10. Scope: the owner's request for separated Friend cards, readable access reviews, icons, and compact Settings with focused dialogs. Source remains version 0.3.2; this work is not a new published release.

## Result

Settings uses an icon navigation rail on larger windows and wrapping navigation on smaller windows. The section heading stays in normal flow, avoiding the previous fixed sticky-header offset. App preferences, timer values, connection tools, and advanced Host controls have summary cards with explicit editor actions. Diagnostics remains its existing read-only workspace.

Friend access keeps the two global switches visible and gives each PC its own bordered card with identity, authenticated-heartbeat freshness, assignments, and grants. Expired/revoked/approval/helper states remain distinct. Filtering to no matches offers Clear filters instead of claiming no PC has paired.

Manage access opens one native dialog for the selected PC. Permissions, Servers, Access time, and PC details are separate tabs. A changed preset shows a reviewed grant comparison before Apply; unchanged presets do not render an unnecessary review. Before/after access uses labeled permission rows and compact unchanged-server disclosure instead of a long-sentence table. Individual permission switches retain their existing immediate-save behavior and independent grants.

Server edits, pending names/presets, and deadline choices retain their scope. Tabs preserve the deadline editor. Unsaved PC changes require deliberate discard before closing; server-only Save retains other unfinished edits. Temporary-helper projections cannot be edited as saved permissions. Busy deadline reload also blocks helper changes. Revoked cards cannot open an unavailable editor, and access removal has a visible in-dialog confirmation.

Host settings editors have local Save/Cancel drafts and submit only their intended fields merged with the current canonical settings. Cancel does not dirty server setup. Timer values reject invalid whole numbers and an extension maximum smaller than one increment. Save failures retain the editor and inputs. App preference results are visible inside the active dialog, and update checking closes its editor before opening the separate update review.

Dialogs have accessible titles, visible Close/Done/Cancel actions, busy dismissal guards, focus return to connected openers, one scrolling body, and fixed header/footer areas. Notification and update-reminder saves report their private busy state so closing cannot abort or hide their results. CSS retains wrapping, text scaling, narrow-layout targets, high contrast, and reduced motion.

## Ownership and validation

The coordinator integrates the flows, shared icons, save handlers, tests, documentation, package, and commit. Two bounded presentation workers own disjoint component/CSS files; a third worker performs read-only review. Workers inherit the configured model without an override; the exact identifier is not exposed by this session. Shared builds and tests are serialized.

Source and synthetic validation is complete. Logs are retained under ignored `local-data/settings-*.log`.

- PASS complete JSDOM suite: **729 tests across 70 files**, 41.38 seconds in the final complete run.
- PASS `scripts/verify-code-only.ps1`: pinned tools, locked installs/restores, TypeScript, zero-warning lint, solution compilation with **zero warnings/errors**, all **20** source/synthetic groups, whitespace, and desktop diagnostic isolation. Source-link subcases are explicitly skipped because this account cannot create symbolic links.
- PASS final UI TypeScript and production bundle. The existing JavaScript chunk advisory remains: 928.10 kB before gzip, above the 500 kB advisory threshold.
- PASS browser-harness JavaScript syntax and served-smoke PowerShell parsing; neither interactive runner was executed. The browser harness now opens/closes the Appearance dialog, preserves inline baseline comparisons, and adds future wide/narrow dialog captures.
- PASS all **112** served-UI text assertions against the built JavaScript, without launching an app or listener.
- PASS candidate publish without launch. The unsigned working-tree artifact is `local-data/settings-flow-candidate-20261010/TogetherServer DEVELOPMENT.exe`, **65,677,565 bytes**, version **0.3.2**, SHA-256 **4844E114BFD055144A9FCA923A25E6241914D56D1D83F8449555B77D092CBB79**. Its informational version retains the pre-edit HEAD `9574c30c6c73fdc7e74e4b78c3d499b5629af68c`; it embeds the newly built UI. The DEVELOPMENT filename selects the existing isolated staging instance when deliberately opened. It was not launched, installed, copied over production, or published.

Early checks exposed an incorrect card ternary and existing tests that assumed inline rehearsal/notification controls. They were corrected. New integration checks also distinguish the app's existing read-only public-address POST from a device mutation and compare against decoded settings rather than a raw fixture. The initial code-only run stopped on two unused references after moving the settings; the unused setup binding was removed and the public-address failure message was preserved in the new connection view. Review caught hidden modal feedback, tab draft loss, helper/notification-save timing, and relocated control styles; those corrections are included in the final validation.

## Acceptance boundaries

Only source compilation, JSDOM, byte inspection, and the code-only verifier run on the owner's active desktop. No app, native window/dialog, browser automation, process/console fixture, game, installer, listener, production data, credential, or real world is used. Hidden/tray/headless labels and SkipDesktop do not establish focus safety.

Actual WebView pixels, supported viewports/text sizes, native keyboard/focus, installed-package behavior, and Friend-PC/network/game/save journeys require an explicitly approved separate test PC or unattended Windows session. Those checks remain unrun, not passed. Source and JSDOM behavior do not certify visual layout, WAN reachability, game joins, or saved-world integrity. Packaging a development candidate does not replace or restart the running app.
