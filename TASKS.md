# Vaktr — Dashboard Edit Mode

**Status:** Steps 1-7 implemented. Two fix passes after hands-on feedback (janky/glitchy resize,
then flicker). The second was an architectural change: the board no longer reflows during a
gesture at all. Also fixed a crash: `ActionChip` started a Storyboard on every `UpdateVisualState`
call (11 call sites), flooding XAML whenever a chip changed state — this is what crashed the app
on "Show processes". Step 8 verification still needs hands-on confirmation.

Grafana-style layout editing: an **Edit** button on Overview that unlocks free panel
resize/reposition, with live reflow, then saves positions that stay locked.

## Goal

- Edit button on the Overview board header. Outside edit mode the board is read-only.
- In edit mode: drag a panel to move it, drag its edges/corner to resize it.
- Other panels move out of the way live while dragging.
- Every panel can be freely sized and pinned to a fixed position.
- Save persists the layout; Cancel reverts. Saved layouts survive restart and window resize.
- **Chart drag-to-zoom keeps working after save** — resize handles must not steal those pointer
  events outside edit mode.

## Design decisions

**Grid.** 12 columns. Divides cleanly by 2/3/4/6, so a third is `W=4`, a half `W=6`, full `W=12`.
Rows are a fixed `RowUnitPx` so `H` means a real height — today rows are `Auto`, which is why only
a column *span* is settable and height is whatever the content wants.

**Panel geometry** is `{X, Y, W, H}` in grid units, replacing `PanelSizePreset` + flow order.
Order-based flow cannot express "pinned at row 3, column 5", which is the whole ask.

**Collision** uses Grafana's rule: a moved panel displaces others *downward*, never sideways, then
the board compacts upward to close gaps. Predictable and stable under repeated drags.

**Responsive.** Saved `W` is honoured at full width; below breakpoints panels are clamped wider
(half, then full) so a narrow window stacks instead of producing unreadable slivers.

---

## Step 1 — Geometry model and persistence

- [x] `Vaktr.Core/Models/PanelLayout.cs` — `{ X, Y, W, H }` with clamping. Width clamps before X
      so an over-wide panel shrinks rather than being shoved off the left edge.
- [x] `VaktrConfig.PanelLayouts: Dictionary<string, PanelLayout>`; normalizes invalid entries.
- [x] Migration via `GridLayoutEngine.FromLegacy` — runs at placement time, not in `ApplyConfig`,
      because the panel list is not populated yet when config is applied.
- [x] Tests: clamping, legacy packing (3 standard panels per row), free-slot search.

## Step 2 — Layout engine

- [x] `Vaktr.Core/Models/GridLayoutEngine.cs` — pure, no XAML (lives in Core so it is directly
      testable): `Resolve`, `Move`, `Resize`, `FindFreeSlot`, `FromLegacy`. Push-down displacement
      then upward compaction; the dragged panel is pinned first so it keeps the cell it was
      dropped on.
- [x] 12 engine tests: push down not sideways, compaction, determinism/stability, move displaces
      occupant, resize clamping, and 14 panels dropped on one cell (the push-down loop's
      pathological case).

## Step 3 — Render fixed geometry

- [x] `_dashboardGrid`: 12 star columns, fixed 48px rows.
- [x] Cards placed by `Grid.SetColumn/Row/ColumnSpan/RowSpan` from resolved geometry.
- [x] Responsive clamping derived from measured width (the breakpoint *count* is 0 on first
      placement and forced everything full width).
- [x] Card `MinHeight` 320 → 140 and `VerticalAlignment` Stretch so height comes from `H`.
- [x] Fixed a rebuild loop: `ReorderDashboardPanelsInPlace` compared the grid's column count
      against the responsive breakpoint count (1-3), which never matches 12, so every reorder
      queued a rebuild that queued another.
- [x] Kept the existing **Size** chip working by translating presets into geometry through the
      engine — otherwise it silently became a no-op.
- [ ] Verify small `H`: charts/gauges/process tables at the minimum allowed height.

## Step 4 — Edit mode shell

