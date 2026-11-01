using System.Diagnostics;

namespace XtremeDaw.Audio.Midi;

/// <summary>
/// The computer's keyboard as a MIDI keyboard, for when there is no device (asked by the user): the keys go into the same
/// ring as a real one (<see cref="MidiInputs.Push"/>, on <see cref="MidiInputs.VirtualPort"/>), so they sound on the
/// tracks that listen and get recorded with no code of their own. The home row is the white keys and the row above the
/// black ones, from <see cref="BasePitch"/>; Z and X move the octave, C and V the velocity, and either Shift is the
/// sustain pedal (or <see cref="ToggleSustain"/>, which leaves it down), so with Shift the notes still play but Z, X, C and V are shortcuts again. Keys are physical (the row
/// goes on past the L whatever the layout says there). A key held counts once (Windows repeats it) and keeps the note it
/// started when the octave moves. A note held from two places (a key and a click on the strip) ends with the last one.
/// Any thread: the keys come from <see cref="RawKeys"/>, the clicks from the window; <see cref="Changed"/> comes on the
/// caller's, so whoever draws has to go back to its own.
/// </summary>
public sealed class VirtualKeyboard(MidiInputs inputs)
{
    /// <summary>The keys by semitone above <see cref="BasePitch"/>, named as on a US keyboard.</summary>
    const string Layout = "awsedftgyhujkolp;']";
    /// <summary>The physical keys of <see cref="Layout"/>, by scan code.</summary>
    static readonly int[] Scans = [0x1E, 0x11, 0x1F, 0x12, 0x20, 0x21, 0x14, 0x22, 0x15, 0x23, 0x16, 0x24, 0x25, 0x18, 0x26, 0x19, 0x27, 0x28, 0x1B];
    const string Controls = "zxcv";
    static readonly int[] ControlScans = [0x2C, 0x2D, 0x2E, 0x2F];
    const int LeftShift = 0x2A, RightShift = 0x36;
    /// <summary>The pedal's bits: each Shift, and left down by <see cref="ToggleSustain"/>.</summary>
    const int LeftBit = 1, RightBit = 2, LatchBit = 4;
    public const int MinOctave = 0, MaxOctave = 8, DefaultOctave = 4, DefaultVelocity = 100, VelocityStep = 10, MaxTranspose = 6;

    readonly object gate = new();
    readonly Dictionary<char, int> held = [];
    readonly HashSet<char> pressed = [];
    readonly int[] down = new int[128];
    /// <summary>What each held key sent: its end has to be that note, even if the transposition moved meanwhile.</summary>
    readonly int[] sent = new int[128];
    /// <summary>How many held keys are sounding each note, after the transposition.</summary>
    readonly int[] sounding = new int[128];
    int shifts;

    /// <summary>The keyboard's state changed: a note, the octave, the velocity or the pedal.</summary>
    public event Action? Changed;

    public int Octave
    {
        get;
        set
        {
            lock (gate) field = Math.Clamp(value, MinOctave, MaxOctave);
            Changed?.Invoke();
        }
    } = DefaultOctave;

    public int Velocity
    {
        get;
        set
        {
            lock (gate) field = Math.Clamp(value, 1, 127);
            Changed?.Invoke();
        }
    } = DefaultVelocity;

    /// <summary>Semitones added to what the keys send: the keys stay where they are, they just sound higher or lower.</summary>
    public int Transpose
    {
        get;
        set
        {
            lock (gate) field = Math.Clamp(value, -MaxTranspose, MaxTranspose);
            Changed?.Invoke();
        }
    }

    /// <summary>The pitch of the first key (octave 4 = C4 = 60).</summary>
    public int BasePitch => 12 * (Octave + 1);

    /// <summary>The pedal is down: a Shift, or left down.</summary>
    public bool Sustain => Volatile.Read(ref shifts) != 0;

    /// <summary>This note is sounding, wherever the transposition put it.</summary>
    public bool IsSounding(int pitch) => pitch is >= 0 and <= 127 && Volatile.Read(ref sounding[pitch]) > 0;

    /// <summary>How many semitones the keys reach.</summary>
    public static int KeyCount => Layout.Length;

    /// <summary>The physical key that plays this many semitones above <see cref="BasePitch"/>, by scan code; 0 = none.</summary>
    public static int ScanOf(int semitone) => semitone >= 0 && semitone < Scans.Length ? Scans[semitone] : 0;

    /// <summary>The keyboard's name for a scan code; '\0' = not one of its keys.</summary>
    public static char CharOf(int scan)
    {
        int i = Array.IndexOf(Scans, scan);
        if (i >= 0) return Layout[i];
        i = Array.IndexOf(ControlScans, scan);
        return i >= 0 ? Controls[i] : '\0';
    }

