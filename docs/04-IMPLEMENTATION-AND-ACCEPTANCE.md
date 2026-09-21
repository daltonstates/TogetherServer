# Implementation order and acceptance

Keep the project small and deliver working vertical slices. Start implementation in the new chat; these documents are the handoff, not the implementation.

## Slice 1 — Local Host app and GUI

- Create one Windows .NET 10 app that shows bundled React/TypeScript assets in its own window over a loopback-only local API, with a clear Host/Friend mode switch. Build and publish without requiring Node at runtime.
- Add local settings for an approved server executable/profile, maximum concurrent managed servers (default 1), idle minutes (initially disabled), and remote-control disabled state. Keep secrets and worlds outside the repo.
- Implement fixed local Start, Stop, and Health actions with a small purpose-built synthetic process fixture. Verify duplicate Start, one-writer-per-world, max count, port conflicts, process identity, crash/restart Unknown, and that Stop never kills an unrelated process.
- The GUI must show actual state, pending actions, and errors. Do not label a fixture or running process as a real Valheim server. This slice is accepted only after a Windows build, meaningful process tests, and a served React GUI check.

## Slice 2 — Friend mode and public-control protocol

- Add per-device pairing, pinned Host TLS identity, Friend outbound heartbeat, saved Host endpoint, per-friend Start/Stop permissions, and typed remote actions. Use one small HTTPS listener; the Host GUI remains loopback-only.
- Implement the owner remote-control toggle and notice. Disabled remote Start/Stop must fail server-side immediately; status/heartbeat still communicates Disabled. Revoke one device and prove its credential stops working while another remains valid.
- Test retry/idempotency, invalid token, wrong certificate, wrong Friend permission, stale/Unknown heartbeat, Host restart, and at least two instances on separate local processes. These tests do not prove public Internet reachability.
- Test the everyday GUI path: new or copied world, world name and password in one compact setup, direct Start, one-click creation/copy of the reusable code, immediate listener enable/disable, scoped refresh revocation, and action-first Host/Friend cards. Verify the custom native minimize, maximize/restore, and close controls. Verify 1440x900, 768x1024, and touch-enabled 390x844 layouts without horizontal overflow. TS1 and TS2 per-device invites remain readable until expiry.
- Test remote Stop first with a disposable Valheim console fixture: an exact permitted-player list, assigned player IDs, fresh closed-game reports, denial when reports or the list change, and a permitted Friend Stop over HTTPS. This is process and policy evidence, not proof of a real Valheim join or save.
- Test the game-driver registry with at least Valheim, an isolated synthetic driver, and fail-closed handling of unknown kinds. For each real driver, verify its declared ports, start-time conflict check, local listener diagnostics, readiness evidence, and graceful stop. A local open port must not be recorded as public reachability.

## Slice 3 — Real Valheim and friend smoke

- Use the owner-installed, owner-approved Valheim Dedicated Server. Verify the selected build, actual startup/readiness, game ports, graceful Ctrl+C-equivalent stop, save folder, and recognizable world change across stop/restart against [upstream guidance](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/). Do not download or accept terms on the owner's behalf.
- Add real Valheim-client process detection on Host and Friend PCs; prove fresh true/false transitions, disconnected/stale Unknown, and the idle timer's final recheck. Auto shutdown remains disabled when companion coverage or permitted-player enforcement is incomplete.
- With the owner's explicit approval, test the chosen public-IP control port from an actual Friend network and the game join path separately. Do not change public router/firewall/DNS settings automatically. A localhost test is not a public reachability pass.
- Record a real Friend start/stop permission test, disable notice, actual game client join, save, stop, restart, idle behavior, and Host recovery. Keep any missing external test marked **blocked**, not passed.

## V1 acceptance gate

V1 is accepted only when the owner can run the bundled Host app; every Friend uses the bundled Friend mode; remote Start/Stop and disable/revoke work over the agreed public-IP route; max-concurrent and one-world writer rules hold; auto shutdown never treats missing heartbeat as zero; Valheim starts, accepts a real friend, stops gracefully, and preserves a recognizable world change through restart. Record exact versions, commands, screenshots/log excerpts with private details removed, and pass/fail/skip counts.

## External gates and data protection

The owner must handle game terms and deliberately initiate any Minecraft installation in the app, public router/firewall approval, credential exchange, and the real Friend/client test. Valheim installation remains an owner/Steam action. Continue independent code and local fixture testing while those are pending. Never delete or overwrite a real world for a test. Create isolated synthetic data under ignored `local-data/` and leave the owner's existing game install and worlds alone.

## Minecraft extension gate

Java and Bedrock have separate built-in drivers. Their fixture checks cover validated settings, TCP/UDP local status responses, concurrent profiles with separate save folders, and fixed graceful console Stop. Setup checks cover bounded file detection, consent denial, fresh Java/Bedrock installs through synthetic HTTP responses, hashes, archive safety, and existing-world preservation. Before claiming real support for either edition, test an owner-installed server, local status response, actual Friend join, recognizable world change after Stop/restart, and the intended network route. Keep Minecraft remote Stop and auto shutdown unavailable until that edition has a verified player-coverage rule.
