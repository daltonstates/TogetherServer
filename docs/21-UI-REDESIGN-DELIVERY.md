# UI redesign: mockup review before application changes

## Authorized sequence

The owner approved the design-first plan on 2026-10-10: create a complete clickable mockup atlas, review it, then implement the application redesign after design approval. This delivery slice covers the atlas only. Application implementation is awaiting the owner's review; it has not begun.

## Design decisions

- Use the Host Settings presentation across the app: a compact application rail, a contextual section sidebar, and focused content panels.
- Give global App Settings the same Appearance, Notifications, Windows behavior, Updates and Keyboard shortcuts destinations from Host and Join.
- Group Host-wide controls under This Host PC. Keep game settings scoped to a selected server, hosting power outside global App Settings, and connection management inside Join.
- Replace the permanent Host server list with a searchable selector. Keep lifecycle, state, authoritative player evidence and current countdown beside each other.
- Use icons with short visible labels for primary workflow and lifecycle actions. Navigation and utility controls remain compact icons with accessible names and anchored tooltips. Keep warning text, field labels and consequential confirmation choices visible.
- Include every current page and application-owned dialog/confirmation, including formerly inline tools and explicit mappings for dialogs merged into full pages.

## Review outputs

Editable catalogs and the shared template are under `docs/mockups/`. `together-ui-review.html` is the inline review fragment; `index.html` is the self-contained offline review. `COVERAGE.md` and `coverage.json` map the current implementation to the proposed views. `VALIDATION.json` records code-only verification.

The review uses synthetic names, reserved addresses and example values. All actions simulate results in memory. It does not connect to an app, read application storage, write a clipboard, start a game, accept terms, open a native picker, download an update or change a world. The offline version blocks network access with its content security policy.

## Completed coverage and validation

The initial atlas contained 29 pages, 149 dialogs and confirmations, and 310 source-to-design mappings, including all 28 current browser-confirmation call sites. Desktop, tablet and narrow previews, both themes, enlarged text and applicable state variants are selectable. The coverage list explicitly marks existing dialogs incorporated into pages. Native startup/fallback/close warnings have review views; native file pickers retain Windows presentation.

Initial verification on 2026-10-10 passed all 18 check groups in `node docs/mockups/verify-atlas.mjs`. It rendered 1,362 states with zero JSDOM script/style errors and verified 1,077 cross-view links. Interaction checks cover identical App Settings routing from Host and Join, notification choices, independent secret reveal/copy simulation, focus-loss masking, dialog focus return, visible pending receipts, required acknowledgements, busy dismissal guards, local draft persistence, stale-result cancellation, game-specific setup and offline boot/tooltips.

The prototype preserves explicit Unknown occupancy, owner Stop review, process-identity blockers, Terraria's Listening/Unknown distinction, game-opening prerequisites and Offline backup/restore review. Staging previews retain an unmistakable development identity. PC notification defaults expose all 13 existing event choices; contextual overrides expose the 12 server events.

Bounded workers produced disjoint catalogs. A `gpt-6.1-sol` Codex CLI worker encountered Windows file-write helper failures; a bounded `gpt-6.1-sol` recovery subagent completed that catalog. Integration corrected cross-catalog aliases and source references. Intermediate checks also exposed reveal targeting, modal feedback/focus, acknowledgement labels and sample-state inconsistencies; those were corrected before the final passing run. The final atlas build and verification were serialized.

Application baseline: `30e101d5e8eb592cb4840366710b6d552c8677d8`. No changes under `ui/`, `src/` or `contracts/` were made for this slice. The application's UI suite, build/lint and `scripts/verify-code-only.ps1` are required for subsequent application changes; this documentation-only slice ran atlas-specific checks.

## Revised atlas: primary actions and guided sharing

The owner reviewed the first atlas and approved a mockup-only revision on 2026-10-10. The revised atlas opens on Shared saves, with a world/Host heading and Get save → Prepare hosting → Start server path. The next action is prominent and labeled. Manage sharing contains named Sharing, Saves & receipts, History & recovery, and Setup & checks sections. Permissions, receipts, votes, history, conflicts and rehearsals remain reachable without filling the landing page.

