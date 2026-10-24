# Host a server

[All documentation](../README.md) · [Game setup](games.md) · [Connection help](connection-help.md)

## Create your first server

1. Choose **Host on this PC** in the welcome guide, or **Servers > New server**.
2. Choose the game. The setup guide walks through Game, World, Server app,
   and **Review and start**. Game-specific installation and preview limits are in
   [Games](games.md).
3. Create a new world where supported, or select an existing save to review.
   For copy-based imports, inspect the source, destination, sizes, and dates,
   then choose **Confirm copy**. TogetherServer uses a separate managed copy
   and preserves the source. Existing Minecraft server folders are used
   directly rather than copied; discovery only reads their setup.
4. Select the installed dedicated server, review the setup, and choose
   **Save and start**. **Finish later** keeps a non-secret local draft; enter
   a game password again when you resume.
5. Use **Invite friends** to copy this server's code and share it privately.
   Prepare the Host's [connection route](connection-help.md) so Friends can
   reach it.

Game terms and installation remain your actions. TogetherServer's installer
does not include game servers or start them for you.

## Use the server workspace

**Start server** and **Stop server** stay above the tabs. Important blockers
and the empty-server countdown remain visible while you browse.

Starting shows a progress bar with **Launch server**, **Check game**, and
**Ready** steps. It shows the current status while the game starts and clears
when Ready arrives. The bar has no percentage because games do not report one.
For Valheim and Minecraft Java/Bedrock, a live **Console log** shows recent
lines below the progress bar. Pause it or scroll back to read earlier output;
**Open full logs** opens the Logs tab. You can still choose **Stop server**
while waiting on this PC; the usual confirmation applies.

| Tab | Use it for |
| --- | --- |
| **Overview** | Connection details, game requirements, status, and connection checks. |
| **Chat** | Server discussion, room membership, and an owner-pinned notice. |
| **Players** | Current reported count, timer, observation details, and added shutdown time. |
| **Logs** | Bounded server logs with pause, refresh, and filters for supported drivers. |
| **Sessions** | Retained run summaries and a seven-day activity summary. |
| **Backups** | Backup library, Sharing & hosting, and Protection settings. |
| **Settings & files** | Reviewed settings and player lists, raw supported text files, add-ons, and setup backups. |
| **Setup & maintenance** | Preparation, maintenance, and game-specific setup checks. |

Connection details are masked. Address and password have separate Show and
Copy actions; shown values hide after 30 seconds or when the app loses focus.
Sharing a Valheim password with a Friend requires its own permission.

The server directory keeps active servers first. With multiple servers,
**Compare all N servers** opens a comparison of status, player evidence,
countdowns, and backups. Missing data stays unavailable. Each running server
needs separate save storage and nonconflicting game ports; the managed-server
limit defaults to one and is configurable under Advanced PC settings.

## Invite friends and choose their access

The first **Invite friends** action creates a persistent server code and
enables the secure Friend listener when needed. The code can connect more PCs
until you replace it. Optional approval lets you review each new PC locally.
Each PC receives separate revocable access, initially assigned only to the
server whose code it used.

Open **This PC settings > Access**, choose a PC's **Manage access**, then
use its tabs:

| Control | Effect |
| --- | --- |
| **Servers** / **Choose servers** | Assign zero, one, or several saved servers. |
| Permissions | Allow Start, Stop, Add shutdown time, and View logs independently, with per-server exceptions. Game-password access is separate. |
| Permission presets | Status only, Can start, and Trusted helper set the four general permissions together and clear per-server exceptions. Mixed choices show Custom. |
| **Access time** | Set or clear an owner access deadline independently of assignments and permissions. |
| Temporary helper | Allow a one-hour or eight-hour helper grant within existing assignments; expiry or End now restores saved permissions. |
| **Remove access** for a selected server | Remove this PC from that server and keep its other server access and permissions. |
| **Remove all server access** | Revoke this PC's saved access to every server on this Host. |

Add-time and View-logs permissions start off. Start and Stop do not imply
either permission. A server assignment alone can provide status and connection
details without lifecycle controls.

Opening Friend access from a server selects that server. The global PC list
opens on **All servers**. Review the selected scope before removing access.
A new approved invitation can grant access again; removal does not ban a person
or computer. Internal credentials used for shared-world hosting are managed by
the app and do not appear as extra Friend PCs. Existing PC permissions stay
with their original access.

