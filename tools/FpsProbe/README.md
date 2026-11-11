# FpsProbe

Finds out why the overlay reads no FPS in a game. A console program with no window: it doesn't
take focus, and it uses its own trace sessions, so it can run while SwissOlly is tracking a
game. It needs the same right as the overlay (Administrators or Performance Log Users).

```
dotnet run -c Release --project tools/FpsProbe -- 15
```

Start it, switch to the game within a second or two, and stay there for the 15 seconds. It then
lists every process that presented frames, with presents per second from each source, and how
long each process owned the window in front:

```
    PID  Process        DXGI/s   D3D9/s  Kernel/s  In front
  15332  Overwatch       140.0      0.0     140.0  6 of 6 s
```

Reading it, for the row that was in front:

- **DXGI or D3D9 has a rate:** the overlay should show it. If it doesn't, the fault is elsewhere
  (the overlay log, `SWISSOLLY_OVERLAY_LOG=1`, says what it tracked).
- **Only Kernel has a rate:** the game presents through Vulkan or OpenGL. The overlay counts the
  kernel's presents for these since 2026-11-09.
- **No rates at all, and another row has the game's frame rate:** the window belongs to one
  process and another one presents. The overlay follows the window's process, so it shows "--".
  Note both process names.
- **The game isn't in front at all:** something else owns the foreground window (an anti-cheat
  or launcher window, or exclusive fullscreen on another display).

To see what the overlay itself would show for one process, with its real FPS code:

```
dotnet run -c Release --project tools/FpsProbe -- track <pid> 10
dotnet run -c Release --project tools/FpsProbe -- track <pid> 10 kernel
```

The second line leaves DXGI and D3D9 off, to compare the kernel's count with the usual one.
Checked on Overwatch (2026-11-09): 137 to 141 fps the usual way, 142 to 143 from the kernel.
