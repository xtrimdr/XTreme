using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XtremeDaw.App.Terminal;

/// <summary>
/// A program running in a Windows pseudo console (ConPTY), so the bench can show it in its own window instead of
/// opening Windows Terminal (asked by the user, #278). Its output comes as VT text through <see cref="Output"/> (read on
/// a thread of its own) and the keys go back as VT text through <see cref="Write"/>. Plain kernel32, no packages.
/// </summary>
sealed class PseudoConsole : IDisposable
{
    nint hpc;
    readonly SafeFileHandle input, output;
    readonly nint process;
    readonly FileStream writer;
    readonly object writeLock = new();
    int disposed;
    volatile bool ended;

    public int ProcessId { get; }
    /// <summary>Raw output, on the reader thread.</summary>
    public event Action<byte[], int>? Output;
    /// <summary>Once the program ended and its output was read to the end, on a background thread.</summary>
    public event Action? Exited;

    PseudoConsole(nint hpc, SafeFileHandle input, SafeFileHandle output, nint process, int pid)
    {
        this.hpc = hpc;
        this.input = input;
        this.output = output;
        this.process = process;
        ProcessId = pid;
        writer = new FileStream(input, FileAccess.Write, 1);
    }

    /// <summary>
    /// Starts <paramref name="commandLine"/> at <paramref name="folder"/> with <paramref name="env"/> in a pseudo console
    /// of <paramref name="cols"/> × <paramref name="rows"/>. Throws <see cref="Win32Exception"/> when Windows refuses.
    /// </summary>
    public static PseudoConsole Start(string commandLine, string folder, IDictionary<string, string?> env, int cols, int rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, 0, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, 0, 0)) throw new Win32Exception();
        int hr = CreatePseudoConsole(new Coord((short)cols, (short)rows), inRead, outWrite, 0, out var hpc);
        // the console holds its own copies of its ends
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr);

        nint list = 0;
        try
        {
            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(list, 0, PseudoConsoleAttribute, hpc, nint.Size, 0, 0)) throw new Win32Exception();

            var si = new StartupInfoEx { AttributeList = list };
            si.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            // no handles of the bench's: the program talks to the pseudo console only
            si.StartupInfo.Flags = UseStdHandles;
            var block = new StringBuilder();
            foreach (var (k, v) in env.Where(e => e.Value != null).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                block.Append(k).Append('=').Append(v).Append('\0');
            block.Append('\0');
            var cmd = new StringBuilder(commandLine);
            if (!CreateProcessW(null, cmd, 0, 0, false, ExtendedStartupInfo | UnicodeEnvironment, block.ToString(), folder, ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.Thread);
            var pc = new PseudoConsole(hpc, inWrite, outRead, pi.Process, pi.ProcessId);
            pc.Run();
            return pc;
        }
        catch
        {
            ClosePseudoConsole(hpc);
            inWrite.Dispose();
            outRead.Dispose();
            throw;
        }
        finally
        {
            if (list != 0)
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
            }
        }
    }

    void Run()
    {
        new Thread(Read) { IsBackground = true, Name = "conpty-read" }.Start();
        new Thread(() =>
        {
            WaitForSingleObject(process, Infinite);
            ended = true;
            // closing the console ends the output, so the reader drains what is left and says it ended
            Close();
            CloseHandle(process);
        }) { IsBackground = true, Name = "conpty-wait" }.Start();
    }

    void Read()
    {
        var buf = new byte[16384];
        using var stream = new FileStream(output, FileAccess.Read, 1);
        try
        {
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0) Output?.Invoke(buf, n);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (disposed != 0 || text.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            lock (writeLock)
            {
                writer.Write(bytes);
                writer.Flush();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (disposed == 0 && hpc != 0) ResizePseudoConsole(hpc, new Coord((short)cols, (short)rows));
    }

    /// <summary>Whether the program is still running.</summary>
    public bool Running => !ended;

    /// <summary>Ends the program (closing the pseudo console sends it the console's close).</summary>
    void Close()
    {
        nint h = Interlocked.Exchange(ref hpc, 0);
        if (h != 0) ClosePseudoConsole(h);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // ClosePseudoConsole may wait for the output to be read: off the UI thread
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Close();
            lock (writeLock) writer.Dispose();
        });
    }

    const uint ExtendedStartupInfo = 0x00080000, UnicodeEnvironment = 0x00000400, Infinite = 0xFFFFFFFF;
    const int UseStdHandles = 0x00000100;
    static readonly nint PseudoConsoleAttribute = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    readonly struct Coord(short x, short y)
    {
        public readonly short X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);
    [DllImport("kernel32.dll")]
    static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint hpc);
    [DllImport("kernel32.dll")]
    static extern int ResizePseudoConsole(nint hpc, Coord size);
    [DllImport("kernel32.dll")]
    static extern void ClosePseudoConsole(nint hpc);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);
    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, string environment, string folder, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
}
