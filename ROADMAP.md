# SwissOlly roadmap

SwissOlly is a hobby project, so this is a direction, not a schedule. Nothing here has a date,
and plans change.

## Where it's going

- **More of the hardware.** CPU temperature is the main gap: the panel exists, but reading the
  sensor needs a kernel driver that SwissOlly doesn't ship yet.
- **Telling you when something is wrong.** Notifications when a reading crosses a limit you set.
- **Getting your data out.** Export of history, and an optional way for other tools to read it.
  Off by default, and never leaving the PC unless you send it somewhere.
- **A longer memory for processes.** History for individual processes, not only the ones pinned
  to a chart.

Ideas and bug reports are welcome as [issues](https://github.com/swissolly/SwissOlly/issues/new/choose).

## 1.2.0 (current)

The first release under the SwissOlly name (previously Vaktr). The full notes are in
[docs/release-notes/v1.2.0.md](docs/release-notes/v1.2.0.md).

**New**

- Arrange the board: drag and resize panels on a grid, with the mouse or the keyboard.
- A process list on the CPU and Memory panels, with pinning to the chart.
- Keeps recording from the tray when the window is closed, and can start with Windows.
- Twelve themes, eight dark and four light.
- An in-game overlay for FPS, CPU, GPU, GPU temperature and memory, with no injection into
  the game.

**Better**

- CPU, GPU, memory and process numbers are measured the way Task Manager measures them.
- Charts leave gaps where there is no history, look the same after a restart, and load long
  ranges faster.
- A full disk or an unplugged drive stops history, not the live board.
- Screen reader names and keyboard access for every control.

**Known limitations**

- The installers are not code-signed, so Windows SmartScreen may warn on first run.
- The overlay works over borderless and windowed games, not exclusive fullscreen, and looks dim
  in HDR games.
- Exact FPS needs one sign-out after installing, and trails live by about a second.
- Windows contrast themes aren't followed.

## Earlier

**1.0** (as Vaktr): live and historical charts for CPU, GPU, memory, disk and network, kept in a
local database, with zoom, retention settings, and dark and light themes.
