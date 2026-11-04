# SwissOlly Roadmap

## Current version: 1.2.0

## Planned

### CPU Temperature Monitoring
CPU temperature sensors on AMD and Intel require kernel-level access (PCI config space / MSR registers) that Windows restricts to signed kernel drivers. Every monitoring app that shows CPU temps (HWiNFO, Core Temp, MSI Afterburner) ships a signed driver for this.

LibreHardwareMonitor supports CPU temps through the [PawnIO](https://pawnio.eu/) kernel driver. The plan is to bundle PawnIO into the SwissOlly installer so CPU temperatures work out of the box with no extra setup.

**Status:** GPU temps work today. CPU temp panel exists in the UI — just needs the driver bundled.

### Notifications & Alerts
Toast notifications when metrics cross configurable thresholds — e.g., CPU > 90%, memory > 85%, drive > 95% full. Gives you passive monitoring without keeping the window open.

**Status:** Planned.

### Data Export
Export historical metric data as CSV or JSON for analysis in external tools. Useful for post-incident investigation, capacity planning, or just curiosity.

**Status:** Planned.

### Prometheus Metrics Endpoint
Expose SwissOlly's collected metrics via a local HTTP endpoint in Prometheus exposition format. This lets advanced users scrape SwissOlly from a Prometheus instance for long-term storage, alerting, or multi-machine dashboards — bridging the gap between SwissOlly's simplicity and Prometheus/Grafana's power. Off by default and bound to localhost, so the no-network default stands.

**Status:** Planned.

### Per-process History
See how an individual process's CPU and memory use changed over time, not just right now. Today a process's line only exists while it is pinned to a chart.

**Status:** Planned.

### More Colour Themes
Additional palettes on top of the twelve available today.

**Status:** Planned.

## Shipped

### v1.2.0 — SwissOlly
The first release under the SwissOlly name (previously Vaktr). An existing install upgrades in place and keeps its settings, history and startup entry.

- **Layout editor:** drag panels on a 12-column grid and resize them from any edge or corner; Tab, arrows and Shift+arrows do the same from the keyboard
- **Process popover:** the CPU and Memory panels list the processes using the most (the top 15, up to 50 as you scroll), services included, with Task Manager's measures (machine-wide CPU share; private working set); sort, and pin processes to the chart with the mouse or the keyboard
- **Accurate counters:** CPU usage and per-core load as Task Manager reports them, the real clock speed including boost, and GPU usage that adds up every process using an engine
- **Tray and startup:** closing keeps SwissOlly recording in the notification area (or exits, if you prefer); Start with Windows; a second launch brings the running window forward
- **Themes:** twelve palettes, among them Obsidian (true black for OLED), Synthwave, Frost and Blossom, previewed in Settings and each checked against the WCAG AA contrast floor
- **Text fields and settings:** new text fields with undo, selection and clipboard; Apply and Revert with clear messages; lowering retention asks before deleting history
- **Charts:** gaps in history stay gaps, a "Collecting since" marker, a loading state, dash patterns that tell series apart without colour, and times in your regional format
- **Accessibility:** buttons, rows and fields carry names and roles for screen readers, and every control works from the keyboard
- **Resilience:** a full disk or unplugged storage drive stops history, not the live board, and saving resumes on its own
- **In-game overlay:** FPS counted from the game's own present calls via a read-only ETW trace — no DLL injection, no anti-cheat conflicts, and SwissOlly never runs elevated. The installer offers the Performance Log Users membership this needs and removes it on uninstall. Three modes (FPS only, all metrics, Custom), any corner, classic and neon colours or any hex value, size, opacity and hotkey, with a live preview in Settings. Click-through layered window: no taskbar or Alt+Tab entry, no code-signing or UIAccess needed

**Known limitations in v1.2.0:**
- Exclusive fullscreen isn't supported by the overlay — an intentional scope decision. SwissOlly targets borderless and windowed-fullscreen games only, which avoids ever requiring code-signing or elevated UIAccess.
- HDR games render the overlay dim (SDR overlay).
- Group membership only applies to a new logon token, so the first session after install shows `--` for FPS until you sign out and back in.
- ETW delivers events in buffers, so the FPS reading trails live by about a second.
- Windows contrast themes aren't followed; SwissOlly always draws with its own palettes.

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
