using XtremeDaw.Audio;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XtremeDaw.Projects;

/// <summary>
/// A shared session (#291): several people editing the same project at once, each on their own copy, seeing the
/// others' changes as they happen. Decided with the user: peer to peer, no server (on the local network or a VPN);
/// whoever invites is the host, and everybody edits everything; each one listens on their own (transport, solos and
/// the audio device are not shared); only the same version of the app joins.
/// <para>The host keeps the order: every change (a <see cref="LiveDiff"/> patch of <see cref="LiveState"/>) goes
/// through it, gets the next number and goes to everyone, the author too. A guest shows its own changes at once and
/// keeps them as pending until the host sends them back; a change from someone else goes under the pending ones, so
/// on the same value the last one the host got wins. The audio of a new take goes after it was recorded, file by
/// file, through the host.</para>
/// <para>The network runs on its own threads and only queues what arrives; the project is touched only in
/// <see cref="Pump"/> and <see cref="Publish"/>, from the thread that owns it (the UI's). <see cref="Signal"/>
/// says when there is something to pump.</para>
/// </summary>
public sealed class LiveSession : IDisposable
{
    /// <summary>The port the host tries first; if it's taken, any free one (the code says which).</summary>
    public const int DefaultPort = 47610;
    /// <summary>How long a guest waits for each address of the code before trying the next.</summary>
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    /// <summary>A file goes in pieces of this size, so edits keep flowing while it travels.</summary>
    const int ChunkBytes = 512 << 10;
    /// <summary>Partial downloads end like this in media/ until they're whole.</summary>
    const string PartSuffix = ".descargando";

