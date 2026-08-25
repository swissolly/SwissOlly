# Vaktr v1.2.0 — Task List

In-game telemetry overlay. Top-most click-through layered window owned by the main Vaktr process; auto-shows when a game has focus, hides otherwise. Three modes (Simple / Advanced / Custom), configurable corner / color / font size / opacity / hotkey. No DLL injection. Vendor-agnostic.

Full plan: `~/.claude/plans/adaptive-foraging-mist.md` (or read this file end-to-end for the actionable summary).

## Goals

- Surface FPS + selected metrics on top of running games without alt-tabbing.
- Configured entirely from the existing settings panel — no second window, exe, taskbar entry, or installer component visible to the user.
- Anti-cheat-safe (no DLL injection, no DXGI hooks, no game-process memory reads).
- Resource budget: total Vaktr footprint stays under 3% CPU / 300 MB during active gaming.
- NVIDIA + AMD + Intel GPUs supported equally.

---

## Step 1 — Project skeleton + rendering primitive

- [x] Create `Vaktr.Overlay/Vaktr.Overlay.csproj` (`net8.0-windows10.0.19041.0`, references `Vaktr.Core`)
- [x] Add the project to `Vaktr.sln`
- [x] Add `<ProjectReference>` from `Vaktr.App/Vaktr.App.csproj` to `Vaktr.Overlay`
- [x] `Vaktr.Overlay/Rendering/Win32Interop.cs` — P/Invoke surface: `CreateWindowEx`, `DestroyWindow`, `UpdateLayeredWindow`, `SetWindowPos`, `GetDpiForWindow`, `GetMonitorInfo`, `MonitorFromWindow`, `SetThreadDpiAwarenessContext`, message-loop primitives
- [x] `Vaktr.Overlay/Rendering/LayeredOverlayWindow.cs` — owns the HWND lifecycle. Window styles: `WS_POPUP` + `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`. Methods: `Show`, `Hide`, `UpdateBitmap(Bitmap)`, `Reposition(corner, monitor)`. Re-asserts topmost on a 2-second timer while visible.
- [x] `Vaktr.Overlay/Rendering/OverlayBitmapRenderer.cs` — GDI+ text → premultiplied BGRA bitmap. Cache static lines; only re-rasterize when values change. Layout text vertically with consistent padding. Apply per-line color and global opacity.
- [ ] **Smoke test**: write a tiny scratch host that creates a window, renders hardcoded `"FPS: 60\nCPU: 50%\nGPU: 70%"`, and parks it in the top-right corner of the primary monitor. Verify it appears on top of Notepad maximized and is click-through.

## Step 2 — Config model + persistence + tests

- [x] `Vaktr.Overlay/OverlayMode.cs` — enum `Simple | Advanced | Custom`
- [x] `Vaktr.Overlay/OverlayCorner.cs` — enum `TopLeft | TopRight | BottomLeft | BottomRight`
- [x] `Vaktr.Core/Models/VaktrConfig.cs` — add nested class `OverlayConfig` (mutable, auto-properties; same shape as the existing class). Properties: `Enabled`, `Mode`, `Corner`, `TextColor` (`#RRGGBB`), `FontSizePx` (10–32), `Opacity` (0.3–1.0), `HotkeyGesture` (e.g. `"Ctrl+Shift+F11"`), `CustomMetrics` (subset of `["fps","cpu","gpu","gpu-temp","memory"]`).
- [x] `OverlayConfig.Normalize()` — clamp numerics, validate hex via `ColorTranslator.FromHtml` with fallback, validate hotkey via `GlobalHotkeyService.TryParse` with fallback, ensure Custom has at least one metric (else fallback to FPS).
- [x] `VaktrConfig.Overlay` property; `VaktrConfig.Normalize()` ends with `Overlay = (Overlay ?? new OverlayConfig()).Normalize();`
- [x] `Vaktr.Tests/VaktrConfigTests.cs` — add five tests:
  - Font size out of range → clamped to default
  - Malformed hex → fallback to default `#66E7FF`
  - Invalid hotkey gesture → fallback to default `Ctrl+Shift+F11`
  - JSON round-trip preserves all properties
  - `VaktrConfig.Overlay == null` after deserialize reconstructs default
- [x] All 31 existing tests still pass; new tests pass: `dotnet test Vaktr.Tests/Vaktr.Tests.csproj -c Release` (47/47 pass)

## Step 3 — OverlayController consuming MetricSnapshot

- [x] `Vaktr.Overlay/OverlayController.cs` — owns state machine (`Hidden | ShownByGame | ShownByHotkey | Suspended`). Subscribes to `CollectorService.SnapshotCollected` (via tee from `ShellWindow.OnSnapshotCollected`). Extracts CPU%, GPU%, GPU temp, memory% from snapshot. Pushes formatted text to `LayeredOverlayWindow` through the renderer.
- [x] Metric extraction helpers (private inside controller):
  - CPU%: `samples.First(s => s.PanelKey == "cpu-total" && s.SeriesKey == "usage").Value`
  - GPU%: `panelKey == "gpu-total"`, `seriesKey == "usage"`
  - GPU temp: `panelKey == "gpu-temperature"`, `seriesKey == "temperature"`
  - Memory%: from `panelKey == "memory"` series `used-gb` / `total-gb`
