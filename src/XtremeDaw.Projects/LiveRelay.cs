using XtremeDaw.Audio;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace XtremeDaw.Projects;

/// <summary>
/// The relay of shared sessions over the internet (#320, decided with the user: a Cloudflare Worker, <c>relay/</c>):
/// the host and each guest open a WebSocket to the room named by the short code (<see cref="LiveInvite.RoomId"/>),
/// and the relay passes the bytes along. It only carries them: the session's own link (<see cref="LiveLink"/>) goes
/// on top, encrypted end to end, the same as on the local network. The host's single socket carries every guest,
/// each message tagged with the guest's number (<see cref="RelayHost"/>).
/// <para>Protocol 2 (#1): the host's socket comes back after a cut (<see cref="RelayHost"/>), and each connection
/// numbers its messages so what got lost on the way is sent again (<see cref="RelayStream"/>). The relay checks that
/// both sides speak the same protocol, and ends a session that runs too long or passes too much traffic.</para>
/// </summary>
public static class LiveRelay
{
    /// <summary>The relay's address; <c>XTREME_RELAY</c> points elsewhere (another deploy, a local <c>wrangler dev</c>).</summary>
    public const string DefaultServer = "https://xtreme-live.martinperez86.workers.dev";
    /// <summary>The relay protocol this copy speaks (the relay's <c>PROTOCOL</c>).</summary>
    public const int Protocol = 2;
    /// <summary>How long opening the socket may take.</summary>
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>The close codes of a room closed from the relay's admin page (#321), too long or with too much traffic (#1).</summary>
    internal const int ClosedByAdminCode = 4001, TooLongCode = 4002, TooMuchCode = 4003;
    /// <summary>
    /// Pings that keep the socket alive through proxies and find a dead one: without a pong in
    /// <see cref="KeepAliveTimeout"/>, the socket is given up (the host then reconnects).
    /// </summary>
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromSeconds(10);

    public static string Server =>
        Environment.GetEnvironmentVariable("XTREME_RELAY") is { Length: > 0 } s ? s.TrimEnd('/') : DefaultServer;

    static Uri Url(string roomId, string role, string? resume)
    {
        string s = Server;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "wss://" + s[8..];
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = "ws://" + s[7..];
        return new Uri($"{s}/r/{roomId}/{role}?v={Protocol}" + (resume != null ? "&resume=" + Uri.EscapeDataString(resume) : ""));
    }

