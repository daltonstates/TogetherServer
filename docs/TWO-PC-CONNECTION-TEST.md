# Test the connection with two PCs

Use two owner-approved Windows PCs and the same reviewed app version. Run
automated checks only in an explicitly approved separate test session.
Prepare a fresh [development package](development/README.md#develop-beside-the-stable-app)
for the synthetic connection rehearsal. Each PC keeps its own test data and
access; never copy production settings, credentials or valued worlds into it.

## Pair and transfer synthetic data

1. Open the development app on each test PC. Choose Host on one and Join on the
   other, and complete setup.
2. On the Host, open the network troubleshooting rehearsal and choose
   **Prepare connection test**. This prepares synthetic files without a game.
3. Select the Host's actual network address and deliberately enable its Friend
   connection. Use an assigned LAN address for a same-network test; loopback
   cannot reach the other PC. The default development Friend port is TCP 5132.
   The owner handles any required network permission separately.
4. Share the rehearsal server's code privately, connect the Friend and approve
   its access on the Host. Grant receiving and enable **Allow saves on this PC**
   on the Friend.
5. Run the Friend connection test with the correct network-scope label. Retain
   the redacted report. Require secure app access, two-way chat, and completed
   test-file transfer with confirmation to pass.
6. Reverse the PCs' roles and repeat with the other PC's own address and code.

A same-LAN pass proves that route only. Outside TCP, game connection and real
game load/restart remain separate checks. A WAN claim needs an actual test PC
on another network.

## Accept shared hosting separately

The synthetic rehearsal cannot accept a real hosting move. Development mode
also restricts imports and restored worlds; it cannot certify every production
hosting path. Prepare separately approved isolated test instances for those
paths, with fresh disposable worlds and unused ports on both PCs.

1. The owner installs the required dedicated server and handles its terms on
   both PCs. Pair the isolated test instances with their own codes.
2. Enable **Allow friends to host this server** on the original Host and grant
   the other PC hosting access. Keep its local save consent enabled.
3. Start and gracefully stop the disposable server. Confirm the other PC has
   the complete verified save and the Host records its receipt.
4. On the other PC, choose **Host locally**, resolve its checks, then explicitly
   start the prepared server. Confirm signed discovery identifies the new Host.
5. Reconnect from the original PC, join with a real game client, make a
   recognizable change, gracefully stop and restart, and confirm it persists.
   Reopen the test apps and check saved-access reconnection separately.

Record app/game versions, network scope, passed and unfinished stages, and
remaining failures. Keep worlds, raw production logs and private details out
of Git. Follow the [current limitations](06-IMPLEMENTATION-STATUS.md) and
[full acceptance requirements](04-IMPLEMENTATION-AND-ACCEPTANCE.md).
