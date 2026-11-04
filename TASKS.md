# SwissOlly v1.2.0 — Final QA Review

Release gate for v1.2.0. Work top to bottom; tick an item only when its **Pass** condition is met.
Record failures as a sub-bullet with what you saw.

Test matrix unless an item says otherwise: Windows 11 x64 (primary), Windows 10 1809+ x64,
one ARM64 device if available; 100% and 150% display scale; no game running unless stated.

**Status, 2026-11-03.** Ticked items were verified on the development PC (Windows 11 x64, 100%
scale). Items marked *(probe)* were driven in-process by the temporary QA probe rather than with a
real mouse and keyboard; repeat them by hand once. Unticked items still need hands-on or
other-hardware testing; what was already covered is noted under each one. Upgrading from
Vaktr 1.0.x and data migration are out of scope for this release.

### Fixed in the last two rounds (re-test these by hand first)

- Crash with the process list open: assigning a hand cursor from a row's `Loaded` handler
  fail-fasted WinUI (0xC000027B; two crash dumps). Nothing sets a cursor from `Loaded` any more,
  process rows set none at all, and rows are reused instead of rebuilt each refresh.
- Crash opening the overlay colour picker: WinUI's `ColorPicker` needs control resources this
  build doesn't ship (`TabViewScrollButtonBackground`). Replaced by a hex field and 16 presets.
- Pinning a process blanked CPU Total and Memory and read their headline as 0%. The panel's own
  line now stays, pinned processes get distinct colours, and the legend names every pin.
- Long sessions lost the left side of long ranges after ~33 h (the point cap deleted it and
  nothing reloaded it). Samples older than an hour now fold into minute averages in memory, the
  same form the store keeps, so a running session and a restart show the same chart; past the
  cap, old data coarsens instead of being deleted.
- Settings chips rebuilt the whole page on every click (≈65 ms, dropped hover and focus). They
  now update in place (0.2 ms), and Settings is pre-built at idle (first open 13 ms, was ~150).
- Overlay: a corner change applied only when the next game was detected (and the hotkey used
  the old corner); the hotkey was held even with the overlay off and auto-repeated; values
  shifted sideways crossing 9→10 (space padding). The over-90% colour is now red, not orange.
- Apply rewrote the Start with Windows entry from a stale flag; a hidden Apply bar was still
  reachable with Tab; hotkeys like Ctrl+O or Shift+A were accepted.
- **Dashboard layout lost on restart.** The first board pass at launch has only the GPU
  temperature panel; it discarded the saved geometry of every other panel, which then got packed
  into free slots when history created them, so an Edit layout arrangement never survived a
  restart. Worse, a Start with Windows (`--tray`) launch saves straight away and wrote that
  one-panel board over the saved layout, order and visibility. Saved entries for panels not on
  the board now stay parked until the panel arrives (`PanelBoard`, with tests), and saves merge them.
- **In-game overlay stuck on the desktop after a game.** Leaving a game started a one-poll
  confirmation before hiding, but the poller skipped any poll whose foreground window hadn't
  changed, so the confirmation never came and the overlay stayed over the desktop until the next
  game (seen over VS Code on 2026-11-03). The show/hide rules are now `OverlayVisibility` in Core,
  with tests. Also: any app missing from a short exclusion list counted as a game, so Discord,
  Spotify, Outlook, terminals and game launchers brought the overlay up; the list now covers the
  common ones, plus anything in the Windows folder and any console window. New in 1.2.0, never
  shipped.
