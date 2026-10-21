# Join a friend's server

[All documentation](../README.md) · [Connection help](connection-help.md) · [Shared saves](backups-and-sharing.md)

## Connect with a server code

1. Open the same TogetherServer app on your PC. Choose **Join a friend** in
   the welcome guide, or **Servers > Connect**.
2. Paste your friend's server code and choose **Connect**. If approval is
   required, wait for the Host to approve this PC.
3. Open the assigned server. Check **Game requirements** before playing.
4. If allowed, request **Start**. The startup bar shows the launch and game-check
   steps while you wait for the server to report **Ready**.
   For supported games, console lines appear below it when the Host has granted
   **View logs**. **Open full logs** opens the complete log viewer.
   Use the connection details and the game's own join screen to connect.

Keep TogetherServer running while using Friend controls. A code initially
assigns this PC to one server. The Host may assign more later without another
code. Selecting a different server or saved Host does not stop heartbeats or
anything you host on this PC.

## Check requirements and open the game

**Game requirements** shows the required game/version and supported enabled
Factorio mods or Bedrock world packs. Detected, owner-reported, and manually
entered versions have distinct labels. Unknown stays unknown; matching
requirements does not prove a successful join. Bedrock client-pack compatibility
remains unknown. Factorio inspection covers the default client mod folder,
not custom launch options.

For Valheim, Factorio, and Terraria, **Open game** asks Steam to open an already
installed client after you click it. It uses fixed game IDs and does not join
the server or pass an address or password to the game. Missing or incomplete
installations and unavailable handlers lead to manual steps.

For Minecraft, open it from Windows Start or your own launcher, then use
Multiplayer/Servers and the card's copied details. Bedrock and Terraria show
the server address and port separately. A game password, when shared with
this PC, has separate Show and Copy actions; seeing a server does not itself
grant password access.

## Understand the controls

| What you see | What it means |
| --- | --- |
| **Start** | The Host has allowed this PC to start this assigned server. |
| **Stop** | Needs permission, a Ready server, and a fresh zero-player count, checked again before Stop. |
| **Restart** | Needs both Start and Stop permission and a successful guarded Stop first. |
| **Add shutdown time** | Needs a separate permission; the Host chooses the increment and per-run maximum. |
| **View logs** | Needs a separate grant for this server and a supported active-run log adapter. |
| Maintenance / controls paused | Status can remain visible while lifecycle requests are denied. |
| Access expired | The owner ended access at a deadline and can extend or clear it; the saved connection remains. |
| Disconnected / Unknown | The Host could not be reached or its current state is unverified. |

The timer comes from the Host. An unavailable player count is never treated
as an empty server. Terraria currently has no trusted count, so Friend Stop
and automatic Stop stay blocked. See [hosting safety](hosting.md#stop-and-empty-server-timers).

**Logs** shows bounded, sanitized records for a supported exact active run.
Pause, refresh, and filters help inspect them. Logs are diagnostics; they do
not establish a player count or prove a saved world is healthy. An old Host
may require an update before it can share logs or other newer features.

## Use server chat

Open **Chat** for the selected server. Assignment, active access, and room
membership are required. Start, Stop, and View-logs permissions are separate;
chat can work while the game is stopped or remote controls are paused.

Messages are limited to 500 characters. Participating PCs keep protected
copies of the newest 200 messages from 30 days. A disconnected Friend can
queue up to 20 drafts for delivery when the Host returns. Unfinished composer
text can be recovered for its exact saved connection and room. Queued entries
can be edited or canceled before submission; accepted messages and uncertain
submissions remain fixed until their result is known.

The owner can add or remove assigned PCs through **People in this room** and
maintain one **Pinned notice** of up to 2000 characters. Notices are signed
and kept independently of chat history. A disconnected copy is labeled as
cached. Removal prevents the next sync but cannot recall messages already
copied to a PC.

Chat needs a reachable Host companion listener. Friends cannot exchange new
chat messages directly with one another while that Host is offline. Shared
world peer exchange has a separate purpose and does not add offline chat.

## Manage saved connections

Rename a saved Host to make it easier to recognize. **Forget** first asks a
reachable Host to remove this PC's access. If offline, it removes the local
credential and warns you to ask the Host to remove the stale PC too.
**Show removed access** reveals connections with confirmed revoked access so
you can delete them. A timeout alone is never recorded as revocation.

If the Host address changes, open the Friend server's **Settings** and choose
**Change Host address**. The new endpoint must prove the already pinned Host
identity and accept your existing access. Never bypass an identity mismatch. Some expired
or long-offline clients need a fresh pairing code.

Current server codes include the initial server and Host identity and remain
active until the owner replaces them. Older TS1/TS2 invites keep their original
expiry rules and need explicit server assignment; TS1 also asks for the Host
IP under **Using an older invite?**.

Notifications and the Attention Center collect important connection, access,
server, and backup events. **Clear activity** hides currently shown events
locally; new events still appear and the Host's retained record remains.
