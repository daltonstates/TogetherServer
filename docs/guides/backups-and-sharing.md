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

Remote Start and Stop permissions are separate from this setting. Being able
to run your own server or control a friend's server does not grant access to
host their save. If hosting access is missing, the original Host must enable
**Allow friends to host this server** for that server and keep this PC approved
and assigned. Repeating the local game or port checks cannot grant permission.

On a permitted Friend PC, choose the **Host locally** icon beside Start.
If the icon is missing, check the hosting-access message on the Friend page.
An unavailable permission check has a **Check hosting access again** action;
it does not mean the owner turned sharing off. If the owner has not granted
hosting, check the original Host's setting and this PC's assigned access.

Opening **Host locally** first reads the checks. **Prepare this PC's hosting
copy** means preparation has not run yet; it does not report a failed download.
The save remains unverified for hosting until preparation checks the actual
files. A signed save announcement alone is not a checked copy.

Confirm the warning that other players will connect to your PC and network.
The app checks the latest available save, its signed history and SHA-256 file
hashes, the matching installed server, free space, local ports and the saved
connection. It prepares the world and starts when the checks pass. If something
needs your action, such as installing the server or fixing its connection,
the dialog explains it and lets you continue. Game terms remain your choice.

The five hosting checks stay visible while they refresh. The check result says
whether anything changed, when the check finished, and what to do next.
**Refresh** reads the saved setup and verified signed access; it does not
enroll this PC again or retry a failed preparation. **Check hosting access
again** is a separate explicit action that can verify enrollment and catch up
signed permission when needed.
The last hosting attempt keeps its failed step and result visible. If a check
fails, the previous results stay visible and starting waits for a successful
recheck. Choose **Show setup steps**
beside a check to open its part of the guide. The guide explains what to do on
this PC and which changes need the original owner. A sharing-history or
verification error needs review; it does not mean the owner turned sharing off.

When the copy is already prepared, the button says **Start hosting**. It uses
that copy and repeats current access, latest-save, file, server and port checks.
It does not run preparation again. Checking for new signed save information is
limited to this server; it does not wait for other saved Host connections or
repeat their save transfers. A newly learned later or conflicting save still
requires preparation or review before Start.

The original Host does not need to be online when this PC retains valid signed
hosting permission and a complete verified save. Approved reachable peers can
exchange signed updates and completed saves. Offline checks cannot show
whether another PC has newer progress or is still hosting. Disconnection is
not evidence that its game stopped. Different histories remain protected for
review; the app does not merge or overwrite them automatically.

If this PC's previous stopped progress has not been shared, the save check says
so and offers **Share stopped progress**. Keep the server stopped. This explicit
action checks the recorded Stop, every current save file and the complete setup
against that Stop's completion metadata. It can create a new verified copy when
the older backup payload changed; the old backup and the working world stay
intact. If the evidence does not match, keep every copy and review the stopped
backup and setup. The app does not pick an older save or reset sharing history.
After sharing succeeds, wait for fresh checks and choose **Start hosting**
separately. Sharing completion does not prove another PC received the save;
that requires its acknowledgement.

The guide reports what a button is doing, then shows its result and the next
step. If permission or sharing history is blocking preparation, later checks
show **Not checked** until that prerequisite is verified.

Preparing a hosting copy can take several minutes. The app shows bytes and
files prepared locally separately from receiving a save from another PC.
Those counts do not prove the final copy passed verification. Preparation
also records separate measured step times for discovery, transfer, local
preparation and verification. Launch request time is separate from the game's
actual Ready state.
Preparation continues while measured work advances; two minutes without progress or the
overall ten-minute limit cancels that attempt. Existing saves remain intact.
After a timeout, keep the source apps open and choose **Host locally** again
to resume preparation. Refresh alone does not resume it.

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

## If sharing history blocks the download

Remote Start/Stop can work while receiving a shared save is blocked. The message
**The saved sharing history changed unexpectedly** concerns the signed sharing
records, before the app requests the save files or sends a download receipt.
It does not establish a failed network check.

Use the [newest preview](https://github.com/daltonstates/TogetherServer/releases)
on both PCs and keep both apps open. History review retains the signed parent
records before advancing its trusted record. It also handles background peer
sync advancing while a sharing check is waiting for revisions. These cases can
show **The signed sharing history failed verification**, including **The
received revisions could not be joined to this PC's saved sharing history**.
On the receiving PC, open **Host locally** and choose **Check hosting access
again**. Catch-up preserves the saved connection, current permissions and
worlds. Changed, damaged or conflicting history remains blocked for review;
keep that evidence instead of resetting app data.

On the original Host, **Share latest backup** retries publication of an existing
completed post-Stop backup. Then let the permitted receiving PC continue through
**Host locally**. Check the receiver's completed verified copy and the Host's
confirmed receipt before treating the save as available on both PCs. A source
version alone does not mean another PC downloaded it.

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

Use the current preview on the original Host and other approved receiving PCs.
After a safe Stop, confirm the exact new completed save and its copy receipts,
including the original Host when it receives a continuation back. An online
app, a signed announcement, or a copy still in progress is not a confirmed
delivery. Offline PCs can catch up when reachable; removed, expired, or
nonconsenting PCs do not receive saves merely because they are online.

If two sessions produced different progress, the app keeps both and blocks an
automatic choice. Partial, damaged or private test copies never become the
latest playable save. Unshared local changes also need review before switching.

Completed post-Stop copies are the normal path. Sharing while a real game is
running stays disabled until that exact installation passes its separate
save/copy/transfer/load/change/restart checks. Hash verification alone cannot
prove a real game loads or preserves progress. See the
[current implementation and acceptance status](../06-IMPLEMENTATION-STATUS.md).

## Move a world without an online handoff

Stop the old server, make a fresh backup, then choose **Prepare move kit**.
On the destination, **Inspect move kit** verifies its payload. Copy that world
into a fresh offline save location, select the locally installed game server,
and pair Friends again. Never overwrite an existing save as an import step.

The kit includes verified world files and basic server details, not game
binaries, passwords, credentials, or TLS keys. Test a real join and a
recognizable saved restart before retiring the old Host. This offline kit is
separate from the paired-PC request/accept workflow.
