using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XtremeDaw.Projects;

/// <summary>
/// One connection of a shared session (#291): messages in frames encrypted and signed with AES-GCM. The keys come
/// from the invitation's key and a random salt from each side (HKDF), one per direction, so no two connections ever
/// reuse a nonce; the nonce is the frame's number, so a frame repeated, dropped or out of order closes the link. A side
/// without the invitation can't produce one frame the other accepts. Writes go one after another from any thread;
/// reads from one thread only. It runs on any stream: a TCP socket on the local network, or a guest's way through the
/// relay over the internet (<see cref="LiveRelay"/>, #320), which never sees inside the frames.
/// </summary>
public sealed class LiveLink : IDisposable
{
    /// <summary>The biggest frame accepted: a whole project with long MIDI fits; a file goes in chunks.</summary>
    public const int MaxFrame = 64 << 20;
    const int SaltBytes = 16, TagBytes = 16, NonceBytes = 12;
    static readonly byte[] Magic = "XTL1"u8.ToArray();

    readonly IDisposable owner;
    readonly Stream stream;
    readonly AesGcm send, receive;
    readonly SemaphoreSlim writing = new(1, 1);
    ulong sent, received;

    LiveLink(Stream stream, IDisposable owner, byte[] sendKey, byte[] receiveKey)
    {
        this.stream = stream;
        this.owner = owner;
        send = new AesGcm(sendKey, TagBytes);
        receive = new AesGcm(receiveKey, TagBytes);
    }

    /// <summary>Both sides send their salt in the clear (with a magic word), then derive the keys.</summary>
    public static Task<LiveLink> Open(TcpClient tcp, byte[] key, bool host, CancellationToken ct)
    {
        tcp.NoDelay = true;
        return Open(tcp.GetStream(), tcp, key, host, ct);
    }

    /// <summary>The same on any stream; <paramref name="owner"/> is what closes it (the socket, or the stream itself).</summary>
    public static async Task<LiveLink> Open(Stream s, IDisposable owner, byte[] key, bool host, CancellationToken ct)
    {
        var mine = RandomNumberGenerator.GetBytes(SaltBytes);
        await s.WriteAsync(Magic.Concat(mine).ToArray(), ct);
        var theirs = new byte[Magic.Length + SaltBytes];
        await s.ReadExactlyAsync(theirs, ct);
        if (!theirs.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new IOException("No es una sesión de XTreme");
        var salt = host ? theirs[Magic.Length..].Concat(mine).ToArray() : mine.Concat(theirs[Magic.Length..]).ToArray();
        byte[] toHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, "xtreme live guest-host"u8.ToArray());
        byte[] toGuest = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, "xtreme live host-guest"u8.ToArray());
        return host ? new LiveLink(s, owner, toGuest, toHost) : new LiveLink(s, owner, toHost, toGuest);
    }

    static byte[] Nonce(ulong n)
    {
        var b = new byte[NonceBytes];
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(4), n);
        return b;
    }

    /// <summary>Sends one message: its type, a JSON header and optional bytes (a piece of a file).</summary>
    public async Task Send(LiveMessage m, CancellationToken ct = default)
    {
        byte[] head = Encoding.UTF8.GetBytes(m.Json ?? "{}");
        int len = 1 + 4 + head.Length + (m.Data?.Length ?? 0);
        var plain = new byte[len];
        plain[0] = (byte)m.Kind;
        BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(1), head.Length);
        head.CopyTo(plain, 5);
        m.Data?.CopyTo(plain, 5 + head.Length);
        var frame = new byte[4 + len + TagBytes];
        BinaryPrimitives.WriteInt32BigEndian(frame, len + TagBytes);
        await writing.WaitAsync(ct);
        try
        {
            send.Encrypt(Nonce(sent++), plain, frame.AsSpan(4, len), frame.AsSpan(4 + len, TagBytes));
            await stream.WriteAsync(frame, ct);
        }
        finally { writing.Release(); }
    }

    /// <summary>The next message; throws when the link closes or a frame doesn't check out.</summary>
    public async Task<LiveMessage> Receive(CancellationToken ct = default)
    {
        var lenBytes = new byte[4];
        await stream.ReadExactlyAsync(lenBytes, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        if (n < TagBytes + 5 || n > MaxFrame) throw new IOException("Mensaje inválido");
        var frame = new byte[n];
        await stream.ReadExactlyAsync(frame, ct);
        int len = n - TagBytes;
        var plain = new byte[len];
        // a wrong key or a changed frame throws here (AuthenticationTagMismatchException)
        receive.Decrypt(Nonce(received++), frame.AsSpan(0, len), frame.AsSpan(len), plain);
        int head = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(1));
        if (head < 0 || 5 + head > len) throw new IOException("Mensaje inválido");
        string json = Encoding.UTF8.GetString(plain, 5, head);
        byte[]? data = len > 5 + head ? plain[(5 + head)..] : null;
        return new((LiveKind)plain[0], json, data);
    }

    public void Dispose()
    {
        try { owner.Dispose(); } catch (Exception e) when (e is SocketException or IOException) { }
        send.Dispose();
        receive.Dispose();
    }
}

/// <summary>The kinds of message of a shared session (<see cref="LiveSession"/>).</summary>
public enum LiveKind : byte
{
    Hello = 1, Welcome = 2, Refuse = 3, Edit = 4, Rev = 5, Peers = 6, GetFile = 7, FileChunk = 8, Missing = 9, Presence = 10,
    /// <summary>The plugins a copy has (to the host), everybody's (from the host), a ping or its answer.</summary>
    Has = 11, Plugins = 12, Ping = 13,
}

/// <summary>One message: its kind, a JSON body and, for a piece of a file, its bytes.</summary>
public sealed record LiveMessage(LiveKind Kind, string? Json, byte[]? Data = null)
{
    public static LiveMessage Of<T>(LiveKind kind, T body, byte[]? data = null) =>
        new(kind, JsonSerializer.Serialize(body, ProjectStore.Json), data);

    public T Body<T>() => JsonSerializer.Deserialize<T>(Json ?? "{}", ProjectStore.Json)!;
}
