using System.Diagnostics;

namespace XtremeDaw.Lab;

/// <summary>
/// One heavy job at a time on this checkout. Several agent sessions share bin\: a comprobar-dsp takes a minute and most
/// of the cores, a few at once starve the whole machine, and a build can't replace the DLLs of a xtlab that is running.
/// So the heavy commands wait for their turn (tools\probar.ps1 and any build, through tools\turno.targets, hold the same
/// one) and run below normal priority, so the desktop and an open XTreme stay responsive. The turn is an open file, not a mutex or a semaphore:
/// Windows frees it when its holder dies, however it dies, so it can't stay taken.
/// </summary>
static class HeavyTurn
{
    public const string FileName = "turno.lock";
    // Exit code when the turn never came; probar.ps1 uses the same one
    public const int BusyCode = 4;
    // Set by tools\probar.ps1 for the xtlab it runs: it already holds the turn
    const string HeldByParent = "XTREME_TURNO";
    // A little under the ten minutes an agent's command can last, so it hears who is in the way
    const int MaxWaitSec = 540;
    const int PollMs = 500;

    // What uses every core for a while, or a lot of memory
    static readonly HashSet<string> Commands =
        ["comprobar-dsp", "exportar", "mezclar", "masterizar", "separar", "extraer-midi", "estirar", "transponer", "escala"];

    // Static so it stays open until the process ends: the collector would close it, and the turn with it
    static FileStream? held;

    /// <summary>Waits for the turn if the command needs one; false if it never came.</summary>
    public static bool Take(string? command)
    {
        if (command == null || !Commands.Contains(command)) return true;
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        if (Environment.GetEnvironmentVariable(HeldByParent) == "1") return true;

        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        var clock = Stopwatch.StartNew();
        bool told = false;
        while (true)
        {
            try
            {
                held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                held.SetLength(0);
                held.Write(System.Text.Encoding.UTF8.GetBytes($"xtlab {command} (pid {Environment.ProcessId})"));
                held.Flush();
                return true;
            }
            catch (IOException)
            {
                if (clock.Elapsed.TotalSeconds > MaxWaitSec)
                {
                    Console.Error.WriteLine($"Sigue ocupado después de {MaxWaitSec} s: {Holder(path)}.");
                    return false;
                }
                if (!told) Console.Error.WriteLine($"Esperando turno: {Holder(path)}.");
                told = true;
                Thread.Sleep(PollMs);
            }
        }
    }

    static string Holder(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(f);
            return r.ReadToEnd();
        }
        catch (IOException) { return "otro proceso"; }
    }
}
