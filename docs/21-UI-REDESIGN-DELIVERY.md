# UI redesign: mockup review before application changes

## Authorized sequence

The owner approved the design-first plan on 2026-10-10: create a complete clickable mockup atlas, review it, then implement the application redesign after design approval. This delivery slice covers the atlas only. Application implementation is awaiting the owner's review; it has not begun.

## Design decisions

- Use the Host Settings presentation across the app: a compact application rail, a contextual section sidebar, and focused content panels.
- Give global App Settings the same Appearance, Notifications, Windows behavior, Updates and Keyboard shortcuts destinations from Host and Join.
- Group Host-wide controls under This Host PC. Keep game settings scoped to a selected server, hosting power outside global App Settings, and connection management inside Join.
- Replace the permanent Host server list with a searchable selector. Keep lifecycle, state, authoritative player evidence and current countdown beside each other.
- Use icons for most actions, including the primary lifecycle toolbar. Show tooltips and accessible names, with visible warning text, field labels and consequential confirmation choices.
- Include every current page and application-owned dialog/confirmation, including formerly inline tools and explicit mappings for dialogs merged into full pages.

## Review outputs

Editable catalogs and the shared template are under `docs/mockups/`. `together-ui-review.html` is the inline review fragment; `index.html` is the self-contained offline review. `COVERAGE.md` and `coverage.json` map the current implementation to the proposed views. `VALIDATION.json` records code-only verification.

The review uses synthetic names, reserved addresses and example values. All actions simulate results in memory. It does not connect to an app, read application storage, write a clipboard, start a game, accept terms, open a native picker, download an update or change a world. The offline version blocks network access with its content security policy.

## Completed coverage and validation

The complete atlas contains 29 pages, 149 dialogs and confirmations, and 310 source-to-design mappings, including all 28 current browser-confirmation call sites. Desktop, tablet and narrow previews, both themes, enlarged text and applicable state variants are selectable. The coverage list explicitly marks existing dialogs incorporated into pages. Native startup/fallback/close warnings have review views; native file pickers retain Windows presentation.

Final verification on 2026-10-10 passed all 18 check groups in `node docs/mockups/verify-atlas.mjs`. It rendered 1,362 states with zero JSDOM script/style errors and verified 1,077 cross-view links. Interaction checks cover identical App Settings routing from Host and Join, notification choices, independent secret reveal/copy simulation, focus-loss masking, dialog focus return, visible pending receipts, required acknowledgements, busy dismissal guards, local draft persistence, stale-result cancellation, game-specific setup and offline boot/tooltips.

The prototype preserves explicit Unknown occupancy, owner Stop review, process-identity blockers, Terraria's Listening/Unknown distinction, game-opening prerequisites and Offline backup/restore review. Staging previews retain an unmistakable development identity. PC notification defaults expose all 13 existing event choices; contextual overrides expose the 12 server events.

Bounded workers produced disjoint catalogs. A `gpt-6.1-sol` Codex CLI worker encountered Windows file-write helper failures; a bounded `gpt-6.1-sol` recovery subagent completed that catalog. Integration corrected cross-catalog aliases and source references. Intermediate checks also exposed reveal targeting, modal feedback/focus, acknowledgement labels and sample-state inconsistencies; those were corrected before the final passing run. The final atlas build and verification were serialized.

Application baseline: `30e101d5e8eb592cb4840366710b6d552c8677d8`. No changes under `ui/`, `src/` or `contracts/` were made for this slice. The application's UI suite, build/lint and `scripts/verify-code-only.ps1` are required for subsequent application changes; this documentation-only slice ran atlas-specific checks.

## Acceptance boundaries

Code-only atlas checks cover source inventory, links, script execution, individual states, accessibility names, navigation, dialog dismissal, masking, setup variants and pending feedback. They do not certify rendered pixels or native WebView behavior. Actual browser viewport/layout review and native application acceptance remain unrun and require an approved separate runner or unattended session. No such interactive check runs on the owner's active desktop.

No runtime UI, backend, public API, settings, credentials, worlds, production process or listener has been changed. Later application slices must preserve protected-draft flush, explicit discards, revocation and expiry distinctions, Unknown occupancy, exact process identity, Offline restore prerequisites, signed shared-world authority and separate game/network acceptance.

## Next authorized work

After the owner approves the complete mockups, implement the navigation/settings split and shared shell first, then compact Host, Join, supporting pages and dialogs. Use bounded workers with disjoint ownership; serialize integration/builds/commits. Run focused UI regression checks, the full UI suite and code-only gate for application changes, and record actual browser/native acceptance separately.
