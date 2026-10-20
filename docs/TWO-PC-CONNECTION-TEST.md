# Test the connection with two PCs

Use two Windows PCs on the same home network. No VM, installation, Node or .NET
SDK is needed. The connection-test ZIP contains the exact production-mode
`0.3.2.2` portable EXE from source `55ef8ad`, two launchers and these instructions.
This is a real network test; it has not been run on the second PC yet.

## Open the test apps

1. Extract the ZIP on each PC.
2. Open **Open connection test.cmd** on each PC. Use the launcher rather than
   double-clicking the EXE. The app will say **TogetherServer DEVELOPMENT**.
3. Choose Host on the first PC and Join on the other PC. Complete the name/setup
   prompts. Keep the test window on whichever monitor you prefer.

The launcher uses a separate test folder under
`%LOCALAPPDATA%\TogetherServer-TwoPc-Connection-Test\20261020-55ef8ad` and local UI
port `55318`. Your normal app, saved access and real worlds are not loaded.
The test's Friend app port defaults to TCP `5132`. Quit any older development
test using that port before enabling the test listener. No app has been launched
automatically, and the launcher does not configure the network or install anything.

## Pair and transfer a test file

1. On the Host, open **This PC settings → Network → Troubleshooting → Test with
   another owned PC → Open rehearsal**, expand **Test with another owned PC**,
   then click **Prepare connection test**. This creates test files without
   running or installing a game.
2. In **Network → Address and ports**, set the Host's home-network address and
   TCP port `5132`, then deliberately enable Friend app connections in **Access**.
   Use the address for this PC, not `127.0.0.1` or `localhost`. Find it in Windows
   **Settings → Network & Internet → your active connection → Properties → IPv4
   address**. For a LAN-only test, use **Advanced address** if the normal Internet
   setup asks for a public address. Only approve Windows firewall access on a
   trusted private network if you choose to allow it. Router port forwarding is
   not needed for this same-network test.
3. Create a server code for **Connection rehearsal**. On the other PC, join
   using that code. Approve it on the Host, assign that test server and grant
   permission to receive saves. On the Friend, turn on **Allow saves on this PC**.
4. On the Friend, open **Connection help → Troubleshooting → Run connection
   test**. Select **Same local network as Host**, then click **Run connection
   test**. Click **Copy report** to retain its result.
5. Switch the test apps' Host/Join modes and repeat with the other PC's own
   home-network address. This checks incoming connections in both directions;
   it does not by itself test the shared-hosting switch.

For this test, require **Secure app connection**, **Two-way server chat**, and
**Test file transfer and confirmation** to pass in each direction. An unavailable
or failed stage needs investigation. Outside-network, game connection and game
load/restart checks remain unverified; those entries are expected here.

## Then test the actual hosting switch

Quit the connection-test apps, then open **Open hosting test.cmd** on both PCs.
This uses normal production behavior with a different, isolated data folder:
`%LOCALAPPDATA%\TogetherServer-TwoPc-Hosting-Test\20261020-55ef8ad`. Its local UI
port is `55319`. Your ordinary production data is not opened. Unlike the first
launcher, the title says **TogetherServer**, without DEVELOPMENT. Use test
profile names such as **Hosting test A** and **Hosting test B** to identify them.

This separate mode matters: development mode requires fresh Valheim worlds and
currently refuses a restored Valheim profile when reopening the app. Do not use
the development launcher to accept the full hosting-switch flow.

Use a fresh disposable game world, with the matching dedicated server already
installed on both PCs. Do not select a valued world. Installing a game or
accepting its terms is a separate owner action. Configure each test app's own
LAN Friend connection on TCP `55320`, choose unused game ports in Setup, and
pair the test PCs again with a new server code. The first rehearsal's saved
access belongs to its separate test data.

1. On the original Host, open the selected server's **Backups → Sharing &
   hosting**, enable **Allow shared hosting**, and grant the other PC **Can host
   this save**. Keep its receive permission and local consent enabled.
2. Start and gracefully stop the test server. Confirm the other PC receives the
   completed save and the Host records its receipt.
3. On the other PC, click **Host on this PC → Continue and start**. Select its installed server files
   if requested. Resolve any reported problem and retry this same action.
4. On the original PC, open **Sharing & hosting → Other Hosts & connections →
   Connect to this Host**. Confirm the new Host/address appeared automatically
   and that Connect succeeds with the original PC's own access.
5. Join the new server from the original PC's game client. Make a recognizable
   change in this disposable world, save, stop and restart, then confirm it
   remains. Quit and reopen the hosting-test app with the same launcher, then
   confirm it reconnects. Stop the test servers and quit the test apps when finished.

Two physical PCs are enough for this flow. The file-only connection rehearsal
cannot substitute for steps 3–5. This proves the tested home-network route;
Internet hosting still needs a PC on another network.
