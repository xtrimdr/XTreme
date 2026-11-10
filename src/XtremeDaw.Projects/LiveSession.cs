using XtremeDaw.Audio;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XtremeDaw.Projects;

/// <summary>
/// A shared session (#291): several people editing the same project at once, each on their own copy, seeing the
/// others' changes as they happen. Decided with the user: on the local network (or a VPN) peer to peer, with no
/// server; over the internet (#320) through the relay (<see cref="LiveRelay"/>), which only passes the encrypted
/// bytes along, so the rest works the same either way. Whoever invites is the host, and everybody edits everything; each one listens on their own (transport, solos and
/// the audio device are not shared); only the same version of the app joins.
/// <para>The host keeps the order: every change (a <see cref="LiveDiff"/> patch of <see cref="LiveState"/>) goes
/// through it, gets the next number and goes to everyone, the author too. A guest shows its own changes at once and
/// keeps them as pending until the host sends them back; a change from someone else goes under the pending ones, so
/// on the same value the last one the host got wins. The audio of a new take goes after it was recorded, file by
/// file, through the host.</para>
/// <para>The network runs on its own threads and only queues what arrives; the project is touched only in
/// <see cref="Pump"/> and <see cref="Publish"/>, from the thread that owns it (the UI's). <see cref="Signal"/>
/// says when there is something to pump.</para>
/// <para>Who is who (asked by the user): the host gives each person their name in the session and a color
/// (<see cref="ColorOf"/>, an index the window turns into one of its own), the same on every copy. Where each one is
/// goes apart from the changes, as <see cref="LivePresence"/> (<see cref="Share"/>, <see cref="Presence"/>), and what
/// each one is editing is read from their changes (<see cref="EditingBy"/>); none of it is saved. Their changes go
/// into the undo's list by name (<see cref="UndoManager.Remote"/>), and a media brought in a session says by whom
/// (<see cref="Media.AddedBy"/>).</para>
/// <para>Plugins (the user's call): only what everybody has is used. Each copy tells the host which plugins it has
/// (its <see cref="Catalog"/> and the project's that <see cref="HasPlugin"/>), the host hands everybody's lists to
/// all, and <see cref="LackingIt"/> says who lacks one: the window doesn't let it be picked, and keeps one already in
/// the project off on every copy (<see cref="LivePlugins"/>). The link's round trip is measured both ways
/// (<see cref="LatencyOf"/>), on the network threads.</para>
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
    /// <summary>The least time between two presences sent (#291): a playhead goes at most ~15 times a second.</summary>
    const long PresenceGapMs = 66;
    /// <summary>How long what someone changed shows as being edited by them after their last change.</summary>
    public const long EditHoldMs = 1500;
    /// <summary>How often each link is pinged to measure its round trip.</summary>
    static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(1);
    /// <summary>A round trip older than this (no answer since) shows as unknown.</summary>
    const long LatencyStaleMs = 5000;

    sealed class Peer(LiveLink link)
    {
        public readonly LiveLink Link = link;
        public string Name = "";
        public bool Welcomed;
        /// <summary>The last round trip (Stopwatch ticks) and when it came (Environment.TickCount64); written by the network thread.</summary>
        long rtt = -1, rttAt;
        public void Measured(long ticks)
        {
            Interlocked.Exchange(ref rtt, ticks);
            Interlocked.Exchange(ref rttAt, Environment.TickCount64);
        }
        /// <summary>The round trip in ms, or null when none came lately.</summary>
        public double? RttMs
        {
            get
            {
                long t = Interlocked.Read(ref rtt);
                if (t < 0 || Environment.TickCount64 - Interlocked.Read(ref rttAt) > LatencyStaleMs) return null;
                return t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }
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
    /// <summary>What a guest gets on joining; <see cref="You"/> is its name in the session (another if its own was taken).</summary>
    public sealed record Welcome(long Rev, string Host, string Project, List<string> People, string? You = null, Dictionary<string, int>? Colors = null);
    sealed record EditBody(JsonObject Patch);
    sealed record RevBody(long Rev, string Author, bool Mine, JsonObject Patch);
    sealed record PeopleBody(List<string> People, Dictionary<string, int>? Colors = null);
    sealed record FileRequest(string Media, string Kind, string Name);
    sealed record FileChunk(string Media, string Kind, string Name, long Offset, long Total, bool Last);
    sealed record HasBody(List<string> Plugins);
    sealed record PluginsBody(Dictionary<string, List<string>> Has);
    /// <summary>A ping (<see cref="Back"/> false) or its answer, with the sender's clock (Stopwatch ticks).</summary>
    sealed record PingBody(long T, bool Back);

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
    /// <summary>The host's room on the relay, over the internet.</summary>
    RelayHost? relay;
    /// <summary>The host's room on the relay, over the internet (null otherwise): whether it's coming back after a cut (#1).</summary>
    public RelayHost? Relay => relay;
    /// <summary>A guest's way through the relay, over the internet.</summary>
    RelayStream? relayed;
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
    /// <summary>What came and the size of each file on its way, by <see cref="Key"/>; written by the network thread.</summary>
    readonly ConcurrentDictionary<string, (long Got, long Total)> progress = new();
    /// <summary>Each person's color by name: the host's, kept after they leave so they get it back if they return; a guest's, as the host sent it.</summary>
    Dictionary<string, int> colors = [];
    /// <summary>Where the others are, by name; never this copy's own.</summary>
    readonly Dictionary<string, LivePresence> presence = [];
    /// <summary>What each of the others changed last (tracks, folders, buses, clips) and when (<see cref="Environment.TickCount64"/>).</summary>
    readonly Dictionary<string, (HashSet<string> Ids, long At)> touched = [];
    LivePresence? shared;
    long sharedAt = long.MinValue / 2;
    /// <summary>The media this copy already has: one that isn't here on <see cref="Publish"/> was brought by this person.</summary>
    readonly HashSet<string> knownMedia = [];
    /// <summary>The links to ping, swapped whole (the ping runs on its own thread).</summary>
    volatile Peer[] pinged = [];
    /// <summary>The plugins this copy has as last told (sorted keys, joined), so the same list isn't sent again.</summary>
    string? hasSent;
    /// <summary>The plugins each of the others has (<see cref="LivePlugins.Key"/>), by name; who hasn't said yet isn't there.</summary>
    Dictionary<string, HashSet<string>> has = [];
    /// <summary>The project's plugins as of the last change (their files).</summary>
    List<string> projectPlugins = [];

    /// <summary>Whether a plugin file is on this computer (<see cref="LivePlugins.Installed"/>); xtlab plays someone without one.</summary>
    public Func<string, bool> HasPlugin { get; set; } = LivePlugins.Installed;

    /// <summary>The plugin files this copy found (its scan), besides the project's: what the others may pick.</summary>
    public Func<IEnumerable<string>>? Catalog { get; set; }

    /// <summary>
    /// Who in the session can't load the plugin at <paramref name="path"/>, this copy included; empty = everybody has it.
    /// Someone who hasn't told what they have yet isn't counted.
    /// </summary>
    public List<string> LackingIt(string path)
    {
        var list = new List<string>();
        string key = LivePlugins.Key(path);
        foreach (var n in people)
            if (n == Name ? !HasPlugin(path) : has.TryGetValue(n, out var set) && !set.Contains(key)) list.Add(n);
        return list;
    }

    /// <summary>Someone lacks a plugin of the project (so it is off for everybody).</summary>
    public bool LacksAny(string who)
    {
        foreach (var path in projectPlugins)
            if (who == Name ? !HasPlugin(path) : has.TryGetValue(who, out var set) && !set.Contains(LivePlugins.Key(path))) return true;
        return false;
    }

    /// <summary>The host's name (this copy's own when it hosts).</summary>
    public string HostName => IsHost ? Name : host!.Name;

    /// <summary>
    /// The round trip of a link in ms: the host sees each guest's; a guest sees its own to the host (asked by its name
    /// or the host's). Null when it isn't this copy's to know, or no answer came lately.
    /// </summary>
    public double? LatencyOf(string who)
    {
        if (IsHost) return who == Name ? null : Array.Find(pinged, p => p.Name == who)?.RttMs;
        return who == Name || who == host!.Name ? host!.RttMs : null;
    }

    LiveSession(bool isHost, LiveInvite invite, string name, string app, TcpListener? listener, Peer? host)
    {
        IsHost = isHost;
        Invite = invite;
        Name = name;
        AppVersion = app;
        this.listener = listener;
        this.host = host;
        people = [name];
        if (isHost) colors[name] = 0;
    }

    /// <summary>The color index of someone in the session now (0 = the host's), or -1.</summary>
    public int ColorOf(string who) => people.Contains(who) && colors.TryGetValue(who, out int c) ? c : -1;

    /// <summary>Where each of the others is now, by name.</summary>
    public IReadOnlyDictionary<string, LivePresence> Presence => presence;

    /// <summary>The ids someone changed in the last <see cref="EditHoldMs"/>: what they are editing.</summary>
    public IReadOnlyCollection<string> EditingBy(string who) =>
        touched.TryGetValue(who, out var t) && Environment.TickCount64 - t.At < EditHoldMs ? t.Ids : [];

    /// <summary>Everyone with something being edited now, and its ids.</summary>
    public IEnumerable<(string Who, IReadOnlyCollection<string> Ids)> Editing()
    {
        long now = Environment.TickCount64;
        foreach (var (who, t) in touched)
            if (now - t.At < EditHoldMs) yield return (who, t.Ids);
    }

    /// <summary>Forgets edits older than <see cref="EditHoldMs"/>; true when one went, so its marks go too.</summary>
    public bool ExpireEditing()
    {
        if (touched.Count == 0) return false;
        long now = Environment.TickCount64;
        bool any = false;
        foreach (var (who, t) in touched)
            if (now - t.At >= EditHoldMs) { touched.Remove(who); any = true; }
        return any;
    }

    /// <summary>
    /// Tells the others where this person is. Call it as often as it changes (a timer): what is the same as last time
    /// isn't sent, and nothing goes sooner than <see cref="PresenceGapMs"/> after the last one: false when it came
    /// too soon, to be made again.
    /// </summary>
    public bool Share(LivePresence p)
    {
        if (Ended) return true;
        if (p.Who != Name) p = p with { Who = Name };
        if (p == shared) return true;
        long now = Environment.TickCount64;
        if (now - sharedAt < PresenceGapMs) return false;
        shared = p;
        sharedAt = now;
        var m = LiveMessage.Of(LiveKind.Presence, p);
        if (IsHost) { foreach (var peer in peers) if (peer.Welcomed) peer.Send(m); }
        else host!.Send(m);
        return true;
    }

    /// <summary>A guest forgets the presence of whoever is no longer in.</summary>
    bool DropGone()
    {
        bool any = false;
        foreach (var who in presence.Keys) if (!people.Contains(who)) any |= presence.Remove(who);
        foreach (var who in touched.Keys) if (!people.Contains(who)) any |= touched.Remove(who);
        return any;
    }

    /// <summary>The ids a change touches: each track, folder or bus changed in itself, and each clip changed or added.</summary>
    static void Touched(JsonObject patch, HashSet<string> ids)
    {
        foreach (var key in (string[])["tracks", "folders", "buses"])
        {
            if (patch[key]?["a"] is not JsonObject a) continue;
            if (a["mod"] is JsonObject mod)
                foreach (var (id, op) in mod)
                {
                    bool own = false;
                    if (op is JsonObject item)
                        foreach (var (k, v) in item)
                        {
                            if (k is "clips" or "midiClips" && v?["a"] is JsonObject clips) ArrayIds(clips, ids);
                            else own = true;
                        }
                    if (own) ids.Add(id);
                }
            if (a["put"] is JsonArray put) PutIds(put, ids);
        }
    }

    static void ArrayIds(JsonObject a, HashSet<string> ids)
    {
        if (a["mod"] is JsonObject mod) foreach (var (id, _) in mod) ids.Add(id);
        if (a["put"] is JsonArray put) PutIds(put, ids);
    }

    static void PutIds(JsonArray put, HashSet<string> ids)
    {
        foreach (var n in put)
            if (n is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id)) ids.Add(id);
    }

    void Touch(string who, JsonObject patch)
    {
        var ids = new HashSet<string>();
        Touched(patch, ids);
        if (ids.Count > 0) touched[who] = (ids, Environment.TickCount64);
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
        _ = s.PingLoop();
        return s;
    }

    /// <summary>How many short codes the host draws before giving up when each one's room is taken.</summary>
    const int RoomTries = 5;

    /// <summary>
    /// Starts hosting over the internet (#320): opens a room on the relay with a new short code (another if that one
    /// is taken). Throws <see cref="IOException"/> when the relay can't be reached.
    /// </summary>
    public static async Task<LiveSession> HostOnline(string name, string appVersion, CancellationToken ct)
    {
        for (int i = 0; i < RoomTries; i++)
        {
            var invite = await Task.Run(LiveInvite.NewOnline, ct);
            if (await LiveRelay.Host(invite.RoomId!, ct) is not { } room) continue;
            var s = new LiveSession(true, invite, name, appVersion, null, null) { relay = room };
            _ = s.RelayLoop(room);
            _ = s.PingLoop();
            return s;
        }
        throw new IOException(Texts.T("live.relayBusy", "El servidor de sesiones no dio una sala libre: probá de nuevo"));
    }

    async Task RelayLoop(RelayHost room)
    {
        while (!stop.IsCancellationRequested)
        {
            Stream stream;
            try { stream = await room.Accept(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                if (!stop.IsCancellationRequested)
                {
                    // the room closed under the host: nobody can reach it any more
                    inbox.Enqueue(new(null, null, Closed: true));
                    Signal?.Invoke();
                }
                return;
            }
            _ = Task.Run(() => Welcomed(stream, stream));
        }
    }

    /// <summary>A guest's connection, from either way in: the link's handshake, then its messages.</summary>
    async Task Welcomed(Stream stream, IDisposable owner)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            cts.CancelAfter(ConnectTimeout * 2);
            var link = await LiveLink.Open(stream, owner, Invite.Key, host: true, cts.Token);
            await ReadLoop(new Peer(link));
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            owner.Dispose();
        }
    }

    async Task AcceptLoop(TcpListener l)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            tcp.NoDelay = true;
            _ = Task.Run(() => Welcomed(tcp.GetStream(), tcp));
        }
    }

    /// <summary>
    /// Joins the session of <paramref name="invite"/>: tries each address, says who it is and waits for the host's
    /// copy of the project. Throws <see cref="LiveRefusedException"/> when the host says no (another version).
    /// </summary>
    public static async Task<(LiveSession Session, Welcome Welcome)> Join(LiveInvite invite, string name, string appVersion, CancellationToken ct)
    {
        if (invite.Online)
        {
            var stream = await LiveRelay.Join(invite.RoomId!, ct);
            try
            {
                LiveLink link;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(ConnectTimeout * 3);
                    link = await LiveLink.Open(stream, stream, invite.Key, host: false, cts.Token);
                }
                var joined = await Greet(link, invite, name, appVersion, ct);
                joined.Session.relayed = stream as RelayStream;
                return joined;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
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
                return await Greet(link, invite, name, appVersion, ct);
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

    /// <summary>A guest's first words on a fresh link: who it is, and the host's copy of the project (or its no).</summary>
    static async Task<(LiveSession Session, Welcome Welcome)> Greet(LiveLink link, LiveInvite invite, string name, string appVersion, CancellationToken ct)
    {
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
        var s = new LiveSession(false, invite, w.You ?? name, appVersion, null, peer) { rev = w.Rev, people = w.People, colors = w.Colors ?? [] };
        _ = s.ReadLoop(peer);
        s.pinged = [peer];
        _ = s.PingLoop();
        return (s, w);
    }

    async Task ReadLoop(Peer peer)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var m = await peer.Link.Receive(stop.Token);
                if (m.Kind == LiveKind.FileChunk) { ReceiveChunk(m); continue; }
                if (m.Kind == LiveKind.Ping)
                {
                    // answered and measured right here: the queue would add the UI's delay to the link's
                    var pb = m.Body<PingBody>();
                    if (pb.Back) peer.Measured(System.Diagnostics.Stopwatch.GetTimestamp() - pb.T);
                    else peer.Send(LiveMessage.Of(LiveKind.Ping, pb with { Back = true }));
                    continue;
                }
                (m.Kind is LiveKind.Edit or LiveKind.Rev ? edits : inbox).Enqueue(new(peer, m));
                Signal?.Invoke();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException
            or System.Security.Cryptography.CryptographicException or JsonException) { }
        inbox.Enqueue(new(peer, null, Closed: true));
        Signal?.Invoke();
    }

    /// <summary>Pings every link once a second, on its own thread, until the session ends.</summary>
    async Task PingLoop()
    {
        using var timer = new PeriodicTimer(PingEvery);
        try
        {
            do
            {
                var m = LiveMessage.Of(LiveKind.Ping, new PingBody(System.Diagnostics.Stopwatch.GetTimestamp(), false));
                foreach (var p in pinged) p.Send(m);
            } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Ties the session to its project and folder (where files go) and takes its state as the starting point. A guest
    /// asks the host for every file it lacks.
    /// </summary>
    public void Start(Project project, string projectDir)
    {
        dir = projectDir;
        view = LiveState.Capture(project);
        knownMedia.Clear();
        foreach (var m in project.Media) knownMedia.Add(m.Id);
        if (!IsHost)
            foreach (var m in project.Media) RequestMissing(m);
        ShareCatalog(project);
    }

    /// <summary>Points the session at the project after it was reopened (same folder): nothing is sent.</summary>
    public void Rebind(Project project)
    {
        view = LiveState.Capture(project);
        knownMedia.Clear();
        foreach (var m in project.Media) knownMedia.Add(m.Id);
    }

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

    static string Key(string media, string kind) => $"{media}/{kind}";

    /// <summary>
    /// Tells the others which plugins this copy has (its catalog and the project's it can load), when that changed: a
    /// guest to the host, the host to everybody with the others' lists. Call it after a scan too. True when the lists
    /// this copy knows changed.
    /// </summary>
    public bool ShareCatalog(Project project)
    {
        projectPlugins = [.. LivePlugins.All(project).Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (Ended) return false;
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        // the scan is believed as it is (no file looked at on every edit); the project's are checked
        foreach (var path in Catalog?.Invoke() ?? []) keys.Add(LivePlugins.Key(path));
        foreach (var path in projectPlugins)
            if (HasPlugin(path)) keys.Add(LivePlugins.Key(path));
        string now = string.Join('\n', keys);
        if (now == hasSent) return false;
        hasSent = now;
        if (!IsHost)
        {
            host!.Send(LiveMessage.Of(LiveKind.Has, new HasBody([.. keys])));
            return false;
        }
        has[Name] = [.. keys];
        SendPlugins(null);
        return true;
    }

    /// <summary>Host: everybody's lists, to everybody (or only to <paramref name="to"/>).</summary>
    void SendPlugins(Peer? to)
    {
        var body = new PluginsBody(has.Where(h => people.Contains(h.Key)).ToDictionary(h => h.Key, h => h.Value.ToList()));
        var m = LiveMessage.Of(LiveKind.Plugins, body);
        if (to != null) { to.Send(m); return; }
        foreach (var p in peers) if (p.Welcomed) p.Send(m);
    }

    /// <summary>Files asked for that haven't arrived.</summary>
    public int Downloading => asked.Count;

    /// <summary>
    /// The bytes still to come of the files on their way (#1): each counts once its first piece came (that one says
    /// the file's size); a file asked for that hasn't started isn't counted yet.
    /// </summary>
    public long DownloadLeft
    {
        get
        {
            long left = 0;
            foreach (var (got, total) in progress.Values) left += Math.Max(0, total - got);
            return left;
        }
    }

    /// <summary>
    /// Sends this copy's changes since the last time: call it after edits (a guest's go to the host; the host's go to
    /// everybody with the next number). Nothing while there's no change; says whether something went.
    /// </summary>
    public bool Publish(Project project)
    {
        if (view == null || Ended) return false;
        // a take or an import that wasn't here was brought by this person; the others keep it with the name
        foreach (var m in project.Media)
            if (knownMedia.Add(m.Id)) m.AddedBy ??= Name;
        var now = LiveState.Capture(project);
        var patch = LiveDiff.Diff(view, now);
        view = now;
        if (patch.Count == 0) return false;
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
        ShareCatalog(project);
        return true;
    }

    void Broadcast(JsonObject patch, Peer? author, string authorName)
    {
        foreach (var p in peers)
            if (p.Welcomed) p.Send(LiveMessage.Of(LiveKind.Rev, new RevBody(rev, authorName.Length > 0 ? authorName : Name, p == author, patch)));
    }

    /// <summary>What a <see cref="Pump"/> did.</summary>
    public sealed record PumpResult(bool Changed, List<string> Arrived, List<Media> NewMedia, bool PeopleChanged, bool Ended, string? LastAuthor, bool PresenceChanged = false)
    {
        /// <summary>Who came in and who went since the last pump (never this copy itself): the window chimes and says so.</summary>
        public List<string> Joined { get; init; } = [];
        public List<string> Left { get; init; } = [];
        /// <summary>Who has which plugin changed, or the project's plugins did (<see cref="LackingIt"/>).</summary>
        public bool PluginsChanged { get; init; }
        /// <summary>Only someone's mouse moved (<see cref="LivePresence.Pointer"/>): nothing else to redo for it (#319).</summary>
        public bool PointersMoved { get; init; }
    }

    /// <summary>
    /// Does what arrived, on the project's thread. With <paramref name="applyEdits"/> false (recording, a dialog that
    /// holds the project) changes wait their turn; the rest (who joined, files) goes on.
    /// </summary>
    public PumpResult Pump(Project project, UndoManager? undo, bool applyEdits)
    {
        var arrived = new List<string>();
        var added = new List<Media>();
        bool changed = false, peopleChanged = false, presenceChanged = false, pluginsChanged = false, pointersMoved = false;
        List<string> joined = [], left = [];
        string? author = null;
        bool wasEnded = Ended;
        while (inbox.TryDequeue(out var item))
        {
            if (item.Arrived != null) { arrived.Add(item.Arrived); continue; }
            if (item.Closed && item.From == null)
            {
                EndReason ??= LiveRelay.Reason(relay?.CloseCode)
                    ?? Texts.T("live.relayLost", "Se cortó la conexión con el servidor de sesiones. Tu proyecto queda guardado.");
                continue;
            }
            if (item.Closed)
            {
                string who = item.From!.Name;
                bool gone = Left(item.From!);
                peopleChanged |= gone;
                presenceChanged |= gone;
                if (gone && IsHost)
                {
                    left.Add(who);
                    has.Remove(who);
                    SendPlugins(null);
                }
                pluginsChanged |= gone;
                continue;
            }
            var m = item.Message!;
            switch (m.Kind)
            {
                case LiveKind.Hello when IsHost:
                    if (!Greet(item.From!, m.Body<Hello>(), project)) break;
                    peopleChanged = true;
                    joined.Add(item.From!.Name);
                    break;
                case LiveKind.Has when IsHost:
                    if (!item.From!.Welcomed) break;
                    has[item.From.Name] = [.. m.Body<HasBody>().Plugins ?? []];
                    SendPlugins(null);
                    pluginsChanged = true;
                    break;
                case LiveKind.Plugins when !IsHost:
                    has = (m.Body<PluginsBody>().Has ?? []).Where(h => h.Key != Name).ToDictionary(h => h.Key, h => h.Value.ToHashSet());
                    pluginsChanged = true;
                    break;
                case LiveKind.Peers when !IsHost:
                    var pb = m.Body<PeopleBody>();
                    foreach (var n in pb.People) if (n != Name && !people.Contains(n)) joined.Add(n);
                    foreach (var n in people) if (n != Name && !pb.People.Contains(n)) left.Add(n);
                    people = pb.People;
                    if (pb.Colors != null) colors = pb.Colors;
                    peopleChanged = true;
                    presenceChanged |= DropGone();
                    break;
                case LiveKind.Presence:
                    var pr = m.Body<LivePresence>();
                    if (IsHost)
                    {
                        if (!item.From!.Welcomed) break;
                        pr = pr with { Who = item.From.Name };
                        var fwd = LiveMessage.Of(LiveKind.Presence, pr);
                        foreach (var p in peers) if (p.Welcomed && p != item.From) p.Send(fwd);
                    }
                    if (pr.Who == Name || !people.Contains(pr.Who)) break;
                    bool still = presence.TryGetValue(pr.Who, out var was) && was.Still == pr.Still;
                    presence[pr.Who] = pr;
                    if (still) pointersMoved = true;
                    else presenceChanged = true;
                    break;
                case LiveKind.GetFile: Serve(item.From!, m.Body<FileRequest>(), project); break;
                case LiveKind.Missing:
                    var miss = m.Body<FileRequest>();
                    string key = Key(miss.Media, miss.Kind);
                    asked.Remove(key);
                    progress.TryRemove(key, out _);
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
                    // only from someone let in (a refused peer's link is still open), and only a patch that is one
                    if (!item.From!.Welcomed || m.Body<EditBody>()?.Patch is not { } patch) continue;
                    Publish(project);
                    added.AddRange(ApplyPatch(project, undo, patch, [], item.From!.Name));
                    foreach (var md in added)
                    {
                        authors.TryAdd(md.Id, item.From!);
                        md.AddedBy ??= item.From!.Name;
                    }
                    Touch(item.From!.Name, patch);
                    presenceChanged = true;
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
                    added.AddRange(ApplyPatch(project, undo, r.Patch, pending, r.Author));
                    if (r.Author != Name) Touch(r.Author, r.Patch);
                    presenceChanged = true;
                    changed = true;
                    author = r.Author;
                }
            }
        foreach (var md in added) RequestMissing(md);
        if (changed && !Ended)
        {
            ShareCatalog(project);
            // the project's plugins may be others now: what is off is looked at again
            pluginsChanged = true;
        }
        if (Ended && presence.Count + touched.Count > 0)
        {
            presence.Clear();
            touched.Clear();
            presenceChanged = true;
        }
        return new(changed, [.. arrived.Select(a => a[..a.IndexOf('/')]).Distinct()], added, peopleChanged, Ended && !wasEnded, author, presenceChanged)
        {
            Joined = joined, Left = left, PluginsChanged = pluginsChanged, PointersMoved = pointersMoved,
        };
    }

    /// <summary>
    /// Someone else's change under this copy's pending ones; the kept undo states get it too, and the undo's list a
    /// step by <paramref name="author"/>.
    /// </summary>
    List<Media> ApplyPatch(Project project, UndoManager? undo, JsonObject patch, List<JsonObject> mine, string author)
    {
        string? before = undo != null ? UndoManager.Capture(project) : null;
        var next = (JsonObject)view!.DeepClone();
        LiveDiff.Apply(next, patch);
        foreach (var p in mine) LiveDiff.Apply(next, p);
        var added = LiveState.Apply(project, next);
        foreach (var m in added) knownMedia.Add(m.Id);
        undo?.Rebase(LiveDiff.Without(patch, "media"));
        if (undo != null)
        {
            string after = UndoManager.Capture(project);
            if (after != before) undo.Remote(author, before!, after, id => project.FindMedia(id)?.Name);
        }
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
        string name = (hello.Name ?? "").Trim();
        if (name.Length == 0) name = "?";
        while (people.Contains(name)) name += "'";
        peer.Name = name;
        peers.Add(peer);
        people = [.. people, name];
        Paint(name);
        peer.Welcomed = true;
        pinged = [.. peers.Where(p => p.Welcomed)];
        peer.Send(LiveMessage.Of(LiveKind.Welcome, new Welcome(rev, Name, SharedCopy(project), people, name, NowColors())));
        SendPeople();
        SendPlugins(peer);
        // where everybody is, so the newcomer sees it before anyone moves
        if (shared != null) peer.Send(LiveMessage.Of(LiveKind.Presence, shared));
        foreach (var pr in presence.Values) peer.Send(LiveMessage.Of(LiveKind.Presence, pr));
        return true;
    }

    /// <summary>The project as a guest gets it: every file named inside its own media/.</summary>
    static string SharedCopy(Project project)
    {
        var o = JsonSerializer.SerializeToNode(project, ProjectStore.Json)!.AsObject();
        if (o["media"] is JsonArray media)
            foreach (var m in media.OfType<JsonObject>())
            {
                string? id = m["id"]?.GetValue<string>();
                m["audio"] = LiveState.Shared(m["audio"]?.GetValue<string>(), id);
                m["video"] = LiveState.Shared(m["video"]?.GetValue<string>(), id);
                m.Remove("audioBytes");
                m.Remove("videoBytes");
            }
        return o.ToJsonString(ProjectStore.Json);
    }

    /// <summary>
    /// Host: the color of someone who just joined: the one they had if they were here before and nobody took it,
    /// otherwise the first free one.
    /// </summary>
    void Paint(string name)
    {
        var used = new HashSet<int>();
        foreach (var n in people) if (n != name && colors.TryGetValue(n, out int c)) used.Add(c);
        if (colors.TryGetValue(name, out int had) && !used.Contains(had)) return;
        int free = 0;
        while (used.Contains(free)) free++;
        colors[name] = free;
    }

    /// <summary>The colors of who is in now.</summary>
    Dictionary<string, int> NowColors()
    {
        var d = new Dictionary<string, int>();
        foreach (var n in people) if (colors.TryGetValue(n, out int c)) d[n] = c;
        return d;
    }

    void SendPeople()
    {
        var colorsNow = NowColors();
        foreach (var p in peers) if (p.Welcomed) p.Send(LiveMessage.Of(LiveKind.Peers, new PeopleBody(people, colorsNow)));
    }

    bool Left(Peer peer)
    {
        peer.Link.Dispose();
        if (!IsHost)
        {
            EndReason ??= LiveRelay.Reason(relayed?.CloseCode)
                ?? Texts.T("live.hostLeft", "El anfitrión cerró la sesión en vivo. Tu copia queda guardada.");
            return true;
        }
        if (!peers.Remove(peer)) return false;
        pinged = [.. peers.Where(p => p.Welcomed)];
        foreach (var (id, who) in authors.Where(a => a.Value == peer).ToList()) authors.Remove(id);
        people = [.. people.Where(n => n != peer.Name)];
        presence.Remove(peer.Name);
        touched.Remove(peer.Name);
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
        FileChunk? c;
        // a broken piece is dropped: thrown from here it would end the read loop without closing the peer
        try { c = m.Body<FileChunk>(); }
        catch (JsonException) { return; }
        if (c?.Name == null) return;
        string name = Path.GetFileName(c.Name);
        if (dir == null || name.Length == 0 || name != c.Name) return;
        // media only: a recording journal pushed in here would be "recovered" on the next open
        if (Path.GetExtension(name).ToLowerInvariant() is ".json" or ".notas") return;
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
            if (c.Last) progress.TryRemove(Key(c.Media, c.Kind), out _);
            else progress[Key(c.Media, c.Kind)] = (c.Offset + (m.Data?.Length ?? 0), c.Total);
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
        inbox.Enqueue(new(null, null, Arrived: Key(c.Media, c.Kind)));
        Signal?.Invoke();
    }

    public void Dispose()
    {
        EndReason ??= Texts.T("live.closed", "Sesión cerrada");
        stop.Cancel();
        listener?.Stop();
        relay?.Dispose();
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