    sealed class Peer(LiveLink link)
    {
        public readonly LiveLink Link = link;
        public string Name = "";
        public bool Welcomed;
        public void Send(LiveMessage m) => _ = SendSafe(m);
        async Task SendSafe(LiveMessage m)
        {
            try { await Link.Send(m); }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    record struct Inbound(Peer? From, LiveMessage? Message, string? Arrived = null, bool Closed = false);

    sealed record Hello(string Name, string App, int Format);
    sealed record Refusal(string Reason, string App, int Format);
    public sealed record Welcome(long Rev, string Host, string Project, List<string> People);
    sealed record EditBody(JsonObject Patch);
    sealed record RevBody(long Rev, string Author, bool Mine, JsonObject Patch);
    sealed record PeopleBody(List<string> People);
    sealed record FileRequest(string Media, string Kind, string Name);
    sealed record FileChunk(string Media, string Kind, string Name, long Offset, long Total, bool Last);

    public bool IsHost { get; }
    public LiveInvite Invite { get; }
    public string Name { get; }
    public string AppVersion { get; }
    /// <summary>Who is in, this copy first; the host's list.</summary>
    public IReadOnlyList<string> People => people;
    /// <summary>Why it ended (the host left, the link dropped, it was closed); null while it goes on.</summary>
    public string? EndReason { get; private set; }
    public bool Ended => EndReason != null;
    public long Rev => rev;
    /// <summary>Raised on a network thread when something arrived: call <see cref="Pump"/> on the project's thread.</summary>
    public event Action? Signal;

    readonly CancellationTokenSource stop = new();
    readonly ConcurrentQueue<Inbound> inbox = new(), edits = new();
    readonly List<Peer> peers = [];
    readonly TcpListener? listener;
    readonly Peer? host;
    List<string> people = [];
    string? dir;
    JsonObject? view;
    readonly List<JsonObject> pending = [];
    long rev;
    /// <summary>Host: who brought each media, to ask them for its files; and who waits for a file that hasn't arrived.</summary>
    readonly Dictionary<string, Peer> authors = [];
    readonly Dictionary<string, List<(Peer Who, FileRequest Req)>> waiting = [];
    readonly HashSet<string> asked = [];

    LiveSession(bool isHost, LiveInvite invite, string name, string app, TcpListener? listener, Peer? host)
    {
        IsHost = isHost;
        Invite = invite;
        Name = name;
        AppVersion = app;
        this.listener = listener;
        this.host = host;
        people = [name];
    }

    /// <summary>Starts hosting: listens and makes the invitation. Call <see cref="Start"/> with the project before pumping.</summary>
    public static LiveSession Host(string name, string appVersion, int port = DefaultPort, IReadOnlyList<IPAddress>? addresses = null)
    {
        TcpListener l;
        try
        {
            l = new TcpListener(IPAddress.Any, port);
            l.Start();
        }
        catch (SocketException)
        {
            l = new TcpListener(IPAddress.Any, 0);
            l.Start();
        }
        int used = ((IPEndPoint)l.LocalEndpoint).Port;
        var invite = LiveInvite.New(used);
        if (addresses != null) invite = invite with { Addresses = addresses };
        var s = new LiveSession(true, invite, name, appVersion, l, null);
        _ = s.AcceptLoop(l);
        return s;
    }

    async Task AcceptLoop(TcpListener l)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    cts.CancelAfter(ConnectTimeout * 2);
                    var link = await LiveLink.Open(tcp, Invite.Key, host: true, cts.Token);
                    var peer = new Peer(link);
                    await ReadLoop(peer);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
                {
                    tcp.Close();
                }
            });
        }
    }

    /// <summary>
    /// Joins the session of <paramref name="invite"/>: tries each address, says who it is and waits for the host's
    /// copy of the project. Throws <see cref="LiveRefusedException"/> when the host says no (another version).
    /// </summary>
    public static async Task<(LiveSession Session, Welcome Welcome)> Join(LiveInvite invite, string name, string appVersion, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var address in invite.Addresses)
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ConnectTimeout);
                await tcp.ConnectAsync(address, invite.Port, cts.Token);
                var link = await LiveLink.Open(tcp, invite.Key, host: false, cts.Token);
                await link.Send(LiveMessage.Of(LiveKind.Hello, new Hello(name, appVersion, Project.CurrentVersion)), ct);
                // the whole project can take a while on a slow link: no timeout but the caller's
                LiveMessage answer;
                try { answer = await link.Receive(ct); }
                catch (System.Security.Cryptography.AuthenticationTagMismatchException) { throw new LiveRefusedException("key", "", 0); }
                if (answer.Kind == LiveKind.Refuse)
                {
                    var r = answer.Body<Refusal>();
                    link.Dispose();
                    throw new LiveRefusedException(r.Reason, r.App, r.Format);
                }
                if (answer.Kind != LiveKind.Welcome) throw new IOException("Respuesta inesperada");
                var w = answer.Body<Welcome>();
                var peer = new Peer(link) { Name = w.Host, Welcomed = true };
                var s = new LiveSession(false, invite, name, appVersion, null, peer) { rev = w.Rev, people = w.People };
                _ = s.ReadLoop(peer);
                return (s, w);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                last = e;
                tcp.Close();
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }
        throw new IOException(Texts.T("live.unreachable", "No se pudo conectar con el anfitrión: revisá el código y la red"), last);
    }

    async Task ReadLoop(Peer peer)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var m = await peer.Link.Receive(stop.Token);
                if (m.Kind == LiveKind.FileChunk) { ReceiveChunk(m); continue; }
                (m.Kind is LiveKind.Edit or LiveKind.Rev ? edits : inbox).Enqueue(new(peer, m));
                Signal?.Invoke();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException
            or System.Security.Cryptography.CryptographicException or JsonException) { }
        inbox.Enqueue(new(peer, null, Closed: true));
        Signal?.Invoke();
    }

    /// <summary>
    /// Ties the session to its project and folder (where files go) and takes its state as the starting point. A guest
    /// asks the host for every file it lacks.
    /// </summary>
    public void Start(Project project, string projectDir)
    {
        dir = projectDir;
        view = LiveState.Capture(project);
        if (!IsHost)
            foreach (var m in project.Media) RequestMissing(m);
    }

    /// <summary>Points the session at the project after it was reopened (same folder): nothing is sent.</summary>
    public void Rebind(Project project) => view = LiveState.Capture(project);

    void RequestMissing(Media m)
    {
        if (dir == null) return;
        foreach (var (kind, path) in new[] { ("audio", m.Audio), ("video", m.Video) })
        {
            if (path == null || Path.IsPathRooted(path)) continue;
            string name = Path.GetFileName(path);
            if (File.Exists(Path.Combine(dir, ProjectStore.MediaDir, name))) continue;
            var req = new FileRequest(m.Id, kind, name);
            if (IsHost)
            {
                if (authors.TryGetValue(m.Id, out var who) && asked.Add(m.Id + "/" + kind)) who.Send(LiveMessage.Of(LiveKind.GetFile, req));
            }
            else if (asked.Add(m.Id + "/" + kind)) host!.Send(LiveMessage.Of(LiveKind.GetFile, req));
        }
    }

    /// <summary>Files asked for that haven't arrived.</summary>
    public int Downloading => asked.Count;

    /// <summary>
    /// Sends this copy's changes since the last time: call it after edits (a guest's go to the host; the host's go to
    /// everybody with the next number). Nothing while there's no change.
    /// </summary>
    public void Publish(Project project)
    {
        if (view == null || Ended) return;
        var now = LiveState.Capture(project);
        var patch = LiveDiff.Diff(view, now);
        view = now;
        if (patch.Count == 0) return;
        if (IsHost)
        {
            rev++;
            Broadcast(patch, null, "");
        }
        else
        {
            pending.Add(patch);
            host!.Send(LiveMessage.Of(LiveKind.Edit, new EditBody(patch)));
        }
    }

    void Broadcast(JsonObject patch, Peer? author, string authorName)
    {
        foreach (var p in peers)
            if (p.Welcomed) p.Send(LiveMessage.Of(LiveKind.Rev, new RevBody(rev, authorName.Length > 0 ? authorName : Name, p == author, patch)));
    }

    /// <summary>What a <see cref="Pump"/> did.</summary>
    public sealed record PumpResult(bool Changed, List<string> Arrived, List<Media> NewMedia, bool PeopleChanged, bool Ended, string? LastAuthor);

    /// <summary>
    /// Does what arrived, on the project's thread. With <paramref name="applyEdits"/> false (recording, a dialog that
    /// holds the project) changes wait their turn; the rest (who joined, files) goes on.
    /// </summary>
    public PumpResult Pump(Project project, UndoManager? undo, bool applyEdits)
    {
        var arrived = new List<string>();
        var added = new List<Media>();
        bool changed = false, peopleChanged = false;
        string? author = null;
        bool wasEnded = Ended;
        while (inbox.TryDequeue(out var item))
        {
            if (item.Arrived != null) { arrived.Add(item.Arrived); continue; }
            if (item.Closed) { peopleChanged |= Left(item.From!); continue; }
            var m = item.Message!;
            switch (m.Kind)
            {
                case LiveKind.Hello when IsHost: peopleChanged |= Greet(item.From!, m.Body<Hello>(), project); break;
                case LiveKind.Peers when !IsHost: people = m.Body<PeopleBody>().People; peopleChanged = true; break;
                case LiveKind.GetFile: Serve(item.From!, m.Body<FileRequest>(), project); break;
                case LiveKind.Missing:
                    var miss = m.Body<FileRequest>();
                    string key = miss.Media + "/" + miss.Kind;
                    asked.Remove(key);
                    if (IsHost && waiting.Remove(key, out var stuck))
                        foreach (var (who, req) in stuck) who.Send(LiveMessage.Of(LiveKind.Missing, req));
                    break;
            }
        }
        foreach (var id in arrived)
        {
            asked.Remove(id);
            if (IsHost && waiting.Remove(id, out var list))
                foreach (var (who, req) in list) Serve(who, req, project);
        }
        if (applyEdits && view != null)
            while (edits.TryDequeue(out var item))
            {
                var m = item.Message!;
                if (IsHost && m.Kind == LiveKind.Edit)
                {
                    var patch = m.Body<EditBody>().Patch;
                    Publish(project);
                    added.AddRange(ApplyPatch(project, undo, patch, []));
                    foreach (var md in added) authors.TryAdd(md.Id, item.From!);
                    rev++;
                    Broadcast(patch, item.From, item.From!.Name);
                    changed = true;
                    author = item.From!.Name;
                }
                else if (!IsHost && m.Kind == LiveKind.Rev)
                {
                    var r = m.Body<RevBody>();
                    rev = r.Rev;
                    if (r.Mine) { if (pending.Count > 0) pending.RemoveAt(0); continue; }
                    Publish(project);
                    added.AddRange(ApplyPatch(project, undo, r.Patch, pending));
                    changed = true;
                    author = r.Author;
                }
            }
        foreach (var md in added) RequestMissing(md);
        return new(changed, [.. arrived.Select(a => a[..a.IndexOf('/')]).Distinct()], added, peopleChanged, Ended && !wasEnded, author);
    }

    /// <summary>Someone else's change under this copy's pending ones; the kept undo states get it too.</summary>
    List<Media> ApplyPatch(Project project, UndoManager? undo, JsonObject patch, List<JsonObject> mine)
    {
        var next = (JsonObject)view!.DeepClone();
        LiveDiff.Apply(next, patch);
        foreach (var p in mine) LiveDiff.Apply(next, p);
        var added = LiveState.Apply(project, next);
        undo?.Rebase(LiveDiff.Without(patch, "media"));
        view = LiveState.Capture(project);
        return added;
    }

    bool Greet(Peer peer, Hello hello, Project project)
    {
        string mine = AppVersion;
        if (hello.App != mine || hello.Format != Project.CurrentVersion)
        {
            peer.Send(LiveMessage.Of(LiveKind.Refuse, new Refusal("version", mine, Project.CurrentVersion)));
            return false;
        }
        // its own changes go first, so the copy it gets matches the number
        Publish(project);
        string name = hello.Name.Trim();
        if (name.Length == 0) name = "?";
        while (people.Contains(name)) name += "'";
        peer.Name = name;
        peers.Add(peer);
        people = [.. people, name];
        peer.Welcomed = true;
        peer.Send(LiveMessage.Of(LiveKind.Welcome, new Welcome(rev, Name, SharedCopy(project), people)));
        SendPeople();
        return true;
    }

    /// <summary>The project as a guest gets it: every file named inside its own media/.</summary>
    static string SharedCopy(Project project)
    {
        var o = JsonSerializer.SerializeToNode(project, ProjectStore.Json)!.AsObject();
        if (o["media"] is JsonArray media)
            foreach (var m in media.OfType<JsonObject>())
            {
                m["audio"] = LiveState.Shared(m["audio"]?.GetValue<string>());
                m["video"] = LiveState.Shared(m["video"]?.GetValue<string>());
                m.Remove("audioBytes");
                m.Remove("videoBytes");
            }
        return o.ToJsonString(ProjectStore.Json);
    }

    void SendPeople()
    {
        foreach (var p in peers) if (p.Welcomed) p.Send(LiveMessage.Of(LiveKind.Peers, new PeopleBody(people)));
    }

    bool Left(Peer peer)
    {
        peer.Link.Dispose();
        if (!IsHost)
        {
            EndReason ??= Texts.T("live.hostLeft", "El anfitrión cerró la sesión en vivo. Tu copia queda guardada.");
            return true;
        }
        if (!peers.Remove(peer)) return false;
        foreach (var (id, who) in authors.Where(a => a.Value == peer).ToList()) authors.Remove(id);
        people = [.. people.Where(n => n != peer.Name)];
        SendPeople();
        return true;
    }

    /// <summary>Sends a file this copy has; the host asks its author for one it lacks, and sends it on when it arrives.</summary>
    void Serve(Peer who, FileRequest req, Project project)
    {
        var m = project.Media.Find(x => x.Id == req.Media);
        string? rel = req.Kind == "video" ? m?.Video : m?.Audio;
        string? path = rel == null || dir == null ? null : Path.IsPathRooted(rel) ? rel : Path.Combine(dir, rel);
        if (path != null && File.Exists(path))
        {
            _ = Task.Run(() => SendFile(who, req, path));
            return;
        }
        string key = req.Media + "/" + req.Kind;
        if (IsHost && m != null && authors.TryGetValue(m.Id, out var author) && author != who)
        {
            if (!waiting.TryGetValue(key, out var list)) waiting[key] = list = [];
            list.Add((who, req));
            if (asked.Add(key)) author.Send(LiveMessage.Of(LiveKind.GetFile, req));
            return;
        }
        who.Send(LiveMessage.Of(LiveKind.Missing, req));
    }

    async Task SendFile(Peer who, FileRequest req, string path)
    {
        try
        {
            await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long total = f.Length, at = 0;
            var buf = new byte[ChunkBytes];
            do
            {
                int n = await f.ReadAsync(buf, stop.Token);
                bool last = at + n >= total || n == 0;
                await who.Link.Send(LiveMessage.Of(LiveKind.FileChunk, new FileChunk(req.Media, req.Kind, req.Name, at, total, last), buf[..n]), stop.Token);
                at += n;
                if (last) break;
            } while (true);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
            who.Send(LiveMessage.Of(LiveKind.Missing, req));
        }
    }

    /// <summary>
    /// Writes a piece of a file, on the network thread: always inside media/, by the file's own name (never a path
    /// from the other side), never over a file that's already there.
    /// </summary>
    void ReceiveChunk(LiveMessage m)
    {
        var c = m.Body<FileChunk>();
        string name = Path.GetFileName(c.Name);
        if (dir == null || name.Length == 0 || name != c.Name) return;
        string media = Path.Combine(dir, ProjectStore.MediaDir);
        string final = Path.Combine(media, name), part = final + PartSuffix;
        try
        {
            Directory.CreateDirectory(media);
            if (File.Exists(final)) { if (c.Last) Arrived(c); return; }
            using (var f = new FileStream(part, c.Offset == 0 ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                if (f.Length != c.Offset) return; // a piece out of place: the next request starts over
                f.Position = c.Offset;
                if (m.Data != null) f.Write(m.Data);
                if (c.Last) f.Flush(flushToDisk: true);
            }
            if (c.Last)
            {
                File.Move(part, final);
                Arrived(c);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void Arrived(FileChunk c)
    {
        inbox.Enqueue(new(null, null, Arrived: c.Media + "/" + c.Kind));
        Signal?.Invoke();
    }

    public void Dispose()
    {
        EndReason ??= Texts.T("live.closed", "Sesión cerrada");
        stop.Cancel();
        listener?.Stop();
        host?.Link.Dispose();
        foreach (var p in peers) p.Link.Dispose();
    }
}

/// <summary>The host said no: another version of the app or of the project format, or the code's key didn't match.</summary>
public sealed class LiveRefusedException(string reason, string app, int format)
    : Exception(reason == "version"
        ? Texts.T("live.otherVersion", "El anfitrión usa otra versión de XTreme ({0}): tienen que tener la misma", app)
        : Texts.T("live.badKey", "El código no es válido para esa sesión"))
{
    public string Reason => reason;
    public string HostApp => app;
    public int HostFormat => format;
}
