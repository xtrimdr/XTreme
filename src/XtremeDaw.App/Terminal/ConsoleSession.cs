using System.Text;
using Avalonia.Threading;

namespace XtremeDaw.App.Terminal;

/// <summary>
/// One Claude console inside the bench (#278): the program in its pseudo console, its screen and the view that draws it.
/// The output is decoded as UTF-8 on the reader thread (a character split between two reads waits for its other half)
/// and the screen's answers go back to the program. When the program ends, <see cref="Ended"/> comes on the UI thread,
/// saying whether the agent asked to close (it left <c>XT_BENCH_CLOSE</c>'s file: <c>tools\cerrar-sesion.ps1</c>).
/// </summary>
sealed class ConsoleSession : IDisposable
{
    /// <summary>Size until the view is laid out.</summary>
    const int StartCols = 120, StartRows = 32;
    /// <summary>How long <see cref="Send"/> waits between the text and its Enter.</summary>
    static readonly TimeSpan EnterAfter = TimeSpan.FromMilliseconds(80);

    readonly PseudoConsole pty;
    readonly Decoder utf8 = Encoding.UTF8.GetDecoder();
    readonly char[] chars = new char[16384 + 4];
    readonly string flag;

    public string Title { get; }
    public TerminalScreen Screen { get; }
    public TerminalView View { get; }
    public int ProcessId => pty.ProcessId;
    public bool Running => !IsEnded;
    public bool IsEnded { get; private set; }
    /// <summary>The program ended; true when its agent asked the console to close.</summary>
    public event Action<bool>? Ended;

    ConsoleSession(string title, PseudoConsole pty, TerminalScreen screen, string flag)
    {
        Title = title;
        this.pty = pty;
        this.flag = flag;
        Screen = screen;
        View = new TerminalView(screen, pty.Write);
        View.CellsChanged += pty.Resize;
        screen.Reply += pty.Write;
        pty.Output += OnOutput;
        pty.Exited += () => Dispatcher.UIThread.Post(OnExited);
    }

    /// <summary>Starts <paramref name="commandLine"/>; throws <see cref="System.ComponentModel.Win32Exception"/> when Windows refuses.</summary>
    public static ConsoleSession Start(string title, string commandLine, string folder, IDictionary<string, string?> env, string flag)
    {
        var screen = new TerminalScreen(StartCols, StartRows);
        var pty = PseudoConsole.Start(commandLine, folder, env, StartCols, StartRows);
        return new ConsoleSession(title, pty, screen, flag);
    }

    void OnOutput(byte[] buf, int n)
    {
        int count = utf8.GetChars(buf, 0, n, chars, 0, false);
        if (count == 0) return;
        var text = new string(chars, 0, count);
        lock (Screen.Sync) Screen.Feed(text);
    }

    void OnExited()
    {
        if (IsEnded) return;
        IsEnded = true;
        bool close = false;
        try
        {
            if (File.Exists(flag))
            {
                File.Delete(flag);
                close = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Ended?.Invoke(close);
    }

    /// <summary>
    /// Types a message and Enter (#278: the box of Agentes answers in the console itself): pasted, so its line breaks
    /// stay inside the message instead of sending it in pieces.
    /// </summary>
    public void Send(string text)
    {
        if (IsEnded) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (Screen.Sync) bracketed = Screen.BracketedPaste;
        pty.Write(bracketed ? "\x1b[200~" + text + "\x1b[201~" : text);
        // Enter a moment later: right after a paste, some prompts take it as part of the paste
        DispatcherTimer.RunOnce(() => { if (!IsEnded) pty.Write("\r"); }, EnterAfter);
    }

    /// <summary>Ends the program if it still runs.</summary>
    public void Dispose() => pty.Dispose();
}
