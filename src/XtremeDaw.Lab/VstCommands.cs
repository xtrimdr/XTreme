using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// VST3 and VST2 plugins without the window: scan them (the same catalog the app uses), list them, manage what Configuración →
/// Plugins manages (the folders to look in, the plugins hidden from the lists), and play one offline through
/// xtvsthost.exe to a WAV, the way the export does, to hear that the bridge works. An effect (<c>vst efecto</c>) goes
/// through a slot both ways, the export's and live (paced like the driver), to check that live comes exactly one block
/// later than the export, which is what the plugin delay compensation counts on.
/// </summary>
static class VstCommands
{
    public static int Run(Args a)
    {
        if (a.Get("datos") is { } data) AppConfig.DirectoryOverride = Path.GetFullPath(data);
        string sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "";
        switch (sub)
        {
            case "escanear":
            {
                if (a.Get("archivo") is { } file)
                {
                    Console.WriteLine($"  abriendo {Path.GetFileNameWithoutExtension(file)}…");
                    Show(PluginCatalog.Rescan(Path.GetFullPath(file)));
                    return 0;
                }
                string[]? folders = a.Get("carpeta") is { } dir ? [Path.GetFullPath(dir)] : null;
                var entries = PluginCatalog.Scan((name, n, total) => Console.WriteLine($"  abriendo {name}… ({n} de {total})"), folders, a.Flag("todo"));
                Show(entries);
                return 0;
            }
            case "lista":
                Show(PluginCatalog.Load());
                return 0;
            case "carpetas":
            {
                if (a.Get("agregar") is { } add)
                    Console.WriteLine(PluginCatalog.AddFolder(add) ? $"Agregada: {Path.GetFullPath(add)}" : "Esa carpeta ya estaba.");
                if (a.Get("sacar") is { } remove)
                    Console.WriteLine(PluginCatalog.RemoveFolder(Path.GetFullPath(remove)) ? "Sacada (sus plugins se olvidan en el próximo escaneo)." : "Esa carpeta no estaba entre las agregadas.");
                var extra = PluginCatalog.ExtraFolders();
                foreach (var f in PluginCatalog.Folders)
                    Console.WriteLine($"{(extra.Contains(f) ? "agregada " : "windows  ")} {f}{(Directory.Exists(f) ? "" : "  (no existe)")}");
                return 0;
            }
            case "ocultar" or "mostrar":
            {
                string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException($"vst {sub} \"nombre del plugin\"");
                var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
                    ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
                PluginCatalog.SetHidden(plugin, sub == "ocultar");
                Console.WriteLine(sub == "ocultar" ? $"{plugin.Name} ya no sale en las listas." : $"{plugin.Name} vuelve a salir en las listas.");
                return 0;
            }
            case "probar":
                return Try(a);
            case "efecto":
                return Effect(a);
            case "parametros":
                return Parameters(a);
            case "estado":
                return StateCheck(a);
            case "vivo":
                return LivePlay(a);
            case "bucle":
                return LoopPlay(a);
            case "miniatura":
                return Thumbnail(a);
            default:
                throw new ArgumentException("vst escanear [--todo] [--carpeta X] [--archivo X] | lista | carpetas [--agregar X] [--sacar X] | ocultar \"nombre\" | mostrar \"nombre\" | probar \"nombre\" [--segundos 4] [--salida x.wav] [--ventana] | vivo \"nombre\" [--segundos 4] [--bloque 256] [--espera 0] [--desde 0] [--sin-carga] [--apuro 1] [--estado archivo] [--tope 192] [--prioridad] [--mmcss] [--ventana] [--salida x.wav] | bucle \"nombre\" [--bloque 256] [--vueltas 4] | efecto \"nombre\" [--entrada x.wav] [--segundos 4] [--bloque 256] [--tarde] [--salida x.wav] [--apagado] | parametros \"nombre\" [--mover \"parámetro\"] [--segundos 2] | estado \"nombre\" | miniatura \"nombre\" [--salida x.png] [--ya]");
        }
    }

