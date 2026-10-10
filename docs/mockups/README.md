# UI review atlas

This design review uses synthetic, local-only content. It does not load app settings, credentials, worlds, logs, or running-process state. Review actions change only the mockup.

Application implementation is gated on the owner's review of the complete atlas. The application source and backend remain unchanged during this design slice.

The catalogs, shared template, and builder are the editable source. The generated `together-ui-review.html` is an inline review fragment; `index.html` is its self-contained offline version. Rebuild with `node docs/mockups/build-atlas.mjs` and verify with `node docs/mockups/verify-atlas.mjs`.

The generated coverage table maps existing pages, dialogs, and confirmations to their proposed destinations. App preferences use one mode-independent destination, while operational controls stay within Host or Join.

Automated validation stays code-only on the owner's active desktop. Browser and native-window acceptance require an approved separate test session or hosted runner.