Access time offers 1 hour, 8 hours, 1 day, 7 days, 30 days, 90 days, or
**Clear deadline**. Advanced accepts a future local date/time with a UTC preview,
up to 365 days away, and rejects ambiguous clock-change times. At expiry the
Friend sees that owner access ended and keeps its saved connection. Extend or
clear the deadline locally; a new code is not required. This is separate from
the app's renewable device credential.

Use **Replace code and remove old access** if a code was shared by mistake.
It replaces that server's code and removes access issued through it. Removing
one PC is a separate action. Revoked records are hidden from ordinary lists;
**Show removed PCs** reveals them for confirmed **Delete PC**. Deleting a
record never makes its old credential valid again.

## Pause Friend controls

Turn remote controls off to deny new remote Start, Stop, Restart, replacement,
and add-time requests immediately. Status and heartbeat can remain available
to show the disabled state. An unreachable Friend learns the change after it
reconnects.

Per-server maintenance pauses those actions for that server. Independently
granted read-only logs and chat have their own access checks. The local owner
retains control of the managed server.

## Stop and empty-server timers

A Friend Stop needs a Ready server with a fresh reported count of exactly
zero. TogetherServer asks the driver again immediately before stopping.
Positive or unavailable counts block it. Friend Restart also needs Start and
Stop permission and cannot start again if Stop fails. The local owner may
choose Stop while people are playing; it still uses the exact tracked process
and the game's graceful Stop path. A timeout leaves the run recorded for review.

Automatic shutdown starts off. Under **This PC settings > Shutdown**, choose
the idle wait, from 1 to 1440 minutes. Exact zero starts the shared countdown;
a player or unknown count cancels it. Friend app presence never decides whether
a game is empty. The Host rechecks zero immediately before graceful Stop.

The Host can add time while a Ready server has a reliable zero or positive
count. A Friend with **Add shutdown time** can add only your configured amount
up to your per-run maximum. Added minutes survive a player joining or an
unavailable count within the same run. Stop/restart, changing the idle wait,
disabling shutdown, lifecycle recovery, or restarting the app clears them.
Restarting the app begins a fresh observed idle window.

An ordinary Start never stops another server to free a port. A Friend can
separately confirm **Stop empty server and start this one** after a managed
port conflict. Every conflict must be Ready, freshly empty, and have no saved
added shutdown time. The Host checks zero again before graceful Stop. Start
permission on the requested server is sufficient for this specific action;
it grants no general access to the other server.

## Maintain settings and files

Open **Settings & files > Game settings** for reviewed simple settings and
player lists. **Settings & files > Server files** has **Files**, **Add-ons**,
and **Setup backups**, with supported folder actions and the raw text editor.
**Prepare changes** pauses Friend controls, checks for zero players before
stopping, and makes an offline setup
checkpoint. If any step fails, maintenance stays on.

Simple settings and player lists offer an exact before/after review. The raw
editor also supports protected draft recovery. Save and Undo require
maintenance, definite Offline state, a matching current file, and a complete
setup checkpoint. **Undo last change** restores one protected prior file;
**Restore setup** restores the world and supported setup. If you edit in
Notepad, use **Reload file** afterward.

After changes, Start, check health, test a real game join and saved restart,
then use **Finish maintenance**. See [game settings and add-ons](games.md#settings-and-add-ons)
and [backup scopes](backups-and-sharing.md#world-backups-and-setup-backups).

## Recovery and Windows sleep

Crash restart is opt-in and available only to eligible built-in drivers. It
requires a previously Ready exact process to be definitively gone, waits 1,
5, then 15 minutes, and suspends after three failed launches. Factorio and
Terraria previews and Custom profiles are excluded. A requested Stop is never
treated as a crash.

**Keep Windows awake while hosting** is opt-in under Shutdown. It requests
idle-sleep prevention while an exact managed process runs; deliberate sleep,
updates, power loss, and shutdown can still interrupt hosting. After a long
sleep gap, readiness, counts, and countdowns need fresh observations.

After an app interruption, TogetherServer reattaches only to an exact recorded
process, offers an explicit **Resume hosting** for an eligible exited run, or
asks for review. Damaged control-state files are quarantined and new actions
can remain blocked until the owner resolves the notice. Dismissing a notice
does not resolve the underlying condition.

Real-game join, count transitions, graceful Stop, and recognizable save/restart
checks are still required before relying on remote Stop, timers, backups, or
recovery for a valued world. See [testing boundaries](../development/testing.md).
