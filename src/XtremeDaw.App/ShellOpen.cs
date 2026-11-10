using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;

namespace XtremeDaw.App;

/// <summary>
/// A .xtp opened from Windows while XTreme is open goes to the open window instead of a second one (asked by the user,
/// #254: a second window opened the same project read-only). The open window listens on a named pipe; a new process
/// with a project on its command line hands the path over and exits. The pipe is per user and per exe, so a test copy
/// and the installed XTreme do not take each other's files.
/// </summary>
static class ShellOpen
{
    /// <summary>How long a new process waits for an open window to answer before opening on its own.</summary>
    const int ConnectTimeoutMs = 400;

    static string PipeName
    {
        get
        {
            string key = (Environment.UserName + "|" + (Environment.ProcessPath ?? "")).ToLowerInvariant();
            return "XTreme-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        }
    }

    /// <summary>The project on the command line: the first argument that is not an option.</summary>
    public static string? ProjectArg(string[] args)
    {
        foreach (var a in args)
            if (!a.StartsWith("--", StringComparison.Ordinal) && a.Length > 0) return a;
        return null;
    }

    /// <summary>Hands <paramref name="path"/> to an open XTreme (this same exe); false when none answered.</summary>
    public static bool Forward(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            // this process got the foreground from Explorer: let the open window take it
            AllowSetForegroundWindow(AsfwAny);
            var bytes = Encoding.UTF8.GetBytes(full);
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Starts listening; each path that arrives runs <paramref name="received"/> on the UI thread.</summary>
    public static void Listen(Action<string> received)
    {
        var thread = new Thread(() => Loop(received)) { IsBackground = true, Name = "XTreme shell open" };
        thread.Start();
    }

    static void Loop(Action<string> received)
    {
        string name = PipeName;
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                string path = reader.ReadToEnd().Trim();
                if (path.Length > 0) Dispatcher.UIThread.Post(() => received(path));
            }
            catch (IOException)
            {
                // another window of this exe already listens (or the pipe broke): that one receives the files
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    const int AsfwAny = -1;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
