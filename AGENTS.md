# TogetherServer repository instructions

## Scope

Build one small Windows-first game hosting application. The same .NET app is installed on the Host PC and every Friend PC, with Host and Friend modes. Bundle a React/TypeScript interface into the app so Node and a separate web server are not runtime dependencies. Valheim, Minecraft Java, and Minecraft Bedrock dedicated servers are external processes controlled by reviewed built-in Host drivers. Minecraft support is fixture-tested until real owner-installed game acceptance is recorded. V1 has no Docker, PostgreSQL, cloud service, billing, Paper/modded servers, or commercial hosting.

Read `README.md`, `docs/00-PRODUCT.md`, `docs/01-ARCHITECTURE.md`, `docs/02-NETWORK-AND-SECURITY.md`, `docs/03-LIFECYCLE.md`, and `docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md` before implementation. The user's current-chat instructions override these files. Preserve data and unrelated changes.

## Delivery

- Work in bounded vertical slices. Start actual code in the first implementation turn; do not stop at a plan or build an orchestration framework.
- Prefer one .NET 10 app with bundled React assets, explicit Host/Friend modes, and simple local durable settings. Use current stable, verified dependency versions and commit lockfiles.
- Keep the public companion API small: authenticated heartbeat, status, and fixed start/stop requests. A remote user must never submit a script, shell command, executable path, world path, or raw arguments.
- Keep the owner's GUI on loopback. Expose the companion API only when the owner deliberately enables it and the configured TLS and pairing requirements are satisfied.
- Give each friend/device a separate revocable credential and explicit permissions. Turning remote controls off is enforced on the Host before the Friend UI receives the notice; heartbeat/status may remain available to communicate the disabled state.
- A missed or stale heartbeat is Unknown for Friend connection status, but Friend app presence never gates automatic shutdown. The game server's reported count is the only occupancy authority: exact zero starts the countdown, while a positive or Unknown count cancels it. Recheck the count inside the lifecycle gate before automatic Stop.
- Track managed game processes by recorded identity, not name alone. Never kill an unrelated process. Gracefully stop and confirm save behavior before claiming real Valheim support.
- Do not claim a fixture or process-exists check proves game readiness, remote reachability, a friend join, world-save integrity, or restart recovery.
- Use focused meaningful tests, then real Windows/process/browser and friend-network tests for the surface changed. Record passes, failures, and skips honestly.

## Large tasks and delegation

- For large tasks, use Codex CLI workers and subagents to complete the authorized scope without overloading the coordinator's context. Give each worker a bounded task, the relevant repository instructions, and a compact handoff rather than the full conversation history.
- Keep worker ownership explicit. Use separate files or isolated worktrees for concurrent edits, and use read-only reviews when a shared build or package check is running. Serialize shared builds, package output, integration, and commits.
- The primary agent remains responsible for integrating findings, validating each completed vertical slice, committing authorized work, and continuing through the full requested scope. Record completed work, remaining checks, and acceptance boundaries in the repository's delivery document so another worker can resume safely.
- Use the configured workhorse model and disclose the worker model when known. Do not select `gpt-6-astra` unless the owner explicitly requests it. Delegation does not authorize access to production data, credentials, or real worlds, or relax any security, lifecycle, or hard-stop rule below.

## Preserve the owner's focus during testing

- Automated work must not take keyboard focus or change the foreground window. Preserve the game or application the owner is using. Use `scripts/verify-code-only.ps1` and other checks that do not launch app windows, game processes, console fixtures, native dialogs or browser automation on the active desktop.
- Hidden windows, tray mode, headless labels and `-SkipDesktop` do not prove focus safety. Console creation/attachment, graceful console commands, app relaunch and native desktop checks can still disturb the foreground. Do not run the full release gate, direct process-check runners, desktop smoke or installer smoke on the owner's active desktop.
- Run those checks only on a separate test PC or a dedicated unattended Windows session with explicit owner approval. The guarded scripts require `-AllowInteractiveTests`; setting that switch is not permission to bypass this rule. Hosted Windows CI may opt in explicitly because it uses a separate runner; do not infer approval from environment variables.
- Include this constraint in every worker handoff. If a test takes focus or the owner reports interference, stop the exact identified test runner and its disposable processes, keep production and unrelated processes running, and record unfinished checks. Do not keep retrying, restore focus after stealing it, or mark skipped checks passed.

## Hard stops

Do not accept Valheim or other game terms, download a terms-gated game binary, spend money, change public firewall/router/DNS settings, request real credentials, or delete/overwrite real worlds without explicit owner authorization. No public listener is enabled by default. Keep secrets, personal identifiers, worlds, binaries, and production logs out of Git.
