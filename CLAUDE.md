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

## Rules for widgets in this fork
- **Always set `minW` / `minH` in `widgets.json` as low as the widget can really work** (ideally 1x1), and make it work there: lay it
  out with `@container` rules on its inner elements. A container query styles only what is INSIDE the container, and `.w` is the
  container, so wrap the content in an inner element (like `.rt-in`) and put the layout, padding and direction on that, never on
  `.w-<type>` itself. The tile is only ~48 px high on the 780x360 phone grid and ~108 px on a 1280x720 desktop; check both with a
  headless Edge screenshot against a scratch agent (`--config <scratch> --port 18765`).
- Candidates to lower later: `brightness-line` (min 2x1, could be 1x1) and `quotabars` (min 2x2).

## Desktop wallpaper behind the panel
- The dashboard window opens `.../panel/?wallpaper=1` (set in `phonebox-dashboard.bat`). `main.js` then sets `--wallpaper` and the
  class `has-wallpaper` on `<html>`; `style.css` (end of file) draws the image with a light dark tint and frosted-glass cards
  (`backdrop-filter: blur`). The phone panel and the editor preview never ask for it.
- `GET /api/wallpaper` (`Wallpaper.cs`, local port only) serves the Windows wallpaper: the registry `WallPaper` path if it is an absolute
  path to an image (jpg/png/bmp/webp/gif, 1 B to 30 MB), else `%APPDATA%\\Microsoft\\Windows\\Themes\\TranscodedWallpaper`. A request never
  names a path. Currently `C:\Users\janin\Desktop\1od2kiuhrtkg1.jpeg`. It is the primary monitor's wallpaper; per-monitor wallpapers
  are not read. Real window transparency is not possible in an Edge `--app` window (it would need the WebView2 host, option B).

## Custom widgets added in this fork
- `brightness-line` ("Brightness line", Control Deck): sun icon + one slider for one monitor, 3x1 (min 2x1). Code in
  `widgets/controls.js` (`W['brightness-line']`, reuses `deckState`/`deckCommand`/`slider`), CSS appended to `controls.css`,
  catalogue entry in `widgets.json`. Option `device` picks the monitor (blank = first that reports brightness, i.e. DISPLAY1).
- `quotabars` ("Quota bars", AI & code): the ring widget's data (`quotaring`) as filling horizontal lines, one per limit (max 3), with
  the pace tick and "resets in". 2x2 (min 2x2), option `provider` (default claude). Code in `widgets/pc.js` (`W.quotabars`),
  CSS at the end of `style.css` (`.qb-*`), palette icon in `WIDGET_ICONS` (`main.js`).
- `roomtemp` ("Room temperature", Web & data; min 1x1, default 3x2; one row high it shows value + humidity, one cell high they stack): temperature + relative humidity from the **Environment** tile of the owner's home-monitor
  page (vaponitor; server-rendered Tabler HTML, no JSON API; `<span>23.3</span>°C, <span>41.2</span>% RH`). The widget's `url` option holds
  the page address; the panel only sends `?widget=pageId/widgetId` to `GET /api/room` (`RoomSensor.cs`, regex parse, 20 s cache), and the
  fetch goes through `FeedHttp` (no loopback/link-local, no redirect to non-public hosts, 4 MB cap). **Use the plain-http LAN address
  `http://192.168.0.110:8080/`**: `https://rp.vaporized.space/` has a Caddy local-CA certificate this PC does not trust, and
  certificate checks are deliberately not bypassed. Tests: `RoomSensorTests.cs`.
- `govee-light` ("Govee light", Control Deck, 3x1, min 2x1): on/off, brightness, colour temperature over Govee's LAN protocol
  (UDP: commands to the light's 4003, replies to our 4002; scan on 4001, multicast got no reply here). `GoveeLan.cs`,
  `GET /api/govee` + `POST /api/govee/command`, option `ip` (the owner's H8076 is at 192.168.0.69, LAN Control on).
  **The light reports its old state for about 0.5 s after a command**, so the agent re-asks until the answer matches.
  The global `input[type=range] { flex: 1 }` collapses sliders stacked in a column: set `flex: none`.
- Temperature metrics show the 5-minute maximum in brackets behind the unit (`71 °C (74)`): `pushPeak`/`peakOf` in `main.js`
  (sliding-window max, fed next to `pushHist`), `metricText()` appends it, and the `cputemp`, `hotspot` and `gputemp` entries of
  `METRICS` opt in with `peak: '<key>'`. The window starts when the panel page loads, not when the agent starts.
- Adding a widget to `widgets.json` changes the count: `tools/check-release.py` requires `README.md` to say `**N widgets**`
  (currently 128). The translated READMEs are not checked and still say 124.

## Sensors (CPU temperature, GPU hot spot)
- Windows has no built-in source for these. The agent first tries LibreHardwareMonitor's web server (port 8085); when that is not
  running (the owner has none) it reads the sensors **directly** with the `LibreHardwareMonitorLib` NuGet package
  (`LhmDirect.cs`, wired into `SystemMonitor.CpuTempLoop`). Windows build only (`Compile Remove` for the cross-platform one).
- The CPU temperature needs the hardware driver, i.e. the agent must run **as administrator**; the GPU hot spot works without.
  A reading of 0 is treated as no reading. The batch file starts the agent elevated (UAC prompt, then waits for port 8765).
- An elevated agent cannot be stopped from a normal shell: use the tray icon > quit, or an elevated PowerShell
  (`Start-Process powershell -Verb RunAs ... Stop-Process -Id <pid>`), before rebuilding.

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
- `C:\Rex\Projects\other\phonebox-hide-from-taskbar.ps1` (started by the bat file): finds the Edge window of the `phonebox-edge` profile and
  sets `WS_EX_TOOLWINDOW` (removes the taskbar button and the Alt+Tab entry; the agent's tray icon stays). The window can then only be
  closed with Alt+F4 while it has focus, or by ending its msedge process; the tray menu has no dashboard entry yet. A proper fix
  would be the WebView2 host (option B: `ShowInTaskbar = false`, tray "Open dashboard").
- The bat file also passes `--lang=en-US --disable-features=Translate,TranslateUI` (no translate popup; the panel is `translate="no"` too).
- Build output: `C:\Rex\Projects\other\phonebox-build`; agent settings/data: `C:\Rex\Projects\other\phonebox-data`.
- The demo server (port 8790) is no longer used by the shortcut; `tools/demo-server.py` still works for fake-data testing.

## Checks
`python tools/check-release.py`, `dotnet test agent/PhoneBox.Agent.Tests`; Playwright UI tests in `tools/ui-tests`.