- [x] `Edit layout` / `Done` + `Cancel` chips in the board section header.
- [x] `_isLayoutEditMode` gates everything; geometry snapshotted on entry for Cancel.
- [x] Edit affordances: accent card borders and a visible corner handle. Verified by screenshot.
- [x] **Critical gate done:** the handle layer is `Visibility.Collapsed` *and*
      `IsHitTestVisible = false` outside edit mode, `_chart.IsHitTestVisible` is restored, and the
      header drag returns early. Verified the handles disappear on Done.
- [ ] Still to prove by hand: chart drag-to-zoom after saving a layout.
- [ ] Nice-to-have: grid overlay while editing, so target cells are visible.

## Step 5 — Drag to move

- [x] Pointer capture + render-transform translation already existed from the legacy reorder.
- [x] Drop converts the card's pixel `DragOffset` into grid cells and calls `GridLayoutEngine.Move`.
- [x] Legacy swap-reorder suppressed in edit mode so the two cannot disagree about one gesture.
- [ ] **Untested by hand.** Displaced panels currently snap to their new cells rather than
      animating — add the reflow animation once the gesture is confirmed correct.
- [ ] Live preview while dragging (the move is currently only applied on drop).

## Step 6 — Drag to resize

- [x] **All 8 handles**: 4 edges + 4 corners, each with the correct cursor. Edges are inset by the
      corner size so a corner hotspot is reachable along its whole length — previously the edge
      handles covered the corners and the diagonal cursors only appeared in stray pixels.
- [x] Corner grips render at 32% opacity in edit mode, 95% on hover. Invisible-until-hover hides
      the affordance: you have to already know the grip is there to find it.
- [x] Left/top edges move position and size together, so the opposite edge stays anchored.
- [x] Min/max enforced by `PanelLayout.Normalize`, with left/top clamped before applying so a
      panel cannot invert through its own minimum.

### Fix pass 1 — compounding, thrash, and edge expression

1. **Compounding deltas (the glitching).** The handle reports a delta absolute from pointer-down,
   but the shell added it to the *live* geometry it had already been mutating. Move one cell →
   `W=5`; move two → `W=5+2=7` instead of 6. Fixed by capturing geometry at drag start. Covered by
   `Apply_Repeated_Growth_Does_Not_Compound`.
2. **`Resize` could not express an edge drag.** Dragging a left edge changes X *and* W together.
   Added `GridLayoutEngine.Apply(key, layout)`; `Move`/`Resize` are now wrappers over it.
3. **Missing corner cursors.** Only 3 handles existed, and the edge handles covered the corners so
   diagonal cursors appeared in stray pixels. Now 8 handles with edges inset by the corner size.

### Fix pass 2 — the flicker (architectural)

Coalescing reflow to one per frame was not enough: **reflowing the real grid during a gesture at
all** is the wrong model. Re-spanning grid cells re-measures the dragged card, its chart, and all
13 siblings on every update.

Replaced with the standard editor pattern (react-grid-layout, Grafana):

- [x] The dragged card leaves grid layout for the gesture: explicit pixel size, pinned top-left,
      raised z-index, so it grows over neighbours without moving them.
- [x] Size feedback is **continuous and un-snapped** — the panel tracks the pointer exactly.
      Snapping the card itself is what made it feel steppy.
- [x] A cheap `Border` placeholder (no chart, no text) shows the snapped destination cell. Only
      the placeholder moves through the grid during the gesture.
- [x] Chart and gauge rendering suspended on the dragged card — redrawing mid-gesture is wasted
      work and the most visible flicker source.
- [x] Exactly **one** layout pass, on release.
- [x] Drag-to-move gets the same placeholder preview.
- [x] Leaving edit mode mid-gesture restores a stranded card rather than leaving it floating.

This matches Apple's HIG split between **direct manipulation** (immediate, visible, proportional
response — the card follows the pointer exactly) and **feedback** (communicating the result —
the placeholder shows where it will land).

## Step 7 — Save / Cancel

- [x] `Done` writes `PanelLayouts` through the debounced persist path — verified, 14 entries.
- [x] `Cancel` restores the entry snapshot and re-renders.
- [x] Leaving edit mode re-enables chart interaction.
- [ ] Cancel untested by hand (needs a real drag to revert).

