using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>xtlab atajos comprobar: no layout of another DAW gives one key to two actions (<see cref="KeyLayouts"/>).</summary>
static class KeyLayoutCommands
{
    public static int Run(Args a)
    {
        int bad = 0;
        foreach (var l in KeyLayouts.Daws)
        {
            var problems = KeyLayouts.Problems(l);
            Console.WriteLine($"  {l.Name,-58} {(problems.Count == 0 ? "ok" : "DISTINTO")}");
            foreach (var p in problems) Console.WriteLine($"    {p}");
            bad += problems.Count;
        }
        return bad == 0 ? 0 : 1;
    }
}
