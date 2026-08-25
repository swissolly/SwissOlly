# Vaktr Roadmap

## Current Version: v1

## Planned

### CPU Temperature Monitoring
CPU temperature sensors on AMD and Intel require kernel-level access (PCI config space / MSR registers) that Windows restricts to signed kernel drivers. Every monitoring app that shows CPU temps (HWiNFO, Core Temp, MSI Afterburner) ships a signed driver for this.

LibreHardwareMonitor supports CPU temps through the [PawnIO](https://pawnio.eu/) kernel driver. The plan is to bundle PawnIO into the Vaktr installer so CPU temperatures work out of the box with no extra setup.

**Status:** GPU temps work today. CPU temp panel exists in the UI — just needs the driver bundled.

### Panel Resizing
Allow users to resize panels between standard (1x1), wide (2x1), and tall (1x2) sizes. Panel sizes would persist across sessions just like panel positions do today.

**Status:** Architecture designed, implementation deferred to a future release.

### Notifications & Alerts
Toast notifications when metrics cross configurable thresholds — e.g., CPU > 90%, memory > 85%, drive > 95% full. Gives you passive monitoring without keeping the window open.

**Status:** Planned.

### Data Export
Export historical metric data as CSV or JSON for analysis in external tools. Useful for post-incident investigation, capacity planning, or just curiosity.

**Status:** Planned.

### Prometheus Metrics Endpoint
Expose Vaktr's collected metrics via a local HTTP endpoint in Prometheus exposition format. This lets advanced users scrape Vaktr from a Prometheus instance for long-term storage, alerting, or multi-machine dashboards — bridging the gap between Vaktr's simplicity and Prometheus/Grafana's power.

**Status:** Planned.

## Shipped

### v1.2.0 — In-Game Telemetry Overlay
A compact, always-on-top overlay that renders FPS and selected system metrics on top of running games. Owned by the main Vaktr process — no second window, no taskbar entry, no Alt+Tab entry.

- Three display modes: Simple (FPS only), Advanced (FPS + CPU + GPU + GPU temp + memory), Custom (pick your own)
- FPS measured via DWM flush timing — no DLL injection, no ETW session, no anti-cheat conflicts (safe with Vanguard, EAC, BattlEye), no admin rights or elevated group membership required
- Auto-shows when a game has focus; hides when focus returns to desktop
- Corner positioning (top-left, top-right, bottom-left, bottom-right)
- Outlined text with no background box — reads cleanly over any game background
- Configurable text color via a color picker with quick presets (or a custom hex value), font size (10–32px), opacity (30–100%), and toggle hotkey
- Configured entirely from the existing control deck — no second settings window
- Click-through layered Win32 window: `WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` — invisible to the shell, no code-signing or UIAccess required
- Resource cost: ~0.4–0.6% CPU, ~12 MB memory added

**Known limitations in v1.2.0:**
- Exclusive fullscreen isn't supported — an intentional scope decision. Vaktr targets borderless and windowed-fullscreen games only, which avoids ever requiring code-signing or elevated UIAccess.
- HDR games render dim (SDR overlay). Deferred to v1.3.0.

**Follow-ups tracked for v1.3.0:**
- HDR-aware overlay rendering

### v1 — Polished Local Telemetry Dashboard
- Live time-series panels for CPU, GPU, memory, disk, network, system activity
- At-a-glance summary cards with live utilization gauges and color-coded thresholds
- GPU temperature monitoring (AMD and Nvidia, no driver needed)
- Per-process CPU and memory breakdown with chart toggle for top processes
- Drag-and-drop panel reordering with direct swap and position persistence
- Chart zoom (click-drag selection and preset ranges from 1m to 30d)
- SQLite persistence with rolling retention and 1-minute rollups
- Dark and light themes with instant switching
- Configurable scrape interval (1–60s), retention (30m–90d), and storage path
- Loading screen with animated startup sequence
- Responsive 1/2/3 column layout with hysteresis breakpoints
- WCAG AA compliant contrast ratios in both themes
- Keyboard accessible controls
- Instant shutdown with background cleanup
- CI/CD pipeline with automated installer builds via Inno Setup
- Basic telemetry collection and chart rendering
- SQLite storage with retention