    /// <summary>
    /// Opens the socket; a room taken (409), missing (404), full or too many rooms (429), another version (426) or new
    /// sessions off or the relay full (503) comes back as its status, other trouble throws.
    /// </summary>
    internal static async Task<(ClientWebSocket? Socket, int Status)> Open(string roomId, string role, CancellationToken ct, string? resume = null)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = KeepAlive;
        ws.Options.KeepAliveTimeout = KeepAliveTimeout;
        ws.Options.CollectHttpResponseDetails = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            await ws.ConnectAsync(Url(roomId, role, resume), cts.Token);
            return (ws, 101);
        }
        catch (WebSocketException) when ((int)ws.HttpStatusCode is 404 or 409 or 426 or 429 or 503)
        {
            int status = (int)ws.HttpStatusCode;
            ws.Dispose();
            return (null, status);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new IOException(Texts.T("live.relayDown", "No se pudo conectar con el servidor de sesiones: revisá tu conexión a internet"), e);
        }
    }

    static string OtherVersion() =>
        Texts.T("live.relayVersion", "Esta sesión usa otra versión de XTreme: actualizá a la última para entrar");

    /// <summary>A guest's way to the host: the bytes of its link. Throws when there's no session with that code.</summary>
    public static async Task<Stream> Join(string roomId, CancellationToken ct)
    {
        var (ws, status) = await Open(roomId, "join", ct);
        if (ws == null)
            throw new IOException(status switch
            {
                429 => Texts.T("live.roomFull", "La sesión está llena"),
                426 => OtherVersion(),
                _ => Texts.T("live.noRoom", "No hay ninguna sesión con ese código: revisalo, o la sesión ya terminó"),
            });
        return new GuestStream(ws);
    }

    /// <summary>
    /// The host's room on the relay; null when that room is taken (another code has to be drawn). Throws when new
    /// sessions are off (from the admin page, #321), the relay is full or this connection has too many rooms (#1).
    /// </summary>
    public static async Task<RelayHost?> Host(string roomId, CancellationToken ct)
    {
        var (ws, status) = await Open(roomId, "host", ct);
        if (status == 503) throw new IOException(Texts.T("live.relayOff", "Las sesiones por internet están desactivadas por ahora. Probá más tarde o usá la red local."));
        if (status == 429) throw new IOException(Texts.T("live.relayTooMany", "Abriste demasiadas sesiones por internet seguidas: cerrá alguna o probá en un rato"));
        if (status == 426) throw new IOException(OtherVersion());
        return ws == null ? null : new RelayHost(ws, roomId);
    }

    /// <summary>Why the relay ended a session, from its close code; null when it wasn't the relay's doing.</summary>
    internal static string? Reason(int? code) => code switch
    {
        ClosedByAdminCode => Texts.T("live.adminClosed", "El servidor de sesiones cerró esta sesión. Tu copia queda guardada."),
        TooLongCode => Texts.T("live.relayTooLong", "La sesión llegó al límite de 12 horas del servidor. Tu copia queda guardada: podés abrir otra."),
        TooMuchCode => Texts.T("live.relayTooMuch", "La sesión pasó el límite de tráfico del servidor. Tu copia queda guardada."),
        _ => null,
    };

    /// <summary>Reads one whole message; null when the socket closed.</summary>
    internal static async Task<byte[]?> ReadMessage(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return ms.ToArray();
    }

    /// <summary>The piece a message carries at most: well under the relay's limit, so a big frame goes in several.</summary>
    internal const int MessageBytes = 256 << 10;

    /// <summary>A guest's side: the socket's messages as a stream of bytes.</summary>
    sealed class GuestStream(ClientWebSocket ws) : RelayStream
    {
        readonly SemaphoreSlim socket = new(1, 1);
        readonly byte[] buffer = new byte[64 << 10];

        protected override async Task<byte[]?> Receive(CancellationToken ct)
        {
            try
            {
                var m = await ReadMessage(ws, buffer, ct);
                if (m == null) CloseCode = (int?)ws.CloseStatus;
                return m;
            }
            catch (WebSocketException e) { throw new IOException(e.Message, e); }
        }

        protected override async Task Transmit(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            await socket.WaitAsync(ct);
            try { await ws.SendAsync(message, WebSocketMessageType.Binary, true, ct); }
            catch (WebSocketException e) { throw new IOException(e.Message, e); }
            finally { socket.Release(); }
        }

        protected override void Shut()
        {
            try { _ = ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => ws.Dispose()); }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { ws.Dispose(); }
        }
    }
}

/// <summary>
/// A connection through the relay as a <see cref="Stream"/>, so <see cref="LiveLink"/> runs on it as on a TCP socket.
/// What comes in is whole messages; reads hand them out in whatever pieces are asked. Only async reads and writes.
/// <para>Each message goes numbered and is kept until the other side confirms it (#1): when the host's socket comes
/// back after a cut, both sides send again what wasn't confirmed and each drops what it already had, so the link on
/// top never sees a gap. A message is <c>[kind:1][n:8, big endian][bytes]</c>: kind 0 = bytes number n, 1 = got
/// everything up to n, 2 = got everything up to n, send the rest again.</para>
/// </summary>
public abstract class RelayStream : Stream
{
    const byte KindData = 0, KindAck = 1, KindResend = 2;
    const int HeadBytes = 9;
    /// <summary>The other side confirms after this many messages or bytes, whichever comes first.</summary>
    const int AckEvery = 16, AckBytes = 1 << 20;

