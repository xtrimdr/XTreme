using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace XtremeDaw.Audio.Vst;

/// <summary>
/// Where the plugin host processes are (src/XtremeDaw.VstHost), next to the app or xtlab: xtvsthost.exe, and
/// xtvsthost32.exe because a 32-bit plugin can only load in a 32-bit process.
/// </summary>
public static class VstHost
{
    public const string ExeName = "xtvsthost.exe", Exe32Name = "xtvsthost32.exe";
    /// <summary>Set it to use another one for 64-bit plugins; null = the one next to the program.</summary>
    public static string? ExePath { get; set; }

    public static string? Find()
    {
        if (ExePath != null) return File.Exists(ExePath) ? ExePath : null;
        string here = Path.Combine(AppContext.BaseDirectory, ExeName);
        return File.Exists(here) ? here : null;
    }

    /// <summary>The host for that plugin file, by its bits.</summary>
    public static string? Find(string pluginPath)
    {
        if (PluginFile.Bits(pluginPath) != 32) return Find();
        string here = Path.Combine(AppContext.BaseDirectory, Exe32Name);
        return File.Exists(here) ? here : null;
    }

    /// <summary>Why no host opens that plugin file.</summary>
    public static string Missing(string pluginPath) => PluginFile.Bits(pluginPath) == 32
        ? Texts.T("err.vst.noHost32", "Falta xtvsthost32.exe: no se pueden abrir plugins de 32 bits.")
        : Texts.T("err.vst.noHost", "Falta xtvsthost.exe.");
}

/// <summary>
/// What a plugin file is, read from its PE headers without loading it: a DLL only runs inside a process of its own
/// bits, and a folder of VST2 plugins is full of DLLs that aren't plugins.
/// </summary>
public static class PluginFile
{
    /// <summary>What a VST2 exports for the host to call.</summary>
    static readonly string[] Vst2Entries = ["VSTPluginMain", "main", "MAIN"];

    public static bool IsVst2Path(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>32 or 64: a .vst3 bundle by what its Contents folder has (64 when it has both). Unreadable counts as 64.</summary>
    public static int Bits(string path)
    {
        if (Directory.Exists(path))
        {
            string contents = Path.Combine(path, "Contents");
            return !Directory.Exists(Path.Combine(contents, "x86_64-win")) && Directory.Exists(Path.Combine(contents, "x86-win")) ? 32 : 64;
        }
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            return pe.PEHeaders.CoffHeader.Machine == Machine.I386 ? 32 : 64;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { return 64; }
    }

    /// <summary>The DLL exports a VST2 entry point.</summary>
    public static bool IsVst2(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            var header = pe.PEHeaders.PEHeader;
            if (header == null || header.ExportTableDirectory.Size == 0) return false;
            var table = pe.GetSectionData(header.ExportTableDirectory.RelativeVirtualAddress).GetReader();
            table.Offset = 24; // the export directory's NumberOfNames
            int count = table.ReadInt32();
            table.Offset = 32; // AddressOfNames
            var names = pe.GetSectionData(table.ReadInt32()).GetReader();
            for (int i = 0; i < count; i++)
            {
                var name = pe.GetSectionData(names.ReadInt32()).GetReader();
                if (Vst2Entries.Contains(name.ReadUTF8(name.GetUtf8Length()))) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException) { return false; }
    }

    static int GetUtf8Length(this ref System.Reflection.Metadata.BlobReader reader)
    {
        int at = reader.Offset, n = 0;
        while (reader.RemainingBytes > 0 && reader.ReadByte() != 0) n++;
        reader.Offset = at;
        return n;
    }
}

/// <summary>A parameter a plugin lets a host automate: its id, its name, where it is now (0..1) and how many steps it has (0 = continuous).</summary>
public sealed record VstParamInfo(uint Id, string Title, double Value, int Steps)
{
    /// <summary>From the host's line: "id value steps title".</summary>
    public static VstParamInfo? Parse(string line)
    {
        var p = line.Split(' ', 4);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return p.Length == 4 && uint.TryParse(p[0], out uint id) && double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out double d) && int.TryParse(p[2], out int steps)
            ? new(id, p[3], d, steps) : null;
    }
}

