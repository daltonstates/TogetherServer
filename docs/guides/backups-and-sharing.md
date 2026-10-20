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

## Let another PC receive saves or host

1. Enable automatic post-Stop backups in **Protection settings**, then enable
   sharing under **Backups > Sharing & hosting**. Open **Manage sharing > People**
   on the original Host and choose an approved paired PC.
2. Allow **Receive saves** for private verified copies. **Can host this save**
   enables receiving and hosting together. Turning hosting off keeps Receive;
   turning Receive off removes hosting eligibility too.
3. On the Friend PC, open the server's **Shared saves** and enable
   **Allow saves on this PC**. Receiving needs local consent as well as the
   Host's grant.
4. Use **Host on this PC** when approved to prepare hosting. The guide reviews
   the game installation, verified copy, storage, ports, and required route
   checks. Starting remains a deliberate local action.

Following signed addresses and sessions does not require storing save files.
Forwarding received saves requires a separate grant, **Forward verified
saves** (Reshare). **Manage sharing** and the older **Recovery voter** grant
are separate too. Solo recovery does not require a vote. Revocation, expiry,
and newly learned signed policy changes block new transfers and hosting
actions; offline PCs cannot learn a change until a reachable peer supplies it.

## Plan a move to a paired PC

Open **Sharing & hosting > Move hosting**, choose the approved destination
PC, and send the hosting request. The recipient accepts or declines in
TogetherServer. Preparation checks its installation and connection using
signed connection details; you do not type a destination HTTPS address.

The move safely stops the source, verifies the final transferred save, and
keeps the source from starting that moved history again. The destination
chooses **Start** separately. A successful transfer does not establish a real
game join or saved restart on that PC. See the
[planned-move delivery record](../31-PAIRED-PC-HOSTING-MOVES.md).

## Recover while the original Host is offline

An eligible receiver with local consent, trusted membership, and a complete
verified local copy can explicitly recover a separate continuation. Choose
the exact history, review the split warning, prepare fresh managed storage,
and manually Start after setup. There is no save-age cutoff, Host-loss wait,
vote, or second-PC observation required for this separate recovery.

Another Host may still be running. Both histories stay separate. Reachable
approved peers can exchange signed addresses, sessions, and save histories
while the original owner is offline. At least one remembered peer must be
reachable to learn new remote information. Inbound peer exchange needs an
explicitly enabled pinned HTTPS IP route; there is no cloud directory, relay,
or automatic NAT configuration. A game's join code alone does not enroll an
app member.

The owner or a sharing manager can select a group default. That choice does
not merge saves, stop games, switch players automatically, or give a returning
owner priority. Partial transfers and private test copies cannot become
playable recovery saves. See [peer-world design and evidence](../26-PEER-SHARED-WORLDS-DELIVERY.md).

## Share a save while the game is running

**Save and share now** has reviewed adapter candidates for Valheim, Java,
Bedrock, Factorio, and Terraria. A game name alone does not enable it. The
exact installed binary, profile, and setup must pass private six-stage
completion/copy/transfer/load/change/restart acceptance first. App evidence
covers the fixed save completion, sealed copy, and signed receipt from a
separate device; you explicitly report load, change, and graceful restart.

Source checks do not accept a real game. Changing the installed setup requires
review again. Until accepted, use the guarded post-Stop sharing path. Bedrock
save-hold recovery remains visible until fresh exact-run evidence confirms
writes resumed; resolve it before requesting another capture. Details and
open checks live in the [peer-world delivery record](../26-PEER-SHARED-WORLDS-DELIVERY.md).

## Move a world without an online handoff

Stop the old server, make a fresh backup, then choose **Prepare move kit**.
On the destination, **Inspect move kit** verifies its payload. Copy that world
into a fresh offline save location, select the locally installed game server,
and pair Friends again. Never overwrite an existing save as an import step.

The kit includes verified world files and basic server details, not game
binaries, passwords, credentials, or TLS keys. Test a real join and a
recognizable saved restart before retiring the old Host. This offline kit is
separate from the paired-PC request/accept workflow.
