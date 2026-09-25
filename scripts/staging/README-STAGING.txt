TogetherServer STAGING
======================

This package is for disposable testing beside the stable TogetherServer app.

Host PC
1. Keep the normal/stable TogetherServer app running as usual.
2. Double-click "Start TogetherServer STAGING Host.cmd".
3. Confirm the orange STAGING banner is visible.
4. Create a fresh staging world. Existing production worlds cannot be selected or copied.
5. Use the staging app's Invite friends flow to create a staging-only server code.

Friend PC
1. Keep the normal/stable TogetherServer app installed and running as usual.
2. Put this staging package in a separate folder.
3. Double-click "Start TogetherServer STAGING Friend.cmd".
4. Confirm the orange STAGING banner is visible, then paste the staging server code.

Isolation
- Normal data stays in %LOCALAPPDATA%\TogetherServer.
- Staging data stays in %LOCALAPPDATA%\TogetherServer-Staging.
- The staging app does not load or copy production profiles, credentials, settings, runs, or worlds.
- Do not manually copy production data or world folders into staging.
- Staging uses local UI port 5128 and Friend-control port 5132 by default.
- New staging game defaults are Valheim 2458-2459, Minecraft Java 25566, and Minecraft Bedrock 19134-19135.
- Windows sign-in startup and automatic release updates are disabled in staging.

Real friend test
- A successful local screen or Host-side port check is not proof that a Friend PC can connect.
- Test from a real Friend PC on another network.
- The staging Friend-control port and staging game's ports need their own reachable route.
- TogetherServer does not change Windows Firewall, router, DNS, or private-mesh settings for you.
- Stop the disposable staging server when the test is complete. Stable hosting remains independent.
