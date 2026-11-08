using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// <c>xtlab colaborar comprobar</c>: the shared session (#291) without the window. The patches (<see cref="LiveDiff"/>)
/// rebuild a state, two changes to different tracks both stay, on the same value the last one wins; the invitation
/// reads back; and a real session over 127.0.0.1: a guest joins, gets the project and its audio, edits in both
/// directions, solos stay each one's, a new take's file travels through the host, undo takes back only one's own
/// step, another version or a wrong code don't get in. Who is who: colors from the host, presence both ways and gone on
/// leaving, what someone edits marked as theirs, the others' steps listed by name, a take saying who brought it.
/// <c>xtlab colaborar anfitrion &lt;carpeta&gt;</c> and <c>unirse &lt;código&gt; &lt;carpeta&gt;</c> run a session by hand.
/// </summary>
static class LiveChecks
{
    const string Version = "lab-1";

    public static int Run(Args a)
    {
        string action = a.Positional.Count > 0 ? a.Positional[0] : "";
        return action switch
        {
            "comprobar" => Check(),
            "anfitrion" when a.Positional.Count > 1 => HostByHand(a.Positional[1], a.Get("nombre") ?? "Anfitrión"),
            "unirse" when a.Positional.Count > 2 => JoinByHand(a.Positional[1], a.Positional[2], a.Get("nombre") ?? "Invitado"),
            _ => Usage(),
        };
    }

    static int Usage()
    {
        Console.WriteLine("xtlab colaborar comprobar | anfitrion <carpeta> [--nombre N] | unirse <código> <carpeta> [--nombre N]");
        return 1;
    }

    static int failed;

    static void Check(string what, bool ok)
    {
        Console.WriteLine($"{(ok ? "ok   " : "DISTINTO")} {what}");
        if (!ok) failed++;
    }

    static Project Sample()
    {
        var a = new Track { Name = "Guitarra" };
        var b = new Track { Name = "Bajo" };
        var c = new Track { Name = "Voz" };
        var p = new Project { Name = "Sesión", Tracks = [a, b, c] };
        p.Normalize();
        return p;
    }

    static int Check()
    {
        failed = 0;
        Patches();
        Invites();
        Session();
        Console.WriteLine(failed == 0 ? "Todo ok" : $"{failed} DISTINTO");
        return failed == 0 ? 0 : 1;
    }

    static void Patches()
    {
        var p = Sample();
        var from = LiveState.Capture(p);
        p.Tracks[0].Channel.Volume = 0.5;
        p.Tracks[1].Name = "Bajo eléctrico";
        (p.Tracks[0], p.Tracks[2]) = (p.Tracks[2], p.Tracks[0]);
        p.Tracks.RemoveAt(1);
        p.Tracks.Add(new Track { Name = "Teclado" });
        p.Markers.Add(new Marker { Name = "Estribillo", Sec = 12 });
        p.Normalize();
        var to = LiveState.Capture(p);
        var patch = LiveDiff.Diff(from, to);
        var rebuilt = (JsonObject)from.DeepClone();
        LiveDiff.Apply(rebuilt, patch);
        Check("aplicar la diferencia rearma el estado (orden, altas, bajas, valores)", JsonNode.DeepEquals(rebuilt, to));
        Check("sin cambios no hay diferencia", LiveDiff.Diff(to, LiveState.Capture(p)).Count == 0);

        // two people, two tracks, the same base
        var q = Sample();
        var baseState = LiveState.Capture(q);
        var one = (JsonObject)baseState.DeepClone();
        ((JsonObject)((JsonArray)one["tracks"]!)[0]!["channel"]!)["volume"] = 0.25;
        var two = (JsonObject)baseState.DeepClone();
        ((JsonObject)((JsonArray)two["tracks"]!)[1]!["channel"]!)["pan"] = -0.5;
        var both = (JsonObject)baseState.DeepClone();
        LiveDiff.Apply(both, LiveDiff.Diff(baseState, one));
        LiveDiff.Apply(both, LiveDiff.Diff(baseState, two));
        var tracks = (JsonArray)both["tracks"]!;
        Check("dos cambios en pistas distintas quedan los dos",
            tracks[0]!["channel"]!["volume"]!.GetValue<double>() == 0.25 && tracks[1]!["channel"]!["pan"]!.GetValue<double>() == -0.5);

        var three = (JsonObject)baseState.DeepClone();
        ((JsonObject)((JsonArray)three["tracks"]!)[0]!["channel"]!)["volume"] = 0.75;
        LiveDiff.Apply(both, LiveDiff.Diff(baseState, three));
        Check("en el mismo valor queda el último", ((JsonArray)both["tracks"]!)[0]!["channel"]!["volume"]!.GetValue<double>() == 0.75);

        var gone = (JsonObject)baseState.DeepClone();
        ((JsonArray)gone["tracks"]!).RemoveAt(0);
        var stale = (JsonObject)baseState.DeepClone();
        LiveDiff.Apply(stale, LiveDiff.Diff(baseState, gone));
        LiveDiff.Apply(stale, LiveDiff.Diff(baseState, one));
        Check("un cambio a una pista que otro borró se descarta", ((JsonArray)stale["tracks"]!).Count == 2);

        var solo = Sample();
        var s0 = LiveState.Capture(solo);
        solo.Tracks[0].Channel.Solo = true;
        Check("el solo no viaja", LiveDiff.Diff(s0, LiveState.Capture(solo)).Count == 0);
    }