    /// <summary>Opens the plugin's window and takes its picture the way the app does: waiting for it to settle, or at once with --ya.</summary>
    static int Thumbnail(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst miniatura \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        string output = a.Get("salida") is { } file ? Path.GetFullPath(file) : PluginThumbs.Prepare(plugin.Path, plugin.ClassId);
        using var live = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, 48000, offline: false);
        var answer = new TaskCompletionSource<bool>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        live.Message += line =>
        {
            if (line == "editor-open") live.Command($"{(a.Flag("ya") ? "snapshot-now" : "snapshot")} {output}");
            else if (line == "snapshot-done") answer.TrySetResult(true);
            else if (line == "snapshot-failed") answer.TrySetResult(false);
        };
        live.OpenEditor(0);
        bool saved = answer.Task.Wait(TimeSpan.FromSeconds(ThumbWaitSec)) && answer.Task.Result;
        Console.WriteLine(saved ? $"{plugin.Name}: miniatura en {sw.Elapsed.TotalSeconds:0.0} s, {new FileInfo(output).Length / 1024} KB: {output}" : $"{plugin.Name}: sin miniatura (la ventana quedó vacía).");
        return saved ? 0 : 1;
    }

    /// <summary>More than the 12 s the host looks at a window for.</summary>
    const int ThumbWaitSec = 20;

    /// <summary>The plugin's saved sound survives: its state, loaded into a new copy, comes back the same (what reopening a project does).</summary>
    static int StateCheck(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst estado \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        string? saved;
        using (var first = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, 48000, offline: true))
            saved = first.GetStateAsync().Result;
        if (saved == null) { Console.WriteLine("No dio su estado."); return 1; }
        using var second = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, 48000, offline: true);
        // two untouched copies can already differ (ids, timestamps): then that many bytes prove nothing
        string? fresh = second.GetStateAsync().Result;
        bool loaded = second.SetState(saved);
        string? again = second.GetStateAsync().Result;
        int noise = Differ(saved, fresh), differ = Differ(saved, again);
        Console.WriteLine($"{plugin.Name}: estado de {Convert.FromBase64String(saved).Length} bytes, {(loaded ? "cargó" : "no cargó")}, {(differ == 0 ? "volvió igual" : $"volvió con {differ} bytes DISTINTOS")} (dos copias sin tocar difieren en {noise}).");
        return loaded && differ <= noise ? 0 : 1;
    }

    /// <summary>How many bytes two saved states differ in.</summary>
    static int Differ(string a, string? b)
    {
        byte[] x = Convert.FromBase64String(a), y = Convert.FromBase64String(b ?? "");
        int n = Math.Abs(x.Length - y.Length);
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (x[i] != y[i]) n++;
        return n;
    }

    static void Show(List<PluginCatalog.Entry> entries)
    {
        var plugins = PluginCatalog.Plugins(entries);
        var hidden = PluginCatalog.Hidden();
        if (plugins.Count == 0) Console.WriteLine("Sin plugins (probá con vst escanear).");
        foreach (var p in plugins)
            Console.WriteLine($"{(p.IsInstrument ? "instrumento" : "efecto     ")}  {p.Name} · {p.FormatText} · {p.Vendor} · {p.Version} · {p.Categories}{(p.KnowsBuses ? $" · entradas: {p.AudioIns} de audio, {p.EventIns} de notas" : "")}{(hidden.Contains(PluginCatalog.Key(p)) ? "  (oculto)" : "")}");
        foreach (var e in entries.Where(e => e.Error != null))
            Console.WriteLine($"no abrió     {Path.GetFileName(e.Path)}: {e.Error}");
    }

    /// <summary>Plays a C major chord (and a bass C), or a beat on a drum kit, through the plugin, offline, into a WAV; --ventana opens its window live first.</summary>
    static int Try(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst probar \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        const int sr = 48000;
        double seconds = a.Double("segundos", 4);
        string? state = null;
        if (a.Flag("ventana"))
        {
            using var live = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: false);
            live.OpenEditor(0);
            Console.WriteLine($"Abrí la ventana de {plugin.Name}: cambiá lo que quieras y apretá Enter.");
            Console.ReadLine();
            state = live.GetStateAsync().Result;
            Console.WriteLine(state == null ? "No me dio su estado." : $"Estado: {state.Length} caracteres.");
        }
        var notes = TestNotes(plugin, seconds, sr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var render = new InstrumentRender(new VstInstance(plugin.Path, plugin.ClassId, plugin.Name, state), notes, 120, sr, offline: !a.Flag("tiempo-real"));
        double opened = sw.Elapsed.TotalSeconds;
        // like the export: some instruments (big sample libraries) keep loading in the background after they open
        if (a.Double("espera", InstrumentRender.WarmSec) is var wait and > 0) Console.WriteLine(render.Warm(wait) ? $"Sonó a los {sw.Elapsed.TotalSeconds:0.0} s." : $"No sonó en {wait:0.#} s.");
        int frames = (int)(seconds * sr);
        var l = new float[frames];
        var r = new float[frames];
        sw.Restart();
        render.Render(0, frames, l, r);
        double took = sw.Elapsed.TotalSeconds;
        float peak = 0;
        for (int i = 0; i < frames; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        string output = Path.GetFullPath(a.Get("salida", $"{plugin.Name}.wav"));
        Wav.Write(output, l, r, sr);
        Console.WriteLine($"{plugin.Name}: abrió en {opened:0.0} s, {seconds:0.#} s de audio en {took:0.00} s, pico {Palette(peak)}");
        Console.WriteLine($"Escrito: {output}");
        return peak > 1e-4 ? 0 : 3;
    }

    /// <summary>A beat on a drum kit (kick, snare and hi-hat, General MIDI, at 120) or a C major chord with a bass C on anything else.</summary>
    static NoteSequence TestNotes(VstPluginInfo plugin, double seconds, int sr)
    {
        if (plugin.Categories.Contains("Drum", StringComparison.OrdinalIgnoreCase))
        {
            var hits = new List<NoteEvent>();
            for (int e = 0; e < (int)((seconds - 1) * 4); e++)
            {
                long at = sr / 10 + e * sr / 4;
                hits.Add(new NoteEvent(at, sr / 8, 0, 0, 42, 90));
                if (e % 2 == 0) hits.Add(new NoteEvent(at, sr / 8, 0, 0, e % 4 == 0 ? 36 : 38, 110));
            }
            return new NoteSequence([.. hits], sr);
        }
        int[] chord = [48, 60, 64, 67];
        long held = (long)((seconds - 1) * sr);
        return new NoteSequence([.. chord.Select(p => new NoteEvent(sr / 10, held, NoteSynth.Hz(p), 0, p, 100))], sr);
    }

    /// <summary>
    /// An instrument played the way the app plays it (<see cref="InstrumentPlayer"/>): stopped for <c>--espera</c>
    /// seconds, then the notes in driver blocks of <c>--bloque</c> at the speed of real time, never waiting for the
    /// plugin. Says how many blocks it missed and how many came out with sound, what each block took on this thread (what
    /// the app's "DSP x %" counts) and on the plugin's, and the CPU of the plugin's whole process. <c>--prioridad</c> runs
    /// this thread at the priority of a driver's.
    /// </summary>
    static int LivePlay(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst vivo \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        const int sr = 48000;
        double seconds = a.Double("segundos", 4), wait = a.Double("espera", 0);
        int block = Math.Clamp(a.Int("bloque", 256), 32, VstBridge.MaxBlock);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // --tope: the block the plugin is told about, if it isn't the driver's
        using var bridge = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: false, a.Int("tope", block));
        Console.WriteLine($"{plugin.Name}: abrió en {sw.Elapsed.TotalSeconds:0.0} s, latencia {bridge.Latency} muestras");
        if (a.Get("estado") is { } stateFile && !bridge.SetState(File.ReadAllText(stateFile).Trim())) Console.WriteLine("No aceptó el estado.");
        if (a.Flag("ventana"))
        {
            bridge.Message += line => Console.WriteLine($"  {line}");
            bridge.Command("toolbar keys=\uE765:Teclado|-|mute=:M|solo=:S|-|panic=\uE71A:Detener notas");
            bridge.Command($"toolbar-track e8743b|Pista de prueba|{plugin.Name}");
            bridge.Command("toolbar-state keys=1 mute=1 solo=0");
            bridge.Command("keys-panel 84");
            bridge.Command("transport-keys 32:0 36:0"); // Space and Home
            bridge.OpenEditor(0);
        }
        var hostStats = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.Message += line => { if (line.StartsWith("stats ")) hostStats.TrySetResult(line); };
        var player = new InstrumentPlayer(bridge) { Notes = TestNotes(plugin, seconds, sr), Tempo = 120 };
        // what the app does before it puts the plugin on the track
        if (!a.Flag("sin-carga")) Console.WriteLine(InstrumentWarmup.Run(bridge, InstrumentWarmup.Pitch(player.Notes), 120, sr, 5) ? $"Sonó a los {sw.Elapsed.TotalSeconds:0.0} s." : "No sonó en 5 s.");
        // --apuro 20: the driver's blocks twenty times as fast as real time, so the plugin always comes late
        double hurry = a.Double("apuro", 1);
        var l = new float[(int)(seconds * sr) + block];
        var r = new float[l.Length];
        var none = new float[block];
        int idle = (int)(wait * sr / block), played = (l.Length - block) / block, sounding = 0;
        long start = (long)(a.Double("desde", 0) * sr);
        nint mmcss = 0;
        if (a.Flag("prioridad")) SetThreadPriority(GetCurrentThread(), ThreadPriorityTimeCritical);
        if (a.Flag("mmcss"))
        {
            // what a driver's thread gets: a real-time priority, above the plugin's own audio thread
            int task = 0;
            mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
            if (mmcss != 0) AvSetMmThreadPriority(mmcss, AvrtPriorityCritical);
        }
        var times = new long[idle + played];
        // what the warmup cost doesn't count
        bridge.Command("stats");
        hostStats.Task.Wait(1000);
        hostStats = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = System.Diagnostics.Process.GetProcessById(bridge.ProcessId);
        var cpuFrom = process.TotalProcessorTime;
        sw.Restart();
        for (int k = 0; k < idle + played; k++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int at = (k - idle) * block;
            if (k < idle) player.Idle(block, none, none);
            else player.Play(start + at, block, l.AsSpan(at, block), r.AsSpan(at, block));
            times[k] = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (k >= idle)
            {
                float peak = 0;
                for (int i = at; i < at + block; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                if (peak > 1e-4f) sounding++;
            }
            double due = (k + 1) * block / (double)sr / hurry;
            while (sw.Elapsed.TotalSeconds < due) Thread.Yield();
        }
        double ran = sw.Elapsed.TotalSeconds;
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpuFrom).TotalSeconds;
        if (a.Flag("prioridad")) SetThreadPriority(GetCurrentThread(), 0);
        if (mmcss != 0) AvRevertMmThreadCharacteristics(mmcss);
        Console.WriteLine($"Bloques de {block}: {bridge.Misses} perdidos de {idle + played}, {sounding} de {played} con sonido, pico {Palette(Peak(l, r))}");
        Array.Sort(times);
        double us = 1e6 / System.Diagnostics.Stopwatch.Frequency, budget = block * 1e6 / sr;
        double mean = times.Average() * us;
        Console.WriteLine($"Hilo de audio: media {mean:0.0} µs ({100 * mean / budget:0.0} % del bloque), 99 % {times[(int)(times.Length * 0.99)] * us:0.0} µs, peor {times[^1] * us:0.0} µs");
        bridge.Command("stats");
        if (hostStats.Task.Wait(1000) && hostStats.Task.Result.Split(' ') is { Length: 6 } st && long.Parse(st[1]) is > 0 and var done)
        {
            double wall = long.Parse(st[3]) / (double)done, onCpu = long.Parse(st[5]) / (double)done;
            Console.WriteLine($"Plugin: {done} bloques, media {wall:0.0} µs de reloj y {onCpu:0.0} µs de CPU ({100 * wall / budget:0.0} % del bloque), peor {st[4]} µs");
        }
        Console.WriteLine($"Proceso del plugin: {100 * cpu / ran:0.0} % de un núcleo");

        // stopped, as the app runs it: the song's tail stays shut, the piano roll's key is heard
        var stoppedL = new float[block];
        var stoppedR = new float[block];
        int RunStopped(double sec)
        {
            int open = 0;
            sw.Restart();
            for (int k = 0, blocks = (int)(sec * sr / block); k < blocks; k++)
            {
                if (player.Idle(block, stoppedL, stoppedR)) open++;
                double due = (k + 1) * block / (double)sr;
                while (sw.Elapsed.TotalSeconds < due) Thread.Yield();
            }
            return open;
        }
        int afterStop = RunStopped(2);
        player.Audition(60, 100, sr / 3);
        Console.WriteLine($"Parado: {afterStop} bloques abiertos después de parar, {RunStopped(1)} con una tecla del piano roll");
        if (a.Get("salida") is { } output) Wav.Write(Path.GetFullPath(output), l, r, sr);
        return sounding > 0 ? 0 : 3;
    }

    /// <summary>
    /// A loop on an instrument, as the engine turns it (<see cref="LoopSplice.Turned"/>) in driver blocks at the speed of
    /// real time: when the first note of each lap sounds, with the loop known ahead and as a plain jump, and on an
    /// instrument that sustains, that a note crossing B doesn't stay on and one that began before A sounds in the next lap.
    /// </summary>
    static int LoopPlay(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst bucle \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        const int sr = 48000, tenth = sr / 10;
        const long A = sr / 2, B = A + sr;
        int block = Math.Clamp(a.Int("bloque", 256), 32, VstBridge.MaxBlock), laps = Math.Clamp(a.Int("vueltas", 4), 2, 20);
        using var bridge = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: false, block);
        var player = new InstrumentPlayer(bridge) { Tempo = 120 };
        if (!InstrumentWarmup.Run(bridge, 60, 120, sr, 5)) Console.WriteLine("No sonó en 5 s.");

        // what it plays, with the loop known ahead (end = B) or not (0), and the block of the output where each lap turned
        (float[] Heard, List<long> Turns) Run(NoteEvent[] notes, long end)
        {
            player.Notes = new NoteSequence(notes, sr);
            // ends before the turn after the last one
            var heard = new float[(laps + 1) * sr + tenth - 2 * block];
            var other = new float[heard.Length];
            var turns = new List<long>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long pos = A - tenth;
            for (int at = 0, k = 0; at + block <= heard.Length; at += block, k++)
            {
                player.Play(pos, block, heard.AsSpan(at, block), other.AsSpan(at, block), null, A, end);
                long next = pos + block;
                if (LoopSplice.Turned(pos, next, A, B, out long to))
                {
                    turns.Add(at + (B - pos));
                    next = to;
                }
                pos = next;
                double due = (k + 1) * block / (double)sr;
                while (sw.Elapsed.TotalSeconds < due) Thread.Yield();
            }
            // the notes go off, and what they leave goes out
            for (int k = 0; k < 30; k++)
            {
                player.Idle(block, other.AsSpan(0, block), other.AsSpan(block, block));
                Thread.Sleep(10);
            }
            return (heard, turns);
        }

        // where the note starts: the first frame over 5 % of its peak, from where A is heard
        int Onset(float[] x, long from)
        {
            float peak = 0;
            for (int i = (int)from; i < from + tenth && i < x.Length; i++) peak = Math.Max(peak, Math.Abs(x[i]));
            for (int i = (int)from; i < from + tenth && i < x.Length; i++) if (Math.Abs(x[i]) > peak * 0.05f) return (int)(i - from);
            return tenth;
        }
        // how late the first note of each lap comes after the same note the first time through
        (int Worst, int Misses) Lateness(long end)
        {
            int missed = bridge.Misses;
            NoteEvent[] beat = [new(A, tenth, 0, 0, 60, 110), new(A + 5 * tenth, tenth, 0, 0, 67, 110)];
            var (heard, turns) = Run(beat, end);
            int first = Onset(heard, tenth), worst = 0;
            foreach (long t in turns) worst = Math.Max(worst, Math.Abs(Onset(heard, t) - first));
            return (worst, bridge.Misses - missed);
        }

        var (plain, plainMisses) = Lateness(0);
        Console.WriteLine($"{plugin.Name}, bloques de {block}, latencia {bridge.Latency}: como un salto, el primer tiempo de la vuelta llega {plain} muestras corrido ({plainMisses} bloques perdidos)");
        var (ahead, aheadMisses) = Lateness(B);
        Console.WriteLine($"Con el bucle sabido de antes: {ahead} muestras corrido ({aheadMisses} bloques perdidos)");

        // one note crosses B, another began before A: the first ends at the turn, the second comes in on every lap
        NoteEvent[] held = [new(A - tenth, 4 * tenth, 0, 0, 60, 110), new(A + 9 * tenth, 4 * tenth, 0, 0, 67, 110)];
        var (sound, marks) = Run(held, B);
        double quiet = 0, landed = 1;
        foreach (long t in marks)
        {
            if (t + sr >= sound.Length) continue;
            float inNote = 0, between = 0;
            for (long i = t + tenth; i < t + 2 * tenth; i++) inNote = Math.Max(inNote, Math.Abs(sound[i]));
            for (long i = t + 7 * tenth; i < t + 8 * tenth + tenth / 2; i++) between = Math.Max(between, Math.Abs(sound[i]));
            quiet = Math.Max(quiet, between);
            landed = Math.Min(landed, inNote);
        }
        Console.WriteLine($"Nota que cruza B: entre notas queda en {Palette((float)quiet)}. Nota que empezó antes de A: suena en {Palette((float)landed)}");
        bool ok = ahead <= LoopTolerance && quiet < 0.1 * landed;
        Console.WriteLine(ok ? "ok" : "DISTINTO");
        return ok ? 0 : 3;
    }

    /// <summary>
    /// 2.7 ms: what the same note moves by itself. Plugins start notes at the edges of their own small blocks (Surge, 32)
    /// and an oscillator's free phase moves where it crosses the threshold (measured: 20 to 80 frames, whatever the driver's block).
    /// </summary>
    const int LoopTolerance = 128;

    /// <summary>
    /// An effect in a slot (<see cref="VstInsert"/>), offline as the export runs it and live in driver blocks of
    /// <c>--bloque</c> at the speed of real time: writes both and checks that the live one is the same sound one block
    /// later (a plugin that changes with time, like a modulated reverb, only lines up roughly). <c>--tarde</c> calls back
    /// as soon as the block before is done, so no block is ever ready on time: it has to come out whole, one block later.
    /// </summary>
    /// <summary>
    /// What a plugin lets a host automate; with --mover, a lane on that parameter from 0 to 1 over the sound, the way the
    /// export plays one, against the same sound with no lane: they must differ, or the value isn't reaching the plugin.
    /// </summary>
    static int Parameters(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst parametros \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        const int sr = 48000, quantum = 128;
        using var bridge = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: true);
        var list = bridge.GetParamsAsync().Result;
        if (list == null) throw new InvalidOperationException("El plugin no contestó la lista de parámetros.");
        if (a.Get("mover") is not { } wanted)
        {
            foreach (var p in list) Console.WriteLine($"{p.Id,12}  {p.Value:0.000}  {(p.Steps > 0 ? $"{p.Steps} pasos" : "continuo"),-9}  {p.Title}");
            Console.WriteLine($"{list.Count} parámetros.");
            return 0;
        }
        var param = list.FirstOrDefault(p => p.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No hay un parámetro \"{wanted}\": corré vst parametros sin --mover.");
        double sec = a.Double("segundos", 2);
        var input = new float[(int)(sec * sr)];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / sr));
        float[] Run(VstParamCurves? curves)
        {
            var fx = new VstInsert(sr);
            fx.Set(new VstSlotSpec(new VstInstance(plugin.Path, plugin.ClassId, plugin.Name, null), true) { Offline = true, Curves = curves }, 120);
            var l = (float[])input.Clone();
            var r = (float[])input.Clone();
            for (int at = 0; at < l.Length; at += quantum)
            {
                int len = Math.Min(quantum, l.Length - at);
                fx.Frame = at;
                fx.Process(l.AsSpan(at, len), r.AsSpan(at, len));
            }
            fx.Dispose();
            return l;
        }
        var still = Run(null);
        var moved = Run(new VstParamCurves([param.Id], [new AutoCurve([0, sec], [0, 1])]));
        double diff = 0;
        for (int i = 0; i < still.Length; i++) diff = Math.Max(diff, Math.Abs(still[i] - moved[i]));
        bool ok = diff > 1e-4;
        Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {plugin.Name} · {param.Title}: diferencia {diff:0.0000} con el carril");
        return ok ? 0 : 1;
    }

    static int Effect(Args a)
    {
        string name = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("vst efecto \"nombre del plugin\"");
        var plugin = PluginCatalog.Find(PluginCatalog.Plugins(PluginCatalog.Load()), name)
            ?? throw new ArgumentException($"No encontré \"{name}\" en la lista: probá con vst escanear.");
        if (plugin.IsInstrument) Console.WriteLine($"Ojo: {plugin.Name} figura como instrumento; lo pruebo como efecto igual.");
        const int sr = 48000, quantum = 128;
        float[] inL, inR;
        if (a.Get("entrada") is { } input)
        {
            var (ch, wsr) = Wav.Read(Path.GetFullPath(input));
            if (wsr != sr) Console.WriteLine($"Aviso: el WAV es de {wsr} Hz; lo leo como si fuera de {sr} Hz.");
            inL = ch[0];
            inR = ch.Length > 1 ? ch[1] : ch[0];
        }
        else
        {
            // plucked chords every half second, as a guitar DI would come in
            int len = (int)(a.Double("segundos", 4) * sr);
            inL = new float[len];
            double[] hz = [82.4, 123.5, 164.8, 207.7];
            for (int i = 0; i < len; i++)
            {
                double t = i % (sr / 2) / (double)sr;
                double v = 0;
                foreach (double f in hz) v += ((t * f * (1 + i / (sr / 2) % 3 * 0.25)) % 1 - 0.5) * Math.Exp(-t * 6);
                inL[i] = (float)(v * 0.25);
            }
            inR = inL;
        }
        int n = inL.Length;
        int block = Math.Clamp(a.Int("bloque", 256), 32, VstBridge.MaxBlock);
        var instance = new VstInstance(plugin.Path, plugin.ClassId, plugin.Name, null);
        bool on = !a.Flag("apagado");

        // the export's way: its own process, offline, waiting for every block
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var offline = new VstInsert(sr);
        offline.Set(new VstSlotSpec(instance, on) { Offline = true }, 120);
        var expL = (float[])inL.Clone();
        var expR = (float[])inR.Clone();
        for (int at = 0; at < n; at += quantum)
        {
            int len = Math.Min(quantum, n - at);
            offline.Process(expL.AsSpan(at, len), expR.AsSpan(at, len));
        }
        int pluginLatency = offline.LatencySamples;
        offline.Dispose();
        Console.WriteLine($"{plugin.Name} como en la exportación: {n / (double)sr:0.0} s en {sw.Elapsed.TotalSeconds:0.00} s, latencia propia {pluginLatency} muestras, pico {Palette(Peak(expL, expR))}");

        // live: the window's process, blocks of the driver's size at the speed of real time, never waiting for it
        using var bridge = VstBridge.Start(plugin.Path, plugin.ClassId, plugin.Name, sr, offline: false, block);
        VstInsert.LiveBlock = block;
        VstInsert.Steady = a.Flag("toma");
        var live = new VstInsert(sr);
        live.Set(new VstSlotSpec(instance, on) { Live = bridge }, 120);
        bool late = a.Flag("tarde");
        var liveL = (float[])inL.Clone();
        var liveR = (float[])inR.Clone();
        sw.Restart();
        for (int at = 0, k = 0; at < n; at += block, k++)
        {
            int len = Math.Min(block, n - at);
            live.Process(liveL.AsSpan(at, len), liveR.AsSpan(at, len));
            if (late)
            {
                // the next callback comes as soon as the block before this one is done: this one is never ready for it
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (bridge.Finished < bridge.Published - 1 && wait.ElapsedMilliseconds < 1000) Thread.SpinWait(20);
                continue;
            }
            // the driver calls back once per block, evenly: wait for the next one spinning (a Sleep(1) lasts up to
            // 15 ms on Windows, and the blocks it put off would then come back to back, with no time for the plugin)
            double due = (k + 1) * block / (double)sr;
            while (sw.Elapsed.TotalSeconds < due) Thread.Yield();
        }
        int expected = live.LatencySamples - pluginLatency;
        Console.WriteLine($"En vivo{(VstInsert.Steady ? ", como en una toma" : "")}{(late ? ", siempre un bloque atrasado" : "")}, bloques de {block}: {live.Misses} bloques que no llegó a tiempo; se oye {live.LatencySamples} muestras tarde ({live.LatencySamples * 1000.0 / sr:0.0} ms)");

        // where the live sound lines up with the export's: one block later
        int best = 0;
        double bestErr = double.MaxValue;
        int reach = 3 * block + 64, from = Math.Min(n / 4, sr), to = n - reach;
        for (int lag = 0; lag <= reach; lag++)
        {
            double e = 0;
            for (int i = from; i < to; i += 7) e += Math.Abs(liveL[i + lag] - expL[i]);
            if (e < bestErr) { bestErr = e; best = lag; }
        }
        double diff = 0, level = 0;
        for (int i = from; i < to; i++)
        {
            diff = Math.Max(diff, Math.Abs(liveL[i + best] - expL[i]));
            level = Math.Max(level, Math.Abs(expL[i]));
        }
        string rel = level > 0 && diff > 0 ? $"{20 * Math.Log10(diff / level):0.0} dB" : "exacto";
        Console.WriteLine($"En vivo llega {best} muestras después de la exportación (esperado {expected}), diferencia {rel}");
        string output = Path.GetFullPath(a.Get("salida", $"{plugin.Name} efecto.wav"));
        Wav.Write(output, expL, expR, sr);
        string liveOut = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + " en vivo.wav");
        Wav.Write(liveOut, liveL, liveR, sr);
        Console.WriteLine($"Escritos: {output} y {liveOut}");
        // the same sound, with no block lost: only a plugin that doesn't change with time gets this far
        bool whole = !late || level > 0 && diff < level * 0.01;
        return best == expected && whole ? 0 : 3;
    }

    const int ThreadPriorityTimeCritical = 15, AvrtPriorityCritical = 2;

    [System.Runtime.InteropServices.DllImport("avrt", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern nint AvSetMmThreadCharacteristicsW(string task, ref int index);

    [System.Runtime.InteropServices.DllImport("avrt")]
    static extern bool AvSetMmThreadPriority(nint handle, int priority);

    [System.Runtime.InteropServices.DllImport("avrt")]
    static extern bool AvRevertMmThreadCharacteristics(nint handle);

    [System.Runtime.InteropServices.DllImport("kernel32")]
    static extern nint GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32")]
    static extern bool SetThreadPriority(nint thread, int priority);

    static float Peak(float[] l, float[] r)
    {
        float p = 0;
        for (int i = 0; i < l.Length; i++) p = Math.Max(p, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
        return p;
    }

    static string Palette(float peak) => peak > 1e-6 ? $"{20 * Math.Log10(peak):0.0} dB" : "silencio";
}
