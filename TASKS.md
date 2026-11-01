# Vaktr v1.2.0 — Tasks

**Status (2026-10-31):** Dashboard edit mode rebuilt after tracing a live gesture frame by frame;
the crash on Reset zoom → 24h fixed; whole-UI UX pass done. Release x64 + ARM64 build clean,
87/87 tests. **Needs hands-on confirmation:** drag/resize feel with a real mouse (all verification
was driven in-process — see "Verifying" below), and Esc/touch paths.

---

## Edit mode — how it works now

Modelled on react-grid-layout (Grafana's grid), adapted to WinUI. Code: `ShellWindow.LayoutEditor.cs`.

- The panel under the pointer **floats**: it spans the whole board (so its slot can never clip it),
  follows the pointer 1:1 at its **real size** — content reflows, nothing is scaled.
- The board is re-resolved through `GridLayoutEngine` only when the **snapped cell** changes.
  Other panels spring to their resolved cells via `UIElement.Translation` — no layout pass.
- A placeholder springs to exactly where the panel will land.
- Release commits once; siblings' springs are re-based onto the committed layout (FLIP), so
  nothing jumps. Pointer input is coalesced to one update per frame.
- Edits are a draft until **Done** (saves) / **Cancel** (panels glide back). **Esc** cancels an
  in-flight drag, then exits edit mode keeping edits. Switching to Settings exits edit mode.
- Grips appear on the hovered panel only; whole card body is the move handle in edit mode.

## This pass — fixed

Root causes of the jank, found by tracing a real gesture (UI-thread tick now 0.12–0.14ms/frame):

- [x] **Resize preview scaled the card** (Codex's version): title, chips and chart magnified like a
      zoom and re-rasterised every frame — 28–76ms frames vs 7ms idle.
- [x] **Drop placeholder never visible** — `RefreshDashboardPanels` cleared its canvas and nothing
      re-added it (`canvasAttached=False` on every traced frame).
- [x] **Snap ran 29% ahead of the pointer** — pixels divided by the 48px row unit, ignoring the 14px
      gap (real pitch 62px). Pitch-correct geometry throughout.
- [x] **Left/top resize fed back on itself** — pointer read in the card's own (moving) space.
- [x] **Growing a panel was clipped** at its original cell (WinUI layout clip on oversized children).
- [x] **Panels didn't fill their height** — chart was a fixed 176px, so taller panels were dead
      space and released content popped. Card is now rows; chart fills, process list shares height.
- [x] **Everything snapped on release** — now springs (velocity-preserving, interruptible).
- [x] Snapshots deferred during motion (no chart re-render mid-gesture). The old window-resize
      deferral re-queued itself at Low priority and spun the dispatcher for the whole resize.

Startup (reported stuck on "Starting telemetry", nothing clickable):

- [x] **Workers called into the UI-thread `Window`.** The startup continuation and every snapshot
      posted via `Window.DispatcherQueue` from background threads - a cross-apartment COM call into
      the UI thread that can stall or be rejected depending on what the UI thread is doing. The
      trace stopped exactly there. All posts now use a `DispatcherQueue` captured on the UI thread
      (agile); startup continuations resume on the UI thread.
- [x] **One throwing subscriber killed sampling for the session.** `SnapshotCollected` threw inside
      the collector's try, `CollectionFailed` threw again from the catch, and the unobserved loop
      task died. Subscribers are now isolated; snapshot handoff is `Interlocked`.
- [x] **A transparent loading overlay could swallow every click** if its removal was late; it now
      stops hit-testing the moment it starts fading.
- [x] Faster: collector PDH setup (~0.45s) moved off the UI thread; store opens first and history
      loads in parallel with collector setup. Board populated ~1.2s after launch (was ~2.5s), first
      live sample ~1.6s (was ~2.9s). `PublishReadyToRun` on for Release publishes.

Crashes and correctness:

- [x] **0x800F1000 crash on Reset zoom → 24h.** The range editor re-added persistent chips that were
      still parented to the previous (detached, not-yet-collected) rows — GC-timing dependent.
      `DetachFromParent` now guards every chip re-parent. Same exception class closed in the legend
      and process lists (duplicate keys added one element twice).
- [x] **Cards silently stopped updating after a board rebuild** — `Loaded` never re-attached the
      view model that `Unloaded` released. Attach is now idempotent and re-runs on load.
- [x] `TelemetryChart` retained rendering: one `PolyLineSegment` per series, pooled grid/labels
      (was ~4,400 objects per scrape). Redraws in the same layout pass during resize.
- [x] Theme brushes cached by key (palette recolours in place) — was ~150 dictionary probes/scrape.
- [x] Summary meters animate a scale instead of setting `Width` each sample.

UI/UX pass:

- [x] Time range is a compact flyout on its chip (was a full-width panel that shoved the board down).
- [x] Settings edit in place — no read-only summary + Edit step. Theme applies and saves on click.
- [x] Card header: removed the per-card **Size** chip (edit mode resizes) and per-card **range** chip
      (it changed every panel — duplicated and misleading). Only **Processes** remains.
- [x] Board header: Done replaces Edit in place, Cancel beside it; range controls step aside in edit
      mode. Edit instructions sit beside the title (no longer truncated in the status line).
- [x] Type ramp on cards/summary/chart (14 title / 12 body / 11 caption / 10.5 label / 10 axis);
      tabular figures on every live number; one numeral family per card; no Opacity stacking on
      muted text (process headers were ~2.4:1).
- [x] Thresholds: 2-point hysteresis; temperatures now warn at 80°C and go critical at 90°C.
- [x] `ActionChip`: keyboard-only focus ring (focus no longer fakes hover); constant 1px border;
      12px label. Preset chips no longer widen when selected.
- [x] Motion tokens (`MotionTokens`: 90/150/220/320ms + spring responses); `SpringValue`;
      reduced-motion setting now refreshes live; each view keeps its scroll position.
- [x] Settings: palette accent for section icons (was hardcoded cyan); proper glyphs for
      Appearance/Overlay; short fields sized to their values; 8 themes on one row when wide.
- [x] Dead UI removed: `_perProcessChartButton`, `_edgeGlow`, settings summary + `AddStatusField`,
      per-field editor surfaces, swap-reorder path.

## Open — next up (ranked)

- [ ] Optional: `WindowsAppSDKSelfContained` would drop the Windows App Runtime bootstrap from
      launch (~0.1-0.3s) at the cost of a larger install — a deployment decision.
- [ ] **Hands-on check of drag/resize with a real mouse**, Cancel glide-back, Esc, and chart
      drag-zoom after saving a layout. (Frame times measured while a game held the GPU at 86–91% and
      the window was occluded — re-measure with no game running.)
- [ ] `InlineTextEntry` simulates a text box (no caret/selection/clipboard/IME). Replace — the app
      skips `XamlControlsResources`, so a styled `TextBox` needs those resources merged or restyled.
- [ ] Status channel: severity is substring-matched English; give it a real severity field.
- [ ] Keyboard: accelerators for views/range, resize handles as tab stops, chart gestures.
- [ ] Chart: cold-start axis (clamp to available data), gesture hints (drag-zoom/click-pin/double-
      click reset have no cursor or tooltip), colour-blind series differentiation.
- [ ] Remaining consistency: corner-radius and border-weight scales; contrast sweep of all 8
      themes (Nord, Sandstone lowest); hit targets below 32px.
- [ ] View switch is a fade-rise; consider a directional transition.
- [ ] Perf: `GridLayoutEngine.Compact` skyline (O(n·W)); hover tooltip caching; collector timeout
      never fires and shutdown races the PDH buffer; cross-thread snapshot fields lack
      `volatile`; overlay GDI handles leak on exception path; `SetLastError = true` on P/Invokes.
- [x] `App.cs` first-chance COMException logger (added for diagnosis) now Debug-only; remove it
      once the 0x800F1000 crash is confirmed gone.

## Security and robustness

- [ ] Audit the Performance Log Users grant (system-wide trace rights); confirm honest docs and
      clean degradation when declined.
- [ ] Installer runs `powershell -ExecutionPolicy Bypass` elevated; prefer `net localgroup` by SID.
- [ ] Validate the storage path (reject UNC / outside-profile).
- [ ] Prove the "no network calls" README claim with a static check or blocked-egress run.
- [ ] Confirm a corrupt/partial config fails safe to defaults.
- [ ] Review the `HKCU\...\Run` auto-launch write for quoting/path injection.

---

## Picking this up

**Where things are:** `ShellWindow.LayoutEditor.cs` (gesture, reflow, placeholder, settle,
auto-scroll, Esc), `TelemetryPanelCard` (gesture input + float API: `BeginFloat` /
`SetFloatGeometry` / `EndFloat` / `SetReflowOffset`), `GridLayoutEngine` (`Apply`, `ResizeByDelta`,
`ResolveResponsive` — keep collision logic there), `Motion.cs` (`MotionTokens`, `SpringValue`).

**Traps already hit — do not re-learn these:**

- Grid pitch includes spacing: `columnPitch = (width + ColumnSpacing) / 12`, `rowPitch = 48 + 14`.
- Read gesture pointers in window-content space, never the card's own (it moves).
- A WinUI child larger than its grid slot is clipped to the slot — float spans the whole board.
- Adding an element that still has a parent throws 0x800F1000, even if that parent is detached.
- `Children.Clear()` + re-add fires `Unloaded` — anything released there must re-attach on `Loaded`.
- `RefreshDashboardPanels` clears the grid: re-add every overlay element it owns.
- Never compare `ColumnDefinitions.Count` to `DetermineDashboardColumns()` (12 vs 1–3 → rebuild loop).
- Don't assign geometry in `ApplyConfig` (panel list not populated yet).
- Bad layout poisons the config: delete `panelLayouts` in `%LocalAppData%\Vaktr\vaktr-settings.json`.

**Verifying:** `C:\Program Files\dotnet\dotnet.exe`; kill `Vaktr.exe` before building. Capture with
`PrintWindow(hwnd, hdc, 2)` on the `WinUIDesktopWin32WindowClass` window — never force foreground
(the owner games on this machine; the in-game overlay also appears over a running game whenever the
app launches). Real input can't be injected safely, so gestures were driven in-process: a temporary
env-gated probe called the card's gesture events with window-space points each frame and logged
geometry + frame timing; frames were captured externally. Re-create it rather than shipping it.
