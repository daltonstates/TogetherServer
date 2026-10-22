# Backups and shared hosting

[All documentation](../README.md) · [Hosting](hosting.md) · [Games](games.md)

## Protect a world

Open the server's **Backups** tab. It groups **Backup library**,
**Sharing & hosting**, and **Protection settings**. Review and save protection
changes in their focused editor.

| Tool | What it does |
| --- | --- |
| Manual checkpoint | Copies the reviewed save files while the server is definitively Offline. |
| Back up after graceful Stop | Optional rolling copy after the exact process exits successfully. Starts off. |
| Verify backup | Re-reads the completed manifest and hashes every payload file without changing the live world. |
| Copy to vault | Copies a completed backup to your selected folder, verifies destination hashes, and keeps the local copy. Choose another drive or PC for protection from local disk loss. |
| Test restore | Copies into disposable app scratch storage, verifies the manifest, then removes that scratch copy. |
| Test a copy in the game | Prepares a fresh disposable copy and guides a real load, recognizable change, and saved restart test. |
| Restore | Requires definite Offline state, verifies the chosen backup, and takes a pre-restore snapshot before replacing the managed save. |
| Safe restart | Graceful Stop, then a successful rolling or manual checkpoint, then Start. A failed checkpoint leaves the server Offline. |

Use **Review backup** to inspect a copy's actions and results. Search and
optional filters help find backups by name, pin, kind, or local date. A server
can pin up to 20 backups totaling 50 GiB in addition to ordinary unpinned
retention. Unpinning does not immediately delete a copy. A name or pin does
not establish integrity or create another-device protection.

Backup, vault, and restore operations are local-owner tools. Friends do not
receive a general backup browser or restore API. Custom profiles have no
built-in backup path because their arbitrary working directory is not a
reviewed save-only folder.

## World backups and setup backups

Each completed backup protects the driver's reviewed world/save files. New
backups also try to capture supported settings, managed add-ons, and recorded
setup details. A completed world payload can remain valid when complete setup
metadata is unavailable; those backups are labeled world-only and cannot be
used for **Restore setup**.

Use the backup library for ordinary world restore.
**Settings & files > Server files > Setup backups** exposes the complete setup
checkpoint and restore tools.

Setup backups exclude game binaries, Java runtimes, Friend credentials, app
preferences, Custom scripts, and unsupported external packs. They need the
same Windows user's protected local setup metadata. A vault copy or move to
another account transfers a world and requires fresh local configuration and
pairing. **Restore setup** verifies the world and supported setup, takes a
current checkpoint, and applies them while Offline. Interrupted restoration
can block Start until the owner resolves it.

## Test that the game can load a copy

**Test restore** proves that copied files match their manifest. It does not
prove the game can load them. Use **Test a copy in the game** on a completed
backup or a currently verified received save for that check.

The guide creates separate disposable storage. Valheim can use an isolated
driver trial with the owner-installed binary, new ports, and a new password;
other games or missing binaries use manual steps. You report load, a
recognizable saved change, and graceful restart explicitly. Original worlds,
backups, and received-copy receipts remain intact. A synthetic fixture trial
does not accept a real installation.

## Let friends host this server

On the original Host, open **Backups > Sharing & hosting** and turn on
**Allow friends to host this server**. That is the shared-hosting setting.
Approved PCs assigned to this server automatically keep and share its completed,
verified saves. Safe post-Stop backups are enabled with it. Newly approved PCs
receive the same permission; removing a PC or its server access still works.

On a permitted Friend PC, choose the **Host locally** icon beside Start.
Confirm the warning that other players will connect to your PC and network.
The app checks the latest available save, its signed history and SHA-256 file
hashes, the matching installed server, free space, local ports and the saved
connection. It prepares the world and starts when the checks pass. If something
needs your action, such as installing the server or fixing its connection,
the dialog explains it and lets you continue. Game terms remain your choice.

The five hosting checks stay visible while they refresh. **Last checked** shows
the age of the displayed results. If a retry fails, the previous results stay
visible and starting waits for a successful recheck. Choose **Show setup steps**
beside a check to open its part of the guide. The guide explains what to do on
this PC and which changes need the original owner. A sharing-history or
verification error needs review; it does not mean the owner turned sharing off.

Hosting requires a configured incoming connection. Confirmation can enable
that saved app connection; TogetherServer never changes Windows Firewall,
router forwards or DNS. Local port and outside TCP checks do not prove that a
particular player can join the game.

## If another PC has never received a save

Sharing must publish a completed save on the Host before another PC can receive
it. In preview 0.3.3.4 and later, open **Backups > Sharing & hosting** on the
original Host and choose **Share latest backup**. It retries an existing
completed backup after Stop, with its original checked files and setup. You do
not need another Stop when that backup already exists. If there is no completed
backup after Stop, stop the server normally once to create the first one.

Keep both apps open while the other PC receives and verifies the copy. **Latest
save available** describes the Host's copy; the confirmed receipt count shows
whether another PC has actually received it. Check any error before hosting.
Retry does not start a game, replace the live world or change permissions.

Preview 0.3.3.4 also fixes Valheim's normal `//` permissions-file comments being
misread as player entries. That bug could block first publication even with
completed backups, valid permissions and enough free space. Update both PCs;
you do not need to edit the game's permissions file to remove its comments.

## Continue from the latest save

Suppose A hosts and B receives the completed session save. With A offline,
C can get that save from B and host it. After C stops safely, B and C keep the
new completed save. When A returns, it can receive the newer continuation
from C and use Start to continue from it. The original world stays preserved
in its existing location while a newer continuation uses fresh managed storage.

The app chooses by verified ancestry, rather than the original Host's identity,
PC clocks or save numbers alone. You do not manage save permissions, forwarding,
votes or a group-default Host. A PC that only received saves can forward them
over an outbound connection to another approved PC's enabled listener.

Keep TogetherServer open long enough to finish copying after Stop. Another
reachable approved PC must have the completed save to survive the current Host
going offline. At least one previously learned pinned peer address must still
work; the app has no cloud relay or automatic NAT setup. Offline PCs cannot
learn new access removals until authenticated information reaches them.

If two sessions produced different progress, the app keeps both and blocks an
automatic choice. Partial, damaged or private test copies never become the
latest playable save. Unshared local changes also need review before switching.

Completed post-Stop copies are the normal path. Sharing while a real game is
running stays disabled until that exact installation passes its separate
save/copy/transfer/load/change/restart checks. Hash verification alone cannot
prove a real game loads or preserves progress. See the
[implementation and verification record](../36-SHARED-HOSTING-SIMPLIFICATION.md).

## Move a world without an online handoff

Stop the old server, make a fresh backup, then choose **Prepare move kit**.
On the destination, **Inspect move kit** verifies its payload. Copy that world
into a fresh offline save location, select the locally installed game server,
and pair Friends again. Never overwrite an existing save as an import step.

The kit includes verified world files and basic server details, not game
binaries, passwords, credentials, or TLS keys. Test a real join and a
recognizable saved restart before retiring the old Host. This offline kit is
separate from the paired-PC request/accept workflow.