## Step 8 — Verify

- [ ] Resize the window across all breakpoints with a saved layout — no overlap, no clipping.
- [ ] Restart: layout restored exactly.
- [ ] **Chart drag-zoom works after saving a layout** (the regression this feature most risks).
- [ ] Reduced-motion honoured for reflow animation.
- [ ] Panel reflow stays smooth with 14 panels; no layout thrash during drag.

---

## Picking this up

**State:** builds clean on x64 + ARM64, 71/71 tests. Board renders from saved geometry; the Size
chip still works. Nothing is worse than before this feature started.

### Where things are

| Thing | File |
|---|---|
| Geometry model | `Vaktr.Core/Models/PanelLayout.cs` |
| Collision engine (pure, testable) | `Vaktr.Core/Models/GridLayoutEngine.cs` |
| Engine tests | `Vaktr.Tests/GridLayoutEngineTests.cs` |
| Geometry state + migration | `DashboardViewModels.EnsureLayoutsFor` / `ApplyPlacements` |
| Placement into the grid | `ShellWindow.Polished.cs` → `PlaceDashboardPanels` |
| Drag-to-reorder (existing, pre-dates this) | `TelemetryPanelCard` drag handlers |

### The engine already does the hard part

`Move(panels, key, x, y)` and `Resize(panels, key, w, h)` return a fully resolved board with the
dragged panel pinned to its target cell. Step 5/6 only need to convert pointer position into grid
units, call these, and animate cards to the result. Do **not** re-implement collision in the UI
layer — it is the part that is already proven.

Pixel to grid: `col = round(pointerX / (gridWidth / 12))`, `row = round(pointerY / 48)`.

### Traps already hit — do not re-learn these

- **Never compare `_dashboardGrid.ColumnDefinitions.Count` against `DetermineDashboardColumns()`.**
  The grid is always 12; that helper returns the responsive breakpoint count (1-3). Mismatching
  them caused an infinite rebuild loop.
- **Responsive clamping must read measured width**, not the breakpoint count — the count is 0 on
  first placement and forces every panel full width.
- **Do not assign geometry in `ApplyConfig`.** The dashboard panel list is not populated yet.
  `EnsureLayoutsFor` runs from the render path for this reason.
- **Writing a bad layout poisons the config.** `%LocalAppData%\Vaktr\vaktr-settings.json` →
  delete the `panelLayouts` key to reset the board while iterating.

### Step 4's real risk

Chart drag-zoom shares the pointer with any move/resize affordance. Handles must be
`IsHitTestVisible = false` outside edit mode — not merely invisible. Verify by dragging a time
range on a chart *after* saving a layout; that is the regression this feature most likely causes.

### Verifying UI changes in this repo

`dotnet` is not on PATH — use `C:\Program Files\dotnet\dotnet.exe`. Kill `Vaktr.exe` before
building or the DLL copy fails.

Screenshot without stealing focus (the user games on this machine — never force foreground):
`PrintWindow(hwnd, hdc, 2)` against the `WinUIDesktopWin32WindowClass` window. Synthesised clicks
(`mouse_event`, `SendInput`, UIA) have been unreliable here; prefer a temporary env-var flag that
drives the app into the state you need, then capture.

When layout looks wrong but `ActualWidth` looks right, paint container backgrounds a solid colour
and screenshot — that localised the ScrollViewer zoom bug in one step after a long hunt.

---

# UI/UX Audit

A deliberate pass over the whole surface rather than spot fixes. Items below are grounded in
things actually observed in this codebase — not a generic checklist.

## A — Button and control organisation

- [ ] **Card header is the busiest element left.** Every panel carries `Processes | Size | 15m` as
      three separate pills. Decide: collapse into one overflow menu (Grafana-style kebab), make
      them icon-only, or show them on hover. Three equal-weight pills per card × 14 cards is the
      single largest source of visual noise.
- [ ] Audit chip *grouping* semantics: range chips are a segmented control (one choice of four),
      but `Processes` is a toggle and `Size` opens a picker. Three different behaviours currently
      share one visual treatment.