- **Overlay hotkey in a game** took two presses to hide it, and then the overlay stayed on the
  desktop after the game with FPS tracking still running. One press now hides it for that game
  and one brings it back; leaving the game ends it and stops tracking (after the same one-poll
  grace as a shown overlay, so a glance at a toast doesn't bring it back). Pinned over the
  desktop, it follows a game that comes to the front, so FPS is tracked and one press hides it
  there; turning the overlay off always wins over a poll in flight. Show/hide is posted under
  the visibility lock (a hotkey and a poll could leave the window out of step), FPS start/stop is
  serialised, overlapping polls are skipped, and shutdown waits for a poll in flight.
- **Game check read game memory:** it used `Process.MainModule`, which opens the foreground app
  with memory-read access, the kind of handle anti-cheat watches for. It now asks Windows for
  the image name with query-only access.
- **"Saved" after an instant setting** shows once the save has landed, and a failed save says
  so in the status line instead.
- **Tray icon at sign-in:** if adding it fails, it's retried for a minute; after that a hidden
  window shows minimized on the taskbar (without taking focus) so the app stays reachable.
- Cancel in Edit layout lost the saved cell of a panel that reached the board during the edit
  (a drive plugged in, or history still loading); the snapshot now includes parked geometry.
- A theme or Startup and closing choice saves on click with no Apply to press, so it looked like
  it might not stick; the floating bar now says "Saved" for a moment once the save lands.

---

## 0. Known defects found while writing this list (fix first)

- [x] **Release workflow: `secrets` in a step `if:`.** The secret maps to a job-level
      `HAS_SIGNING_CERT` env var, which the signing steps test.
- [x] **Release workflow: the `create-release` job never checks out the repo.** It checks out
      before tagging.
- [x] **Release workflow defaults** to `1.2.0`, and the tag is validated against `<Version>` in
      `Directory.Build.props`.
- [x] **Stale signing comment** reworded (SmartScreen and publisher trust); `signtool.exe` is
      found in whichever Windows SDK is installed.
- [x] **CI on push** for `main`, `dev` and `cwyrick/**`.
- [x] **Formatting enforced:** `dotnet format --verify-no-changes` fails the build, and the tree
      is clean.

## 1. UI (visual)

- [x] **Theme sweep, now twelve themes** (Dark, Obsidian, Midnight, Nord, Violet, Synthwave,
      Forest, Ember; Light, Frost, Sandstone, Blossom): Overview, Settings, edit mode, the range
      popover and a process popover screenshotted in each *(probe, re-run 2026-11-03 on the final
      build)*. Contrast, checked by script against every palette: text ≥ 4.5:1 on every surface
      (pinned-row captions now use the secondary colour; ≥ 5.4:1), primary-button text ≥ 4.9:1,
      focus and selection rings ≥ 3:1, and chart lines ≥ 3.2:1 on light themes (darkened there;
      they measured 1.1–2.5:1 before).
- [ ] **Display scaling** at 100 / 125 / 150 / 200%. Only 100% checked so far.
- [x] **Window sizes** from the 720 × 560 minimum to 2560 × 1440 *(probe)*: the board reflows,
      the theme grid goes from eight cards a row to three, chip rows wrap, and the overlay card
      stacks below two columns.
- [ ] **Long names** (adapters, disk models, `Microsoft.CodeAnalysis.LanguageServer`). Process
      names trim with an ellipsis and tooltips cap their width; needs a pass with real long names.
- [x] **Empty, loading and error states** *(probe)*: "Loading history", "Collecting since",
      a missing GPU sensor, "History isn't being saved", "History unavailable".
- [x] **Number and unit formatting** in en-US, en-GB and de-DE *(probe)*.
- [x] **Charts:** dash patterns match the legend; "Collecting since" is placed left of its line
      and hidden when there is no room, so it never covers data; gaps instead of bridging lines;
      in-plot value labels sit on a plate above the lines instead of under them.
- [x] **Data after a restart** *(probe, real exit and relaunch)*: all 15,088 stored points from
      the 40 minutes before the restart came back with identical timestamps and values; the
      42 s the app was closed shows as a gap; 5m, 15m, 1h and 24h charts match before and after.
      A 72-minute run, so the session had folded its oldest samples into minute averages in
      memory: after relaunch, all 1,476 minute averages and 71,324 raw samples (41 series) matched
      exactly, nothing missing; 1h and 24h charts look the same before and after.
- [x] **Layout after a restart**: a custom 14-panel board (mixed widths and heights) survived a
      `--tray` launch, whose save lands before history loads, and a visible launch; the file
      kept every cell, the order and the visibility through both, and the board drew as saved.
- [ ] **Window chrome** in light and dark themes: caption buttons follow the palette in
      screenshots; the taskbar and tray icon need a look on a real taskbar.

## 2. UX (flows)

- [ ] **First run** on a clean machine. Startup to the first live sample measured at ~1.2 s on
      this PC with history present; a fresh install hasn't been timed.
- [ ] **Text fields with a real keyboard** and one IME. Editing, selection, clipboard and the
      right-click menu work in-process; the menu opens without error.
- [x] **Settings** *(probe)*: the floating Apply bar appears only with unapplied changes and says
      why an Apply was rejected; invalid interval, retention, storage path and
      overlay size, opacity, colour or hotkey are rejected with a reason and keep the draft;
      themes and window behaviour apply on click.
- [x] **Retention lowered** asks first, saying how much history goes *(probe, never confirmed)*.
- [x] **Storage folder changed:** the status says new history goes to the new folder and old
      history stays put.
- [x] **Time range** *(probe)*: every preset, custom ranges (valid, reversed, future, malformed)
      with clear messages. Ctrl+[, Ctrl+] and the new Ctrl+S (apply settings) still need a real
      keyboard.
- [ ] **Chart interactions** with a real mouse: drag-zoom, click to pin a reading, double-click
      or Esc to reset.
- [ ] **Layout editing with a real mouse** (drag, every resize edge, auto-scroll, Cancel, Done
      across restart) and with the keyboard.
- [ ] **Process popover with a real mouse:** hover, scroll, sort, pin and unpin while it
      refreshes; this is the path of the reported crash. Done in-process across 8 s of refreshes
      with pins at full and minimum width: no stall over 22 ms on the final build. (A ThemeShadow
      tried on the popover cost over a second of UI time per 8 s and was taken out.) Pins are by
      process name: every "Code" row pins and unpins together, and the line is their sum.
- [x] **Window behaviour:** close to tray vs exit, the tray menu, `--tray` start, and a second
      launch focusing the running instance. Start with Windows still needs a real reboot.
- [ ] **In-game overlay** over a real game: appears only over a focused game, hotkey, every
      corner and mode, colours (now 6 classic and 10 neon), live apply, no focus stealing. Fixed
      from code review but not yet seen in a game: corner changes apply live and to the hotkey;
      the hotkey is held only while the overlay is on and doesn't auto-repeat; values no longer
      shift sideways (figure-space padding); over 90% is red. The Settings preview draws the
      same padding, alignment, colours and opacity as the overlay. Also check the 2026-11-04 fix:
      quit a game (and alt-tab out of one) and the overlay leaves within about a second, and it
      doesn't come up over Discord, Spotify, a terminal or a game launcher. One hotkey press hides
      it in a game and one brings it back; quitting after hiding it leaves nothing on the desktop.
      Worth one session in an anti-cheat game (Overwatch, Battlefield) to confirm nothing complains.

## 3. Accessibility

- [ ] **Keyboard-only pass.** Process rows, legend rows, theme cards and colour swatches are now
      Tab stops (one per group, arrows move within) with a visible ring; needs a full hands-on pass.
- [ ] **Narrator pass.** UI Automation found no unnamed focusable element; chips, rows, theme
      cards and swatches report Button with Invoke and "Selected". Listen to it once.
- [x] **High contrast:** not supported; the app keeps its own palette. Documented in the release
      notes (Known limitations) and the README.
- [ ] **Reduced motion** with animation effects off.
- [ ] **Text scaling** at 150%.

## 4. Metrics accuracy

Compare against Task Manager, Resource Monitor and HWiNFO side by side for 2+ minutes at idle
and under load.

- [x] **CPU total and per core** use the same counter as Task Manager (`% Processor Utility`).
- [x] **CPU clock** follows boost (frequency × % performance), not the base clock.
- [x] **Process CPU %** is cycle-based and normalised to the whole machine.
- [x] **Process memory** is the private working set, Task Manager's "Memory" column.
- [ ] **Memory used and available** vs Task Manager under load.
- [ ] **GPU usage** (busiest engine, as Task Manager), **VRAM** and **GPU temperature** on AMD
      and Nvidia.
- [ ] **Disk throughput** against a known file copy; **drive capacity** vs Explorer.
- [ ] **Network** against a known download, per adapter.
- [x] **Process, thread and handle counts** come from the same kernel snapshot Task Manager uses.
- [x] **History tiers:** raw for the last hour, 1-minute averages before that, wide ranges
      averaged in SQL; restarts and sleep show as gaps (unit-tested and seen in charts). A running
      session folds its own older samples the same way, and a test against the real SQLite store
      shows a reload is point for point what the session held, outage included.
- [x] **Per-process CPU** falls back to CPU time where cycle counts aren't reported (some VMs),
      instead of reading 0% (unit-tested).
- [ ] **Scrape interval** spacing at 1 s and 60 s over several minutes.
- [ ] **Time handling:** time-zone change, DST, clock adjustment.
- [ ] **Sleep and resume:** gap, resumed collection, no spike.

## 5. Performance

- [x] **Idle cost:** 0.19% CPU average; no UI-thread stall over 20 ms in a 20 s idle window.
- [ ] **24-hour soak.** 300 popover cycles plus 100 range-editor cycles showed no linear growth
      in handles, GDI or memory; the full 24 h run is still to do.
- [x] **Startup:** first sample ~1.15 s warm; history loads off the UI thread (~0.5 s).
- [ ] **Large history:** a year of retention with a seeded database.
- [ ] **Database growth per day** at 1 s and 2 s.
- [x] **Interaction cost** *(probe, Debug build)*: range switch 1–3 ms in the click handler with
      no stall over 31 ms after; process popover opens in 10–15 ms; Settings 13 ms the first time
      (pre-built at idle) and 1–2 ms after; Settings chips 0.2 ms; picking a theme 37–53 ms.
      Measured in a Debug build: under the Visual Studio debugger everything is slower still,
      so judge responsiveness with Ctrl+F5 or a Release build.
- [ ] **Gaming impact:** FPS with the app running vs closed, overlay on and off.

## 6. Security and privacy

- [x] **No network calls:** nothing in the app's code opens a socket or HttpClient, and none
      were seen in a monitored run.
- [x] **Installer elevation:** Setup uses `net localgroup` with the group resolved from its SID
      (S-1-5-32-559), no PowerShell. The consent wording is the "Allow exact FPS" task: "adds you
      to the Performance Log Users group (works after you sign out and back in; removed on
      uninstall)".
- [ ] **Uninstall** reverses the Run key, group membership and shortcuts.
- [x] **Least privilege:** `asInvoker`; process data comes from one kernel snapshot, so no
      process handles are opened at all.
- [x] **Process access rights:** moot. `PROCESS_VM_READ` is gone with `OpenProcess`.
- [x] **Untrusted settings file:** bad values, wrong types and path tricks fall back safely
      (unit-tested); UNC and device storage paths are rejected.
- [ ] **Data at rest:** ACLs on a custom folder; document in the README.
- [x] **Supply chain:** Actions are pinned to SHAs and Dependabot watches NuGet and Actions.
      `dotnet list package --vulnerable` flagged the native SQLite that Microsoft.Data.Sqlite
      brings in (SQLitePCLRaw 2.1.6, SQLite 3.41.2: CVE-2025-6965, High); a direct
      `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14 reference lifts it to SQLite 3.54.0, and the check is
      now clean.
- [x] **Signing:** shipping unsigned; the release notes say so under Known limitations, with the
      SmartScreen steps and `SHA256SUMS.txt`, and the website's install section and FAQ explain it.
      The workflow signs both the exe and the installer if a `CODE_SIGN_PFX` secret is added
      (the uninstaller would also need `SignTool=` in the `.iss`).

## 7. Reliability and data integrity

- [ ] **Install paths:** fresh install, reinstall, uninstall. (Upgrading from Vaktr 1.0.x and
      data migration are out of scope for 1.2.0.)
- [ ] **Abrupt termination** during a write.
- [x] **Storage unavailable:** a folder that disappears gives a clear status, the live view keeps
      going, and saving resumes when the folder returns (unit-tested). Disk full not tested.
- [ ] **Hardware changes:** GPU driver reset, adapters and disks coming and going.
- [ ] **Long uptime:** 7 days across sleep cycles.
- [ ] **Crash reporting:** unhandled exceptions are logged. Remove the Debug-only first-chance
      COM logger in `App.cs` once 0x800F1000 is confirmed gone.

## 8. CI/CD and release

- [x] Everything in §0.
- [ ] **Tests on ARM64,** or document why x64 is enough. ARM64 Release builds warning-free.
- [x] **Installer compiles** locally (Inno Setup, 67.7 MB). Add it to CI.
- [ ] **Dry-run release** on a throwaway tag in a fork. `release.yml` now has a `verify` job that
      runs first: an exact `vX.Y.Z` tag (a manual run given `v1.2.0` used to tag `vv1.2.0`), the
      version in `Directory.Build.props`, the commit on `main`, the tag not already taken (manual
      runs), and the tests. Inno Setup is pinned to 6.7.1 in both workflows (the ISCC path is
      version 6's). The release is created as a **draft**: publish it with "Set as latest".
      To dry-run: push the release commit to `main` of a private scratch repo, push `v1.2.0`,
      and check both installers, `SHA256SUMS.txt` (two lines) and the draft's notes.
- [x] **Checksums:** `create-release` writes `SHA256SUMS.txt` for both installers and attaches it.
- [x] **Release notes** in `docs/release-notes/v1.2.0.md`, including the new themes, the
      Settings redesign, neon overlay colours and the fixes above.
- [x] **Decision:** `WindowsAppSDKSelfContained` is on for Release builds.

## 9. Website (SwissOlly.Web)

- [x] **Replace screenshots:** captured from the app in the Dark theme (Overview, Processes,
      overlay settings, themes) plus a 1200 × 630 link card; the README uses them too. See
      SwissOlly.Web's TASKS.md for the site's own list.
- [x] **Domain:** swissolly.io is live on a Cloudflare Worker, with HTTPS enforced. vaktr.app is
      being retired, not redirected.
- [ ] **Lighthouse** ≥ 90 and the link card on Discord and X.
- [ ] **Download** links resolve to the 1.2.0 release. Blocked: `github.com/swissolly/SwissOlly`
      is private, so every Download and GitHub link on the site returns 404 to visitors.
- [x] **CI:** the validate workflow fails on broken links now (it never did).

## 10. Repository hygiene

- [x] README, ROADMAP and the website agree on features, platforms and license.
- [x] No "Vaktr" strings outside migration code and its tests.
- [ ] Remove the old `C:\Repos\Vaktr` folder outside the repo.
- [x] Release builds for x64 and ARM64 are warning-clean.

---

## Reference — traps already hit (keep)

Not tasks: engineering notes carried over so they aren't re-learned.

- **Where things are:** `ShellWindow.LayoutEditor.cs` (gesture, reflow, placeholder, settle,
  keyboard nudge), `TelemetryPanelCard` (gesture input, float API, process popover),
  `GridLayoutEngine` (`Apply`, `ResizeByDelta`, `ResolveResponsive`: keep collision logic
  there), `Motion.cs` (`MotionTokens`, `SpringValue`), `UiMetrics` (shape and size tokens),
  `ThemeCatalog` (every palette; the theme picker previews read it too).
- Grid pitch includes spacing: `columnPitch = (width + ColumnSpacing) / 12`, `rowPitch = 48 + 14`.
- Read gesture pointers in window-content space, never the card's own (it moves).
- A WinUI child larger than its grid slot is clipped to the slot; the floating card spans the board.
- Adding an element that still has a parent throws 0x800F1000, even if that parent is detached.
- `Children.Clear()` + re-add fires `Unloaded`; anything released there must re-attach on `Loaded`.
- `RefreshDashboardPanels` clears the grid: re-add every overlay element it owns.
- Never compare `ColumnDefinitions.Count` to `DetermineDashboardColumns()` (12 vs 1–3 → rebuild loop).
- Don't assign geometry in `ApplyConfig` (the panel list isn't populated yet).
- A bad layout poisons the config: delete `panelLayouts` in `%LocalAppData%\SwissOlly\swissolly-settings.json`.
- This build ships no WinUI control resources: `TextBox`, `ColorPicker` (opening it fail-fasts on
  `TabViewScrollButtonBackground`) and `new XamlControlsResources()` all fail. `Button`,
  `ScrollViewer`, `Flyout` and `MenuFlyout` work. Test any other framework control in-process,
  opened, before using it.
- Never set `ProtectedCursor` from `Loaded`; use the shared cursors in `PointerCursors`, set in a
  constructor (or, for a long-lived card in layout edit mode, from its pointer events).
- Settings controls update in place through `RefreshSettingsDeck`; only a new theme or column
  count rebuilds the page. A chip's selection is `IsActive`; `IsFilled` means a primary button
  (solid accent), never "selected".
- No `ThemeShadow` on popovers: it projects onto the whole window and stalled the UI thread for
  over a second in every eight while the process list refreshed beneath it.
- Live buffers fold samples older than an hour into minute averages (`MetricHistory.Compact`),
  matching the store's rollups and the startup read; keep the two windows equal
  (`MetricPanelViewModel.RawSampleWindow`, `ShellWindow.RawHistoryWindow`).
- **Verifying:** use `C:\Program Files\dotnet\dotnet.exe`, and kill `SwissOlly.exe` before
  building (check first that it isn't your own debug session). Capture with
  `PrintWindow(hwnd, hdc, 2)` on the `WinUIDesktopWin32WindowClass` window (popups aren't
  captured) and never force foreground (the owner games on this machine). Drive UI in-process with
  a temporary env-gated probe (`SWISSOLLY_PROBE=1`) and delete it afterwards.