    /// <summary>True for <see cref="LeftShift"/> and <see cref="RightShift"/>.</summary>
    public static bool IsShift(int scan) => scan is LeftShift or RightShift;

    /// <summary>A Shift went down or up: the pedal is down while either one is.</summary>
    public void ShiftKey(bool left, bool isDown)
    {
        lock (gate)
        {
            int bit = left ? LeftBit : RightBit;
            SetPedal(isDown ? shifts | bit : shifts & ~bit);
        }
        Changed?.Invoke();
    }

    /// <summary>Leaves the pedal down, or lets it go.</summary>
    public void ToggleSustain()
    {
        lock (gate) SetPedal(shifts ^ LatchBit);
        Changed?.Invoke();
    }

    /// <summary>The pedal up, even if it was left down.</summary>
    public void ReleaseSustain()
    {
        lock (gate) SetPedal(0);
        Changed?.Invoke();
    }

    void SetPedal(int bits)
    {
        bool was = shifts != 0;
        shifts = bits;
        if (was != (bits != 0)) Send(MidiKind.Sustain, 0, bits != 0 ? 127 : 0);
    }

    /// <summary>A key of the keyboard: with Shift held, the ones that aren't notes are shortcuts.</summary>
    public bool IsKey(char key) => Layout.Contains(key) || !Sustain && Controls.Contains(key);

    /// <summary>A key went down. True when it is one of the keyboard's, so it isn't a shortcut.</summary>
    public bool KeyDown(char key)
    {
        if (!IsKey(key)) return false;
        lock (gate)
        {
            if (!pressed.Add(key)) return true;
            switch (key)
            {
                case 'z': Octave--; break;
                case 'x': Octave++; break;
                case 'c': Velocity -= VelocityStep; break;
                case 'v': Velocity += VelocityStep; break;
                default:
                    int pitch = BasePitch + Layout.IndexOf(key);
                    if (pitch > 127) break;
                    held[key] = pitch;
                    Press(pitch);
                    break;
            }
        }
        return true;
    }

    /// <summary>A key went up. True when it was one of the keyboard's.</summary>
    public bool KeyUp(char key)
    {
        lock (gate)
        {
            if (!pressed.Remove(key)) return false;
            if (held.Remove(key, out int pitch)) Release(pitch);
        }
        return true;
    }

    /// <summary>A key: it sounds <see cref="Transpose"/> away.</summary>
    public void Press(int pitch) => Press(pitch, pitch + Transpose);

    /// <summary>The note itself, as the strip shows it: it is already where it sounds.</summary>
    public void PressExact(int pitch) => Press(pitch, pitch);

    void Press(int pitch, int sounds)
    {
        if (pitch is < 0 or > 127) return;
        lock (gate)
        {
            if (down[pitch]++ == 0)
            {
                sent[pitch] = sounds;
                if (sounds is >= 0 and <= 127) sounding[sounds]++;
                Send(MidiKind.NoteOn, sounds, Velocity);
            }
        }
        Changed?.Invoke();
    }

    public void Release(int pitch)
    {
        if (pitch is < 0 or > 127) return;
        lock (gate)
        {
            if (down[pitch] == 0) return;
            if (--down[pitch] == 0) NoteOff(pitch);
        }
        Changed?.Invoke();
    }

    /// <summary>Everything up: the mode went off, or the window lost the keyboard (the key's release would never come).</summary>
    public void AllOff()
    {
        bool any = false;
        lock (gate)
        {
            pressed.Clear();
            held.Clear();
            for (int p = 0; p < down.Length; p++)
            {
                if (down[p] == 0) continue;
                down[p] = 0;
                any = true;
                NoteOff(p);
            }
            // the Shifts' release would never come either; one left down stays
            if ((shifts & (LeftBit | RightBit)) != 0)
            {
                SetPedal(shifts & LatchBit);
                any = true;
            }
        }
        if (any) Changed?.Invoke();
    }

    void NoteOff(int pitch)
    {
        if (sent[pitch] is >= 0 and <= 127) sounding[sent[pitch]]--;
        Send(MidiKind.NoteOff, sent[pitch], 0);
    }

    /// <summary>Queues a message; a note the transposition took out of the MIDI range is not played.</summary>
    void Send(MidiKind kind, int pitch, int velocity)
    {
        if (pitch is < 0 or > 127) return;
        inputs.Push(new MidiEvent
        {
            Ticks = Stopwatch.GetTimestamp(), Port = MidiInputs.VirtualPort, Channel = 1, Kind = kind, Pitch = (byte)pitch, Velocity = (byte)velocity,
        });
    }
}