- [ ] Decide where global vs per-panel controls live. The global range chip sits in the section
      header while per-panel range sits in each card — confirm that reads as a hierarchy.
- [ ] Settings "Edit" button placement and label consistency against the new board Edit button.

## B — Startup animation

- [ ] Re-time after the Step 1-3 grid change: the cascade was tuned for flowing rows, and fixed
      geometry may want a different order (top-left outward rather than list order).
- [ ] Decide whether summary cards and panels should share one cascade or read as two groups.
- [ ] Confirm the loading screen → dashboard handoff has no flash of unstyled/empty panels.
- [ ] Verify `UISettings.AnimationsEnabled` is respected on *every* animated path, not just
      `Motion` (panel card Storyboards and the new drop settle included).

## C — Hover and focus

- [ ] Sweep for any remaining hover handler that touches a layout property. `BorderThickness` was
      fixed in three places; audit `Margin`, `Padding`, `Width`, `FontSize` for the same mistake.
- [ ] Unify hover vocabulary. Cards lift 1px, chips scale on press, legend rows change background,
      process rows brighten a gradient — four different idioms. Pick a system.
- [ ] Focus rings: `ActionChip` has one; confirm `InlineTextEntry`, legend rows, process rows and
      nav buttons are all keyboard-reachable and visibly focused.
- [ ] Hit targets: verify chips and rows meet a sensible minimum height for pointer accuracy.

## D — Panel dragging

- [ ] Drag affordance is currently invisible until you try it. Needs a grip cue (on hover, or in
      edit mode once Step 4 lands).
- [ ] Drop target indication: confirm where a panel will land *before* releasing.
- [ ] Verify the drop settle animation tuning once drag is exercised by hand (untested).
- [ ] Decide whether dragging stays available outside edit mode or becomes edit-only — currently
      reorder-drag and the new edit mode would overlap.

## E — Charts

- [ ] **Cold start looks broken.** On first launch the plot is empty across most of its width
      until history accumulates. Either clamp the axis to available data or show an explicit
      "collecting" state — this is the first thing a new user sees.
- [ ] Last-point marker: 14px glow ellipse at 0.22 opacity plus a 5.5px dot. Review whether the
      glow still reads correctly against the richer gradient fill (now `120→38→0`).
- [ ] Review area fill opacity for multi-series panels (CPU Cores, 16 overlapping fills at 0.45) —
      check it does not turn to mud.
- [ ] Gridlines are now near-invisible hairlines; confirm they still orient the eye at large `H`.
- [ ] Series colour palette is blue-heavy (CPU cyan, GPU blue, Network blue, System blue). Disk
      moved to violet for threshold reasons; consider whether the rest needs separating.
- [ ] Non-colour series differentiation for colour-blind users (dash pattern or marker).

## F — Typography

- [ ] The eyebrow/hero treatment (10.5px tracked label over a 36px tight value) was applied to
      panel cards only. Audit summary cards, settings cards and section headers for the same
      hierarchy — right now they each set their own sizes.
- [ ] Consolidate the type scale. Sizes currently range across 9.5/10/10.5/11/11.5/12.5/13/15.5/
      22/36 set ad hoc at call sites; define a small named scale and use it.
- [ ] Mixed font families: `Bahnschrift` for numerals, `Segoe UI Variable Text/Display` for UI.
      Confirm that is intentional and applied consistently.
- [ ] Verify tabular figures everywhere a number updates live, so values do not reflow.

## G — Consistency and theming

- [ ] Audit all 8 themes, not just Dark. Sweep for remaining hardcoded hex fallbacks that drift
      when the palette changes (this already bit `ActionChip` and `InlineTextEntry`).
- [ ] Verify WCAG AA contrast for text and threshold colours in every theme — `Nord` and
      `Sandstone` are the lowest-contrast and most likely to fail.
- [ ] Corner radius inventory: 3/8/9/10/11/14/16/20 appear across controls. Reduce to a scale.
- [ ] Spacing inventory: confirm padding and gaps come from shared constants rather than literals.
- [ ] Confirm threshold colours are reserved for thresholds everywhere (the Disk accent collision
      was one instance; check gauges, summary bars and the overlay agree).
- [ ] Empty, loading and error states: give them the same care as the populated state.

