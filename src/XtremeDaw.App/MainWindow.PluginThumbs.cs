using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The pictures of the plugins (<see cref="PluginThumbs"/>), for instruments and effects alike: the first time a window
/// opens the host takes one on its own, without a word; the button of its bar takes it again, of what shows at that
/// moment, for a plugin that was still on its login or that changed look. The lists catch up through <see cref="PluginScan.Changed"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Plugins whose picture the user asked for: those get an answer, the automatic ones don't.</summary>
    readonly HashSet<VstBridge> thumbAsked = [];

    static string ThumbBarButton => $"shot=:{Loc.T("vst.bar.thumb")}";

    /// <summary>A line of the plugin's process about its picture; true if nothing else has to read it.</summary>
    bool PluginThumbSaid(VstBridge bridge, PluginRef plugin, string line)
    {
        void Take(string command)
        {
            try { bridge.Command($"{command} {PluginThumbs.Prepare(plugin.Path, plugin.ClassId)}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status(Loc.T("vst.thumb.failed", plugin.Name)); }
        }
        switch (line)
        {
            case "editor-open":
                if (PluginThumbs.Due(plugin.Path, plugin.ClassId)) Take("snapshot");
                return false;
            case "toolbar shot":
                thumbAsked.Add(bridge);
                Take("snapshot-now");
                return true;
            case "snapshot-done":
                PluginScan.ThumbsChanged();
                if (thumbAsked.Remove(bridge)) Toast(Loc.T("vst.thumb.done", plugin.Name));
                return true;
            case "snapshot-failed":
                if (thumbAsked.Remove(bridge)) Toast(Loc.T("vst.thumb.failed", plugin.Name));
                return true;
            default:
                return false;
        }
    }
}
