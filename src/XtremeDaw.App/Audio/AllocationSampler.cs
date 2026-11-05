using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace XtremeDaw.App.Audio;

/// <summary>
/// Who is making the garbage that stalls the audio thread: listens to the runtime's allocation ticks (one every
/// ~100 KB allocated, with the type of the object that crossed the mark) and adds them up per type and per thread
/// (the UI thread or any other). <see cref="AudioGapLog"/> writes the top of the list next to a dropout. It runs in the
/// process instead of needing a profiler because the dropouts only show up on the user's machine, with their project.
/// The events arrive on the runtime's dispatch thread, never on the audio thread (which allocates nothing).
/// </summary>
public sealed class AllocationSampler : EventListener
{
    /// <summary>The runtime's GC keyword; at Verbose it carries GCAllocationTick.</summary>
    const EventKeywords GcKeyword = (EventKeywords)0x1;
    /// <summary>GCAllocationTick_V4 (all versions share the id).</summary>
    const int AllocationTickId = 10;
    /// <summary>How many types the summary names.</summary>
    const int TopTypes = 5;

    readonly object gate = new();
    readonly Dictionary<string, long> byType = [];
    readonly uint uiThread = GetCurrentThreadId();
    long total, ui;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventId != AllocationTickId || e.Payload == null || e.PayloadNames == null) return;
        long amount = 0;
        string type = "?";
        for (int i = 0; i < e.PayloadNames.Count; i++)
        {
            switch (e.PayloadNames[i])
            {
                case "AllocationAmount64": amount = Convert.ToInt64(e.Payload[i]); break;
                case "AllocationAmount" when amount == 0: amount = Convert.ToInt64(e.Payload[i]); break;
                case "TypeName": type = e.Payload[i] as string ?? "?"; break;
            }
        }
        lock (gate)
        {
            byType[type] = byType.GetValueOrDefault(type) + amount;
            total += amount;
            if (e.OSThreadId == uiThread) ui += amount;
        }
    }

    /// <summary>"UI 92 % · System.String 31 %, …" for what was sampled since the last call, and starts over; null if nothing was.</summary>
    public string? Take()
    {
        lock (gate)
        {
            if (total <= 0) return null;
            var top = byType.OrderByDescending(p => p.Value).Take(TopTypes).Select(p => $"{p.Key} {p.Value * 100 / total}%");
            string s = $"UI {ui * 100 / total}% · {string.Join(", ", top)}";
            byType.Clear();
            total = ui = 0;
            return s;
        }
    }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
