using System.Net;
using System.Security.Cryptography;
using XtremeDaw.Audio;

namespace XtremeDaw.Lab;

/// <summary>
/// <c>xtlab componentes</c>: the state of the optional components, installing one (<c>--url</c> points the stems at a
/// test server) and, with <c>comprobar</c>, the download itself against a local server that cuts the connection.
/// </summary>
static class ComponentCommands
{
    public static int Run(Args a)
    {
        string action = a.Positional.Count > 0 ? a.Positional[0] : "estado";
        if (a.Get("url") is { } url) Environment.SetEnvironmentVariable(StemsComponent.UrlVariable, url);
        if (action == "comprobar") return Check();
        if (action == "convertir")
        {
            foreach (var m in StemsComponent.Models)
            {
                StemConverter.Convert(Path.Combine(a.Positional[1], m.Checkpoint), m.File, a.Positional[2]);
                Console.WriteLine($"{m.File}: coincide");
            }
            return 0;
        }
        Components.Refresh();
        if (action == "instalar")
        {
            string id = a.Positional.Count > 1 ? a.Positional[1] : "";
            var c = Components.All.FirstOrDefault(x => x.Id == id);
            if (c == null) { Console.WriteLine("Elegí un componente: " + string.Join(", ", Components.All.Select(x => x.Id))); return 1; }
            if (!c.CanInstall) { Console.WriteLine("No se puede instalar: falta la URL o winget."); return 1; }
            var p = c.Packages.FirstOrDefault(x => x.Id == a.Get("paquete")) ?? c.Packages.LastOrDefault();
            c.InstallAsync(p, f => { if (f >= 0) Console.Write($"\r{f:P0} "); }, default).GetAwaiter().GetResult();
            Console.WriteLine("\nListo.");
            return 0;
        }
        foreach (var c in Components.All)
        {
            Console.WriteLine($"{c.Id}: {(c.Ready ? "instalado" : c.Partial ? "en parte" : "falta")}, {(c.CanInstall ? "se puede instalar" : "sin instalador")}");
            foreach (var p in c.Packages) Console.WriteLine($"  {p.Id}: {p.Bytes / 1e6:0} MB, {p.Files.Count} archivos");
        }
        return 0;
    }

    static int Check()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xtlab-descarga-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var data = new byte[3_000_000];
        new Random(1).NextBytes(data);
        string hash = Convert.ToHexString(SHA256.HashData(data));
        int port = 40000 + Random.Shared.Next(9000), requests = 0, cuts = 1, ranged = 0;
        bool whole = false;
        using var server = new HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext c;
                try { c = await server.GetContextAsync(); } catch { return; }
                requests++;
                long from = 0;
                string? range = c.Request.Headers["Range"];
                if (range != null && !whole)
                {
                    from = long.Parse(range[6..^1]);
                    ranged++;
                    c.Response.StatusCode = 206;
                    c.Response.AddHeader("Content-Range", $"bytes {from}-{data.Length - 1}/{data.Length}");
                }
                try
                {
                    c.Response.ContentLength64 = data.Length - from;
                    if (cuts > 0)
                    {
                        cuts--;
                        await c.Response.OutputStream.WriteAsync(data.AsMemory((int)from, 1_000_000));
                        await c.Response.OutputStream.FlushAsync();
                        await Task.Delay(100);
                        c.Response.Abort();
                        continue;
                    }
                    await c.Response.OutputStream.WriteAsync(data.AsMemory((int)from));
                    c.Response.Close();
                }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
            }
        });

        int bad = 0;
        void Say(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok " : "MAL")} {what}");
            if (!ok) bad++;
        }
        string url = $"http://localhost:{port}/m.bin", dest = Path.Combine(dir, "m.bin");
        void Get(string h) => FileDownload.GetAsync(url, dest, data.Length, h, _ => { }).GetAwaiter().GetResult();

        Get(hash);
        Say("se corta y sigue donde quedó", ranged == 1 && requests == 2 && File.ReadAllBytes(dest).AsSpan().SequenceEqual(data));
        Say("no queda el temporal", !File.Exists(dest + ".part"));

        File.Delete(dest);
        File.WriteAllBytes(dest + ".part", data[..500_000]);
        ranged = 0;
        Get(hash);
        Say("retoma un .part de una sesión anterior", ranged == 1 && File.ReadAllBytes(dest).AsSpan().SequenceEqual(data));

        File.Delete(dest);
        File.WriteAllBytes(dest + ".part", data[..500_000]);
        whole = true;
        Get(hash);
        Say("el servidor ignora Range: empieza de nuevo", File.ReadAllBytes(dest).AsSpan().SequenceEqual(data));
        whole = false;

        File.Delete(dest);
        try { Get(new string('0', 64)); Say("rechaza un hash distinto", false); }
        catch (InvalidDataException) { Say("rechaza un hash distinto", !File.Exists(dest) && !File.Exists(dest + ".part")); }

        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { FileDownload.GetAsync(url, dest, data.Length, hash, _ => { }, cancel.Token).GetAwaiter().GetResult(); Say("cancelar", false); }
            catch (OperationCanceledException) { Say("cancelar", !File.Exists(dest)); }
        }

        server.Stop();
        Directory.Delete(dir, true);
        Console.WriteLine(bad == 0 ? "Descarga correcta." : $"{bad} fallaron.");
        return bad == 0 ? 0 : 1;
    }
}