    byte[]? current;
    int at;
    bool closed;
    /// <summary>Numbers messages and sends them in that order.</summary>
    readonly SemaphoreSlim sending = new(1, 1);
    /// <summary>Sent and not confirmed yet, in order; locked.</summary>
    readonly Queue<(ulong Seq, byte[] Message)> unacked = new();
    ulong nextSeq = 1;
    /// <summary>The last message received in order, and the last one confirmed to the other side (read thread only).</summary>
    ulong received, acked;
    long sinceAck;

    /// <summary>The relay's close code when it ended the connection (<see cref="LiveRelay.Reason"/>).</summary>
    public int? CloseCode { get; protected set; }

    /// <summary>The relay closed it from its admin page (#321), not the other side.</summary>
    public bool ClosedByAdmin => CloseCode == LiveRelay.ClosedByAdminCode;

    /// <summary>The next message off the wire; null when the connection ended.</summary>
    protected abstract Task<byte[]?> Receive(CancellationToken ct);
    /// <summary>Puts one message on the wire.</summary>
    protected abstract Task Transmit(ReadOnlyMemory<byte> message, CancellationToken ct);
    protected abstract void Shut();

    /// <summary>Messages sent and not confirmed yet (xtlab).</summary>
    internal int Unconfirmed { get { lock (unacked) return unacked.Count; } }