- [x] Wire in `Vaktr.App/ShellWindow.Polished.cs` `OnSnapshotCollected` (line 923-933): tee to `_overlayController.OnSnapshot(snapshot)` on the same Low-priority dispatch.
- [ ] **Test (always-on mode)**: with `Enabled=true` and no foreground detection yet, verify the overlay shows live CPU/GPU/memory (no FPS) on top of Notepad maximized.

## Step 4 — Foreground detection and auto-show/hide

- [x] `Vaktr.Overlay/Detection/ForegroundWatcher.cs` — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, ..., WINEVENT_OUTOFCONTEXT)`. Raises an event when the foreground HWND changes; debounces 250 ms (Alt-Tab fires multiple events).
- [x] `Vaktr.Overlay/Detection/GameProcessClassifier.cs` — combines: (a) foreground HWND covers an entire monitor (`GetWindowRect` vs `MonitorFromWindow` + `GetMonitorInfo`), (b) PID's main module is not in deny-list (`explorer.exe`, `dwm.exe`, `Vaktr.exe`, `ApplicationFrameHost.exe`, `SearchHost.exe`, `StartMenuExperienceHost.exe`, `ShellExperienceHost.exe`, `TextInputHost.exe`). Return `bool IsLikelyGame(IntPtr hwnd)`.
- [x] `OverlayController` subscribes to `ForegroundWatcher`, calls classifier, transitions state. Stores currently-tracked PID for the FPS source (Step 6).
- [ ] **Test**: verify overlay hides when Notepad maximized is foreground; appears when a borderless game is focused (or any non-deny-list fullscreen-sized exe for now).

## Step 5 — Global hotkey

- [x] `Vaktr.Overlay/Hotkeys/GlobalHotkeyService.cs` — parses gesture string (`"Ctrl+Shift+F11"` → `MOD_CONTROL | MOD_SHIFT`, `VK_F11`); calls `RegisterHotKey` against the main Vaktr window HWND. Supports `Ctrl`, `Alt`, `Shift`, `Win` modifiers + letter / function / punctuation key.
- [x] In `Vaktr.App/ShellWindow.Polished.cs`: get the shell HWND via `WinRT.Interop.WindowNative.GetWindowHandle(this)`; install a `SetWindowSubclass` callback (mirroring the existing `ResizeInterop` pattern); forward `WM_HOTKEY` (0x0312) to `_overlayController.OnHotkeyPressed`.
- [x] Hotkey toggles overlay between forced-shown (`ShownByHotkey`) and auto (`ShownByGame` or `Hidden` depending on foreground).
- [x] On `RegisterHotKey` failure (gesture conflict): write a friendly status message via the existing `_viewModel.StatusText` channel; do not crash.
- [ ] **Test**: with no game in focus and overlay disabled, press hotkey → overlay appears with current metrics; press again → hides.

## Step 6 — FPS source

> **Superseded**: this step originally shipped an ETW DXGI-based FPS source (`DxgiEtwFpsSource`,
> `Microsoft.Diagnostics.Tracing.TraceEvent`). It was later replaced by `DwmFpsSource`, which times
> successive `DwmFlush()` calls instead — same accuracy for windowed/borderless games, no admin
> rights or `Performance Log Users` membership required, and no ETW session/ARM64 binary dependency.
> `DxgiEtwFpsSource.cs`, `ForegroundWatcher.cs` (superseded by a simple foreground-window poll in
> `OverlayController`), and the `TraceEvent` package reference have been deleted as dead code.

- [x] `Vaktr.Overlay/Fps/IFpsSource.cs` — interface with `void StartTracking(uint pid)`, `void StopTracking()`, `event Action<double> FpsUpdated`.
- [x] `Vaktr.Overlay/Fps/DwmFpsSource.cs` — times successive `DwmFlush()` calls on a dedicated background thread; computes FPS over a rolling ~1s window, fires `FpsUpdated` at ≤2Hz. Detects exclusive fullscreen (DWM bypassed, `DwmFlush` returns non-zero HRESULT immediately) and reports `--` instead of blocking or polling with an arbitrary timeout.
- [x] **ARM64**: works natively — `DwmFlush` is a plain Win32 API call, no architecture-specific binary dependency.
- [ ] **Test on a real game**: launch a game, verify FPS appears and updates smoothly.

## Step 7 — Settings UI (Control Deck expansion)

- [x] `Vaktr.App/Controls/IconFactory.cs` — add `"overlay"` icon glyph
- [x] `Vaktr.App/ShellWindow.Polished.UI.cs` `RenderEditableControlDeck()` — add a second row to the existing 3-column grid containing one full-width-spanning OVERLAY card built via `CreateControlEditorCard()`. Header: icon `"overlay"`, title `"OVERLAY"`, summary value (dynamic), sub-caption (dynamic), accent color `"#7CE38B"`.
- [x] Inside the card, top-to-bottom rows:
  - **Row 1** — enable toggle + segmented `Simple / Advanced / Custom` mode chips + helper text
  - **Row 2** (only when `Mode = Custom`) — five toggle chips: `FPS`, `CPU%`, `GPU%`, `GPU temp`, `Memory%`
  - **Row 3** — left half: 4-chip corner picker (`↖ TL`, `↗ TR`, `↙ BL`, `↘ BR`); right half: hex-color `InlineTextEntry` with live validation
  - **Row 4** — left half: font-size `InlineTextEntry` (10–32) + preset chips `12 / 14 / 16 / 20`; right half: opacity `InlineTextEntry` (30–100%) + preset chips `60% / 75% / 90% / 100%`
  - **Row 5** — full-width hotkey `InlineTextEntry` + right-aligned `Reset` chip
- [x] Add three helper functions in the same file:
  - `CreateSegmentedChips<TEnum>(TEnum currentValue, params (TEnum value, string label)[] options)` — emits an array of `ActionChip` with one marked `IsActive`. Used for Mode and Corner.
  - `CreateMultiSelectChips(IList<string> selectedKeys, params (string key, string label)[] options)` — used for Custom mode field picker.
  - `CreateColorSwatch(string hex)` — small circular Border with parsed color background, used in summary view.
- [x] Summary view (when control deck is collapsed): status line (`Off` or `On — Advanced @ Top Right`), hotkey line, color swatch.
- [x] Add `_draftOverlay*` fields and OnChange handlers following the existing `_draftScrapeIntervalInput` pattern.
- [x] Save flow: `OnSaveSettingsClick` validates → `_viewModel.ApplyConfig` → `_configStore.SaveAsync` → `ApplyRuntimeSettingsAsync` propagates to `_overlayController.ApplyConfig(config.Overlay)`.
- [x] `_overlayController.ApplyConfig` diffs and only restarts subsystems that need it (re-register hotkey only on gesture change; restart PresentMon only on `Enabled` flip; etc.).

## Step 8 — Multi-monitor + DPI hardening

- [x] In `LayeredOverlayWindow`: call `SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` immediately before `CreateWindowEx`. Handle `WM_DPICHANGED` by recalculating bitmap dimensions and re-rendering. Handle `WM_DISPLAYCHANGE` to reposition when monitors are added/removed.
- [x] When a foreground game is detected, place the overlay on the same monitor as the game window: `MonitorFromWindow(gameHwnd, MONITOR_DEFAULTTONEAREST)` → use that monitor's work area for corner positioning.
- [ ] **Test**: drag the main Vaktr window across monitors at different DPI; launch game on first monitor → overlay scales correctly; disconnect second monitor mid-session → overlay survives without crash.

## Step 9 — Documentation + release prep

- [x] `README.md` — add a short "Overlay" subsection under Features
- [x] `ROADMAP.md` — move "in-game overlay" from Roadmap to Done; add follow-ups for v1.3.0 (HDR-aware rendering, color picker control, installer adds user to PLU group, PresentMonAPI2 swap)
- [x] Verify `dotnet build Vaktr.sln -c Release -p:Platform=x64` clean
- [x] Verify `dotnet build Vaktr.sln -c Release -p:Platform=ARM64` clean (FPS field shows `--` on ARM64 if PresentMon ARM64 unavailable)
- [x] All tests pass: `dotnet test Vaktr.Tests/Vaktr.Tests.csproj -c Release` (47/47)
- [ ] Manual smoke matrix:
  - Borderless-fullscreen modern game (Steam, Unreal/Unity title)
  - Exclusive-fullscreen with FSO enabled (default)
  - FSO-disabled game → overlay does not appear; tray tooltip warns
  - Anti-cheat-protected (Valorant or CS2) — confirm game launches normally
  - HDR game (note: overlay renders dim — known v1.2.0 limitation)
  - Multi-monitor mixed-DPI: game on secondary monitor, overlay scales correctly
- [ ] Resource budget: idle <250 MB / <2% CPU; gaming with overlay active <300 MB / <3% CPU
- [ ] Tag `v1.2.0`, push, verify CI builds both x64 and ARM64 installers, draft GitHub Release populates with both `.exe` files

## Known limitations shipping in v1.2.0

- Exclusive fullscreen isn't supported — by design. Vaktr targets borderless and windowed-fullscreen games, which keeps the overlay from ever needing code-signing or elevated UIAccess.
- HDR games render the overlay in SDR — appears dim. Defer HDR-aware rendering to v1.3.0.
- ~~PresentMon requires admin or Performance Log Users membership for ETW~~ — resolved. `DwmFpsSource` needs neither elevation nor group membership.
- ~~No color picker control in v1.2.0 — hex input only~~ — resolved. Color picker with quick presets shipped.
- ~~ARM64 FPS depends on PresentMon ARM64 build availability~~ — resolved. `DwmFpsSource` has no ARM64-specific dependency.
