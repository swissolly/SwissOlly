# SwissOlly v1.2.0 — Final QA Review

Release gate for v1.2.0. Work top to bottom; tick an item only when its **Pass** condition is met.
Record failures as a sub-bullet with what you saw.

Test matrix unless an item says otherwise: Windows 11 x64 (primary), Windows 10 1809+ x64,
one ARM64 device if available; 100% and 150% display scale; no game running unless stated.

**Status, 2026-11-02.** Ticked items were verified on the development PC (Windows 11 x64, 100%
scale). Items marked *(probe)* were driven in-process by the temporary QA probe rather than with a
real mouse and keyboard; repeat them by hand once. Unticked items still need hands-on or
other-hardware testing; what was already covered is noted under each one.

### Fixed in the last round (re-test these by hand first)

- Crash with the process list open: assigning a hand cursor from a row's `Loaded` handler
  fail-fasted WinUI (0xC000027B; two crash dumps). Rows no longer set a cursor, every cursor is
  assigned once in a constructor, and rows are reused instead of rebuilt each refresh.
- Crash opening the overlay colour picker: WinUI's `ColorPicker` needs control resources this
  build doesn't ship (`TabViewScrollButtonBackground`). Replaced by a hex field and 16 presets.
- Pinning a process blanked CPU Total and Memory and read their headline as 0%. The panel's own
  line now stays, pinned processes get distinct colours, and a legend names them.
- Settings chips rebuilt the whole page on every click (≈65 ms, dropped hover and focus). They
  now update in place (0.2 ms). Chips that started selected no longer stay lit.
- "Collecting since" no longer sits over the data; the "Showing …" status clears when you pick
  a preset or reset zoom.

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
      popover and a process popover screenshotted in each *(probe)*. Every palette passes WCAG AA
      on every surface: text ≥ 4.5:1, focus and selection rings ≥ 3:1.
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
      and hidden when there is no room, so it never covers data; gaps instead of bridging lines.
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
      with pins at full and minimum width, with no stalls over 24 ms.
- [x] **Window behaviour:** close to tray vs exit, the tray menu, `--tray` start, and a second
      launch focusing the running instance. Start with Windows still needs a real reboot.
- [ ] **In-game overlay** over a real game: appears only over a focused game, hotkey, every
      corner and mode, colours (now 6 classic and 10 neon), live apply, no focus stealing. The
      Settings preview shows the drafted overlay.

## 3. Accessibility

- [ ] **Keyboard-only pass.** Process rows, legend rows, theme cards and colour swatches are now
      Tab stops (one per group, arrows move within) with a visible ring; needs a full hands-on pass.
- [ ] **Narrator pass.** UI Automation found no unnamed focusable element; chips, rows, theme
      cards and swatches report Button with Invoke and "Selected". Listen to it once.
- [ ] **High contrast:** not supported; the app keeps its own palette. Decide or document.
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
      averaged in SQL; restarts and sleep show as gaps (unit-tested and seen in charts).
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
- [x] **Interaction cost** *(probe)*: range switch 1–3 ms in the click handler with no stall
      over 31 ms after; process popover opens in 9–13 ms; Settings 66 ms the first time and 1 ms
      after; Settings chips 0.2 ms; picking a theme 36–56 ms.
- [ ] **Gaming impact:** FPS with the app running vs closed, overlay on and off.

## 6. Security and privacy

- [x] **No network calls:** nothing in the app's code opens a socket or HttpClient, and none
      were seen in a monitored run.
- [ ] **Installer elevation:** replace the elevated PowerShell call with `net localgroup` by SID
      (S-1-5-32-559) and confirm the consent wording.
- [ ] **Uninstall** reverses the Run key, group membership and shortcuts.
- [x] **Least privilege:** `asInvoker`; process data comes from one kernel snapshot, so no
      process handles are opened at all.
- [x] **Process access rights:** moot. `PROCESS_VM_READ` is gone with `OpenProcess`.
- [x] **Untrusted settings file:** bad values, wrong types and path tricks fall back safely
      (unit-tested); UNC and device storage paths are rejected.
- [ ] **Data at rest:** ACLs on a custom folder; document in the README.
- [ ] **Supply chain:** `dotnet list package --vulnerable`; Actions are pinned to SHAs and
      Dependabot is configured.
- [ ] **Signing:** a signed installer and exe, or say "unsigned" in the release notes.

## 7. Reliability and data integrity

- [ ] **Upgrade paths:** 1.0.x (Vaktr) → 1.2.0 in place, fresh install, reinstall, uninstall.
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
- [ ] **Dry-run release** on a throwaway tag in a fork.
- [ ] **Checksums:** SHA-256 per installer.
- [x] **Release notes** drafted in `docs/release-notes/v1.2.0.md`; add the new themes, the
      Settings redesign and neon overlay colours.
- [x] **Decision:** `WindowsAppSDKSelfContained` is on for Release builds.

## 9. Website (SwissOlly.Web)

- [ ] **Replace screenshots** in the default theme at 1x and 2x (show the new themes).
- [ ] **Domain:** canonical, Open Graph and sitemap URLs still point at `vaktr.app`.
- [ ] **Lighthouse** ≥ 90 and the link card on Discord and X.
- [ ] **Download** links resolve to the 1.2.0 release.
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
- Never set `ProtectedCursor` from `Loaded`; assign it once in the constructor via `PointerCursors`.
- Settings controls update in place through `RefreshSettingsDeck`; only a new theme or column
  count rebuilds the page. A chip's selection is `IsActive` alone — never `IsFilled`.
- **Verifying:** use `C:\Program Files\dotnet\dotnet.exe`, and kill `SwissOlly.exe` before
  building (check first that it isn't your own debug session). Capture with
  `PrintWindow(hwnd, hdc, 2)` on the `WinUIDesktopWin32WindowClass` window (popups aren't
  captured) and never force foreground (the owner games on this machine). Drive UI in-process with
  a temporary env-gated probe (`SWISSOLLY_PROBE=1`) and delete it afterwards.