    static byte[] Control(byte kind, ulong n)
    {
        var m = new byte[HeadBytes];
        m[0] = kind;
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(1), n);
        return m;
    }

    /// <summary>The next bytes that arrived in order; null when the connection ended.</summary>
    async Task<byte[]?> Next(CancellationToken ct)
    {
        while (true)
        {
            var m = await Receive(ct);
            if (m == null) return null;
            if (m.Length < HeadBytes) continue;
            ulong n = BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(1));
            switch (m[0])
            {
                case KindData:
                    // a repeat (sent again after a cut) or one past a gap (the rest comes again): dropped
                    if (n != received + 1) continue;
                    received = n;
                    sinceAck += m.Length;
                    if (received - acked >= AckEvery || sinceAck >= AckBytes) Confirm(KindAck);
                    return m[HeadBytes..];
                case KindAck:
                    Drop(n);
                    break;
                case KindResend:
                    Drop(n);
                    _ = SendAgain();
                    break;
            }
        }
    }

    void Drop(ulong upTo)
    {
        lock (unacked) while (unacked.Count > 0 && unacked.Peek().Seq <= upTo) unacked.Dequeue();
    }

    void Confirm(byte kind)
    {
        acked = received;
        sinceAck = 0;
        _ = SendControl(Control(kind, received));
    }

    async Task SendControl(byte[] m)
    {
        try { await Transmit(m, CancellationToken.None); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    /// <summary>
    /// The connection is back after a cut (the host's side): asks the other side for what didn't arrive and sends
    /// again everything it hasn't confirmed.
    /// </summary>
    internal async Task Resume()
    {
        await SendControl(Control(KindResend, received));
        await SendAgain();
    }

    async Task SendAgain()
    {
        await sending.WaitAsync();
        try
        {
            (ulong, byte[])[] all;
            lock (unacked) all = [.. unacked];
            foreach (var (_, m) in all) await Transmit(m, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        finally { sending.Release(); }
    }

    async Task Put(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await sending.WaitAsync(ct);
        try
        {
            for (int pos = 0; pos < data.Length; pos += LiveRelay.MessageBytes)
            {
                int n = Math.Min(LiveRelay.MessageBytes, data.Length - pos);
                var m = new byte[HeadBytes + n];
                ulong seq = nextSeq++;
                BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(1), seq);
                data.Slice(pos, n).CopyTo(m.AsMemory(HeadBytes));
                lock (unacked) unacked.Enqueue((seq, m));
                await Transmit(m, ct);
            }
        }
        finally { sending.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default)
    {
        while (current == null || at >= current.Length)
        {
            if (closed) return 0;
            current = await Next(ct);
            at = 0;
            if (current == null) { closed = true; return 0; }
        }
        int n = Math.Min(dest.Length, current.Length - at);
        current.AsMemory(at, n).CopyTo(dest);
        at += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => new(Put(data, ct));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Put(buffer.AsMemory(offset, count), ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed)
        {
            closed = true;
            Shut();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The host's room on the relay: one socket for every guest. Each message says which guest it's from or for and
/// whether one arrived, sent bytes or left (the relay's <c>op</c>); <see cref="Accept"/> hands out each new guest as
/// its own <see cref="RelayStream"/>. When the socket ends, every guest's stream ends with it.
/// <para>If the socket drops (the network, not the relay closing the room), it opens again with the room's token
/// for up to <see cref="Grace"/> (#1): the guests wait in the room, writes wait for the socket, and each guest's
/// stream sends again what didn't get through (<see cref="RelayStream.Resume"/>).</para>
/// </summary>
public sealed class RelayHost : IDisposable
{
    const byte OpOpen = 1, OpData = 2, OpClose = 3, OpToken = 4, OpResumed = 5;
    const int HeadBytes = 5;
    /// <summary>How long it keeps trying to come back: a bit less than the relay waits (its <c>HOST_GRACE_MS</c>).</summary>
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(28);
    /// <summary>The pause between tries to come back.</summary>
    static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    readonly string roomId;
    ClientWebSocket ws;
    /// <summary>Done while the socket is up; a new one while it comes back; failed when it won't.</summary>
    volatile TaskCompletionSource online = Done();
    string? token;
    readonly SemaphoreSlim sending = new(1, 1);
    readonly ConcurrentDictionary<uint, Pipe> guests = new();
    readonly Channel<Stream> arrivals = System.Threading.Channels.Channel.CreateUnbounded<Stream>();
    readonly CancellationTokenSource stop = new();
    volatile bool disposed;
    /// <summary>How long saying goodbye to the relay may take.</summary>
    static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The relay's close code when it ended the room (<see cref="LiveRelay.Reason"/>).</summary>
    public int? CloseCode { get; private set; }

    /// <summary>The relay closed the room from its admin page (#321).</summary>
    public bool ClosedByAdmin => CloseCode == LiveRelay.ClosedByAdminCode;

    /// <summary>The socket dropped and it's trying to come back.</summary>
    public bool Reconnecting => !online.Task.IsCompleted;

    /// <summary>How many times it came back after a cut.</summary>
    public int Resumes { get; private set; }

    static TaskCompletionSource Done()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    internal RelayHost(ClientWebSocket ws, string roomId)
    {
        this.ws = ws;
        this.roomId = roomId;
        _ = ReadLoop();
    }

    /// <summary>The next guest that arrived; throws when the room closed.</summary>
    public async Task<Stream> Accept(CancellationToken ct)
    {
        try { return await arrivals.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { throw new IOException("closed"); }
    }

    /// <summary>Cuts the socket without closing the room, as a network drop does (xtlab).</summary>
    public void Cut() => ws.Abort();

    async Task ReadLoop()
    {
        var buffer = new byte[64 << 10];
        while (true)
        {
            try
            {
                while (true)
                {
                    var m = await LiveRelay.ReadMessage(ws, buffer, stop.Token);
                    if (m == null)
                    {
                        CloseCode = (int?)ws.CloseStatus;
                        break;
                    }
                    if (m.Length < HeadBytes) continue;
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(1));
                    switch (m[0])
                    {
                        case OpOpen:
                            var pipe = new Pipe(this, id);
                            guests[id] = pipe;
                            arrivals.Writer.TryWrite(pipe);
                            break;
                        case OpData:
                            if (guests.TryGetValue(id, out var g)) g.Incoming.Writer.TryWrite(m[HeadBytes..]);
                            break;
                        case OpClose:
                            if (guests.TryRemove(id, out var gone)) gone.Incoming.Writer.TryComplete();
                            break;
                        case OpToken:
                            token = System.Text.Encoding.ASCII.GetString(m, HeadBytes, m.Length - HeadBytes);
                            break;
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            // a close the relay meant (the room ended) or this copy's own: over; anything else is a cut
            if (stop.IsCancellationRequested || disposed || token == null || CloseCode is (int)WebSocketCloseStatus.NormalClosure
                or (int)WebSocketCloseStatus.EndpointUnavailable or >= 4000 || !await Reconnect()) break;
        }
        online.TrySetException(new IOException("closed"));
        arrivals.Writer.TryComplete();
        foreach (var g in guests.Values) g.Incoming.Writer.TryComplete();
        guests.Clear();
    }

    /// <summary>Opens the room's socket again with its token; true when it's back and the guests resumed.</summary>
    async Task<bool> Reconnect()
    {
        online = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CloseCode = null;
        var until = DateTime.UtcNow + Grace;
        while (DateTime.UtcNow < until && !stop.IsCancellationRequested && !disposed)
        {
            try
            {
                var (back, status) = await LiveRelay.Open(roomId, "host", stop.Token, token);
                if (back == null) return false; // the room is gone (it waited too long, or was closed)
                var first = await LiveRelay.ReadMessage(back, new byte[64 << 10], stop.Token);
                if (first is not { Length: >= HeadBytes } || first[0] != OpResumed) { back.Dispose(); return false; }
                var still = new HashSet<uint>();
                for (int i = HeadBytes; i + 4 <= first.Length; i += 4) still.Add(BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(i)));
                var old = ws;
                ws = back;
                old.Dispose();
                foreach (var (id, g) in guests)
                    if (!still.Contains(id) && guests.TryRemove(id, out _)) g.Incoming.Writer.TryComplete();
                Resumes++;
                online.TrySetResult();
                foreach (var g in guests.Values) _ = g.Resume();
                return true;
            }
            catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            try { await Task.Delay(RetryEvery, stop.Token); }
            catch (OperationCanceledException) { break; }
        }
        return false;
    }

    async Task Send(byte op, uint id, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // while the socket comes back, writes wait for it (or fail with it)
        try { await online.Task.WaitAsync(ct); }
        catch (IOException) { throw new IOException("closed"); }
        await sending.WaitAsync(ct);
        try
        {
            var m = new byte[HeadBytes + data.Length];
            m[0] = op;
            BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(1), id);
            data.CopyTo(m.AsMemory(HeadBytes));
            await ws.SendAsync(m, WebSocketMessageType.Binary, true, ct);
        }
        // a cut while sending: the message stays unconfirmed and goes again when the socket is back
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException && token != null && !stop.IsCancellationRequested) { }
        catch (WebSocketException e) { throw new IOException(e.Message, e); }
        finally { sending.Release(); }
    }

    /// <summary>
    /// Closes the room for everybody. The close goes before stopping the reads: a read cancelled aborts the socket,
    /// which the relay would take for a cut and keep the room waiting.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        _ = Close();
    }

    async Task Close()
    {
        try
        {
            using var cts = new CancellationTokenSource(CloseTimeout);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        stop.Cancel();
        ws.Dispose();
    }

    /// <summary>One guest, as seen by the host.</summary>
    sealed class Pipe(RelayHost room, uint id) : RelayStream
    {
        public readonly Channel<byte[]> Incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });

        protected override async Task<byte[]?> Receive(CancellationToken ct)
        {
            try { return await Incoming.Reader.ReadAsync(ct); }
            catch (ChannelClosedException)
            {
                CloseCode = room.CloseCode;
                return null;
            }
        }

        protected override Task Transmit(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            if (!room.guests.ContainsKey(id)) throw new IOException("closed");
            return room.Send(OpData, id, message, ct);
        }

        protected override void Shut()
        {
            if (!room.guests.TryRemove(id, out _)) return;
            _ = SendClose();
        }

        async Task SendClose()
        {
            try { await room.Send(OpClose, id, ReadOnlyMemory<byte>.Empty, CancellationToken.None); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        }
    }
}