/// <summary>
/// An event for a plugin: where in the block, then a note (on or off, which key, how hard, 0..1; an off's is its
/// release velocity) or a <see cref="Control"/> (<see cref="Pitch"/> the controller, <see cref="Velocity"/> its value 0..1,
/// as in <see cref="Dsp.NoteControl"/>).
/// </summary>
public struct VstNote
{
    public int Offset;
    public int Type; // 0 on, 1 off, 2 control
    public int Pitch;
    public float Velocity;

    public const int On = 0, Off = 1, Control = 2;

    /// <summary>
    /// Plugins want events in time order; at one frame, controls first (a note attacks with its bend already there),
    /// then offs before ons (a note retriggered right where it ends). Stable and without allocating: audio thread.
    /// </summary>
    public static void Sort(Span<VstNote> events)
    {
        for (int i = 1; i < events.Length; i++)
        {
            var x = events[i];
            int j = i - 1;
            while (j >= 0 && (events[j].Offset > x.Offset || events[j].Offset == x.Offset && events[j].Type < x.Type))
            {
                events[j + 1] = events[j];
                j--;
            }
            events[j + 1] = x;
        }
    }
}

/// <summary>
/// One VST3 or VST2 plugin running in its own host process (the user's call: a plugin that hangs or crashes never takes a
/// recording down). They talk through shared memory laid out as in <c>src/XtremeDaw.VstHost/shared.h</c> (the offsets
/// below mirror it), two events to wake each other, and text lines on the process's stdin/stdout for what is not audio
/// (state, the plugin's window).
/// <para>
/// Live (<see cref="Exchange"/>), the audio thread never waits for the other process: each block it publishes the next
/// request and takes the output of the one before, so the plugin is heard one block late (the notes go that much early,
/// <see cref="InstrumentPlayer"/>). If the plugin did not finish in time, nothing else stops: that stretch of it is
/// silent and its block is heard with the next one. Offline (<see cref="Render"/>, the export) it waits for each block.
/// </para>
/// </summary>
public sealed unsafe class VstBridge : IDisposable
{
    // shared.h
    const int Magic = 0x53564347, ProtocolVersion = 3;
    public const int MaxBlock = 8192, MaxEvents = 512, MaxParams = 128;
    const int OffMagic = 0, OffVersion = 4, OffSampleRate = 8, OffMaxBlock = 12, OffStatus = 16, OffLatency = 20, OffOutputs = 24,
        OffInstrument = 28, OffRequested = 32, OffDone = 40, OffError = 48, OffSlots = 560, SlotSize = 140336, TotalSize = 281232;
    const int SlotSeq = 0, SlotFrames = 8, SlotNumEvents = 12, SlotTime = 16, SlotTempo = 24, SlotFlags = 32, SlotEvents = 40,
        SlotIn = 8232, SlotOut = 73768, SlotNumParams = 139304, SlotParams = 139312, EventSize = 16;
    /// <summary>FlagReset: the plugin forgets its tails and delay lines before this block (the transport starts).</summary>
    public const int FlagAllNotesOff = 1, FlagPlaying = 2, FlagReset = 4;
    const int StatusReady = 1, StatusFailed = 2, StatusCrashed = 3;

    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    readonly byte* mem;
    readonly EventWaitHandle request, done;
    readonly Process process;
    readonly object inputGate = new();
    readonly ManualResetEventSlim readyEvent = new(false);
    TaskCompletionSource<string?>? stateReply, loadReply;
    bool disposed;
    /// <summary>The last block published (audio thread, or the export's thread).</summary>
    long seq;
    /// <summary>The last block whose output <see cref="Exchange"/> took.</summary>
    long taken;

