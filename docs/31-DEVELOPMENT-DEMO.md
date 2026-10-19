# Isolated shared-hosting UI demo

The owner requested a development build containing fake information while production remains running. The separately named `TogetherServer DEVELOPMENT DEMO.exe` starts a dedicated demo backend and bundled UI on loopback port 5129. It uses `%LOCALAPPDATA%\TogetherServer-Demo`, with its own marker and lock. Production and ordinary development roots are rejected. Keep the exact filename, or deliberately start with `--demo`.

The demo backend starts before normal application helpers and constructs no game manager, Friend service, companion listener, updater, discovery or lifecycle supervisor. Its real API only serves instance/window information, native show and native quit. Other API requests are rejected. Demo WebView navigation and requests are confined to its own UI assets and read-only bootstrap metadata; external links, game launch links and downloads are disabled.

The browser verifies real instance metadata before installing the fake transport. The query string alone cannot enable it on production or ordinary staging. The transport handles every request in memory and rejects unsupported requests without falling back to a network call. It supplies example servers, PCs and saves, and simulates the hosting permission, preparation checklist and explicit Start actions. A permanent fake-data bar offers Host sharing, Friend hosting, missing server and offline Host scenes, plus Reset. Reset remounts the UI and discards the session's fake operations.

Build a fresh package without starting it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-demo-package.ps1 -Build
```

The package contains the renamed native EXE, a brief usage guide and static bundle evidence. Packaging refuses a nonempty destination. The verifier parses the compressed .NET single-file bundle without loading it, matches its application assembly to the current build, checks that all current UI assets are embedded, and records hashes and the Windows GUI subsystem.

Validation on 2026-10-19:

- `scripts/verify-code-only.ps1` passed: pinned SDK/Node, locked dependency installation/restore, TypeScript, lint, solution build with zero warnings/errors, all 52 source/synthetic groups including demo identity/marker/URI/middleware checks, whitespace and diagnostic isolation. The existing synthetic source-link subcases were skipped because this account cannot create symbolic links.
- Focused UI verification passed 141 tests across five files, including bootstrap trust gating, normal shell/contracts and 20 demo transport/UI cases. The whole-app owner permission -> Join -> Prepare -> explicit Start journey was then expanded and both whole-app cases passed again. No real fetch was dispatched by the demo cases.
- Packaging PowerShell parsed successfully. The Python verifier passed pure extraction, synthetic package and rejection checks; the final published EXE will also be verified before delivery.

Local evidence is ignored under `local-data/demo-code-only.log`, `demo-ui-final.log`, `demo-ui-owner-flow.log` and `demo-ui-integration.log`.

No app, game, browser, installer or native dialog was launched for testing on the owner's active desktop. Production was not launched, stopped, installed or replaced. Native visual acceptance and real game/Friend-PC/network/save acceptance remain separate and unverified by this demo.
