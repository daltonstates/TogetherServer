# Live save sharing acceptance

The built-in adapters currently describe candidate save controls. **Live capture is disabled for every game.** TogetherServer shares only its hash-verified post-Stop backup until the owner records the game-specific test below. A command reply, file timestamp, open port, or running process does not establish a complete save.

The Java candidate has an internal exact managed-process console dispatcher and a disposable fixture check. It sends the fixed `save-all flush` text only after the recorded PID, start time, executable, and console members match. The fixture confirms receipt while the process remains running. The dispatcher reports only that the command was sent; it does not report flush completion, create a live snapshot, or publish a shared version. There is no Host or Friend action for it.

| Game | Candidate save control | Completion evidence still needed |
| --- | --- | --- |
| Valheim | Wait for its configured autosave; no reviewed manual live-save command | Completion from the exact managed run, a consistent copy, and a load test. [Dedicated-server guide](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/) |
| Minecraft Java | Fixed `save-all flush` to the exact managed console | Correlate command and completed flush with that run, then verify a copied world loads. |
| Minecraft Bedrock | Fixed `save hold`, `save query`, and `save resume` sequence | Copy the held snapshot described by the query and always resume writes, including on failure. Test a load. [Save command](https://learn.microsoft.com/en-us/minecraft/creator/commands/commands/save?view=minecraft-bedrock-stable) |
| Factorio | Fixed `/server-save` through authenticated local RCON | Confirm the save ZIP has closed and loads; an RCON reply alone is insufficient. [Server save command](https://wiki.factorio.com/Console), [multiplayer saving](https://wiki.factorio.com/Multiplayer) |
| Terraria | Fixed `save` to the exact managed console | Confirm completion from that run and load a copied world. [Dedicated-server commands](https://terraria.wiki.gg/wiki/Dedicated_Server) |

For **each game separately**, use a disposable staging world on a real Windows Host and Friend PC. Make a recognizable in-game change while the server runs, complete the candidate save, transfer and verify it on the Friend PC, load it with a newly configured Host, then gracefully restart and confirm the change remains. Record game/app versions, the exact completion signal, failed or interrupted attempts, and the direct-IP route. Enable that game's live capture only after the test passes and the adapter can stage an immutable, hash-verified snapshot without exposing a half-written world. Until then the interface offers no “Save and share now” action.
