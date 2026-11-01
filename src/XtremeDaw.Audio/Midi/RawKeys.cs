using System.Runtime.InteropServices;

namespace XtremeDaw.Audio.Midi;

/// <summary>
/// The computer's keys as Windows gets them (Raw Input), on a thread of their own: the window's key events wait for the
/// UI thread (a frame being drawn, a GC) and a note can't. It reads the physical key, so the layout doesn't matter, and
/// sees every key in the system while it runs: whoever uses it checks that the program is in front
/// (<see cref="ProgramInFront"/>). Runs only while <see cref="VirtualKeyboard"/>'s mode is on.
/// </summary>
public sealed class RawKeys : IDisposable
{
    const uint WmInput = 0x00FF, WmQuit = 0x0012, RidInput = 0x10000003, RidevInputSink = 0x100;
    const ushort KeyBreak = 1, KeyE0 = 2, KeyE1 = 4;
    const int HeaderSize = 24; // RAWINPUTHEADER on x64
    static readonly IntPtr MessageOnly = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClass
    {
        public uint Style;
        public WndProc Proc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu;
        public string Name;
    }
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WndClass c);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern bool RegisterRawInputDevices(ref RawDevice d, uint n, uint size);
    [DllImport("user32")] static extern unsafe uint GetRawInputData(IntPtr raw, uint cmd, byte* data, ref uint size, uint headerSize);
    [DllImport("user32")] static extern int GetMessageW(out Msg m, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();

    const int VkControl = 0x11, VkMenu = 0x12;
    const uint VscToVk = 1, VkToChar = 2;
    const string ClassName = "WtRawKeys";
    /// <summary>The class outlives every instance: its procedure can't be collected.</summary>
    static readonly WndProc Proc = (hwnd, msg, w, l) => msg == WmInput && Current is { } me ? me.Input(hwnd, msg, w, l) : DefWindowProcW(hwnd, msg, w, l);
    static bool classMade;
    static volatile RawKeys? Current;

    readonly Action<int, bool> key;
    readonly Thread thread;
    readonly ManualResetEventSlim up = new(false);
    bool ok;
    uint threadId;

    RawKeys(Action<int, bool> key)
    {
        this.key = key;
        thread = new Thread(Run) { IsBackground = true, Name = "raw keys", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>
    /// Starts reading: <paramref name="key"/> gets each physical key (its scan code, and whether it went down), repeats
    /// included, on the thread of this class. Null if Windows said no: then the keys only come through the window.
    /// </summary>
    public static RawKeys? Start(Action<int, bool> key)
    {
        var r = new RawKeys(key);
        Current = r;
        r.thread.Start();
        r.up.Wait(2000);
        if (r.ok) return r;
        Current = null;
        return null;
    }

    void Run()
    {
        threadId = GetCurrentThreadId();
        if (!classMade)
        {
            var c = new WndClass { Proc = Proc, Name = ClassName };
            RegisterClassW(ref c);
            classMade = true;
        }
        var hwnd = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, MessageOnly, 0, 0, 0);
        var device = new RawDevice { Page = 1, Usage = 6, Flags = RidevInputSink, Target = hwnd };
        ok = hwnd != 0 && RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>());
        up.Set();
        if (!ok) return;
        while (GetMessageW(out var m, 0, 0, 0) > 0)
            if (m.Message == WmInput) Proc(m.Hwnd, m.Message, m.WParam, m.LParam);
        DestroyWindow(hwnd);
    }

    unsafe IntPtr Input(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        byte* data = stackalloc byte[64];
        uint size = 64;
        if (GetRawInputData(l, RidInput, data, ref size, HeaderSize) != uint.MaxValue)
        {
            ushort scan = *(ushort*)(data + HeaderSize), flags = *(ushort*)(data + HeaderSize + 2);
            // the extended ones are other keys with the same code (the arrows, the right Ctrl)
            if ((flags & (KeyE0 | KeyE1)) == 0) key(scan, (flags & KeyBreak) == 0);
        }
        return 0;
    }

    /// <summary>Processes of plugins whose window counts as the program's own (<see cref="ProgramInFront"/>), swapped whole.</summary>
    public static volatile int[] AlsoFront = [];

    /// <summary>A window of this program has the keyboard, or the window of one of its plugins.</summary>
    public static bool ProgramInFront()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
        return process == Environment.ProcessId || Array.IndexOf(AlsoFront, (int)process) >= 0;
    }

    /// <summary>Ctrl or Alt are down: the key is a shortcut, not a note.</summary>
    public static bool CommandHeld() => GetAsyncKeyState(VkControl) < 0 || GetAsyncKeyState(VkMenu) < 0;

    /// <summary>What the physical key says in the current layout (Ñ where a US keyboard has a semicolon).</summary>
    public static string Label(int scan)
    {
        uint vk = MapVirtualKeyW((uint)scan, VscToVk);
        uint c = MapVirtualKeyW(vk, VkToChar) & 0x7FFF;
        return c == 0 ? "" : char.ToUpperInvariant((char)c).ToString();
    }

    public void Dispose()
    {
        PostThreadMessageW(threadId, WmQuit, 0, 0);
        thread.Join(500);
        if (Current == this) Current = null;
        up.Dispose();
    }
}
