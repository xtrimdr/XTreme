using System.Runtime.InteropServices;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The listening button of a plugin's window (asked by the user): it waits until a parameter moves in that window and
/// gives it a lane, so it doesn't have to be found in a list that can have thousands. The lane comes once the control is
/// let go: made during the gesture, it would take the control away from the hand that is moving it. It holds where the
/// control ended, so the sound doesn't jump. One plugin listens at a time. Only a track's plugins have the button: lanes
/// are the tracks'.
/// </summary>
public partial class MainWindow
{
    /// <summary>A plugin waiting for a gesture, and the lane's owner.</summary>
    sealed class LaneListen(VstBridge bridge, LiveTrack track, Func<uint, string> target)
    {
        public readonly VstBridge Bridge = bridge;
        public readonly LiveTrack Track = track;
        public readonly Func<uint, string> Target = target;
        public readonly int From = bridge.Touches;
        public int Seen = bridge.Touches;
        public long Moved;
    }

    /// <summary>How long a control has to stay still before its lane comes (a gesture can end without holding a button: the wheel).</summary>
    const int ListenSettleMs = 300;
    const int LeftButton = 0x01, RightButton = 0x02;

    LaneListen? laneListen;

    // not the window's pointer: a plugin's window is another process
    [DllImport("user32")] static extern short GetAsyncKeyState(int key);

    static bool ButtonHeld => (GetAsyncKeyState(LeftButton) & 0x8000) != 0 || (GetAsyncKeyState(RightButton) & 0x8000) != 0;

    /// <summary>The button in a plugin's bar; a project that can't change has no lanes to give.</summary>
    string ListenBarButton => store is { ReadOnly: false } ? $"listen=:{Loc.T("vst.bar.listen")}" : "";

    /// <summary>The button was clicked: this plugin listens, or stops listening.</summary>
    void ToggleLaneListen(VstBridge bridge, LiveTrack track, Func<uint, string> target)
    {
        bool same = laneListen?.Bridge == bridge;
        StopLaneListen();
        if (same) return;
        laneListen = new LaneListen(bridge, track, target);
        bridge.Command("toolbar-state listen=1");
    }

    void StopLaneListen()
    {
        if (laneListen is not { } l) return;
        laneListen = null;
        l.Bridge.Command("toolbar-state listen=0");
    }

    /// <summary>The plugin's window closed, or the plugin is going.</summary>
    void StopLaneListen(VstBridge bridge)
    {
        if (laneListen?.Bridge == bridge) laneListen = null;
    }

    void TickListen()
    {
        if (laneListen is not { } l) return;
        // another project, or the track went
        if (!tracks.Contains(l.Track))
        {
            StopLaneListen();
            return;
        }
        int touches = l.Bridge.Touches;
        if (touches != l.Seen)
        {
            l.Seen = touches;
            l.Moved = Environment.TickCount64;
        }
        if (touches == l.From || restoring || Environment.TickCount64 - l.Moved < ListenSettleMs || ButtonHeld || l.Bridge.Touched is not { } id) return;
        StopLaneListen();
        _ = ListenLane(l, id);
    }

    /// <summary>A plugin's lane starts where its window left the parameter, which only the plugin can say now.</summary>
    async Task ListenLane(LaneListen l, uint id)
    {
        var list = await l.Bridge.GetParamsAsync() ?? [];
        if (list.Count > 0) pluginParams.AddOrUpdate(l.Bridge, list);
        if (list.FirstOrDefault(p => p.Id == id) is not { } p)
        {
            Toast(Loc.T("auto.listen.none", l.Bridge.Name));
            return;
        }
        string target = l.Target(id);
        await Automate(l.Track, target, p.Title, p.Value);
        Toast(Loc.T("auto.listen.done", LaneName(l.Track, new AutomationLane { Target = target, Name = p.Title })));
    }
}
