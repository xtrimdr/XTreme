using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Audio;

/// <summary>
/// The live engine's sidechain keys, one per track that some slot listens to (<see cref="SidechainKey"/>), made the first
/// time a chain asks for one (<see cref="For"/>) and kept for the session: a slot that listens again finds the same one.
/// The engine plugs each into its track's chain (<see cref="Attach"/>) and clears them all at every block (<see cref="All"/>,
/// swapped whole for the audio thread). UI thread, but <see cref="All"/>.
/// </summary>
public static class LiveKeys
{
    static readonly Dictionary<string, SidechainKey> keys = [];
    static IReadOnlyList<LiveTrack> tracks = [];
    static volatile SidechainKey[] all = [];

    /// <summary>Every key, for the audio thread to clear before the tracks write.</summary>
    public static SidechainKey[] All => all;

    /// <summary>The key of a track, made if it's new and plugged into the track if it's there.</summary>
    public static SidechainKey? For(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (keys.TryGetValue(id, out var key)) return key;
        key = keys[id] = new SidechainKey(TrackDspChain.MaxBlockSize);
        all = [.. keys.Values];
        foreach (var t in tracks)
            if (t.Track.Id == id) Plug(t, key);
        return key;
    }

    /// <summary>The engine's tracks changed: each key goes to its track's chain, or listens to nothing until it comes back.</summary>
    public static void Attach(IReadOnlyList<LiveTrack> list)
    {
        tracks = list;
        foreach (var k in keys.Values) k.Attached = false;
        foreach (var t in list)
        {
            var key = keys.GetValueOrDefault(t.Track.Id);
            t.Chain.KeyOut = key;
            if (key != null) key.Attached = true;
        }
    }

    static void Plug(LiveTrack t, SidechainKey key)
    {
        t.Chain.KeyOut = key;
        key.Attached = true;
    }
}
