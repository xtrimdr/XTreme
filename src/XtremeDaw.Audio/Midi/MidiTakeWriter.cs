using System.Buffers.Binary;

namespace XtremeDaw.Audio.Midi;

/// <summary>
/// Keeps a <see cref="MidiTake"/> on disk while it is recorded, so a crash doesn't cost the notes: the audio thread only
/// fills the take's array and this writer's own thread copies what is new to the file every <see cref="FlushMs"/>,
/// forced to the disk, with the playhead after it (a key still held at the crash ends there). Fixed-size records,
/// appended and never rewritten, without allocating (a collection would cut the audio): whatever the file ends with,
/// <see cref="Read"/> takes every whole record and ignores the rest. If the disk fails the file stops growing and the
/// take goes on in memory (<see cref="Error"/>).
/// </summary>
public sealed class MidiTakeWriter : IDisposable
{
    /// <summary>How much of a take a crash can cost.</summary>
    public const int FlushMs = 250;
    const int HeaderSize = 8, RecordSize = 12;
    const byte KindOn = 1, KindOff = 2, KindPosition = 3;
    static ReadOnlySpan<byte> Magic => "XTMT"u8;

    readonly MidiTake take;
    readonly Func<long> position;
    readonly FileStream fs;
    readonly byte[] buf = new byte[RecordSize * 2048];
    readonly ManualResetEventSlim stop = new();
    readonly Thread thread;
    int written;
    long lastPosition = long.MinValue;
    bool closed;

    /// <summary>Why the file stopped growing; null while all is well.</summary>
    public string? Error { get; private set; }

    /// <param name="position">The playhead in frames, read off the audio thread.</param>
    public MidiTakeWriter(string path, MidiTake take, int sampleRate, Func<long> position)
    {
        this.take = take;
        this.position = position;
        fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
        Magic.CopyTo(buf);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), sampleRate);
        fs.Write(buf, 0, HeaderSize);
        fs.Flush(true);
        thread = new Thread(Run) { IsBackground = true, Name = "MIDI take" };
        thread.Start();
    }

    void Run()
    {
        while (true)
        {
            bool stopping = stop.Wait(FlushMs);
            try
            {
                Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error = e.Message;
                return;
            }
            if (stopping) return;
        }
    }

    void Flush()
    {
        // the count first: the playhead read after it is past every note it covers
        int count = take.Count;
        long pos = position();
        if (count == written && pos == lastPosition) return;
        bool notes = count != written;
        int n = 0;
        for (; written < count; written++)
        {
            if (n == buf.Length)
            {
                fs.Write(buf, 0, n);
                n = 0;
            }
            var (frame, note) = take[written];
            Put(n, frame, note.On ? KindOn : KindOff, note.Pitch, note.Sounds, note.Velocity);
            n += RecordSize;
        }
        if (n == buf.Length)
        {
            fs.Write(buf, 0, n);
            n = 0;
        }
        Put(n, pos, KindPosition, 0, 0, 0);
        fs.Write(buf, 0, n + RecordSize);
        // the program dying doesn't touch what Windows already has; only notes are worth the wait for the disk
        if (notes) fs.Flush(true);
        lastPosition = pos;
    }

    void Put(int at, long frame, byte kind, byte pitch, byte sounds, byte velocity)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(at), frame);
        buf[at + 8] = kind;
        buf[at + 9] = pitch;
        buf[at + 10] = sounds;
        buf[at + 11] = velocity;
    }

    /// <summary>Writes what is left and closes the file (it stays, for the caller to delete once the project has the take).</summary>
    public void Close()
    {
        if (closed) return;
        closed = true;
        stop.Set();
        thread.Join();
        stop.Dispose();
        try
        {
            fs.Dispose();
        }
        catch (IOException e)
        {
            Error ??= e.Message;
        }
    }

    public void Dispose() => Close();

    /// <summary>A take read back from its file: its notes, and the frame the recording had reached.</summary>
    public sealed record Saved(MidiTake Take, long EndFrame, int SampleRate);

    /// <summary>The take in a file that may have been cut anywhere: everything up to the first record that isn't whole and valid.</summary>
    public static Saved Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("No es una toma MIDI");
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var take = new MidiTake();
        long end = long.MinValue;
        for (int at = HeaderSize; at + RecordSize <= data.Length; at += RecordSize)
        {
            long frame = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at));
            byte kind = data[at + 8], pitch = data[at + 9];
            if (kind is < KindOn or > KindPosition || pitch > 127) break; // a power cut can leave zeros at the end
            if (kind == KindPosition)
            {
                end = Math.Max(end, frame);
                continue;
            }
            take.Add(frame, new LiveNote { On = kind == KindOn, Pitch = pitch, Sounds = data[at + 10], Velocity = data[at + 11] });
            end = Math.Max(end, frame + 1);
        }
        return new Saved(take, end, sampleRate);
    }
}
