using System.Globalization;
using XtremeDaw.App.Video;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Audio;

/// <summary>
/// Diagnostic for audible dropouts: every UI tick it reads the engine's longest wait between ASIO callbacks
/// and longest callback, and when either is past what one block allows, appends a line to
/// <c>%APPDATA%\XTreme\audio-cortes.log</c> with what happened in that tick (garbage collections
/// per generation and how long they paused the process, seeks, FFmpeg restarts). Every <see cref="SummaryEvery"/>,
/// next to a dropout, a line with how much the app allocates per second and what (<see cref="AllocationSampler"/>):
/// a collection stalls every managed thread, the audio one included, so the garbage is what has to go.
/// Quiet ticks allocate nothing; the file is written off the UI thread.
/// </summary>
public sealed class AudioGapLog : IDisposable
{
    /// <summary>A wait this many blocks long is a dropout (callbacks normally come one block apart).</summary>
    const double GapBlocks = 1.6;
    /// <summary>
    /// A wait past the driver's usual one by this many blocks is a dropout. Wrappers over WASAPI (FL Studio ASIO,
    /// ASIO4ALL) call back in bursts, two blocks every ~10 ms: that cadence is theirs, not a dropout.
    /// </summary>
    const double OverUsualBlocks = 0.6;
    /// <summary>How many ticks the driver's usual longest wait is the median of.</summary>
    const int UsualTicks = 64;
    readonly double[] recentGaps = new double[UsualTicks], sortedGaps = new double[UsualTicks];
    int gapCount, gapAt;
    /// <summary>The allocation summary goes at most this often (it covers everything since the last one).</summary>
    static readonly TimeSpan SummaryEvery = TimeSpan.FromSeconds(10);

    static readonly string LogPath = Path.Combine(AppConfig.Dir, "audio-cortes.log");
    readonly object fileGate = new();
    readonly AllocationSampler sampler = new();
    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
    int restarts = VideoDecoder.Restarts;
    TimeSpan paused = GC.GetTotalPauseDuration();
    long summaryAt = Environment.TickCount64, allocAt = GC.GetTotalAllocatedBytes(), uiAllocAt = GC.GetAllocatedBytesForCurrentThread();
    bool started;

    public void Tick(PlaybackEngine engine)
    {
        var (gap, run, input, tracks, block, seeks) = engine.TakeTimings();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2), vr = VideoDecoder.Restarts;
        var pause = GC.GetTotalPauseDuration();
        double usual = Usual(gap);
        bool bad = block > 0 && (gap > Math.Max(block * GapBlocks, usual + block * OverUsualBlocks) || run > block);
        if (bad)
        {
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff}  hueco {1,6:0.0} ms  callback {2,5:0.0} ms (entrada {3:0.0}, pistas {4:0.0}, resto {5:0.0})  bloque {6:0.0} ms  {7}  GC {8}/{9}/{10} pausa {11:0.0} ms  saltos {12}  ffmpeg {13}",
                DateTime.Now, gap, run, input, tracks, run - input - tracks, block, engine.Playing ? "sonando" : "parado", g0 - gen0, g1 - gen1, g2 - gen2,
                (pause - paused).TotalMilliseconds, seeks, vr - restarts);
            long now = Environment.TickCount64;
            if (now - summaryAt >= SummaryEvery.TotalMilliseconds)
            {
                long alloc = GC.GetTotalAllocatedBytes(), uiAlloc = GC.GetAllocatedBytesForCurrentThread();
                double sec = (now - summaryAt) / 1000.0, mb = 1024 * 1024;
                line += Environment.NewLine + string.Format(CultureInfo.InvariantCulture,
                    "          asigna {0:0.0} MB/s (hilo de la UI {1:0.0} MB/s)  ·  {2}",
                    (alloc - allocAt) / mb / sec, (uiAlloc - uiAllocAt) / mb / sec, sampler.Take() ?? "sin muestras");
                (summaryAt, allocAt, uiAllocAt) = (now, alloc, uiAlloc);
            }
            string? header = started ? null : string.Format(CultureInfo.InvariantCulture,
                "{0}--- {1:yyyy-MM-dd HH:mm:ss} ---  driver {2}, bloque {3:0.0} ms, espera habitual {4:0.0} ms, GC {5}{0}",
                Environment.NewLine, DateTime.Now, engine.DriverName ?? "-", block, usual,
                System.Runtime.GCSettings.IsServerGC ? "servidor" : "estación");
            started = true;
            ThreadPool.QueueUserWorkItem(_ => Append(line, header));
        }
        (gen0, gen1, gen2, restarts, paused) = (g0, g1, g2, vr, pause);
    }

    /// <summary>
    /// The driver's usual longest wait per tick: the median of the last <see cref="UsualTicks"/>, this one included.
    /// A dropout is rare, so it doesn't move the median. Allocates nothing.
    /// </summary>
    double Usual(double gap)
    {
        if (gap <= 0) return 0;
        recentGaps[gapAt] = gap;
        gapAt = (gapAt + 1) % UsualTicks;
        if (gapCount < UsualTicks) gapCount++;
        Array.Copy(recentGaps, sortedGaps, gapCount);
        Array.Sort(sortedGaps, 0, gapCount);
        return sortedGaps[gapCount / 2];
    }

    void Append(string line, string? header)
    {
        try
        {
            lock (fileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (header != null) File.AppendAllText(LogPath, header);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // a diagnostic never gets in the way
        }
    }

    public void Dispose() => sampler.Dispose();
}
