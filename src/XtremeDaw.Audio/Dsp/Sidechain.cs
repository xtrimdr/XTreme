namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// A track's signal for the slots that listen to it (a sidechain): after its own slots and before its pan and fader, so its
/// fader doesn't change how hard it ducks the others and a muted track still triggers them (a "ghost" kick). The source
/// writes each block (<see cref="TrackDspChain.KeyOut"/>) and the listeners read the same frames
/// (<see cref="KeyTap"/>); the sources run before the rest. Preallocated: <see cref="Sets"/> stretches of
/// <see cref="Frames"/> each, one per export buffer set (live, one). Cleared at every block or stretch, so a source that
/// didn't play is silence, never an old block. <see cref="Attached"/>: a track writes it; a listener without one hears its own input.
/// </summary>
public sealed class SidechainKey
{
    public readonly float[] L, R;
    public int Frames { get; }
    public int Sets { get; }

    public SidechainKey(int frames, int sets = 1)
    {
        Frames = frames;
        Sets = sets;
        L = new float[frames * sets];
        R = new float[frames * sets];
    }

    /// <summary>Whether a track is writing it (UI thread sets it, the audio thread reads it).</summary>
    public volatile bool Attached;

    /// <summary>Clears <paramref name="n"/> frames from <paramref name="at"/> before the sources write.</summary>
    public void Begin(int at, int n)
    {
        L.AsSpan(at, n).Clear();
        R.AsSpan(at, n).Clear();
    }

    /// <summary>The source's block, from <paramref name="at"/>.</summary>
    public void Write(int at, ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        l.CopyTo(L.AsSpan(at));
        r.CopyTo(R.AsSpan(at));
    }
}

/// <summary>
/// Where a slot that listens reads its key: the key itself (null = its own input) and the frame of it the next sample is.
/// The chain sets <see cref="Pos"/> before running the slot, and the slot moves it on by what it processed, so an effect
/// inside a <see cref="FxShell"/> that runs it in pieces still reads the right frames.
/// </summary>
public sealed class KeyTap
{
    volatile SidechainKey? source;
    public SidechainKey? Source { get => source; set => source = value; }
    public int Pos;

    /// <summary>The key for the next <paramref name="n"/> frames, and moves on; false (and nothing moves) without one.</summary>
    public bool Take(int n, out ReadOnlySpan<float> l, out ReadOnlySpan<float> r)
    {
        var s = source;
        if (s == null || !s.Attached || Pos < 0 || Pos + n > s.L.Length)
        {
            l = r = default;
            return false;
        }
        l = s.L.AsSpan(Pos, n);
        r = s.R.AsSpan(Pos, n);
        Pos += n;
        return true;
    }
}

/// <summary>A slot that can listen to another track instead of its own input (<see cref="InsertSpec.Key"/>).</summary>
public interface IKeyed
{
    KeyTap Key { get; }
}

/// <summary>The kinds that take a sidechain, and the order a set of tracks runs in so each source comes before its listeners.</summary>
public static class Sidechains
{
    /// <summary>The slot of <paramref name="insert"/> that listens, in or out of its <see cref="FxShell"/>; null if it doesn't.</summary>
    public static IKeyed? Of(ITrackInsert insert) => insert is FxShell { Inner: IKeyed k } ? k : insert as IKeyed;

    /// <summary>
    /// A run order for <paramref name="count"/> tracks: by level, a source before the tracks that listen to it (and before
    /// those that listen to them), the list's order within a level. <paramref name="sources"/>: the track indexes each one
    /// listens to. A loop (two that listen to each other) is cut where it closes: that one hears the other a block late.
    /// </summary>
    public static int[][] Levels(int count, Func<int, IEnumerable<int>> sources)
    {
        var level = new int[count];
        var state = new byte[count]; // 0 = not seen, 1 = being worked out, 2 = done
        int Level(int k)
        {
            if (state[k] == 2) return level[k];
            if (state[k] == 1) return -1;
            state[k] = 1;
            int l = 0;
            foreach (int s in sources(k))
                if (s >= 0 && s < count && s != k && Level(s) is var ls && ls >= 0) l = Math.Max(l, ls + 1);
            state[k] = 2;
            return level[k] = l;
        }
        for (int k = 0; k < count; k++) Level(k);
        return [.. Enumerable.Range(0, count).GroupBy(k => level[k]).OrderBy(g => g.Key).Select(g => g.ToArray())];
    }
}
