# Getting started

[All documentation](../README.md) · [Host a server](hosting.md) · [Join a server](joining.md)

## Install on Windows

Download `TogetherServer-Setup-win-x64.exe` from
[GitHub Releases](https://github.com/daltonstates/TogetherServer/releases).
The installer runs for your Windows account without an administrator prompt,
adds a Start-menu shortcut, and registers an uninstaller under
**Settings > Apps > Installed apps**. The portable
`TogetherServer-win-x64.exe` is available on the same release.

You need Windows x64 and the Microsoft Edge WebView2 Runtime. The app bundles
its .NET runtime and interface; Node and the .NET SDK are only for building
from source. Published builds are currently unsigned, so Windows may show
**Unknown Publisher**.

As of October 20, 2026, [stable is 0.3.2](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.2)
and [0.3.2.2 is a preview](https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.2.2).
The published 0.3.2 updater cannot discover four-part version tags: download
and install the preview directly the first time. Guides describe current
source and preview work; an older release may have different labels or fewer
features.

## Finish first-use setup

1. Open TogetherServer and enter **Your name** when asked. An existing saved
   name is kept after an update. Change it later in **App Settings > Profile**.
2. In the welcome guide, choose **Host on this PC** or **Join a friend**.
   Return to either walkthrough through **Guides** in the top bar. The empty
   server workspace also offers **Host a server** and **Join a server**.
3. Follow [hosting](hosting.md) to prepare a server, or [joining](joining.md)
   to connect with a friend's server code.

The name appears beside your PC label and on new chat posts. It is a display
name, not proof of identity or a permission. Installation itself does not
install or start a game server, copy a world, enable public access, or change
network settings.

## Find your way around

| Place | What it contains |
| --- | --- |
| **Servers** | Your servers, servers you host for friends, and Friend servers; **New server** and **Connect** start the main tasks. |
| Local server workspace | Overview, Chat, Players, Logs, Sessions, Backups, Settings & files, and Setup & maintenance. |
| Friend server workspace | Play, Chat, Logs, Shared saves, and Settings, subject to access and feature availability. |
| **This PC settings** | Access, Shutdown, Network, and Advanced settings for hosting on this PC. |
| **Notifications** | Recent notices and **Open Attention Center** for items that need review. |
| **App Settings** | Profile, appearance, notifications, Windows behavior, updates, and keyboard shortcuts. |

Hosting and connecting to other Hosts run together. Selecting another server
or opening App Settings does not stop a game or its Friend connection.

Drag a sidebar divider to resize it. The focused divider supports Left/Right,
Shift for larger steps, Home/End, and double-click to reset. Server pages share
a remembered width; App Settings has its own. Narrow windows stack the sidebar
above the content.

## Use the tray and Windows sign-in options

**Open at Windows sign-in** and **Close to tray** start off. Enable them in
App Settings when you want them; the installer also offers these optional
choices.

- Sign-in startup opens TogetherServer quietly in the tray under your account.
- Close to tray makes the window's X hide the app while hosting and Friend
  checks continue. Double-click its tray icon to reopen, or right-click for
  **Open** and **Quit**. Opening the EXE again also brings back the running app.
- Quit asks you to stop or resolve managed servers first.

For the portable app, move the EXE to its intended location before enabling
sign-in startup. Keep the app running while hosting or using Friend controls.

## Install updates when you are ready

The production app checks for updates at startup and every 30 minutes.
**App Settings > Updates** lets you check on demand. Stable releases are the
default. **Include prerelease versions** opts only this PC into previews.
Turning it off returns to stable checks without downgrading the installed app.

Review the release notes, then choose **Update and restart**. Stop hosted
servers first: active or unresolved runs block the update. You can defer
reminders or skip a version's reminder and still update later. The app never
installs or restarts unattended.

Previews use your normal settings, pairings, servers, and worlds. Preview
enrollment does not create an isolated test app. Contributors who need that
should use the [development package](../development/README.md#develop-beside-the-stable-app).
See [update verification](../development/releases.md#how-the-app-verifies-an-update)
for hashes, publisher trust, checkpoints, and rollback limits.

## Where your data lives

Normal app data lives in `%LOCALAPPDATA%\TogetherServer`. The installed EXE
lives separately in `%LOCALAPPDATA%\Programs\TogetherServer`. Uninstalling
removes the app and shortcuts while preserving the data directory, including
settings and managed worlds.

Data storage is versioned. Older app versions can refuse a directory written
by a newer version; rollback requires a matching pre-migration backup. Never
lower a schema marker by hand. See [recovery and rollback](../development/releases.md#data-and-rollback).