Client previews cover Original Host PC, the next Host (Friend PC A), and Friend PC B. A complete planned journey receives the initial copy, prepares the final post-Stop save on the original Host, receives/confirms that exact final copy, completes the hosting decision, restores fresh managed storage, checks the pinned control connection from another approved PC, then manually Starts. Start remains Starting until a separate status observation. Other clients review/connect to the new Host before showing fresh Ready or enabling Open game. Hosting authority, completed save and current gameplay are distinct.

The shared preview retains separate Receive, Eligible host, Recovery voter, local consent, trust, pinned identity and approval checks. Recovery retains continuously observed Host loss, explicit risk acknowledgements and two independent votes in the three-voter example; its decision leaves the server Offline. Host-loss status never proves the old game process stopped. Competing histories show disputed authority. Client/world changes cancel pending results and preserve prior copies. Ordinary lifecycle controls use the same shared state; More cannot bypass incomplete hosting setup.

Host and Join defaults now emphasize the selected server and the main task. Host supporting evidence, backup protection/tools, server folders and maintenance context moved into focused dialogs. Join Play uses its sidebar selectors rather than a repeated saved-Host list. Search and pins remain in a named Saved Hosts picker. Chat, backup/restore, settings and maintenance pages lead with their task, while explicit Logs, Diagnostics and Connection help pages retain detail.

One product-owned tooltip layer serves inline and offline presentations. Its tail follows the triggering control, the box stays within narrow edges, and placement flips at the lower edge. Keyboard focus, Escape dismissal, ARIA description cleanup and touch opening share the same implementation. Geometry assertions use synthetic DOM bounds; actual rendered pixels remain unverified.

Final revised verification: **24 PASS groups, 1,414 rendered states, zero JSDOM script/style errors**, 29 pages, 158 dialogs/confirmations, all 310 source mappings and all 28 browser-confirmation call sites. The static catalog has 1,026 cross-view links; dynamic guide/management navigation is exercised separately. Primary-action labels, the complete three-client journey, independent grants/trust, exact current-copy guards, peer route checks, nested detail focus, stale identity, pending scope changes and tooltip anchoring have focused checks. Final fragment size is 274,921 bytes. `git diff --check` and the unchanged `ui/`, `src/`, `contracts/` boundary also passed.

Two bounded `gpt-6.1-sol` workers owned the Host and Join catalogs. The coordinator owned the shared model, shell, tooltips, build and verification; integration/builds were serialized. A read-only Codex CLI review encountered the Windows `setup refresh` helper failure and was stopped; read-only subagent reviews completed instead. Those reviews caught stale lifecycle banners, missing independent permission checks, an incorrect Friend voting instruction, stale readiness in denied states, and a duplicate Open game dispatch. Corrections are included in the final pass. Intermediate verification also caught a missing icon and an outdated offline tooltip selector; a Stop regression was corrected to perform its required acknowledgement before submission.

This revision changes only mockups/documentation. Browser/native layout acceptance and real game/network/save acceptance remain unrun. Approval of the revised atlas is pending before application implementation.

## Acceptance boundaries

Code-only atlas checks cover source inventory, links, script execution, individual states, accessibility names, navigation, dialog dismissal, masking, setup variants and pending feedback. They do not certify rendered pixels or native WebView behavior. Actual browser viewport/layout review and native application acceptance remain unrun and require an approved separate runner or unattended session. No such interactive check runs on the owner's active desktop.

No runtime UI, backend, public API, settings, credentials, worlds, production process or listener has been changed. Later application slices must preserve protected-draft flush, explicit discards, revocation and expiry distinctions, Unknown occupancy, exact process identity, Offline restore prerequisites, signed shared-world authority and separate game/network acceptance.

## Next authorized work

After the owner approves the complete mockups, implement the navigation/settings split and shared shell first, then compact Host, Join, supporting pages and dialogs. Use bounded workers with disjoint ownership; serialize integration/builds/commits. Run focused UI regression checks, the full UI suite and code-only gate for application changes, and record actual browser/native acceptance separately.