---

# Audit findings

Two read-only audits (performance/correctness, and UI/UX against Apple's HIG). Items marked `[x]`
were fixed in this pass; the rest are ranked and ready to pick up.

## P0 — fixed

- [x] **Crash on "Show processes."** `ActionChip.ApplyPressScale` ran from all 11 `UpdateVisualState`
      call sites, starting a Storyboard on every hover, focus, `IsActive`/`IsFilled` change and
      theme refresh. One click cascaded into a storyboard flood across every chip. Now only
      animates on a real press transition. Verified with 40 rapid toggles.
- [x] **UI-thread deadlock on close.** `OnWindowClosed` blocked on `_pendingHistoryLoad`, whose
      continuation was posted back to the very `DispatcherQueue` it was blocking. Closing during a
      long-retention history load hung the process with no window and no exit. Now
      `ConfigureAwait(false)` + an explicit marshal back, and the close wait is bounded to 2s.
- [x] **Leaked kernel ETW session.** Every failure path after a successful `StartTrace` freed the
      properties buffer and zeroed the handle without stopping the session, leaving
      `VaktrFpsSession` live with DXGI+D3D9 enabled for the rest of the OS boot — and
      unrecoverable, since `StopTracking` then early-returns. `Cleanup` now stops first.
- [x] **Native callback could be collected mid-trace.** The delegate field is the only GC root for
      the thunk ETW holds; it was nulled after a 2s join timeout even if `ProcessTrace` had not
      returned. Next event → access violation on the consumer thread. Root is now kept unless the
      consumer demonstrably exited (join raised to 5s).
- [x] **Off-screen panel gate never engaged.** `EffectiveViewport` is in the element's own
      coordinate space, so `Width > 0 && Height > 0` was true even for panels scrolled far away —
      all 14 panels kept redrawing charts when four were visible. Now an intersection test.
- [x] **Zombie cards.** `TelemetryPanelCard` never unsubscribed from its view model, and the view
      models are permanent — every toggled-off panel stayed alive redrawing its chart forever.
      Added `Unloaded` detach.
- [x] **Blocking file I/O per overlay frame, during gameplay.** `OverlayLog.Write` did a
      synchronous open/append/close on the overlay message-loop thread 2-3×/second while gaming,
      from four threads, unbounded in size. Now off by default (`VAKTR_OVERLAY_LOG=1`), queued,
      single background writer, size-capped.
- [x] **Panels could be resized into overlap.** `ApplySizePreset` forced `MinHeight` 280-392px
      while the grid reserved only `MinHeight = 3` rows (144px). Grid floor raised to 7 rows
      (336px) and the per-preset override removed, so the two floors agree.
- [x] **Wrong-axis clamp.** `UpdateResizePreview` clamped *width* with `MinHeight / 2`.
- [x] **Two contradictory drop previews.** A proximity swap highlighted a neighbour ("I'll swap
      with this") while the authoritative placeholder said otherwise — and the swap was discarded
      in edit mode anyway, so the louder preview was simply wrong. Dead path removed entirely.
- [x] **Hover erased the edit affordance.** `SetHoverState` was not gated on edit mode, so moving
      the pointer toward a panel wiped the accent border telling you it was draggable.
- [x] **`MotionTicker` had no per-callback exception isolation.** One throwing animator aborted
      the whole frame, was never removed, and threw again at 60Hz forever.

## P1 — performance, not yet done

- [ ] **`TelemetryChart.Redraw` rebuilds ~4,400 XAML objects per scrape** (~9,000/s at a 1s
      interval). One `LineSegment` *per point*, twice when a fill is drawn, plus a full grid
      rebuild even when only data changed. Two fixes: a single `PolyLineSegment` with a mutable
      `PointCollection` (N→1 objects, and retainable the way `UsageGauge` already retains its
      `ArcSegment`), and a separate dirty flag for the grid. **Highest-value remaining item.**
- [ ] Summary-card meter sets `Width` every snapshot — use `ScaleTransform.ScaleX`.
- [ ] `GridLayoutEngine.Compact` is O(n²·Y) walking one row at a time with a clone per step
      (~15,000 overlap tests per drop). A per-column skyline makes it O(n·W).
- [ ] Hover tooltip rebuilds strings and re-measures every frame; cache on anchor timestamp.
- [ ] Collector: `CollectAsync` is synchronous so its timeout can never fire; shutdown disposes
      the collector without awaiting the loop task (races the PDH buffer).
- [ ] The 5s resize safety timer is never cancelled and can abort a second resize mid-gesture.
- [ ] `ThemeVisuals.ResolveBrush` does a `ResourceDictionary` probe ~150×/scrape; cache it.
- [ ] Snapshot handoff fields are cross-thread without `volatile`/`Interlocked`.
- [ ] GDI handles leak on the overlay's exception path (no try/finally); `Dispose` never calls
      `DestroyWindow`.
- [ ] `Marshal.GetLastWin32Error()` read from P/Invokes declared without `SetLastError = true` —
      the reported codes are garbage and will mislead the next ETW/ULW investigation.

## P1 — UI/UX, not yet done

- [ ] **Promote `Motion.cs` to a real token set** (durations + 2 easings) and route hover, edit-mode
      entry, process expand/collapse, legend dim, threshold colour and view switching through it.
      The app currently has **7 animations total**; nearly every deliberate state change snaps.
      This is the bulk of the "extremely smooth" gap.
- [ ] Reduced motion is honoured in only 3 of 7 animations, and `AnimationsEnabled` caches once
      per process and never refreshes.
- [ ] `InlineTextEntry` simulates a text box: no caret, selection, arrow keys, Ctrl+A/C/V, IME or
      undo — and it governs every settings field. Biggest interaction-correctness debt.
- [ ] Status text is the only feedback channel: one truncating 300px caption with severity decided
      by substring-matching English prose. Edit-mode instructions overflow it.
- [ ] View switching is a hard cut and discards scroll position (HIG: preserve context).
- [ ] Card border thickness drifts across five code paths, each a layout pass — contradicting the
      comment that says it must stay constant. `ActionChip` does the same on focus.
- [ ] The 30px live value has no tabular figures, so the headline number reflows every tick.
      Two numeral typefaces in one card.
- [ ] Threshold colour has no hysteresis (flickers at 74.8/75.2) and is percent-only, so
      temperature panels — the hottest readings in the app — never turn amber or red.
- [ ] Keyboard support is one `if`: Enter/Space on `ActionChip`. No accelerators, no Escape from
      edit mode, resize handles aren't tab stops, chart gestures are pointer-only.
- [ ] Contrast failures, all from stacking `Opacity` on already-muted brushes: process column
      headers measure **2.2-2.6:1** in every theme (AA needs 4.5). Use the semantic text brushes.
- [ ] Focus is conflated with hover — `GotFocus` sets `_isHovered = true`.
- [ ] Hit targets are 22-36px across five sizes, below both HIG (44pt) and Windows (32px).
- [ ] Dead UI: `_perProcessChartButton` is fully wired but never added to the tree; `_edgeGlow`
      can never render; three of four arms of `AddStatusField`'s accent switch are unreachable.
- [ ] Legacy cyan hardcoded into settings cards bypasses the palette, so Sandstone/Ember/Violet
      still show cyan icons. Off-palette resource *fallbacks* compound it.
- [ ] Scale inventories to consolidate: **19 font sizes**, **8 corner radii**, **8 border weights**.
- [ ] Chart has three gestures (drag-zoom, click-pin, double-click-reset) with no cursor, tooltip
      or hint.

## Security and robustness

- [ ] Audit the Performance Log Users grant — it permits system-wide trace collection, not just
      Vaktr's. Confirm it is documented honestly and that declining degrades cleanly (it does).
- [ ] Installer uses `powershell -ExecutionPolicy Bypass` from an elevated context; prefer a
      direct API or `net localgroup` by SID.
- [ ] Validate the user-configurable storage path (reject outside-profile and UNC paths).
- [ ] Prove the "no network calls" README claim with a static check or blocked-egress run.
- [ ] Confirm a corrupt/partial config fails safe to defaults.
- [ ] Review the `HKCU\...\Run` auto-launch write for quoting/path injection.
