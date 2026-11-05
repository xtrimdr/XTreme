using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace XtremeDaw.Audio;

/// <summary>GPU load and video memory, as the web app's sampler reports them.</summary>
public sealed record GpuStats(double Load, long VramUsed, long VramTotal, string Name);

/// <summary>One reading of the whole PC: CPU %, threads, RAM and the GPU (null while it is still being measured).</summary>
public sealed record SystemStatsSample(double Cpu, int Cores, long RamUsed, long RamTotal, GpuStats? Gpu);

/// <summary>
/// Port of the web app's systemStats.js: CPU and RAM straight from the system, GPU from the Windows
/// "GPU Engine" / "GPU Adapter Memory" performance counters. Task Manager's GPU % is the busiest engine, where an
/// engine's load is the sum over the processes using it; the VRAM total comes from the driver's registry key.
/// Same as the web except it reads the counters through PDH on a background thread instead of a PowerShell
/// process, and the sampler stops when nobody has asked for <see cref="IdleStop"/> (the web's IDLE_MS).
/// </summary>
public sealed class SystemStats : IDisposable
{
    /// <summary>The GPU sampler stops after this long without a <see cref="Read"/>.</summary>
    static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(15);
    /// <summary>The counters need a second between collections to give a rate (Get-Counter -SampleInterval 1).</summary>
    static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1);

    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    Thread? sampler;
    long lastRead;
    volatile GpuStats? gpu;
    bool gpuUnavailable = !OperatingSystem.IsWindows();
    ulong lastIdle, lastTotal;

    public SystemStats()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            lastIdle = idle;
            lastTotal = kernel + user;
        }
    }

    public SystemStatsSample Read()
    {
        Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
        StartSampler();

        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // kernel time includes idle time
            ulong total = kernel + user;
            ulong dTotal = total - lastTotal, dIdle = idle - lastIdle;
            lastIdle = idle;
            lastTotal = total;
            if (dTotal > 0) cpu = Math.Clamp((1 - dIdle / (double)dTotal) * 100, 0, 100);
        }
        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref mem);
        return new SystemStatsSample(cpu, Environment.ProcessorCount, (long)(mem.TotalPhys - mem.AvailPhys), (long)mem.TotalPhys,
            gpuUnavailable ? null : gpu);
    }

    void StartSampler()
    {
        lock (gate)
        {
            if (sampler != null || gpuUnavailable || stop.IsCancellationRequested) return;
            sampler = new Thread(Sample) { IsBackground = true, Name = "GPU stats", Priority = ThreadPriority.BelowNormal };
            sampler.Start();
        }
    }

    void Sample()
    {
        var (vramTotal, name) = VideoCard();
        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0
                || PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engines) != 0
                || PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out var memory) != 0)
            {
                gpuUnavailable = true;
                return;
            }
            PdhCollectQueryData(query);
            var busy = new Dictionary<string, double>();
            var byEngine = busy.GetAlternateLookup<ReadOnlySpan<char>>();
            while (!stop.Token.WaitHandle.WaitOne(SampleEvery))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead)) > IdleStop) break;
                if (PdhCollectQueryData(query) != 0) continue;
                // the engines stay the same between readings: their keys are reused, so a reading allocates nothing
                foreach (var key in busy.Keys) CollectionsMarshal.GetValueRefOrNullRef(busy, key) = 0;
                int n = Values(engines, ref buffer, ref bufferSize);
                for (int i = 0; i < n; i++)
                {
                    double value = Item(buffer, i, out var instance);
                    var id = EngineId(instance);
                    if (!id.IsEmpty)
                    {
                        if (byEngine.TryGetValue(id, out double sum)) byEngine[id] = sum + value;
                        else byEngine[id] = value;
                    }
                }
                double load = 0;
                foreach (double v in busy.Values) load = Math.Max(load, v);
                long used = 0;
                n = Values(memory, ref buffer, ref bufferSize);
                for (int i = 0; i < n; i++) used = Math.Max(used, (long)Item(buffer, i, out _));
                var last = gpu;
                if (last == null || last.Load != Math.Min(100, load) || last.VramUsed != used) gpu = new GpuStats(Math.Min(100, load), used, vramTotal, name);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { gpuUnavailable = true; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            bufferSize = 0;
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            gpu = null;
            lock (gate) sampler = null;
        }
    }

    /// <summary>The sampler's PDH buffer, kept between readings (only its own thread uses it).</summary>
    IntPtr buffer;
    uint bufferSize;

    /// <summary>
    /// "luid_…_phys_0_eng_3" out of "pid_123_luid_…_phys_0_eng_3_engtype_3D": the engine, whichever process uses it.
    /// Empty if the instance is not an engine's.
    /// </summary>
    static ReadOnlySpan<char> EngineId(ReadOnlySpan<char> instance)
    {
        int start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return default;
        var rest = instance[start..];
        int end = rest.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        return end > 0 && rest[..end].Contains("_eng_", StringComparison.OrdinalIgnoreCase) ? rest[..end] : default;
    }

    /// <summary>Fills the buffer with the counter's current values (growing it if needed) and says how many there are.</summary>
    static int Values(IntPtr counter, ref IntPtr buf, ref uint bufSize)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out uint count, IntPtr.Zero) != PdhMoreData || size == 0) return 0;
        if (size > bufSize)
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            bufSize = size * 2; // instances come and go with the processes: room to grow without reallocating each time
            buf = Marshal.AllocHGlobal((int)bufSize);
        }
        size = bufSize;
        return PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, buf) == 0 ? (int)count : 0;
    }

    /// <summary>The i-th value in the buffer, its instance name read in place; a failed one reads as an empty name.</summary>
    static unsafe double Item(IntPtr buf, int i, out ReadOnlySpan<char> instance)
    {
        var item = ((PdhItem*)buf)[i];
        instance = default;
        if (item.Status != 0 || item.Name == IntPtr.Zero) return 0;
        instance = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.Name);
        return item.Value;
    }

    /// <summary>The biggest adapter in the display class key (Win32_VideoController.AdapterRAM is capped at 4 GB).</summary>
    static (long Total, string Name) VideoCard()
    {
        long total = 0;
        string name = "";
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return (0, "");
            foreach (string sub in cls.GetSubKeyNames())
            {
                if (!sub.StartsWith('0')) continue;
                using var key = cls.OpenSubKey(sub);
                long mem = key?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                    _ => 0,
                };
                if (mem > total) { total = mem; name = key?.GetValue("DriverDesc") as string ?? ""; }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return (total, name);
    }

    public void Dispose() => stop.Cancel();

    const uint PdhFmtDouble = 0x00000200;
    const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct PdhItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>Whether FFmpeg is on the PATH and its version, like the web server's `ffmpeg -version` check.</summary>
public static class FfmpegInfo
{
    /// <summary>The version ("7.1", "N-118…"), "" if it ran without printing one, or null if it is not on the PATH.</summary>
    public static string? Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.StandardError.ReadToEndAsync();
            string first = p.StandardOutput.ReadLine() ?? "";
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } }
            var m = Regex.Match(first, @"ffmpeg version (\S+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
