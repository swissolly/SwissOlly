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

- [x] ~~`Vaktr.Overlay/Detection/ForegroundWatcher.cs` — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, ...)`~~ — superseded and deleted. Replaced by a 500 ms foreground poll in `OverlayController.PollForeground`, which needs no hook and gets the Alt-Tab debounce for free via its two-poll hide confirmation.
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

> **History**: this step first shipped an ETW DXGI source built on
> `Microsoft.Diagnostics.Tracing.TraceEvent`, then swapped to `DwmFpsSource` to shed the admin
> requirement and the package's native ARM64 dependency. That swap traded away correctness:
> `DwmFlush` timing measures the **compositor's** cadence — the display refresh rate — and is
> attributed to no process at all (its `pid` argument was unused). A game running 45 fps on a
> 144 Hz panel reported 144. It only ever read correctly when the game happened to be v-synced
> to the refresh rate.
>
> Resolved by returning to present-based measurement, but written against the raw Win32 ETW
> APIs instead of the `TraceEvent` package — those are plain `advapi32` entry points, so the
> ARM64 concern that motivated the original swap does not apply. The admin requirement is
> handled at install time instead of runtime (Step 6b).

- [x] `Vaktr.Overlay/Fps/IFpsSource.cs` — `StartTracking(uint pid)`, `StopTracking()`, `FpsUpdated`, `ErrorOccurred`, and `FpsAccuracy Accuracy` so callers can tell an exact reading from an approximate one.
- [x] `Vaktr.Overlay/Fps/EtwInterop.cs` — hand-rolled ETW consumer P/Invoke (`StartTrace`, `EnableTraceEx2`, `OpenTrace`, `ProcessTrace`, `ControlTrace`, `CloseTrace`) plus faithful `EVENT_TRACE_PROPERTIES` / `EVENT_TRACE_LOGFILE` / `EVENT_RECORD` layouts. No NuGet dependency, identical on x64 and ARM64.
- [x] `Vaktr.Overlay/Fps/EtwPresentFpsSource.cs` — real-time session over `Microsoft-Windows-DXGI` (+ `Microsoft-Windows-D3D9` for older titles), counting present-start events attributed to the tracked PID. Rolling 1s window, emits at ≤2Hz, suppresses duplicate emits when ETW's ~1s buffer flush is slower than the emit timer, and reports a stall when a tracked game stops presenting.
- [x] Recovers from a stale session left by a crashed process (`ERROR_ALREADY_EXISTS` → stop → retry once).
- [x] `Vaktr.Overlay/Fps/AdaptiveFpsSource.cs` — probes ETW availability once, uses `EtwPresentFpsSource` when permitted and falls back to `DwmFpsSource` otherwise, raising `AccuracyChanged` so the UI can say which is live.
- [x] `DwmFpsSource` retained as the documented fallback, with its comment corrected to state plainly that it measures refresh rate and not the game.
- [x] **ARM64**: builds clean; no architecture-specific binary dependency.
- [x] **Struct layout verified** against documented x64 offsets — all sizes and the fields ETW reads (`EVENT_TRACE_LOGFILE.EventRecordCallback` @ 424, `EVENT_HEADER.ProcessId` @ 12, etc.) confirmed correct.
- [x] **Verified live**: two processes tracked simultaneously on one 144 Hz display reported genuinely different rates (VS Code GPU process steady at 144, Edge GPU process varying 43–100). The DWM source reports 144 for both, which is the bug this fixes.
- [ ] **Test on a real game**: launch a game, verify FPS appears, matches an independent counter (Steam overlay / RTSS), and updates smoothly.

## Step 6b — Install-time privilege grant

- [x] `installer/vaktr-setup.iss` — at `ssPostInstall`, add the installing user to the built-in **Performance Log Users** group (by SID `S-1-5-32-559`, so it works on localised Windows). Setup already runs elevated, so this needs no extra prompt and Vaktr itself still runs unelevated.
- [x] Non-fatal on failure: already-a-member and not-permitted both fall through to the approximate source rather than blocking install.
- [x] `OverlayController` surfaces a one-time control-deck message when FPS is approximate, explaining that sign-out/sign-in is needed for the group to take effect.
- [ ] **Test**: install on a machine whose user is not in the group → approximate FPS + status message; sign out and back in → exact FPS.
- [ ] **Test**: portable/build-from-source run with no group membership → falls back cleanly, no crash, no elevation prompt.

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

- [x] `README.md` — add a short "Overlay" subsection under Features; state plainly that FPS is present-counted and document the sign-out/sign-in step
- [x] `ROADMAP.md` — move "in-game overlay" from Roadmap to Done; correct the FPS description; carry HDR rendering and the UI/UX polish pass into v1.3.0
- [x] Verify `dotnet build Vaktr.sln -c Release -p:Platform=x64` clean
- [x] Verify `dotnet build Vaktr.sln -c Release -p:Platform=ARM64` clean
- [x] All tests pass: `dotnet test Vaktr.Tests/Vaktr.Tests.csproj -c Release` (47/47)
- [ ] Manual smoke matrix:
  - Borderless-fullscreen modern game (Steam, Unreal/Unity title)
  - Exclusive-fullscreen with FSO enabled (default)
  - FSO-disabled game → overlay does not appear; tray tooltip warns
  - Anti-cheat-protected (Valorant or CS2) — confirm game launches normally
  - HDR game (note: overlay renders dim — known v1.2.0 limitation)
  - Multi-monitor mixed-DPI: game on secondary monitor, overlay scales correctly
  - [x] **FPS cross-check** — verified against Overwatch: the overlay read 143 FPS while Overwatch's own built-in performance counter read 143 in the opposite corner of the same frame. Also confirms the overlay paints correctly over a borderless anti-cheat-protected title (Overwatch runs Defence Matrix) with the game running normally.
  - Repeat the cross-check on an **uncapped** title where the frame rate sits away from the refresh rate — Overwatch at 143 on a 144 Hz panel is close enough to the refresh rate that it does not by itself distinguish the ETW source from the old compositor-based one.
  - **Uncapped frame rate**: confirm the reading tracks the game rather than pinning to the refresh rate — this is the specific failure the ETW source exists to fix
  - **Frame-limited game** (cap set below refresh, e.g. 60 on a 144 Hz panel) → reads 60, not 144
  - **D3D9-era title** → present events arrive on the D3D9 provider and FPS appears
  - Alt-tab out and back repeatedly → FPS recovers each time, never sticks at `--`
  - Two games running, focus switching between them → tracked PID follows focus
- [ ] Resource budget: idle <250 MB / <2% CPU; gaming with overlay active <300 MB / <3% CPU
- [ ] Tag `v1.2.0`, push, verify CI builds both x64 and ARM64 installers, draft GitHub Release populates with both `.exe` files

## Known limitations shipping in v1.2.0

- Exclusive fullscreen isn't supported — by design. Vaktr targets borderless and windowed-fullscreen games, which keeps the overlay from ever needing code-signing or elevated UIAccess.
- HDR games render the overlay in SDR — appears dim. Defer HDR-aware rendering to v1.3.0.
- Exact FPS needs Performance Log Users membership. The installer grants it, but Windows only picks up group changes in a freshly built logon token, so the **first session after install falls back to approximate FPS** until the user signs out and back in. The overlay says so in the control deck rather than showing a wrong number silently.
- Portable / build-from-source runs get approximate FPS unless the user joins the group manually.
- ETW real-time delivery is buffered, so the FPS reading trails live by roughly a second. Fine for a readout; not suitable for frame-time analysis. Injection-based overlays (RTSS) are lower latency by nature.
- Present counting assumes one swapchain per frame. An application presenting multiple swapchains per frame would over-report; this is normal for browsers and compositors but rare for games.
- ~~ARM64 FPS depends on PresentMon ARM64 build availability~~ — resolved. The hand-rolled ETW layer uses only `advapi32` entry points.
- ~~No color picker control in v1.2.0 — hex input only~~ — resolved. Color picker with quick presets shipped.
- ~~FPS reads the refresh rate rather than the game's frame rate~~ — resolved in Step 6.
- ~~Overlay latches to `FPS: --` after any transient FPS error and never recovers~~ — resolved; a live reading now clears the error state.

---

# Step 10 — UI/UX polish and smoothness pass

The functional surface of v1.2.0 is complete. This step is about how the app *feels*: nothing
should snap, stutter, jump, or leave the user guessing what a control does. Every task below is
anchored to code that exists today.

Ordering note: 10.1 and 10.2 are the two the user actually feels every session. Do those first.

## 10.1 — Value motion (no more snapping)

Numbers and gauges currently jump straight to their new value. `TelemetryPanelCard` already has a
full easing vocabulary (28 `DoubleAnimation` uses); the gauges and readouts never got the same
treatment.

- [x] `Vaktr.App/Controls/Motion.cs` — shared motion primitives. `MotionTicker` holds exactly one
  process-wide `CompositionTarget.Rendering` subscription, attaching on the first active animator
  and detaching when the last one settles, so a static dashboard costs nothing. `AnimatedDouble`
  eases toward a target and retargets mid-flight from its current value rather than restarting.
- [x] `UsageGauge` — the value arc now eases over 300 ms with ease-out-cubic instead of snapping.
- [x] `UsageGauge` — retained geometry. `Redraw` used to `Children.Clear()` and allocate a fresh
  `Path` + `PathGeometry` + `PathFigure` + `ArcSegment` pair every call; the arcs are now built
  once per size/theme change and the value arc mutates its existing `ArcSegment`. This is what
  makes 60 fps animation affordable — the per-frame path is allocation-free.
- [x] Animation duration (300 ms) stays well under the fastest scrape interval (1 s), so a gauge
  is never permanently mid-flight.
- [x] Numeric readout tweens with the arc, and the format moved from `0.#` to integer percent —
  the old format alternated between `45%` and `45.3%` as the fraction crossed zero, which jittered
  a centred readout and would have done so every frame once animated.
