# MetricsCheck

A console harness that runs SwissOlly's collector (`WindowsMetricCollector`, `CollectorService`)
outside the app and compares its readings with Windows' own numbers. It doesn't touch the app,
its settings or `%LocalAppData%\SwissOlly`. Not part of `SwissOlly.sln`, so CI doesn't build it.

```
dotnet build tools/MetricsCheck -c Release
tools/MetricsCheck/bin/Release/net8.0-windows/MetricsCheck.exe <check> [options]
```

| Check | What it compares | Load it creates |
|---|---|---|
| `memory [--seconds 40] [--alloc-gb 3]` | Used / available against `GlobalMemoryStatusEx` and `\Memory\Available Bytes`; Task Manager's "In use" (total − available − modified) shown alongside | With `--alloc-gb`, a helper process holds that much from t = 5 s until 10 s before the end. Refuses if commit headroom or free memory is short |
| `disk [--size-gb 2] [--dir <folder>] [--phases write,read,copy,flush]` | MB/s integrated over each phase against the bytes moved by an unbuffered write, an unbuffered read and a flushed `File.Copy` (default `write,read,copy`; `flush` adds a buffered write + flush). Also a separate `\LogicalDisk(X:)` PDH query, and a probe that times free-space queries and PDH collects every 100 ms | Two temp files of `--size-gb` in `%TEMP%\swissolly-metricscheck` (system drive), deleted at the end. About 40 s of disk activity for 3 GiB on a SATA SSD |
| `capacity` | Drive panels against `DriveInfo` and what Explorer prints (`StrFormatByteSize`) | None |
| `network [--seconds 60] [--ping-gateway]` | Per adapter, Mbps integrated over the run against the adapter's byte counters (`GetIfEntry2` octets) and a separate `\Network Interface` PDH query | With `--ping-gateway`, 60,000-byte pings to the default gateway, one at a time, for 30 s from t = 10 s (about 20 Mbit/s each way on Wi-Fi; stays on the LAN) |
| `gpu [--seconds 60] [--load]` | Usage against the GPU Engine counters aggregated as Task Manager does (each engine summed across processes, busiest engine); VRAM against `\GPU Adapter Memory`; temperature against the graphics kernel's adapter temperature (`D3DKMTQueryAdapterInfo`, what Task Manager shows) and LibreHardwareMonitor's sensors; nvidia-smi alongside when installed | With `--load`, Direct3D 11 texture copies for 30 s from t = 10 s (about 40% of the 3D engine) holding 1 GiB of VRAM |
| `interval [--interval 1] [--minutes 5] [--fake] [--stall-seconds 0]` | `CollectorService` timing: missed ticks, the least-squares period (drift), jitter against the fitted schedule, spacing of snapshot timestamps, collect durations. `--stall-seconds` makes the 6th collect of a fake collector hang, to show the catch-up | None |

Close games first if you want clean numbers: background disk or network traffic adds to what
the collector counts, while the known transfer stays the same. The `SO vs PDH` figures are the
cleaner check in that case, as both see the same background.

Units: SwissOlly shows memory, VRAM and drive sizes in GiB and disk rates in MiB/s (labelled
MB/s, as Task Manager and Explorer do); network in Mbit/s of 1,000,000 bits, as Task Manager.

## Results, 2026-11-04 (Ryzen 7 5800XT, 32 GB, RTX 4070 Super, C: SATA SSD, Wi-Fi)

- **Memory:** used and available within 5 MiB of `\Memory\Available Bytes` over 40 s with a
  3 GiB helper allocation (both moved by 3.01 GiB); the app's Memory % within 0.1 point of the
  OS memory load. "Used" is total − available, so it reads the modified list (~200 MB here)
  above Task Manager's "In use".
- **Disk:** on C: (SATA), an unbuffered read of 3 GiB counted as 3,072 MiB (0.0%), 486 MiB/s
  against 484.5 measured; writes matched the separate PDH query within 0.02% but ran 11–25%
  above the file size, as another process was writing 70–170 MiB/s to C: at the time. On the
  idle B: (NVMe), 4 GiB: write +0.1%, read 0.0%, copy read 0.0% and write 0.0%. No free-space
  query or LogicalDisk collect took over 1 ms during any phase.
- **Capacity:** C: and B: identical to `DriveInfo` to the byte; Explorer's "929 GB" is the
  panel's 929.9 GiB.
- **Network:** 30 s of gateway pings: 72.76 MB down and 70.64 MB up by the adapter counters,
  SwissOlly within 0.01% of both.
- **GPU:** usage within 0.23 points of the Task Manager rule per sample under a ~42% load
  (mean 20.99% vs 20.94% over 55 s); VRAM equal to `\GPU Adapter Memory` and 9 MiB from
  nvidia-smi, +1.009 GiB for the 1 GiB of textures. Temperature read the hot spot, 10.4 °C above
  Task Manager; fixed to the core sensor, now within 0.5 °C at steady state (it refreshes every
  20 s, so it trails a fast ramp by a few degrees).
- **Interval:** at 1 s, 300 samples in 5 min, none missed, period 999.999 ms (−4 ms an hour),
  jitter sd 4.9 ms (max 12.7 ms). At 60 s, 10 samples in 10 min, none missed, period
  60,000.13 ms, spacing 59,991–60,012 ms. A collect that hangs past a tick gives one catch-up
  sample when it returns, then the original schedule; one that hangs past the 5 s timeout loses
  the ticks in between and resumes on schedule. One real collect hung for 7 s once, during the
  first 3 GiB copy on C:; three repeats with the probe running didn't reproduce it.
