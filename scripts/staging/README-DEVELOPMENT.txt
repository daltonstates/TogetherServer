TogetherServer DEVELOPMENT / STAGING
====================================

This package is a usable, persistent development instance that runs beside the stable TogetherServer app.

The obvious choice
- Double-click "TogetherServer DEVELOPMENT.exe". It selects the separate staging instance automatically.
- The development app has DEVELOPMENT in its window and tray labels, a square D tray/taskbar icon, and a bright orange DEVELOPMENT / STAGING banner.
- The ordinary stable app remains "TogetherServer.exe" with its normal title and round T icon.

Host PC
1. Keep the normal/stable TogetherServer app running as usual.
2. Double-click "Start TogetherServer DEVELOPMENT Host.cmd" (or open the development EXE and choose Host).
3. Confirm the bright DEVELOPMENT / STAGING banner is visible.
4. Create a separate development world. It persists across development launches; existing production worlds cannot be selected or copied.
5. Use the development app's Invite friends flow to create a staging-only server code.

Friend PC
1. Keep the normal/stable TogetherServer app installed and running as usual.
2. Put this development package in a separate folder.
3. Double-click "Start TogetherServer DEVELOPMENT Friend.cmd" (or open the development EXE and choose Join).
4. Confirm the bright DEVELOPMENT / STAGING banner is visible, then paste the staging server code.

Isolation
- Normal data stays in %LOCALAPPDATA%\TogetherServer.
- Development/staging data stays in %LOCALAPPDATA%\TogetherServer-Staging.
- Development profiles, credentials, settings, and worlds persist there until you remove them.
- The development app does not load or copy production profiles, credentials, settings, runs, or worlds.
- Do not manually copy production data or world folders into staging.
- Staging uses local UI port 5128 and Friend-control port 5132 by default.
- New staging game defaults are Valheim 2458-2459, Minecraft Java 25566, and Minecraft Bedrock 19134-19135.
- Windows sign-in startup and automatic release updates are disabled in development/staging.

Real friend test
- A successful local screen or Host-side port check is not proof that a Friend PC can connect.
- Test from a real Friend PC on another network.
- The staging Friend-control port and staging game's ports need their own reachable route.
- TogetherServer does not change Windows Firewall, router, DNS, or private-mesh settings for you.
- Stop the development server when you are done hosting. Its saved development setup and world remain available next time, while stable hosting stays independent.
