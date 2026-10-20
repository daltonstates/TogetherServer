@echo off
setlocal DisableDelayedExpansion
if not exist "%~dp0TogetherServer.exe" (
  echo Extract the complete connection-test ZIP, then open this launcher beside TogetherServer.exe.
  pause
  exit /b 1
)
if not defined LOCALAPPDATA (
  echo Windows did not provide a local app-data folder. No app was opened.
  pause
  exit /b 1
)
set "TOGETHERSERVER_DATA_DIR=%LOCALAPPDATA%\TogetherServer-TwoPc-Hosting-Test\20261020-55ef8ad"
start "" /D "%~dp0" "%~dp0TogetherServer.exe" --desktop --port 55319
exit /b