- [x] Zero-value arcs collapse instead of painting a stray round-cap dot at the 135° origin.
- [x] Respect the OS reduce-motion setting via `UISettings.AnimationsEnabled` — when off,
  `AnimatedDouble` degrades to an immediate snap.
- [x] `SetRenderingSuspended` and `Unloaded` stop the animator so a frozen or removed gauge can't
  hold the render tick open.
- [ ] Extend the same treatment to the panel-card current-value readouts.

## 10.2 — Chart interaction smoothness

> **Correction**: an earlier draft of this section claimed drag-zoom caused a full XAML teardown
> per pointer move, and that hover used a linear point scan. Both were wrong — reading the code
> showed the selection rectangle already lives on its own retained `_interactionCanvas` and never
> triggers a chart redraw, and `FindNearestPoint` is already a binary search. The real cost was
> event *frequency*, not per-event work.

- [x] Coalesce pointer moves to one visual update per rendered frame. A 1000 Hz mouse raises
  `PointerMoved` far more often than the compositor draws, and every hover update rebuilt the
  tooltip string and ran a `Measure()` layout pass. Cheap state (`_selectionCurrentX`) is still
  written synchronously so `CompleteSelection` always sees the true release position; only the
  expensive visual work is deferred.
- [x] Cancel the queued flush on `PointerExited`, `Unloaded`, and selection completion so a chart
  can't leak a render-tick subscription.
