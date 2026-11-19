# PhoneBox (personal fork)

This is the owner's **fork** of ersinkoc/phonebox (latest upstream version, 0.9.22 at fork time). They make their own edits here.

## Goal of this fork
Run the dashboard **locally on the PC** (Windows 11), fullscreen, on the **second monitor**, preferably **not in a browser tab**.
No Android phone, adb, Wi-Fi pairing or Tailscale is wanted. Keep everything on loopback (Wi-Fi option stays off, the default).

## How it works (verified in the code)
- `agent/PhoneBox.Agent`: .NET 10 agent. Windows target = WinForms tray app + Kestrel on `127.0.0.1:8765`. Settings page is `/`.
- The dashboard UI is plain HTML/JS (no build step) in `android/app/src/main/assets/www/` (`main.js`, `widgets/*.js`, `style.css`).
  The agent embeds it and already serves it at `/panel/` (`Program.cs` ~line 330); `/agent/api/...` is rewritten to `/api/...` (~line 159).
  So `http://127.0.0.1:8765/panel/` shows the live dashboard on the PC.
- Widgets: catalogue in `agent/PhoneBox.Agent/widgets.json`, templates in `templates.json`; grid size from `Catalog.Cols/Rows` (phone-shaped canvas, about 780x360 CSS px).
- `window.Android` is a native bridge (dimming, kiosk, dictation); not available on desktop.
- The agent is a background process; a window is only a viewer. It samples only what is on screen.

## Plan / options discussed
- A: Edge `--app` window (done, via bat file below). Still a browser engine.
- **B (preferred): fullscreen borderless WinForms window with WebView2 inside the agent**, tray item "Open dashboard", monitor picker, F11/Esc.
- C: separate Tauri/Electron app. D: native rewrite (not recommended).
- Open issue: make the grid/canvas desktop-sized (the editor canvas is phone-shaped); new templates for widescreen.

## Custom widgets added in this fork
- `brightness-line` ("Brightness line", Control Deck): sun icon + one slider for one monitor, 3x1 (min 2x1). Code in
  `widgets/controls.js` (`W['brightness-line']`, reuses `deckState`/`deckCommand`/`slider`), CSS appended to `controls.css`,
  catalogue entry in `widgets.json`. Option `device` picks the monitor (blank = first that reports brightness, i.e. DISPLAY1).
- `quotabars` ("Quota bars", AI & code): the ring widget's data (`quotaring`) as filling horizontal lines, one per limit (max 3), with
  the pace tick and "resets in". 2x2 (min 2x2), option `provider` (default claude). Code in `widgets/pc.js` (`W.quotabars`),
  CSS at the end of `style.css` (`.qb-*`), palette icon in `WIDGET_ICONS` (`main.js`).
- Adding a widget to `widgets.json` changes the count: `tools/check-release.py` requires `README.md` to say `**N widgets**`
  (currently 126). The translated READMEs are not checked and still say 124.

## Local environment (as of 2026-11-19)
- .NET 10 SDK (10.0.404) installed via winget. Python 3.10 is available.
- The user-level NuGet config (`%APPDATA%\NuGet\NuGet.Config`) has **no package sources** (left as is), so pass the source per build.
- Build (from `agent/PhoneBox.Agent`; rerun after every code edit, with the agent stopped):
  `dotnet build -f net10.0-windows10.0.19041.0 -c Release -o C:\Rex\Projects\other\phonebox-build --source https://api.nuget.org/v3/index.json`
- The built agent runs with an isolated config and port: `PhoneBoxAgent.exe --config C:\Rex\Projects\other\phonebox-data\config.json --port 8765`
  (see docs/DEVELOPMENT.md "Isolated agent runs"). Panel: `http://127.0.0.1:8765/panel/`, settings: `http://127.0.0.1:8765/`.
- Stop the agent: tray icon > quit, or `Stop-Process -Id (Get-NetTCPConnection -LocalPort 8765 -State Listen).OwningProcess`.
- Do **not** download the upstream release or run `setup.ps1`; the owner wants to build their own fork.
- Demo with fake data: `python tools/demo-server.py` then `http://127.0.0.1:8790/panel/` (settings at `/settings/`).
- Monitors: DISPLAY1 primary 2194x1234 at (0,0); DISPLAY2 1234x823 at (2194,410).

## Helper files (outside the repo)
- `C:\Rex\Projects\other\phonebox-dashboard.bat`: starts the built real agent if port 8765 is free, then opens the panel in an Edge app
  window, fullscreen on monitor 2 (`--window-position=2294,460`). Desktop shortcut: `Desktop\PhoneBox Dashboard.lnk`.
- Build output: `C:\Rex\Projects\other\phonebox-build`; agent settings/data: `C:\Rex\Projects\other\phonebox-data`.
- The demo server (port 8790) is no longer used by the shortcut; `tools/demo-server.py` still works for fake-data testing.

## Checks
`python tools/check-release.py`, `dotnet test agent/PhoneBox.Agent.Tests`; Playwright UI tests in `tools/ui-tests`.
