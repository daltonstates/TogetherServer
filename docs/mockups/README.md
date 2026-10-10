# UI review atlas

This design review uses synthetic, local-only content. It does not load app settings, credentials, worlds, logs, or running-process state. Review actions change only the mockup.

Application implementation is gated on the owner's review of the complete atlas. The application source and backend remain unchanged during this design slice.

The atlas opens on the simplified Shared saves page. Primary actions retain icons and short labels. Supporting controls live in More or named detail sections; one tooltip layer with an anchored tail works in both presentations.

To review a hosting transfer, start as Next Host and get the save. Use Client to switch to Original Host PC for Stop/final-copy preparation, back to Next Host for receiving the final copy, then Original Host PC to complete the transfer. Prepare hosting on Next Host, check its connection from Friend PC B, and return to Next Host for manual Start and the separate status check. Shared saves, Play and Host Overview retain the same hosting decision. Other clients explicitly connect to the new Host before showing fresh readiness or opening the game.

State selects standalone blocked/recovery/progress examples and resets that world's scenario. Page navigation and client changes preserve completed steps; changing a client or world cancels a pending simulated action. No operation is sent to another PC.

The catalogs, shared template, and builder are the editable source. The generated `together-ui-review.html` is an inline review fragment; `index.html` is its self-contained offline version. Rebuild with `node docs/mockups/build-atlas.mjs` and verify with `node docs/mockups/verify-atlas.mjs`.

The generated coverage table maps existing pages, dialogs, and confirmations to their proposed destinations. App preferences use one mode-independent destination, while operational controls stay within Host or Join.

Automated validation stays code-only on the owner's active desktop. Browser and native-window acceptance require an approved separate test session or hosted runner.
