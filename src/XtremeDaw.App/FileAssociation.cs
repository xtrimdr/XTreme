using System.Runtime.InteropServices;
using Microsoft.Win32;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The .xtp association with Windows (asked by the user, #254): the installer registers it for the installed exe, and
/// "Asociar archivos .xtp a XTreme" in Configuración › Asociación de archivos registers the running one, so a copy that was never installed (a test copy, a
/// portable folder) also opens with a double click. It writes the user's classes (HKCU, no admin needed), which take
/// precedence over the machine's that an admin install wrote; the same keys as installer\XTreme.iss. A program the user
/// picked with "Abrir con" (Explorer's UserChoice) wins over both, and Windows does not let a program change it.
/// </summary>
static class FileAssociation
{
    /// <summary>The installer's ProgId and type name (installer\XTreme.iss): both keep the same entry.</summary>
    const string ProgId = "XTreme.Project";
    const string TypeName = "XTreme Project";
    const string UserChoiceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ProjectStore.Extension + @"\UserChoice";
    const int ShcneAssocChanged = 0x08000000;

    public enum State { None, This, Other, Chosen }

    public static string Exe => Environment.ProcessPath ?? "";

    /// <summary>What a double click on a .xtp opens today, and with which exe when it is another one.</summary>
    public static (State State, string? Exe) Read()
    {
        try
        {
            using (var choice = Registry.CurrentUser.OpenSubKey(UserChoiceKey))
                if (choice?.GetValue("ProgId") is string chosen && chosen.Length > 0 && !chosen.Equals(ProgId, StringComparison.OrdinalIgnoreCase))
                    return (State.Chosen, CommandExe(chosen));
            using var ext = Registry.ClassesRoot.OpenSubKey(ProjectStore.Extension);
            if (ext?.GetValue("") is not string prog || prog.Length == 0) return (State.None, null);
            if (CommandExe(prog) is not { } exe) return (State.None, null);
            return (SamePath(exe, Exe) ? State.This : State.Other, exe);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (State.None, null);
        }
    }

    /// <summary>Makes .xtp open with this exe for the current user; false (and why) when the registry refused.</summary>
    public static bool Register(out string? error)
    {
        error = null;
        string exe = Exe;
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var k = classes.CreateSubKey(ProjectStore.Extension)) k.SetValue("", ProgId);
            using (var k = classes.CreateSubKey(ProjectStore.Extension + @"\OpenWithProgids")) k.SetValue(ProgId, "");
            using (var k = classes.CreateSubKey(ProgId)) k.SetValue("", TypeName);
            using (var k = classes.CreateSubKey(ProgId + @"\DefaultIcon")) k.SetValue("", exe + ",0");
            using (var k = classes.CreateSubKey(ProgId + @"\shell\open\command")) k.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            error = e.Message;
            return false;
        }
        // Explorer refreshes the icons and the "Abrir" of .xtp files
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
        return true;
    }

    /// <summary>The exe a ProgId's "open" runs, from its command line (quoted or not).</summary>
    static string? CommandExe(string progId)
    {
        using var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
        if (cmd?.GetValue("") is not string c || (c = c.Trim()).Length == 0) return null;
        if (c[0] == '"') return c.IndexOf('"', 1) is int end and > 1 ? c[1..end] : null;
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
