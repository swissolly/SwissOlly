# Vaktr

Local Windows system monitor. Live and historical telemetry, stored on-disk in SQLite, rendered in WinUI 3.

![Vaktr Dashboard](Vaktr.App/Assets/dashboard.png)

## Overview

Vaktr samples hardware counters on a configurable interval, persists them to a local SQLite database, and renders time-series charts and per-process breakdowns. No network calls, no cloud sync, no account.

## What it shows

![Vaktr Live Board](Vaktr.App/Assets/dashboard2.png)

Summary gauges for CPU, GPU, memory, and drives with threshold coloring at 75% and 90%.

Panels for:

| Metric | Tracked |
|--------|---------|
| CPU | Total usage, per-core, clock frequency |
| GPU | Utilization, VRAM, temperature |
| Memory | Used / available / total |
| Disk | Read/write throughput per drive, capacity |
| Network | Down/up per interface |
| Processes | Per-process CPU and memory, sortable, chart overlay |

Chart interactions:

- Click-drag to zoom, or pick a preset range (1m, 5m, 15m, 1h, 2d, 5d, 7d, 30d, 90d, 1y)
- Click a point to pin a timestamp tooltip
- Click a legend entry to isolate one series
- Panels freeze when zoomed into history
- Double-click resets zoom and clears pinned tooltips
- Panels can be reordered by dragging and resized with per-panel size presets; layout is saved locally

## Overlay

An in-game telemetry overlay that paints on top of running games. Owned by the main Vaktr process — no second window, no taskbar entry, no Alt+Tab entry. Configure from the control deck.

- Three modes: Simple (FPS only), Advanced (FPS + CPU + GPU + GPU temp + memory), Custom (pick your own)
- Corner positioning (top-left, top-right, bottom-left, bottom-right)
- Configurable text color (color picker with quick presets, or a custom hex value), font size, opacity, and toggle hotkey
- Outlined text with no background box, for a clean HUD look over any game
- Auto-shows when a game has focus; hides on Alt+Tab back to desktop
- FPS counts the game's own DXGI/D3D9 present calls via ETW (not monitor refresh, and not a count of frames confirmed displayed) — if ETW access is unavailable, Vaktr shows no FPS instead of a misleading refresh-rate estimate
- NVIDIA, AMD, and Intel GPU support

Targets borderless and windowed-fullscreen games. True exclusive fullscreen isn't supported — by design, so Vaktr never needs code-signing or elevated UIAccess to draw the overlay.

Reading the per-process present rate requires membership in the built-in **Performance Log Users** group. The installer adds you at install time, so Vaktr itself runs as a normal user process — but Windows only applies group changes to a new logon token, so **sign out and back in once after installing**, then restart Vaktr. Until ETW access is available, the FPS field shows `--` rather than the monitor refresh rate.

## Control Deck

![Control Deck](Vaktr.App/Assets/control-deck.png)

Scrape cadence (1-60s), retention (30 minutes to 10 years), storage path, theme, overlay.

### Themes

Eight palettes, picked from a swatch grid in Settings:

| Theme | Tone |
|-------|------|
| Dark | Neutral charcoal (default) |
| Light | Clean white |
| Midnight | Deep blue-black, cyan accent |
| Ember | Warm charcoal, amber accent |
| Forest | Deep green-grey, mint accent |
| Violet | Muted plum, lavender accent |
| Nord | Soft blue-grey, low contrast |
| Sandstone | Warm paper, slate accent |

Every palette keeps the same structural relationships — hairline strokes, one-step card elevation, muted text clear of the AA contrast floor — so a theme is a recolour, never a relayout. Threshold colours stay amber/red across all eight, because those encode meaning rather than style.

## Install

Installers at [Releases](https://github.com/WyrickC/Vaktr/releases):

- `VaktrSetup-x64.exe` — Intel/AMD 64-bit
- `VaktrSetup-ARM64.exe` — ARM64

### Build from source

```powershell
git clone https://github.com/WyrickC/Vaktr.git
cd Vaktr
dotnet restore Vaktr.sln
dotnet run --project Vaktr.App/Vaktr.App.csproj -p:Platform=x64
```

Or open `Vaktr.sln` in Visual Studio, set `Vaktr.App` as startup, choose Debug / x64, press F5.

## Requirements

- Windows 10 1809+ (build 17763)
- .NET 8 (bundled in the installer)
- x64 or ARM64

## Architecture

```
Vaktr.Core        Models, enums, interfaces
Vaktr.Collector   PDH counters, WMI, LibreHardwareMonitor, process enumeration
Vaktr.Store       SQLite (WAL) + JSON config
Vaktr.App         WinUI 3 shell
```

Background collector samples hardware counters every 2 seconds (configurable). Snapshots are written to SQLite with automatic retention pruning. The UI updates via DispatcherQueue. Chart rendering uses deterministic min/max bucket downsampling.

## Defaults

| Setting | Default |
|---------|---------|
| Scrape interval | 2 seconds |
| Graph window | 15 minutes |
| Retention | 24 hours |
| Storage | `%LocalAppData%\Vaktr\Data` |
| Theme | Dark (8 available) |

## Privacy

No network calls, no telemetry, no update pings, no file access outside the configured storage directory.

## Roadmap

See [ROADMAP.md](ROADMAP.md).

## License

See [LICENSE](LICENSE).