    static void Invites()
    {
        var inv = new LiveInvite([IPAddress.Parse("192.168.1.20"), IPAddress.Parse("100.101.102.103")], 47610, new byte[16].Select((_, i) => (byte)(i * 7)).ToArray());
        var back = LiveInvite.Parse(inv.Code);
        Check($"el código se lee igual ({inv.Code})", back != null && back.Port == 47610 && back.Addresses.SequenceEqual(inv.Addresses) && back.Key.SequenceEqual(inv.Key));
        var fromLink = LiveInvite.Parse(inv.Link.ToLowerInvariant().Replace("xtreme://unirse/", "xtreme://unirse/"));
        Check("el link también, en minúsculas", fromLink != null && fromLink.Key.SequenceEqual(inv.Key));
        Check("un código cortado no se acepta", LiveInvite.Parse(inv.Code[..^3]) == null && LiveInvite.Parse("hola") == null);
    }

    static bool Until(Func<bool> done, Action pump, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            pump();
            if (done()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static string TempDir(string name)
    {
        string d = Path.Combine(Path.GetTempPath(), $"xtlab-vivo-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(d, ProjectStore.MediaDir));
        return d;
    }

    static void Session()
    {
        string hostDir = TempDir("anfitrion"), guestDir = TempDir("invitado");
        try
        {
            var hp = Sample();
            var audio = new byte[1_300_000];
            new Random(5).NextBytes(audio);
            File.WriteAllBytes(Path.Combine(hostDir, "media", "toma1.wav"), audio);
            hp.Media.Add(new Media { Name = "toma1", Audio = "media/toma1.wav", DurationSec = 3 });
            hp.Tracks[0].Clips.Add(new Clip { MediaId = hp.Media[0].Id, AtSec = 1 });
            var hostUndo = new UndoManager();
            using var host = LiveSession.Host("Martín", Version, 0, [IPAddress.Loopback]);
            host.Start(hp, hostDir);
            List<string> hostJoined = [], hostLeft = [], guestJoined = [], guestLeft = [];
            void PumpHost()
            {
                var r = host.Pump(hp, hostUndo, true);
                hostJoined.AddRange(r.Joined);
                hostLeft.AddRange(r.Left);
            }

            var invite = LiveInvite.Parse(host.Invite.Code)!;
            var joining = LiveSession.Join(invite, "Ana", Version, default);
            Until(() => joining.IsCompleted, PumpHost);
            Check("el invitado entra", joining.IsCompletedSuccessfully);
            if (!joining.IsCompletedSuccessfully) return;
            var (guest, welcome) = joining.Result;
            using var _g = guest;
            var gp = ProjectStore.Parse(welcome.Project);
            gp.Normalize();
            var guestUndo = new UndoManager();
            guest.Start(gp, guestDir);
            void PumpGuest()
            {
                var r = guest.Pump(gp, guestUndo, true);
                guestJoined.AddRange(r.Joined);
                guestLeft.AddRange(r.Left);
            }
            void PumpBoth() { PumpHost(); PumpGuest(); }

            Check("recibe el proyecto con sus pistas y clips", gp.Tracks.Count == 3 && gp.Tracks[0].Clips.Count == 1);
            Check("los dos se ven", Until(() => host.People.Count == 2 && guest.People.Count == 2, PumpBoth) && guest.People.Contains("Martín"));
            Check("el anfitrión se entera de que entró (aviso y sonido)", hostJoined.SequenceEqual(["Ana"]));
            Check("se mide la latencia: el anfitrión la de cada invitado, el invitado la suya",
                Until(() => host.LatencyOf("Ana") is > 0 && guest.LatencyOf(guest.Name) is > 0, PumpBoth) && host.LatencyOf("Martín") == null);
            string got = Path.Combine(guestDir, "media", "toma1.wav");
            Check("le llega el audio entero", Until(() => File.Exists(got), PumpBoth) && File.ReadAllBytes(got).AsSpan().SequenceEqual(audio));

            gp.Tracks[1].Channel.Volume = 0.3;
            guest.Publish(gp);
            Check("un cambio del invitado llega al anfitrión", Until(() => hp.Tracks[1].Channel.Volume == 0.3, PumpBoth));
            Check("lo que toca el invitado se marca como suyo en el anfitrión", host.EditingBy("Ana").Contains(hp.Tracks[1].Id));
            Check("el paso del invitado lleva su nombre en el historial del anfitrión",
                hostUndo.Steps(hp, _ => null).Any(s => s.Author == "Ana" && s.Index < 0));

            // presence (#291): who has what selected, plays or records, and where their cursor goes
            Check("cada uno tiene su color, el mismo en las dos copias",
                host.ColorOf("Martín") >= 0 && host.ColorOf("Ana") >= 0 && host.ColorOf("Martín") != host.ColorOf("Ana")
                && Until(() => guest.ColorOf("Ana") == host.ColorOf("Ana") && guest.ColorOf("Martín") == host.ColorOf("Martín"), PumpBoth));
            guest.Share(new LivePresence { Track = gp.Tracks[0].Id, Clip = gp.Tracks[0].Clips[0].Id, Rolling = true, Cursor = 2.5 });
            Check("la presencia del invitado llega al anfitrión",
                Until(() => host.Presence.TryGetValue("Ana", out var pr) && pr.Track == hp.Tracks[0].Id && pr.Clip == hp.Tracks[0].Clips[0].Id && pr.Cursor == 2.5, PumpBoth));
            host.Share(new LivePresence { Playing = hp.Tracks[2].Id, Recording = true });
            Check("y la del anfitrión al invitado",
                Until(() => guest.Presence.TryGetValue("Martín", out var pr) && pr.Playing == gp.Tracks[2].Id && pr.Recording, PumpBoth)
                && !guest.Presence.ContainsKey("Ana"));

            hostUndo.Checkpoint(hp);
            hp.Tracks[2].Name = "Voz principal";
            host.Publish(hp);
            Check("uno del anfitrión llega al invitado", Until(() => gp.Tracks[2].Name == "Voz principal", PumpBoth));
            Check("y en el historial del invitado es del anfitrión", guestUndo.Steps(gp, _ => null).Any(s => s.Author == "Martín"));
            Check("los pasos propios no llevan nombre", hostUndo.Steps(hp, _ => null).Where(s => s.Index > 0).All(s => s.Author == null));

            gp.Tracks[0].Channel.Pan = 0.4;
            guest.Publish(gp);
            Until(() => hp.Tracks[0].Channel.Pan == 0.4, PumpBoth);
            hostUndo.Undo(hp);
            host.Publish(hp);
            Check("deshacer en el anfitrión saca solo su paso, no el del invitado",
                hp.Tracks[2].Name == "Voz" && hp.Tracks[0].Channel.Pan == 0.4
                && Until(() => gp.Tracks[2].Name == "Voz", PumpBoth) && gp.Tracks[0].Channel.Pan == 0.4);

            hp.Tracks[0].Channel.Solo = true;
            host.Publish(hp);
            PumpBoth();
            Thread.Sleep(100);
            PumpBoth();
            Check("el solo del anfitrión no cambia el del invitado", !gp.Tracks[0].Channel.Solo);

            // the guest records: a new media with its file; the host asks for it
            var take = new byte[700_000];
            new Random(9).NextBytes(take);
            File.WriteAllBytes(Path.Combine(guestDir, "media", "toma2.wav"), take);
            var m2 = new Media { Name = "toma2", Audio = "media/toma2.wav", DurationSec = 2 };
            gp.Media.Add(m2);
            gp.Tracks[1].Clips.Add(new Clip { MediaId = m2.Id, AtSec = 4 });
            guest.Publish(gp);
            string atHost = Path.Combine(hostDir, "media", "toma2.wav");
            Check("una toma nueva del invitado llega con su archivo al anfitrión",
                Until(() => File.Exists(atHost) && hp.Tracks[1].Clips.Count == 1, PumpBoth)
                && File.ReadAllBytes(atHost).AsSpan().SequenceEqual(take) && hp.Media.Any(m => m.Id == m2.Id));
            Check("la toma dice quién la trajo", m2.AddedBy == "Ana" && hp.Media.Find(m => m.Id == m2.Id)?.AddedBy == "Ana");

            Plugins(host, guest, hp, gp, hostDir, guestDir, guestUndo, PumpBoth);

            // a second guest joins late and gets both files: one through the host
            string thirdDir = TempDir("tercero");
            try
            {
                var j3 = LiveSession.Join(invite, "Ana", Version, default);
                Until(() => j3.IsCompleted, PumpBoth);
                var (third, w3) = j3.Result;
                using var _t = third;
                var tp = ProjectStore.Parse(w3.Project);
                third.Start(tp, thirdDir);
                Check("un tercero entra con otro nombre y recibe todo",
                    Until(() => File.Exists(Path.Combine(thirdDir, "media", "toma2.wav")) && File.Exists(Path.Combine(thirdDir, "media", "toma1.wav")),
                        () => { PumpBoth(); third.Pump(tp, null, true); })
                    && third.People.Contains("Ana'"));
                Check("el tercero recibe quién trajo cada toma", tp.Media.Find(m => m.Id == m2.Id)?.AddedBy == "Ana");
                Check("el tercero se ve con el nombre que le dio el anfitrión", third.Name == "Ana'");
                third.Share(new LivePresence { Track = tp.Tracks[1].Id });
                Check("su presencia les llega a los otros dos",
                    Until(() => host.Presence.ContainsKey("Ana'") && guest.Presence.ContainsKey("Ana'"), () => { PumpBoth(); third.Pump(tp, null, true); }));
                third.Dispose();
                Check("al irse, su presencia se borra en los dos",
                    Until(() => !host.Presence.ContainsKey("Ana'") && !guest.Presence.ContainsKey("Ana'") && host.ColorOf("Ana'") < 0, PumpBoth));
                Check("los dos se enteran de que entró y de que salió",
                    Until(() => hostLeft.Contains("Ana'") && guestLeft.Contains("Ana'"), PumpBoth) && hostJoined.Contains("Ana'") && guestJoined.Contains("Ana'")
                    && !guestJoined.Contains("Ana"));
            }
            finally { TryDelete(thirdDir); }

            var other = LiveSession.Join(invite, "Otro", "otra-versión", default);
            Until(() => other.IsCompleted, PumpHost);
            Check("otra versión no entra", other.Exception?.InnerException is LiveRefusedException { Reason: "version" });

            var wrong = LiveSession.Join(invite with { Key = new byte[16] }, "Intruso", Version, default);
            Until(() => wrong.IsCompleted, PumpHost);
            Check("con otra clave no entra", wrong.IsFaulted);

            host.Dispose();
            Check("si el anfitrión cierra, el invitado se entera", Until(() => guest.Ended, PumpGuest));
        }
        finally
        {
            TryDelete(hostDir);
            TryDelete(guestDir);
        }
    }

    /// <summary>
    /// Plugins in a session (#291, the user's call: only what everybody has is used): each copy says what it has, both
    /// know who lacks a plugin of the project or of someone's list, and it is known again when it is installed or the one
    /// without it leaves. Nothing of it goes into the project.
    /// </summary>
    static void Plugins(LiveSession host, LiveSession guest, Project hp, Project gp, string hostDir, string guestDir, UndoManager guestUndo, Action pumpBoth)
    {
        string fake = Path.Combine(hostDir, "Fake.vst3");
        File.WriteAllBytes(fake, [1]);
        bool guestHas = false;
        guest.HasPlugin = path => string.Equals(path, fake, StringComparison.OrdinalIgnoreCase) ? guestHas : LivePlugins.Installed(path);
        var synth = new Track { Name = "Sinte", Kind = TrackKinds.Midi, Instrument = new PluginRef { Path = fake, ClassId = "c1", Name = "Fake" } };
        hp.Tracks.Add(synth);
        hp.Normalize();
        host.Publish(hp);
        Check("al invitado le falta el instrumento: los dos lo saben",
            Until(() => host.LackingIt(fake).SequenceEqual(["Ana"]) && guest.LackingIt(fake).SequenceEqual(["Ana"]), pumpBoth)
            && host.LacksAny("Ana") && guest.LacksAny("Ana") && !host.LacksAny("Martín"));
        int steps = guestUndo.Steps(gp, _ => null).Count;

        // a plugin only the guest found: the host lacks it
        string other = Path.Combine(guestDir, "Otro.vst3");
        File.WriteAllBytes(other, [2]);
        host.HasPlugin = path => !string.Equals(path, other, StringComparison.OrdinalIgnoreCase) && LivePlugins.Installed(path);
        guest.Catalog = () => [other];
        guest.ShareCatalog(gp);
        Check("lo que el invitado encontró llega: el anfitrión no lo tiene",
            Until(() => host.LackingIt(other).SequenceEqual(["Martín"]) && guest.LackingIt(other).SequenceEqual(["Martín"]), pumpBoth));
        string plain = Path.Combine(hostDir, "Comun.vst3");
        File.WriteAllBytes(plain, [3]);
        guest.Catalog = () => [other, plain];
        guest.ShareCatalog(gp);
        host.Catalog = () => [plain];
        host.ShareCatalog(hp);
        Check("uno que tienen los dos no le falta a nadie",
            Until(() => host.LackingIt(plain).Count == 0 && guest.LackingIt(plain).Count == 0, pumpBoth));

        guestHas = true;
        guest.Publish(gp);
        guest.ShareCatalog(gp);
        Check("al instalarlo, ya no le falta a nadie", Until(() => host.LackingIt(fake).Count == 0 && guest.LackingIt(fake).Count == 0, pumpBoth));
        Check("nada de esto es un paso del deshacer ni cambia la pista",
            guestUndo.Steps(gp, _ => null).Count == steps && gp.Tracks.Find(t => t.Id == synth.Id) is { Instrument.Path: var gpath, Freeze: null } && gpath == fake);
        host.HasPlugin = LivePlugins.Installed;
        guest.HasPlugin = LivePlugins.Installed;
        guest.Catalog = null;
        host.Catalog = null;
    }

    static void TryDelete(string d)
    {
        try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static int HostByHand(string dir, string name)
    {
        using var store = ProjectStore.Open(dir);
        using var s = LiveSession.Host(name, Version);
        s.Start(store.Project, dir);
        Console.WriteLine($"Código: {s.Invite.Code}");
        Console.WriteLine("Enter para cerrar.");
        var undo = new UndoManager();
        while (!Console.KeyAvailable)
        {
            var r = s.Pump(store.Project, undo, true);
            if (r.PeopleChanged) Console.WriteLine("En la sesión: " + string.Join(", ", s.People));
            if (r.Changed) { Console.WriteLine($"Cambio de {r.LastAuthor} (n.º {s.Rev})"); store.Save(); }
            Thread.Sleep(50);
        }
        return 0;
    }

    static int JoinByHand(string code, string dir, string name)
    {
        var inv = LiveInvite.Parse(code) ?? throw new ArgumentException("Código inválido");
        var (s, w) = LiveSession.Join(inv, name, Version, default).GetAwaiter().GetResult();
        using var _ = s;
        var p = ProjectStore.Parse(w.Project);
        using var store = ProjectStore.Create(dir, p);
        s.Start(store.Project, dir);
        Console.WriteLine($"Adentro, con {string.Join(", ", s.People)}. Enter para salir.");
        while (!Console.KeyAvailable && !s.Ended)
        {
            var r = s.Pump(store.Project, null, true);
            if (r.Changed) { Console.WriteLine($"Cambio de {r.LastAuthor}"); store.Save(); }
            foreach (var id in r.Arrived) Console.WriteLine($"Llegó el audio de {id}");
            Thread.Sleep(50);
        }
        Console.WriteLine(s.EndReason);
        return 0;
    }
}
