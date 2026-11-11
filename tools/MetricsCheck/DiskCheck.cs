using System.Runtime.InteropServices;
using SwissOlly.Collector;

namespace MetricsCheck;

/// <summary>
/// Disk throughput against transfers of a known size: an unbuffered write, an unbuffered read
/// and a File.Copy (flushed) of a temp file on one drive. The collector's MB/s, integrated over
/// each phase, should account for the bytes moved; the harness's own LogicalDisk query is the
/// second reference.
/// </summary>
internal static class DiskCheck
{
    private const double MiB = 1024d * 1024d;
    private const int BlockSize = 4 * 1024 * 1024;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static async Task<int> RunAsync(Options options)
    {
        var sizeBytes = (long)(options.Number("size-gb", 2) * 1024 * 1024 * 1024);
        sizeBytes -= sizeBytes % BlockSize;
        var folder = options.Text("dir") ?? Path.Combine(Path.GetTempPath(), "swissolly-metricscheck");
        var drive = Path.GetPathRoot(Path.GetFullPath(folder))!.TrimEnd('\\');
        var free = new DriveInfo(drive).AvailableFreeSpace;
        if (free < (2 * sizeBytes) + (10L * 1024 * 1024 * 1024))
        {
            Console.WriteLine($"Not enough free space on {drive} for two {sizeBytes / MiB / 1024:0.#} GiB files plus 10 GiB.");
            return 1;
        }

        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.bin");
        var copy = Path.Combine(folder, "copy.bin");
        var panelKey = $"disk-{Snapshots.Sanitize(drive)}";
        Console.WriteLine($"Drive {drive}, file {sizeBytes / MiB:0} MiB in {folder}. Rates in MiB/s (SwissOlly and Task Manager label them MB/s).");
        Console.WriteLine();
        Console.WriteLine("    t | SO read  SO write | PDH read PDH write | phase");

        var samples = new List<DiskSample>();
        var phases = new List<Phase>();
        string currentPhase = "idle";
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            await using var collector = new WindowsMetricCollector();
            using var pdh = new PdhQuery();
            var readCounter = pdh.Add($@"\LogicalDisk({drive})\Disk Read Bytes/sec");
            var writeCounter = pdh.Add($@"\LogicalDisk({drive})\Disk Write Bytes/sec");
            pdh.Collect();
            _ = await collector.CollectAsync(CancellationToken.None);
            samples.Add(new DiskSample(Clock.Seconds, 0, 0, 0, 0));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(CancellationToken.None) && !stop.IsCancellationRequested)
            {
                // The reference query is collected first and the collector's own PDH collect is
                // the first thing CollectAsync does, so both cover nearly the same interval.
                var t = Clock.Seconds;
                pdh.Collect();
                var pdhMs = (Clock.Seconds - t) * 1000;
                var collectStart = Clock.Seconds;
                var snapshot = await collector.CollectAsync(CancellationToken.None);
                var collectMs = (Clock.Seconds - collectStart) * 1000;
                var freeStart = Clock.Seconds;
                _ = new DriveInfo(drive).AvailableFreeSpace;
                var freeMs = (Clock.Seconds - freeStart) * 1000;
                var sample = new DiskSample(
                    t,
                    Snapshots.Value(snapshot, panelKey, "read"),
                    Snapshots.Value(snapshot, panelKey, "write"),
                    (pdh.Single(readCounter) ?? double.NaN) / MiB,
                    (pdh.Single(writeCounter) ?? double.NaN) / MiB);
                lock (samples)
                {
                    samples.Add(sample);
                }

                var slow = Math.Max(pdhMs, Math.Max(collectMs, freeMs)) > 250
                    ? $"  slow: PDH collect {pdhMs:0} ms, CollectAsync {collectMs:0} ms, free space {freeMs:0} ms"
                    : string.Empty;
                Console.WriteLine($"{t,5:0.0} | {sample.Read,7:0.0} {sample.Write,8:0.0} | {sample.PdhRead,8:0.0} {sample.PdhWrite,9:0.0} | {Volatile.Read(ref currentPhase)}{slow}");
            }
        });

        // Polls the volume's free space and a LogicalDisk PDH collect every 100 ms, to show whether
        // either blocks while the file system is busy (both are on the collector's sampling path).
        var probe = Task.Run(() => ProbeLatency(drive, () => Volatile.Read(ref currentPhase), stop.Token));
        var selected = (options.Text("phases") ?? "write,read,copy").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        try
        {
            await Task.Delay(3000);
            if (selected.Contains("write") || selected.Contains("read") || selected.Contains("copy"))
            {
                phases.Add(await RunPhaseAsync("write (unbuffered)", () => WriteUnbuffered(source, sizeBytes), sizeBytes, 0, sizeBytes, p => Volatile.Write(ref currentPhase, p)));
                await Task.Delay(4000);
            }

            if (selected.Contains("read"))
            {
                phases.Add(await RunPhaseAsync("read (unbuffered)", () => ReadUnbuffered(source, sizeBytes), sizeBytes, sizeBytes, 0, p => Volatile.Write(ref currentPhase, p)));
                await Task.Delay(4000);
            }

            if (selected.Contains("copy"))
            {
                phases.Add(await RunPhaseAsync("File.Copy + flush", () => CopyAndFlush(source, copy), sizeBytes, sizeBytes, sizeBytes, p => Volatile.Write(ref currentPhase, p)));
                await Task.Delay(4000);
            }

            if (selected.Contains("flush"))
            {
                phases.Add(await RunPhaseAsync("buffered write + flush", () => WriteBufferedAndFlush(copy, sizeBytes), sizeBytes, 0, sizeBytes, p => Volatile.Write(ref currentPhase, p)));
                await Task.Delay(4000);
            }
        }
        finally
        {
            stop.Cancel();
            await sampler;
            await probe;
            TryDelete(source);
            TryDelete(copy);
            try
            {
                Directory.Delete(folder);
            }
            catch (IOException)
            {
            }
        }

        Console.WriteLine();
        foreach (var phase in phases)
        {
            Summarise(phase, samples);
        }

        return 0;
    }

    private static void ProbeLatency(string drive, Func<string> currentPhase, CancellationToken cancellationToken)
    {
        using var pdh = new PdhQuery();
        _ = pdh.Add($@"\LogicalDisk({drive})\% Free Space");
        var worst = new Dictionary<string, (double FreeSpaceMs, double PdhMs)>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var phase = currentPhase();
            var start = Clock.Seconds;
            _ = new DriveInfo(drive).AvailableFreeSpace;
            var freeMs = (Clock.Seconds - start) * 1000;
            start = Clock.Seconds;
            pdh.Collect();
            var pdhMs = (Clock.Seconds - start) * 1000;
            var previous = worst.GetValueOrDefault(phase);
            worst[phase] = (Math.Max(previous.FreeSpaceMs, freeMs), Math.Max(previous.PdhMs, pdhMs));
            if (Math.Max(freeMs, pdhMs) > 250)
            {
                Console.WriteLine($"  probe at {Clock.Seconds:0.0} ({phase}): free space query {freeMs:0} ms, LogicalDisk PDH collect {pdhMs:0} ms");
            }

            Thread.Sleep(100);
        }

        foreach (var (phase, (freeMs, pdhMs)) in worst)
        {
            Console.WriteLine($"Probe, slowest during {phase}: free space query {freeMs:0} ms, LogicalDisk PDH collect {pdhMs:0} ms");
        }
    }

    private static void WriteBufferedAndFlush(string path, long size)
    {
        var block = new byte[BlockSize];
        Random.Shared.NextBytes(block);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        for (long written = 0; written < size; written += BlockSize)
        {
            stream.Write(block);
        }

        stream.Flush(flushToDisk: true);
    }

    private static async Task<Phase> RunPhaseAsync(string name, Action action, long size, long readBytes, long writeBytes, Action<string> setPhase)
    {
        setPhase(name);
        var start = Clock.Seconds;
        await Task.Run(action);
        var end = Clock.Seconds;
        setPhase("idle");
        return new Phase(name, start, end, size, readBytes, writeBytes);
    }

    private static void Summarise(Phase phase, List<DiskSample> samples)
    {
        // Each sample is the average rate over the interval since the previous one. Count every
        // interval that overlaps the phase plus two seconds of tail (lazy writes); the phases are
        // separated by idle time, so the overlap adds little.
        double soRead = 0, soWrite = 0, pdhRead = 0, pdhWrite = 0;
        var insideRates = new List<(double Read, double Write)>();
        for (var i = 1; i < samples.Count; i++)
        {
            var (from, to) = (samples[i - 1].T, samples[i].T);
            if (to <= phase.Start || from >= phase.End + 2)
            {
                continue;
            }

            var dt = to - from;
            soRead += samples[i].Read * MiB * dt;
            soWrite += samples[i].Write * MiB * dt;
            pdhRead += samples[i].PdhRead * MiB * dt;
            pdhWrite += samples[i].PdhWrite * MiB * dt;
            if (from >= phase.Start && to <= phase.End)
            {
                insideRates.Add((samples[i].Read, samples[i].Write));
            }
        }

        var seconds = phase.End - phase.Start;
        Console.WriteLine($"{phase.Name}: {phase.Size / MiB:0} MiB in {seconds:0.00} s = {phase.Size / MiB / seconds:0.0} MiB/s measured");
        if (phase.ReadBytes > 0)
        {
            Console.WriteLine($"  read : SO counted {soRead / MiB,7:0} MiB ({Stats.Percent(soRead, phase.ReadBytes),+5:0.0}%), PDH {pdhRead / MiB,7:0} MiB ({Stats.Percent(pdhRead, phase.ReadBytes),+5:0.0}%), SO vs PDH {Stats.Percent(soRead, pdhRead):+0.00;-0.00}%");
        }

        if (phase.WriteBytes > 0)
        {
            Console.WriteLine($"  write: SO counted {soWrite / MiB,7:0} MiB ({Stats.Percent(soWrite, phase.WriteBytes),+5:0.0}%), PDH {pdhWrite / MiB,7:0} MiB ({Stats.Percent(pdhWrite, phase.WriteBytes),+5:0.0}%), SO vs PDH {Stats.Percent(soWrite, pdhWrite):+0.00;-0.00}%");
        }

        if (insideRates.Count > 0)
        {
            Console.WriteLine($"  SO rate over the {insideRates.Count} whole seconds inside the phase: read {insideRates.Average(r => r.Read):0.0} MiB/s, write {insideRates.Average(r => r.Write):0.0} MiB/s");
        }
    }

    private static unsafe void WriteUnbuffered(string path, long size)
    {
        var buffer = (byte*)NativeMemory.AlignedAlloc(BlockSize, 4096);
        try
        {
            var span = new Span<byte>(buffer, BlockSize);
            Random.Shared.NextBytes(span);
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering);
            for (long offset = 0; offset < size; offset += BlockSize)
            {
                RandomAccess.Write(handle, span, offset);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static unsafe void ReadUnbuffered(string path, long size)
    {
        var buffer = (byte*)NativeMemory.AlignedAlloc(BlockSize, 4096);
        try
        {
            var span = new Span<byte>(buffer, BlockSize);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);
            for (long offset = 0; offset < size;)
            {
                var read = RandomAccess.Read(handle, span, offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static void CopyAndFlush(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        using var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Could not delete {path}: {ex.Message}");
        }
    }

    private sealed record DiskSample(double T, double Read, double Write, double PdhRead, double PdhWrite);

    private sealed record Phase(string Name, double Start, double End, long Size, long ReadBytes, long WriteBytes);
}
