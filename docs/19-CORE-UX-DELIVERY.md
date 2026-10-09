# Core hosting and joining UX

The owner's 2026-10-09 direction replaces the previous Overview hierarchy: people should first see how to create or join a server, start or stop it, and get the details needed to play. Diagnostics, history, compatibility inventories and maintenance support those tasks.

## Delivered behavior

- The selected Host server has a compact name/status header and one primary Start or Stop above its tabs. The controls remain available in the other tabs. Active, fresh empty-server countdowns stay visible. Recovery, maintenance and unsaved-edit blockers remain explicit; lifecycle endpoints and owner player-impact confirmations are unchanged.
- Overview leads with masked connection details for a running server and the local join address. An Offline server gives one short next step. Version/add-on inventory, status/network checks, restart/checkpoint options, health and access tools remain findable through named disclosures or their owning tabs. Routine Offline/status explanations no longer repeat above Start.
- All server tabs use the same content inset and gap. Tabs have icons and scroll on narrow screens. The Sessions filters, match count and list now have internal padding instead of touching their containing border.
- Players leads with the current trusted count and empty-server timer. Stale/unavailable/display-only warnings stay visible. Count source/freshness, detailed timer evidence, driver help and recent count changes start collapsed. Add-time tools follow the count and are disclosed separately. Unknown never becomes zero and an invalid/stale deadline never becomes a valid countdown.
- Friend Play leads with Start when Offline, or Open game and masked connection details when available. Permitted Stop stays in the main action area, with occupied/unknown/stale/paused/maintenance guards. Detailed join instructions and server tools are secondary; requirements remain available after connection details. Each pending action gets its own label and spinner.
- Create and Join have equal first-use buttons. Saved Host and Join pages offer direct navigation to the other core task. First-server draft recovery, saved-connection access and guarded navigation remain intact.
- Chat Sync now immediately shows a disabled Syncing button and spinner, then a short success/error result. It has a separate single-flight request and cancels late results after scope changes or closure. Background polls cannot complete that manual request; an offline local copy cannot claim a successful Host sync.
- The executable now embeds a multi-size production ICO. Native window/tray code loads durable embedded production or distinct development ICOs rather than a temporary single-size drawing. Both include 16 through 256-pixel 32-bit frames and transparency masks. A byte-only check validates the assets without constructing a Form, Icon or window.

## Ownership

The coordinator owns integration, the common action/header/tab layout, Friend/first-use flows, shared icons/spacing, documentation, validation and commit. Bounded workers used the configured `gpt-6.1-sol` with max reasoning: native icon resources and Chat sync through subagents; Players through Codex CLI. File ownership was disjoint. Builds, integration checks and commits are serialized.

## Validation

The authorized source and synthetic validation is complete. Only source compilation, JSDOM, synthetic/fake-adapter checks and `scripts/verify-code-only.ps1` ran on the owner's active desktop.

- PASS focused interactions: 136 tests across AppFlows, FriendPlayFlow, PlayersPanel, ServerChat and HostFirstServer.
- PASS complete JSDOM regression suite: 670 tests in 65 files, 35.11 seconds.
- PASS UI TypeScript, lint and production bundle. Vite retains the existing warning for a JavaScript chunk over 500 kB; the bundle is 902.50 kB before gzip.
- PASS final `scripts/verify-code-only.ps1`: pinned tools, locked installs/restores, TypeScript/lint, solution compilation with zero warnings/errors, all 20 source/synthetic groups including embedded icon bytes, whitespace and diagnostic isolation. Native-import source-link subcases remain SKIPPED because this account cannot create symbolic links.
- PASS candidate publish with the new bundled UI and icons, without launching it. A byte-only PE inspection confirms all ten native executable icon frames match the reviewed production ICO exactly.

The unsigned working-tree candidate is `local-data/core-ux-candidate-20261009/TogetherServer DEVELOPMENT.exe`, version 0.3.1, SHA-256 `DB70D821DB9F6A3A22E7FB2F9538CF18F1242DC857AE8D9127688385C7C843C3`. The DEVELOPMENT filename selects the existing isolated staging path when deliberately opened. It was not opened, installed or copied over production, and it is not a published release. Logs are retained under ignored `local-data/ux-core-workers/`.

The initial full JSDOM run passed 653 cases and failed 12. Failures exposed old first-use naming and pre-join ordering assertions, repeated hidden status/recovery content, an incorrect hidden-disclosure assertion, and an overlong combined tab-navigation case. The duplicate content was removed, expectations now describe the owner's new hierarchy, and navigation coverage is split into bounded cases. The focused and full final UI runs pass.

The initial icon failure was a mistaken assertion that the 16-pixel development frame must contain an exactly zero-alpha pixel. Its antialiased corner samples have alpha 3 through 35 while its orange tile and dark D are opaque and visible. The corrected check requires low-alpha corners, an opaque correctly colored center, visible palette/glyph areas, valid frame directories and matching legacy masks; it does not weaken blank-icon detection. The icon assets are unchanged.

Read-only integration review identified and corrected an always-visible Start bypassing file-editor draft guards, countdowns being hidden with supporting tools, and Friend Stop losing its action-specific pending feedback. Focused regressions cover those behavioral boundaries.

## Acceptance boundaries

No TogetherServer app, browser, native window/dialog, fixture/process/console runner, installer, listener or game is launched by this work on the active desktop. Hidden/tray/headless labels and SkipDesktop do not establish focus safety. No AllowInteractiveTests switch is used. Production data, credentials, managed worlds, public routing and the running production app are untouched.

Supported viewport/layout review, enlarged text and keyboard interaction in the actual WebView, Windows taskbar/shortcut/DPI/tray behavior and installed package review require an explicitly approved separate test PC or dedicated unattended Windows session. These remain unrun, not passed. Source/icon bytes and JSDOM order/visibility do not certify actual pixel layout, taskbar cache behavior, network reachability, real game joins, saves or restart recovery.
