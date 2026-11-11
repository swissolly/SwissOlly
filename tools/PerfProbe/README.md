# PerfProbe

Temporary tooling for timing the UI and checking themes in a Release build. Nothing here is
compiled into the app: copy the probe in for a run, then take it out again.

Last used 2026-11-09 for the theme timings, the 12-theme stale-colour pass and the startup
animation frames; the results are in TASKS-APP.md sections 1 and 5. Still unmeasured in Release:
the range picker, the process popover and Edit layout.

## Theme timings and screenshots

1. Copy `PerfProbe.cs` and `ShellWindow.PerfProbe.cs` into `SwissOlly.App/`.
2. Add two temporary lines, each marked `// PERF-PROBE`:
   - at the end of `BuildFullUi`: `StartPerfProbeIfEnabled();`
   - in `BuildThemePicker`, where each card is created: `_themeCards[mode] = card;`
3. Build Release x64, back up `%LocalAppData%\SwissOlly\swissolly-settings.json`, close the
   running copy (note its command line), and run the build with `SWISSOLLY_PERF_PROBE=1` set.
   The probe clicks through the themes by itself. It writes timings to
   `%TEMP%\swissolly-perf\probe-*.log` and screenshots to `%TEMP%\swissolly-perf\shots\`.
4. Restore the settings file, relaunch the owner's copy as it was, then delete the two copied
   files and the marked lines. `git grep PERF-PROBE -- SwissOlly.App` must find nothing.

## Startup frames

```powershell
.\StartupFrames.ps1 -Exe <path to the Release SwissOlly.exe> -OutDir <folder>
```

It launches the app and captures its window with `PrintWindow` every 30 ms for 1.5 s
(`-DurationMs`, `-IntervalMs`), then writes the frames as PNGs. It never takes the foreground
itself, though the app activates its own window as it starts, and it leaves the app running:
close it afterwards. `-ResizeAtMs 600 -ResizeBy 300` narrows the window part way through, to
check that a chart's draw-in clip doesn't outlive a resize. To see the launch with animation
effects off, turn them off in Windows (Accessibility > Visual effects) for the run.

## Looking at a running copy

```powershell
.\Drive.ps1 -OutDir <folder> -Do 'shot:overview','invoke:Settings','wait:600','scroll:100','shot:settings-end'
```

Drives a running Release copy without the mouse, the keyboard or the foreground. Steps, in
order: `shot:name` (the whole window, by `PrintWindow`), `crop:name:x,y,w,h`, `size:WxH`
(no activation), `wait:ms`, `invoke:<accessible name>` (a button, chip or theme card, through
UI Automation; theme cards are named "Dark theme" and so on), `scroll:<percent>`, and `dump`
(lists the names it can invoke). A theme click saves at once and chips change drafts, so back
up and restore the settings file as above. Used 2026-11-10 to check the summary strip's ring
gauges and the Settings wording.

`CloseApp.ps1` closes every running copy the way the X button does. A copy started from Visual
Studio ignored a plain `WM_CLOSE`.

Before any of these: check that no game is running or in the foreground (see CLAUDE.md).