    public string Name { get; }
    public int SampleRate { get; }
    // automated parameters waiting for the next block (the audio thread's, or the export's)
    readonly uint[] paramIds = new uint[MaxParams];
    readonly float[] paramValues = new float[MaxParams];
    int paramCount;
    List<VstParamInfo>? paramList;
    TaskCompletionSource<IReadOnlyList<VstParamInfo>?>? paramsReply;
    /// <summary>The parameter last moved in the plugin's window, or null: what "Último tocado" automates.</summary>
    public uint? Touched { get; private set; }
    int touches;
    /// <summary>How many gestures its window has had: a parameter touched again leaves <see cref="Touched"/> the same.</summary>
    public int Touches => Volatile.Read(ref touches);
    /// <summary>The plugin's process.</summary>
    public int ProcessId => process.Id;
    /// <summary>Its own latency in frames, as it reported when it loaded.</summary>
    public int Latency { get; private set; }
    public bool IsInstrument { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Blocks it did not finish in time (live), for the diagnostics.</summary>
    public int Misses;
    /// <summary>The last block sent, and the last one the plugin finished: how far behind it runs.</summary>
    public long Published => Volatile.Read(ref seq);
    public long Finished => Volatile.Read(ref *(long*)(mem + OffDone));

    /// <summary>A line from the plugin's process that is not an answer: "dirty", "editor-open", "editor-closed", "latency N"
    /// (after <see cref="Latency"/> changed), "editor-rect left top right bottom" (where its window is, in pixels; alone
    /// when it is minimized), "editor-active", "editor-inactive", "toolbar id" (a button of its bar), "transport-key n scan" (a shortcut pressed in its window), "error …". Raised on a background thread.</summary>
    public event Action<string>? Message;
    /// <summary>The process ended (it crashed, or was closed). Raised on a background thread.</summary>
    public event Action? Exited;

    public bool Ready => Volatile.Read(ref *(int*)(mem + OffStatus)) == StatusReady;
    /// <summary>It failed to load or crashed: it plays nothing.</summary>
    public bool Broken => Volatile.Read(ref *(int*)(mem + OffStatus)) is StatusFailed or StatusCrashed;

    VstBridge(string pluginPath, string cid, string name, int sampleRate, bool offline, int maxBlock)
    {
        Name = name;
        SampleRate = sampleRate;
        string exe = VstHost.Find(pluginPath) ?? throw new FileNotFoundException(VstHost.Missing(pluginPath));
        string id = $"XtVst-{Environment.ProcessId}-{Guid.NewGuid():N}";
        map = MemoryMappedFile.CreateNew(id, TotalSize);
        view = map.CreateViewAccessor(0, TotalSize);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        mem = p + view.PointerOffset;
        new Span<byte>(mem, TotalSize).Clear();
        *(int*)(mem + OffMagic) = Magic;
        *(int*)(mem + OffVersion) = ProtocolVersion;
        *(int*)(mem + OffSampleRate) = sampleRate;
        *(int*)(mem + OffMaxBlock) = MaxBlock;
        request = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".req");
        done = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".done");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in (string[])["--host", pluginPath, "--cid", cid, "--shm", id, "--sr", sampleRate.ToString(), "--block", maxBlock.ToString(), "--parent", Environment.ProcessId.ToString()])
            psi.ArgumentList.Add(a);
        if (offline) psi.ArgumentList.Add("--offline");
        process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (!Ready && !Broken) Error ??= Texts.T("err.vst.exited", "El plugin se cerró al abrir.");
            *(int*)(mem + OffStatus) = StatusCrashed;
            readyEvent.Set();
            stateReply?.TrySetResult(null);
            loadReply?.TrySetResult(null);
            paramsReply?.TrySetResult(null);
            if (!disposed) Exited?.Invoke();
        };
        process.Start();
        new Thread(ReadOutput) { IsBackground = true, Name = "vst " + name }.Start();
    }

    /// <summary>
    /// Starts <paramref name="name"/>'s process and waits until the plugin loaded (or failed: then <see cref="Error"/>
    /// says why and it throws). Slow plugins (big sample libraries) take seconds: never on the UI thread.
    /// <paramref name="maxBlock"/> is the largest block it is told about: live, the driver's (<see cref="VstInsert.LiveBlock"/>),
    /// since plugins size their work by it and pay for more than they get; a larger block still works, the host splits it.
    /// </summary>
    public static VstBridge Start(string pluginPath, string cid, string name, int sampleRate, bool offline, int maxBlock = MaxBlock, int timeoutMs = 60000)
    {
        var b = new VstBridge(pluginPath, cid, name, sampleRate, offline, Math.Clamp(maxBlock, 1, MaxBlock));
        if (!b.readyEvent.Wait(timeoutMs)) b.Error ??= Texts.T("err.vst.timeout", "El plugin tardó demasiado en abrir.");
        if (b.Ready) return b;
        string error = b.Error ?? Texts.T("err.vst.failed", "No se pudo abrir.");
        bool crashed = Volatile.Read(ref *(int*)(b.mem + OffStatus)) == StatusCrashed;
        b.Dispose();
        throw crashed ? new VstCrashedException(error) : new InvalidOperationException(error);
    }

    /// <summary>
    /// Opens a plugin for the export: offline, with the largest block. A plugin that crashes when told so (Serum, #266)
    /// opens again the way it already plays live, in real time with the driver's block (<see cref="VstInsert.LiveBlock"/>):
    /// the render still waits for every block, only the plugin is not told.
    /// </summary>
    public static VstBridge StartForExport(string pluginPath, string cid, string name, int sampleRate)
    {
        try { return Start(pluginPath, cid, name, sampleRate, offline: true); }
        catch (VstCrashedException) { return Start(pluginPath, cid, name, sampleRate, offline: false, VstInsert.LiveBlock); }
    }

    void ReadOutput()
    {
        try
        {
            var r = process.StandardOutput;
            while (r.ReadLine() is { } line)
            {
                int space = line.IndexOf(' ');
                string cmd = space < 0 ? line : line[..space], arg = space < 0 ? "" : line[(space + 1)..];
                switch (cmd)
                {
                    case "ready":
                        var parts = arg.Split(' ');
                        Latency = parts.Length > 0 && int.TryParse(parts[0], out int lat) ? Math.Max(0, lat) : 0;
                        IsInstrument = parts.Length > 2 && parts[2] == "1";
                        readyEvent.Set();
                        break;
                    case "error":
                        Error = arg;
                        if (!Ready) readyEvent.Set();
                        loadReply?.TrySetResult(null);
                        Message?.Invoke(line);
                        break;
                    case "state":
                        stateReply?.TrySetResult(arg);
                        break;
                    case "loaded":
                        loadReply?.TrySetResult("ok");
                        break;
                    case "param":
                        if (VstParamInfo.Parse(arg) is { } info) paramList?.Add(info);
                        break;
                    case "params-end":
                        paramsReply?.TrySetResult(paramList);
                        break;
                    case "touched":
                        if (!uint.TryParse(arg, out uint touched)) break;
                        Touched = touched;
                        Interlocked.Increment(ref touches);
                        break;
                    case "latency":
                        if (int.TryParse(arg, out int now)) Latency = Math.Max(0, now);
                        Message?.Invoke(line);
                        break;
                    default:
                        Message?.Invoke(line);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    void Send(string line)
    {
        lock (inputGate)
        {
            if (disposed || process.HasExited) return;
            try
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
            catch (IOException) { }
        }
    }

    /// <summary>The plugin's state (its sound as the user left it), as base64; null if it did not answer in time.</summary>
    public Task<string?> GetStateAsync(int timeoutMs = 3000)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        stateReply = tcs;
        Send("state");
        // (no await: the class is unsafe)
        return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ContinueWith(t => t.Result == tcs.Task ? tcs.Task.Result : null, TaskScheduler.Default);
    }

    /// <summary>Gives it back a saved state; false if it refused it or did not answer.</summary>
    public bool SetState(string base64, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loadReply = tcs;
        Send("load-state " + base64);
        return tcs.Task.Wait(timeoutMs) && tcs.Task.Result == "ok";
    }

    /// <summary>The parameters it lets a host automate; null if it did not answer in time.</summary>
    public Task<IReadOnlyList<VstParamInfo>?> GetParamsAsync(int timeoutMs = 3000)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<VstParamInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        paramList = [];
        paramsReply = tcs;
        Send("params");
        return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ContinueWith(t => t.Result == tcs.Task ? tcs.Task.Result : null, TaskScheduler.Default);
    }

    /// <summary>
    /// Audio thread (or the export's): a parameter's new value, sent with the next block. False when the block already
    /// carries <see cref="MaxParams"/>: the caller tries again on the next one.
    /// </summary>
    public bool QueueParam(uint id, float value)
    {
        if (paramCount >= MaxParams) return false;
        paramIds[paramCount] = id;
        paramValues[paramCount++] = value;
        return true;
    }

    /// <summary>Opens the plugin's own window (or brings it up), owned by <paramref name="owner"/> so it stays above it.</summary>
    public void OpenEditor(nint owner) => Send(owner != 0 ? "editor " + owner : "editor");

    public void CloseEditor() => Send("editor-close");

    /// <summary>A line for the plugin's window: its colors, title and bar (see main.cpp's list of commands).</summary>
    public void Command(string line) => Send(line);

    Span<float> SlotSpan(int slot, int offset, int channel, int frames) =>
        new(mem + OffSlots + slot * SlotSize + offset + channel * MaxBlock * sizeof(float), frames);

    void WriteRequest(long s, ReadOnlySpan<VstNote> notes, int frames, long timeSamples, double tempo, int flags,
        ReadOnlySpan<float> inL, ReadOnlySpan<float> inR)
    {
        byte* slot = mem + OffSlots + (int)(s & 1) * SlotSize;
        int count = Math.Min(notes.Length, MaxEvents);
        *(long*)(slot + SlotSeq) = s;
        *(int*)(slot + SlotFrames) = frames;
        *(int*)(slot + SlotNumEvents) = count;
        *(long*)(slot + SlotTime) = timeSamples;
        *(double*)(slot + SlotTempo) = tempo;
        *(int*)(slot + SlotFlags) = flags;
        for (int i = 0; i < count; i++)
        {
            byte* e = slot + SlotEvents + i * EventSize;
            *(int*)e = notes[i].Offset;
            *(int*)(e + 4) = notes[i].Type;
            *(int*)(e + 8) = notes[i].Pitch;
            *(float*)(e + 12) = notes[i].Velocity;
        }
        *(int*)(slot + SlotNumParams) = paramCount;
        for (int i = 0; i < paramCount; i++)
        {
            *(uint*)(slot + SlotParams + i * 8) = paramIds[i];
            *(float*)(slot + SlotParams + i * 8 + 4) = paramValues[i];
        }
        paramCount = 0;
        if (!inL.IsEmpty)
        {
            inL[..frames].CopyTo(SlotSpan((int)(s & 1), SlotIn, 0, frames));
            (inR.IsEmpty ? inL : inR)[..frames].CopyTo(SlotSpan((int)(s & 1), SlotIn, 1, frames));
        }
        // the slot is complete before the host can see it
        Interlocked.Exchange(ref *(long*)(mem + OffRequested), s);
        request.Set();
    }

    /// <summary>
    /// Audio thread, once per block, never waits: adds the output of the block sent last time to <paramref name="outL"/>
    /// / <paramref name="outR"/> (if the plugin finished it) and sends this one. False when this one could not be sent
    /// (the plugin is two blocks behind): the caller keeps its notes for the next block.
    /// </summary>
    public bool Exchange(ReadOnlySpan<VstNote> notes, int frames, long timeSamples, double tempo, int flags,
        Span<float> outL, Span<float> outR, ReadOnlySpan<float> inL = default, ReadOnlySpan<float> inR = default)
    {
        if (!Ready) return true;
        frames = Math.Clamp(frames, 1, MaxBlock);
        long finished = Volatile.Read(ref *(long*)(mem + OffDone));
        if (seq > 0)
        {
            if (finished < seq) Misses++;
            // a block that came late is still there and is heard one block later: dropping it, a plugin that is always
            // a little late would never be heard at all
            long take = finished >= seq ? seq : seq - 1;
            if (finished >= take && take > taken)
            {
                taken = take;
                int slot = (int)(take & 1);
                int n = Math.Min(Math.Min(*(int*)(mem + OffSlots + slot * SlotSize + SlotFrames), outL.Length), outR.Length);
                var l = SlotSpan(slot, SlotOut, 0, n);
                var r = SlotSpan(slot, SlotOut, 1, n);
                for (int i = 0; i < n; i++)
                {
                    outL[i] += l[i];
                    outR[i] += r[i];
                }
            }
        }
        // the slot this request takes was last used by the block before the previous one: it must be done
        if (finished < seq - 1) return false;
        seq++;
        WriteRequest(seq, notes, frames, timeSamples, tempo, flags, inL, inR);
        return true;
    }

    /// <summary>
    /// Audio thread, an effect (<see cref="VstInsert"/>): sends <paramref name="inL"/> / <paramref name="inR"/> as the next
    /// block without waiting. Returns its number, or -1 when it can't go (the plugin is two blocks behind: the slot it
    /// would take is still in use).
    /// </summary>
    public long TrySend(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, int frames, long timeSamples, double tempo, int flags)
    {
        if (!Ready) return -1;
        frames = Math.Clamp(frames, 1, MaxBlock);
        if (Volatile.Read(ref *(long*)(mem + OffDone)) < seq - 1) return -1;
        seq++;
        WriteRequest(seq, [], frames, timeSamples, tempo, flags, inL, inR);
        return seq;
    }

    /// <summary>
    /// Audio thread: block <paramref name="block"/>'s output (sent with <see cref="TrySend"/>) into
    /// <paramref name="outL"/> / <paramref name="outR"/>. Returns its frames, or -1 if the plugin hasn't finished it.
    /// </summary>
    public int TryTake(long block, Span<float> outL, Span<float> outR)
    {
        if (Volatile.Read(ref *(long*)(mem + OffDone)) < block) return -1;
        int slot = (int)(block & 1);
        // the slot is reused two blocks later: past that, what is there is no longer this block
        if (*(long*)(mem + OffSlots + slot * SlotSize + SlotSeq) != block) return -1;
        int n = Math.Min(Math.Min(*(int*)(mem + OffSlots + slot * SlotSize + SlotFrames), outL.Length), outR.Length);
        SlotSpan(slot, SlotOut, 0, n).CopyTo(outL);
        SlotSpan(slot, SlotOut, 1, n).CopyTo(outR);
        return n;
    }

    /// <summary>The export: sends a block and waits for it, writing its output to <paramref name="outL"/> / <paramref name="outR"/>.</summary>
    public void Render(ReadOnlySpan<VstNote> notes, int frames, long timeSamples, double tempo, int flags, Span<float> outL, Span<float> outR,
        ReadOnlySpan<float> inL = default, ReadOnlySpan<float> inR = default)
    {
        frames = Math.Clamp(frames, 1, MaxBlock);
        seq++;
        WriteRequest(seq, notes, frames, timeSamples, tempo, flags, inL, inR);
        while (Volatile.Read(ref *(long*)(mem + OffDone)) < seq)
        {
            if (Broken) throw new InvalidOperationException(Texts.T("err.vst.crashed", "El plugin {0} se cerró en medio de la exportación.", Name));
            done.WaitOne(1000);
        }
        int slot = (int)(seq & 1);
        SlotSpan(slot, SlotOut, 0, frames).CopyTo(outL);
        SlotSpan(slot, SlotOut, 1, frames).CopyTo(outR);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Send("quit");
        lock (inputGate)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
        try
        {
            if (!process.WaitForExit(3000)) process.Kill(true);
        }
        catch (InvalidOperationException) { }
        process.Dispose();
        request.Dispose();
        done.Dispose();
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        view.Dispose();
        map.Dispose();
        readyEvent.Dispose();
    }
}

/// <summary>The plugin's process died while it loaded (not a refusal): <see cref="VstBridge.StartForExport"/> tries it again as live.</summary>
public sealed class VstCrashedException(string message) : InvalidOperationException(message);
