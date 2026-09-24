# TogetherServer handoff

Continue work in `G:\repo\TogetherServer`. Treat the live checkout and current chat as authoritative. Before changing code, run `git status --short` and read `AGENTS.md`, `README.md`, `docs/00-PRODUCT.md`, `docs/01-ARCHITECTURE.md`, `docs/02-NETWORK-AND-SECURITY.md`, `docs/03-LIFECYCLE.md`, and `docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md`. Preserve unrelated work and never treat the chronological entries in `docs/06-IMPLEMENTATION-STATUS.md` as newer authority than those documents.

## Current product

TogetherServer is one Windows-first .NET 10 application with a bundled React/TypeScript interface. The same EXE provides Host and Join modes. Built-in Host drivers currently cover Valheim, Minecraft Java, and Minecraft Bedrock; an explicitly advanced Host-only Custom script driver is also present. Node and the .NET SDK are build dependencies, not runtime dependencies.

The loopback owner UI is separate from the optional companion HTTPS listener. Remote access remains off until the owner opens bounded pairing and the Host has valid TLS/pairing state. Each Friend PC has a separate revocable credential, explicit server assignments, and per-server action permissions. Friends submit only fixed actions against saved profile IDs; they never submit scripts, executable paths, world paths, or raw arguments.

Pairing uses bounded owner-opened windows, optional local approval, scoped assignments, per-device revoke, and emergency lineage revoke. Existing TS1/TS2 formats are migration-only; unscoped legacy devices receive no server access. Endpoint changes require the already pinned Host identity plus an existing device credential. Remote controls are denied during maintenance and rechecked during execution.

Managed process identity is PID, creation time, and executable path. Local owner Stop is allowed after that identity is confirmed. Remote Stop, Restart, empty-port replacement, and automatic idle Stop fail closed: the game driver's fresh authoritative `OnlinePlayers` must be exactly zero, and the Host repeats the query inside its serialized lifecycle gate immediately before graceful Stop. Positive or Unknown cancels/denies the action. Friend app presence and game-client heartbeats never establish occupancy and never gate automatic shutdown.

Crash recovery and rolling backups are opt-in and built-in-driver-only. Restore is owner-loopback-only, requires Offline, verifies the completed manifest, and creates a pre-restore snapshot. Fixture results do not prove real-game save integrity.

## Build, verification, and release state

Development version is `0.1.7`. The .NET SDK and Node versions are pinned by `global.json` and `.node-version`. Run the complete serial development matrix with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-release.ps1 -Build
```

That command builds one candidate, validates the committed NuGet lock, runs the source/process suites, and exercises the packaged companion, served, and hidden desktop surfaces against the exact candidate. It does not show the interactive desktop. Unsigned development candidates cannot exercise the real updater handoff and explicitly report that skip.

Automatic in-app updates are disabled when the installed EXE is unsigned or has an invalid Authenticode signature. A downloaded candidate must pass size, SHA-256, version, Windows Authenticode trust, and same-publisher-public-key checks at download and replacement time. Preparing a release requires a clean tree, an unused version/tag/output directory, `signtool.exe`, and a trusted code-signing certificate supplied by the owner:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-github-release.ps1 -SigningCertificateThumbprint CERT_THUMBPRINT
```

The script signs and verifies one exact candidate and prepares local GitHub assets. It does not create a tag or publish a release. Never invent signing credentials, accept game terms, download a terms-gated game binary, spend money, change firewall/router/DNS settings, or touch a valued world without explicit owner authorization.

## Acceptance boundary

Local fixtures and loopback smoke prove only the behavior they exercise. They do not prove a real Friend PC can traverse a public/private route, a real player joined, a game saved correctly, or recovery restored a recognizable world. Keep those claims open until the corresponding two-PC, owner-installed-game, zero/one/disconnect, graceful save/restart, route, and valued-world evidence in `docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md` is recorded.

When handing work back, report the exact source state, commands run, pass/fail/skip results, candidate path/hash/signature status, external prerequisites, and any real-world acceptance still outstanding. Do not tag or publish without explicit permission.
