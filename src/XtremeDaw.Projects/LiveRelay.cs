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
/// </summary>
public static class LiveRelay
{
    /// <summary>The relay's address; <c>XTREME_RELAY</c> points elsewhere (another deploy, a local <c>wrangler dev</c>).</summary>
    public const string DefaultServer = "https://xtreme-live.martinperez86.workers.dev";
    /// <summary>How long opening the socket may take.</summary>
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>The close code of a room closed from the relay's admin page (#321).</summary>
    internal const int ClosedByAdminCode = 4001;
    /// <summary>Control pings keep the socket alive through proxies when nobody is talking.</summary>
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    public static string Server =>
        Environment.GetEnvironmentVariable("XTREME_RELAY") is { Length: > 0 } s ? s.TrimEnd('/') : DefaultServer;

    static Uri Url(string roomId, string role)
    {
        string s = Server;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "wss://" + s[8..];
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = "ws://" + s[7..];
        return new Uri($"{s}/r/{roomId}/{role}");
    }

    /// <summary>Opens the socket; a room taken (409), missing (404), full (429) or new sessions off (503) comes back as its status, other trouble throws.</summary>
    static async Task<(ClientWebSocket? Socket, int Status)> Open(string roomId, string role, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = KeepAlive;
        ws.Options.CollectHttpResponseDetails = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            await ws.ConnectAsync(Url(roomId, role), cts.Token);
            return (ws, 101);
        }
        catch (WebSocketException) when (ws.HttpStatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict
            or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
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

    /// <summary>A guest's way to the host: the bytes of its link. Throws when there's no session with that code.</summary>
    public static async Task<Stream> Join(string roomId, CancellationToken ct)
    {
        var (ws, status) = await Open(roomId, "join", ct);
        if (ws == null)
            throw new IOException(status == 429
                ? Texts.T("live.roomFull", "La sesión está llena")
                : Texts.T("live.noRoom", "No hay ninguna sesión con ese código: revisalo, o la sesión ya terminó"));
        return new GuestStream(ws);
    }

    /// <summary>
    /// The host's room on the relay; null when that room is taken (another code has to be drawn). Throws when new
    /// sessions are off (from the admin page, #321).
    /// </summary>
    public static async Task<RelayHost?> Host(string roomId, CancellationToken ct)
    {
        var (ws, status) = await Open(roomId, "host", ct);
        if (status == 503) throw new IOException(Texts.T("live.relayOff", "Las sesiones por internet están desactivadas por ahora. Probá más tarde o usá la red local."));
        return ws == null ? null : new RelayHost(ws);
    }

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
        readonly SemaphoreSlim sending = new(1, 1);
        readonly byte[] buffer = new byte[64 << 10];

        protected override async Task<byte[]?> Next(CancellationToken ct)
        {
            try
            {
                var m = await ReadMessage(ws, buffer, ct);
                if (m == null && (int?)ws.CloseStatus == ClosedByAdminCode) ClosedByAdmin = true;
                return m;
            }
            catch (WebSocketException e) { throw new IOException(e.Message, e); }
        }

        protected override async Task Put(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await sending.WaitAsync(ct);
            try
            {
                for (int at = 0; at < data.Length; at += MessageBytes)
                    await ws.SendAsync(data.Slice(at, Math.Min(MessageBytes, data.Length - at)), WebSocketMessageType.Binary, true, ct);
            }
            catch (WebSocketException e) { throw new IOException(e.Message, e); }
            finally { sending.Release(); }
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
/// </summary>
public abstract class RelayStream : Stream
{
    byte[]? current;
    int at;
    bool closed;

    /// <summary>The relay closed it from its admin page (#321), not the other side.</summary>
    public bool ClosedByAdmin { get; protected set; }

    /// <summary>The next message that arrived; null when the connection ended.</summary>
    protected abstract Task<byte[]?> Next(CancellationToken ct);
    protected abstract Task Put(ReadOnlyMemory<byte> data, CancellationToken ct);
    protected abstract void Shut();

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
/// </summary>
public sealed class RelayHost : IDisposable
{
    const byte OpOpen = 1, OpData = 2, OpClose = 3;
    const int HeadBytes = 5;

    readonly ClientWebSocket ws;
    readonly SemaphoreSlim sending = new(1, 1);
    readonly ConcurrentDictionary<uint, Pipe> guests = new();
    readonly Channel<Stream> arrivals = System.Threading.Channels.Channel.CreateUnbounded<Stream>();
    readonly CancellationTokenSource stop = new();

    /// <summary>The relay closed the room from its admin page (#321).</summary>
    public bool ClosedByAdmin { get; private set; }

    internal RelayHost(ClientWebSocket ws)
    {
        this.ws = ws;
        _ = ReadLoop();
    }

    /// <summary>The next guest that arrived; throws when the room closed.</summary>
    public async Task<Stream> Accept(CancellationToken ct)
    {
        try { return await arrivals.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { throw new IOException("closed"); }
    }

    async Task ReadLoop()
    {
        var buffer = new byte[64 << 10];
        try
        {
            while (true)
            {
                var m = await LiveRelay.ReadMessage(ws, buffer, stop.Token);
                if (m == null)
                {
                    ClosedByAdmin = (int?)ws.CloseStatus == LiveRelay.ClosedByAdminCode;
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
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        arrivals.Writer.TryComplete();
        foreach (var g in guests.Values) g.Incoming.Writer.TryComplete();
        guests.Clear();
    }

    async Task Send(byte op, uint id, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await sending.WaitAsync(ct);
        try
        {
            int at = 0;
            do
            {
                int n = Math.Min(LiveRelay.MessageBytes, data.Length - at);
                var m = new byte[HeadBytes + n];
                m[0] = op;
                BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(1), id);
                data.Slice(at, n).CopyTo(m.AsMemory(HeadBytes));
                await ws.SendAsync(m, WebSocketMessageType.Binary, true, ct);
                at += n;
            } while (at < data.Length);
        }
        catch (WebSocketException e) { throw new IOException(e.Message, e); }
        finally { sending.Release(); }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { _ = ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => ws.Dispose()); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { ws.Dispose(); }
    }

    /// <summary>One guest, as seen by the host.</summary>
    sealed class Pipe(RelayHost room, uint id) : RelayStream
    {
        public readonly Channel<byte[]> Incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });

        protected override async Task<byte[]?> Next(CancellationToken ct)
        {
            try { return await Incoming.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }

        protected override Task Put(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!room.guests.ContainsKey(id)) throw new IOException("closed");
            return room.Send(OpData, id, data, ct);
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
