# TogetherServer

TogetherServer is a small, Windows-first Valheim hosting app for one owner and a known group of friends. This folder is a **new project**. It does not depend on or modify `G:\repo\LetsServive`.

The same app will run in **Host** mode on the owner's PC and **Friend** mode on each player's PC. One app process on each PC serves its own bundled React interface; the Valheim dedicated server remains a separate game process managed by Host mode. Friends use the companion to report whether their Valheim game is running and, when the owner permits it, request start and stop actions over a public-IP connection.

This folder contains requirements and a new-chat implementation prompt. **No application code or game binaries have been added yet.**

## Start in a new chat

1. Open a new Codex chat with working directory `G:\repo\TogetherServer`.
2. Paste the contents of [START_NEW_CHAT.md](START_NEW_CHAT.md) as the first message.
3. Keep the new chat in this folder. Do not resume the old GameHost worker or modify `G:\repo\LetsServive`.

The first implementation slice should produce a working local Host app and React GUI using a synthetic process fixture. Later slices add the Friend companion, authenticated public control connection, and real Valheim validation. [docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md](docs/04-IMPLEMENTATION-AND-ACCEPTANCE.md) gives the order and acceptance checks.

## Owner gates

Do not accept game legal terms, download terms-gated binaries, open host/router firewall ports, change DNS, spend money, or alter/delete real worlds without explicit owner authorization. The owner will install/approve Valheim Dedicated Server and perform the real friend join test. The app must never silently treat an interrupted heartbeat as proof that a player left.