- [x] ~~Separate the interaction overlay onto its own canvas~~ — already the case.
- [x] ~~Replace the linear hover scan~~ — already a binary search.
- [ ] Profile a drag-zoom on a busy panel (per-core CPU, all series visible) and record before/after
  frame times. The coalescing change is sound in principle but is currently unmeasured.
- [ ] Retain and mutate the data-layer `Path`/`PathGeometry` objects across redraws instead of
  clearing and reallocating. This is per *scrape*, not per pointer move, so it is a lower priority
  than it first appeared — but it still allocates a full path set every 2 s per panel.
- [ ] Animate zoom range changes (including preset-range clicks and double-click reset) so the
  viewport eases rather than cutting.

## 10.3 — Overlay HUD polish

Grounded in `Vaktr.Overlay/Rendering/OverlayBitmapRenderer.cs` and `OverlayController.BuildLines`.

- [x] **Column alignment**: `OverlayLine` (label / value / colour) replaces the pre-joined string,
  and the renderer measures the widest label once per pass to fix the column every value starts at.
  Values no longer sit ragged behind labels of wildly different widths.
- [x] **Label / value hierarchy**: labels draw at 72 % alpha of the configured colour, values at
  full strength, so the numbers read first.
- [x] **Colour-coded thresholds**: CPU / GPU / memory use the dashboard's exact `WarningBrush`
  (#F0C968, >75 %) and `CriticalBrush` (#FF9761, >90 %) values; GPU temperature uses 75 °C / 85 °C
  bands. Verified by rendering the HUD over a mixed bright/dark backdrop.
- [ ] **Fade in/out**: `LayeredOverlayWindow.Show()` / `Hide()` pop instantly. Ramp
  `UpdateLayeredWindow`'s `SourceConstantAlpha` over ~150 ms so the HUD arrives and leaves softly.
  Note `UpdateLayeredWindow` accepts a null source DC to change blend alone, so the fade does not
  need a full DIB re-blit per step — this needs an interop overload that passes `IntPtr.Zero`.
- [ ] **Metric cadence**: FPS refreshes at 2 Hz but CPU/GPU/memory come from `CollectorService` at
  the scrape interval (2 s default), so half the HUD is visibly stale. Either give the overlay its
  own lightweight faster sampler for the three cheap counters, or interpolate between snapshots.
  Decide explicitly — do not leave the mismatch.
- [ ] **Optional scrim**: outlined text is unreadable over high-contrast, high-detail scenes. Add
  an opt-in subtle rounded background at low alpha; keep "none" the default so the current clean
  look is preserved.
- [ ] **Live preview in the control deck**: render the actual overlay bitmap into the OVERLAY card
  so corner, colour, size, and opacity changes are visible without launching a game. The renderer
  is already decoupled enough to call directly — the scratch harness used during this step rendered
  it straight to a PNG.

## 10.4 — Control deck intuitiveness

- [ ] Settings currently apply only on Save. Live-preview colour, font size, and opacity as the
  user edits them, reverting on Cancel — editing a HUD you cannot see is the least intuitive part
  of the current flow.
- [ ] Show the resolved FPS source state (`OverlayController.FpsAccuracy`) in the OVERLAY card, with
  the sign-out/sign-in remedy inline when it reads Approximate. Today that only appears as a
  transient status line.
- [ ] Validation is currently pass/fail on Save. Show inline, per-field validation as the user
  types, with the acceptable range stated next to the field rather than discovered by failing.
- [ ] Hotkey capture: let the user press the combination to record it instead of typing
  `"Ctrl+Shift+F11"` as free text, and flag conflicts at capture time rather than at registration.
- [ ] Make the disabled state legible — when the overlay is off, visibly de-emphasise the rest of
  the card instead of leaving live-looking controls that do nothing.

## 10.5 — Window and layout transitions

- [ ] Panel reflow between the 1/2/3-column breakpoints currently cuts. Animate position and size
  through the reflow so a window resize reads as continuous.
- [ ] Panel drag-reorder: add a clear drop-target indicator and animate the displaced panel into
  its new slot rather than swapping instantly.
- [ ] Theme switch is instant; cross-fade brush changes over ~200 ms.
- [ ] Audit first-paint: confirm the loading sequence hands off to the dashboard without a flash of
  unstyled or empty panels.

## 10.6 — Accessibility and input

- [ ] Keyboard-drive the whole control deck: tab order, visible focus rings on `ActionChip` and
  `InlineTextEntry`, Enter/Escape to commit/cancel.
- [ ] Chips currently read as generic elements to a screen reader — set `AutomationProperties.Name`
  and a toggle-appropriate control pattern on `ActionChip`.
- [ ] Verify WCAG AA contrast still holds for any new state colours added in 10.3/10.4, in both
  themes. v1 claimed AA compliance; keep it true.
- [ ] Charts convey series only by colour — add a non-colour affordance (dash pattern or marker)
  for colour-blind users.

## 10.7 — Perf guardrails

- [ ] Measure idle and gaming CPU/memory after 10.1–10.3 and confirm the v1.2.0 budget still holds
  (idle < 250 MB / < 2 %; gaming < 300 MB / < 3 %). Animation work cuts directly against this —
  measure, don't assume. **Not yet measured.**
- [x] Motion is architecturally guaranteed to settle: `MotionTicker` detaches its
  `CompositionTarget.Rendering` handler when the last animator finishes, so a dashboard showing
  static values has no render subscription at all. Still worth confirming empirically above.
- [ ] Add the ETW struct-layout assertions to a real test project so the interop ABI is locked
  down permanently. Requires bumping `Vaktr.Tests` to `net8.0-windows10.0.19041.0` and adding a
  `Vaktr.Overlay` project reference (verified during Step 6 with a scratch harness, but not yet a
  standing test).

## 10.8 — Visual density pass (nested boxes)

The dominant reason the dashboard read as busy rather than sleek: almost every element drew its
own outline, so a chart appeared as a rectangle inside a rectangle inside a card inside a panel.
Fixing density is mostly deletion — the spacing and colour work was already sound.

- [x] **Legend rows** (`TelemetryPanelCard.CreateLegendRow`) — were an inset fill + 0.8px outline
  + radius 11 each. A 16-core CPU panel drew 16 of them, which is what made that card look like a
  stack of boxes. Now transparent and borderless, with a subtle hover fill as the only affordance.
  Click-to-isolate still works; focus state was always carried by `Row.Opacity`, not the border.
- [x] **Chart plot outline** (`TelemetryChart._plotFrame`) — stroke removed. The inset fill alone
  separates plot from card.
- [x] **Chart frame** (`TelemetryPanelCard._chartFrame`) — border removed; it was drawing a second
  rounded rectangle a few pixels outside the plot surface's own.
- [x] **Range / sort chips** (`ActionChip`) — inactive chips keep their surface fill but lose their
  outline, so `1m / 5m / 15m / 1h` reads as one segmented control with the selection bordered and
  filled, rather than four competing buttons. Inactive labels drop to secondary text weight so the
  active range is obvious at a glance. Focus ring is preserved unconditionally.
- [x] **Scale caption** (`_scalePill`) — "100% ceiling · 100.8k handles" is a caption, never
  clickable; unboxed so it stops competing with the real controls beside it.
- [x] **Single-series legends** — a one-row legend repeating the header value is removed, along
  with its divider. Affects CPU Total, Memory, GPU Memory, GPU Temperature, and both drive panels.
- [x] **Footer replay caption** — hidden unless zoomed. Unzoomed it said "1h replay", duplicating
  the highlighted range chip; zoomed it reports the real span, which nothing else shows.
- [x] **Section filler text** — "Current node snapshot." / "Real-time telemetry panels." dropped;
  the eyebrow already names the section and the cards below show what they are.
- [x] Process rows examined and **deliberately left alone** — they already use a 0.4px bottom
  hairline with a Grafana-style gradient meter fill, which is good design, not clutter.

**Verified in the running app** (captured with `PrintWindow`, no focus steal): legend rows, chart
outlines, segmented chips, and single-series legend removal all confirmed visually against the
previous `Assets/dashboard.png`. The scale caption, chart frame, footer caption, and section-filler
changes are built and tested but **not yet seen running** — they are the same class of change as
the verified ones, but should be eyeballed on next launch.

### Still open after this pass

- [ ] **Dead vertical space**: cards stretch to match the tallest in their row, so panels without a
  process table (GPU Memory, the drive panels) show a large empty region. Either let cards size
  independently or give the short panels something to fill with. Biggest remaining layout problem.
- [ ] **Header block**: logo + "Vaktr" + "SYSTEM TELEMETRY DASHBOARD" consumes a lot of vertical
  space above the fold for little information.
- [ ] **Process table chrome**: section label + sort chips + PROCESS/DETAILS/VALUE header is three
  rows of furniture above a simple list.
- [ ] A stray horizontal scrollbar artifact appears under the process list at some widths.

## 10.9 — Design pass (making it look designed, not plotted)

10.8 removed clutter but the result still read as generic: decluttering is subtraction, and nothing
had been added back. Three things were actually making it look basic.

### Strokes were wireframe, not hairline

The dark palette's strokes were saturated mid-blues at high contrast — `#28445E` shell, `#315274`
surface, `#426A8C` grid. Every card, chip, and gridline therefore drew a *visible outline*, which
is what made the dashboard look like a technical diagram rather than a designed surface.

- [x] `SurfaceStroke` `#315274` → `#1E2835`, `ShellStroke` `#28445E` → `#18212D`. Separation now
  comes from surface elevation; the stroke only catches the edge.
- [x] `SurfaceGrid` `#426A8C` → `#1A222D`. Gridlines should be felt, not read.
- [x] `TextMuted` `#8098B2` → `#7C8B9E` — slightly desaturated so metadata stops competing.
- [x] Light theme matched: strokes `#C8D2DE` → `#E3E9F0`, `#CAD4DF` → `#DFE6EE`, grid `#C7D2DE` →
  `#E7ECF2`.

### Type hierarchy had no focal point

Card title was 15.5px against a 28px value — only **1.8x** apart. Nothing dominated, so every panel
read as a wall of similarly-weighted text.

- [x] Title demoted to an eyebrow: 10.5px SemiBold, `CharacterSpacing` 95, uppercased
  ("CPU Total" → "CPU TOTAL"), secondary text colour, single line. It identifies the panel and
  gets out of the way.
- [x] Value promoted to hero: 28px → **36px**, `CharacterSpacing` -12 → **-28**. Negative tracking
  on large numerals is the detail that separates a designed readout from a big default font size.
- [x] Ratio is now ~3.4x. There is a clear focal point per card.

### Charts were washed out

The area gradient topped out at alpha 64 and was then multiplied by a 0.7 layer opacity — landing
near 18%, enough to tint the plot bed but too weak to give the trace any body.

- [x] Gradient richer at the crest and falling away faster: `64→12→0` at offsets `0/0.7/1` becomes
  `120→38→0` at `0/0.45/1`.
- [x] Single-series fills render at full opacity (was 0.7); multi-series drop to 0.45 so
  overlapping fills don't turn to mud.
- [x] Stroke weight up for sparse series: `2.0/1.7/1.35` → `2.4/1.9/1.4`.

**Status: built clean on x64 + ARM64, 47/47 tests pass, but NOT yet seen running.** This is a
larger visual change than 10.8 and the type hierarchy in particular should be eyeballed — the
36px hero value with `NoWrap` + ellipsis needs checking against the longest strings
("27.2 GiB used", "Drive C: Capacity") at the narrow 1-column breakpoint.

### Still open

- [ ] **Card depth.** Cards are flat fills with a hairline. Real elevation — a soft shadow under
  each card — is the remaining lever for making them feel like objects rather than regions.
  WinUI `ThemeShadow` + `Translation` is the mechanism; `TelemetryPanelCard` already animates
  `Translation` on hover, so the hook exists.
- [ ] **Backdrop depth.** The shell is a flat fill. A very subtle radial gradient behind the
  content would stop the background reading as a void.
- [ ] Dead vertical space, header block, and process-table chrome (carried from 10.8).
