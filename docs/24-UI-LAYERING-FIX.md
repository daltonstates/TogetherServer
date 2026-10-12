# UI layering correction

Date: 2026-10-12. Scope: alerts covering the selected-server dropdown, and consistent container, menu and modal stacking. This is an unreleased UI correction on version 0.3.2.

The sticky contextual sidebar already created a stacking context, but had no explicit priority over the adjacent content. A positioned dismissible result could therefore paint above the sidebar's dropdown despite the dropdown's own high z-index. Increasing the dropdown's child value alone cannot change its parent context's priority.

## Result

`ui/src/layers.css`, imported after the layout styles, defines the shared layers. These values apply inside the nearest owning container:

| Layer | Value | Purpose |
| --- | --- | --- |
| Surface | 0 | Content and inline alerts |
| Raised | 1 | Navigation, banners and sticky controls |
| Popover | 2 | Menus inside their owning header or sidebar |
| Header | 3 | App header and local heading controls |
| Modal | 4 | Settings, setup, confirmation, update and command dialogs |
| Tooltip | 5 | Help inside the page or its open dialog |

The shell, app body, main workspace, contextual layouts, sidebars and content panes establish their own stacking contexts. Sidebars outrank adjacent content, including when narrow layouts make the sidebar a static grid item. Alerts and their absolute close buttons remain inside their own surface. Dialog bodies are contained below their sticky headers and footers. Existing numeric z-index declarations now reference the shared tokens; the header's secondary app menu also has an explicit local popover layer.

Native `showModal()` continues to place dialogs above ordinary document content and to order nested dialogs through the browser's top layer. The existing tooltip portal mounts inside its open dialog. Styling changes do not replace either behavior.

## Verification

- PASS `scripts/verify-code-only.ps1`: locked installs/restores, TypeScript, lint, .NET solution compilation with zero warnings/errors, all 20 source/synthetic groups, whitespace and desktop diagnostic isolation. Synthetic symbolic-link cases remain skipped because this account cannot create links.
- PASS **120/120 focused UI tests across seven files** for alerts, server pickers, tooltips, settings dialogs, command/navigation controls, setup dialogs and Host/Join flows. The first run passed 119/120; the existing Join-to-Host saved-selection assertion observed an empty stored preference after the correct selection rendered. That case passed in isolation, and the unchanged complete focused group then passed. No unrelated test or application behavior was changed to obtain the pass. The final JSON report is under ignored `local-data/layering-ui-tests-20261012.json`.
- PASS production UI build. Vite retains the existing advisory for a JavaScript chunk exceeding 500 kB.
- PASS **21 bundled-CSS declaration and hierarchy checks** using JSDOM with the production stylesheet and resolved root layer tokens. These verify the screenshot's sidebar/content relationship, alert containment, banner/header order, modal controls and tooltip layer. Evidence is under ignored `local-data/layering-css-evidence-20261012.json`.

## Acceptance boundaries

JSDOM does not perform browser painting or hit testing. Browser/viewport and native WebView/packaged-app visual acceptance remain unrun; they require an approved separate PC or unattended Windows session under the repository's foreground-safety rules. No app, browser, game, fixture process, installer or native window was launched. No running installation was replaced or restarted, and no production data, game worlds, credentials or network configuration was changed. No executable handoff, tag or public release is part of this correction.
