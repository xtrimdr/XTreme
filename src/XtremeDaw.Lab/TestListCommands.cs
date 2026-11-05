using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab lista: the changes waiting to be tried and the ones the user asks for (<see cref="TestList"/>).
/// <c>tools\probar.ps1</c> runs it to add to the list, to read it and to take a request. The text can come in <see cref="TextVar"/>: Windows PowerShell breaks quotes inside an argument.
/// </summary>
static class TestListCommands
{
    const string TextVar = "XTREME_PROBAR_TEXTO";

    public static int Run(Args a)
    {
        string action = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "ver";
        if (action == "comprobar") return Check();
        string path = a.Get("archivo") ?? throw new ArgumentException("lista: falta --archivo pruebas\\lista.json");
        string? text = Environment.GetEnvironmentVariable(TextVar)
            ?? (a.Positional.Count > 1 && action is "agregar" or "pedir" ? a.Positional[1]
                : a.Positional.Count > 2 && action is "comentar" or "titulo" ? a.Positional[2] : a.Get("texto"));
        switch (action)
        {
            case "ver":
                Show(TestList.Read(path));
                return 0;
            case "agregar":
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("lista agregar \"qué probar\" --copia nombre");
                Console.WriteLine($"En la lista: #{TestList.Add(path, text, Copy(a), TopicOption(a.Get("tema")))}");
                return 0;
            case "pedir":
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("lista pedir \"qué cambiar\" --tipo error|mejora|funcion|idea [--copia nombre]");
                string kind = KindNames.FirstOrDefault(k => k.Option == (a.Get("tipo") ?? "").ToLowerInvariant()).Kind
                    ?? throw new ArgumentException("lista pedir: --tipo error | mejora | funcion | idea");
                Console.WriteLine($"Pedido: #{TestList.AddRequest(path, text, kind, a.Get("copia") ?? "", TopicOption(a.Get("tema")))}");
                return 0;
            case "tema":
                const string topicUsage = "lista tema número tema (o «ninguno»); temas: ";
                int tagged = Number(a, topicUsage + TopicList);
                string? option = a.Positional.Count > 2 ? a.Positional[2] : a.Get("tema");
                if (string.IsNullOrWhiteSpace(option)) throw new ArgumentException(topicUsage + TopicList);
                string? topic = option.ToLowerInvariant() is "ninguno" or "sin" ? null : TopicOption(option);
                if (!TestList.SetTopic(path, tagged, topic))
                {
                    Console.Error.WriteLine($"La lista no tiene el #{tagged}.");
                    return 2;
                }
                Console.WriteLine(topic == null ? $"#{tagged} queda sin tema." : $"#{tagged}: {TopicTitle(topic)}.");
                return 0;
            case "tipo":
                const string kindUsage = "lista tipo número error|mejora|funcion|idea (o «ninguno»)";
                int kinded = Number(a, kindUsage);
                string? kindOption = a.Positional.Count > 2 ? a.Positional[2] : a.Get("tipo");
                if (string.IsNullOrWhiteSpace(kindOption)) throw new ArgumentException(kindUsage);
                string? newKind = kindOption.ToLowerInvariant() is "ninguno" or "sin" ? null
                    : KindNames.FirstOrDefault(k => k.Option == kindOption.ToLowerInvariant()).Kind ?? throw new ArgumentException(kindUsage);
                if (!TestList.SetKind(path, kinded, newKind))
                {
                    Console.Error.WriteLine($"#{kinded} no es un pedido de la lista.");
                    return 2;
                }
                Console.WriteLine(newKind == null ? $"#{kinded} queda sin tipo." : $"#{kinded}: {KindTitle(newKind)}.");
                return 0;
            case "temas":
                foreach (var t in TopicNames) Console.WriteLine($"  {t.Option,-16} {t.Title}: {t.About}");
                return 0;
            case "tomar":
                int take = Number(a, "lista tomar número");
                switch (TestList.Take(path, take, out var holder))
                {
                    case TestList.TakeResult.Taken:
                        Console.WriteLine($"#{take} es tuyo. Al terminar: tools\\probar.ps1 -NoAbrir -Arregla {take}");
                        return 0;
                    case TestList.TakeResult.Busy:
                        Console.Error.WriteLine($"#{take} ya lo tomó otra sesión ({WhoName(holder)}).");
                        return 2;
                    case TestList.TakeResult.NotOpen:
                        Console.Error.WriteLine($"#{take} no es un pedido ni está marcado como No anda, Anda mal o A mejorar.");
                        return 2;
                    default:
                        Console.Error.WriteLine($"La lista no tiene el #{take}.");
                        return 2;
                }
            case "soltar":
                int free = Number(a, "lista soltar número");
                if (!TestList.Release(path, free))
                {
                    Console.Error.WriteLine($"#{free} no es un pedido ni algo marcado de la lista.");
                    return 2;
                }
                Console.WriteLine($"#{free} queda libre.");
                return 0;
            case "arreglado":
                int id = Number(a, "lista arreglado número --copia nombre [--texto \"qué probar\"]");
                switch (TestList.Fixed(path, id, Copy(a), text))
                {
                    case TestList.FixResult.NotFound:
                        Console.Error.WriteLine($"La lista no tiene el #{id}.");
                        return 2;
                    case TestList.FixResult.Closed:
                        Console.Error.WriteLine($"#{id} ya está marcado como que anda: no vuelve a la lista. Si es otro cambio, anotalo aparte.");
                        return 2;
                }
                Console.WriteLine($"#{id} vuelve a estar para probar.");
                return 0;
            case "historial":
                int of = a.Positional.Count > 1 && int.TryParse(a.Positional[1].TrimStart('#'), out var h) ? h : 0;
                var events = TestList.History(path, of);
                if (of == 0) events = events.TakeLast(HistoryAll).ToList();
                if (events.Count == 0) Console.WriteLine(of == 0 ? "Sin historial." : $"Sin historial del #{of}.");
                foreach (var e in events)
                {
                    Console.WriteLine($"  {e.At:dd/MM HH:mm}  #{e.Id}  {WhoName(e.Who),-18} {WhatName(e)}");
                    if (e.Note != null) Console.WriteLine($"                       Nota: {e.Note}");
                }
                return 0;
            case "ticket":
                int show = Number(a, "lista ticket número");
                if (TestList.Read(path).Items.Find(i => i.Id == show) is not { } ticket)
                {
                    Console.Error.WriteLine($"La lista no tiene el #{show}.");
                    return 2;
                }
                ShowTicket(ticket);
                return 0;
            case "comentar":
                int to = Number(a, "lista comentar número \"qué decir\"");
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("lista comentar número \"qué decir\"");
                if (!TestList.Comment(path, to, text))
                {
                    Console.Error.WriteLine($"La lista no tiene el #{to}.");
                    return 2;
                }
                Console.WriteLine($"Dicho en el #{to}.");
                return 0;
            case "titulo":
                int named = Number(a, "lista titulo número \"título\" (vacío lo saca)");
                if (!TestList.SetTitle(path, named, text))
                {
                    Console.Error.WriteLine($"La lista no tiene el #{named}.");
                    return 2;
                }
                Console.WriteLine(string.IsNullOrWhiteSpace(text) ? $"#{named} queda sin título." : $"#{named}: {text.Trim()}");
                return 0;
            default:
                throw new ArgumentException("lista [ver | ticket número | agregar \"qué probar\" | arreglado número | pedir \"qué cambiar\" | comentar número \"qué\" | titulo número \"título\" | tema número tema | tipo número tipo | temas | tomar número | soltar número | historial [número] | comprobar] --archivo x --copia y [--tema t]");
        }
    }

    /// <summary>A ticket whole: its subject, state and conversation, as the bench shows it.</summary>
    static void ShowTicket(TestItem i)
    {
        string kind = i.Kind is { } k ? $" [{KindTitle(k)}]" : i.State == TestItem.Request ? " [sin tipo]" : "";
        Console.WriteLine($"#{i.Id}{kind} {{{TopicTitle(i.Topic)}}}  {StateName(i.State)} · intento {i.Tries} · copia {i.Build}{(i.Unseen ? " · nuevo, sin ver" : "")}");
        Console.WriteLine(i.Title is { } title ? $"  {title}" : $"  {i.Text}   (sin título: tools\\probar.ps1 -Titula {i.Id} \"título\")");
        if (i.State == TestItem.Request && i.Kind == null) Console.WriteLine($"  (sin tipo: tools\\probar.ps1 -Clasifica {i.Id} -Tipo error|mejora|funcion|idea)");
        foreach (var m in i.Thread) Console.WriteLine($"  {m.At:dd/MM HH:mm}  int. {m.Try}  {WhoName(m.Who),-16} {MessageText(m)}");
        if (i.Reply != null) Console.WriteLine($"  (el usuario está escribiendo{(i.Draft != null ? $", {StateName(i.Draft)}" : "")}: {i.Reply})");
    }

    static string MessageText(TestMessage m) => m.Kind switch
    {
        TestMessage.Open => $"abrió: {m.Text}",
        TestMessage.Fix => m.Text is { } t ? $"arreglo: {t}" : "mandó un arreglo",
        TestMessage.Verdict => (m.State is { } s ? StateName(s) : "nota") + (m.Text is { } n ? $": {n}" : ""),
        _ => $"dice: {m.Text}",
    };

    /// <summary>The title, with the subject after it; the subject alone while it has none.</summary>
    static string Subject(TestItem i) => i.Title is { } t ? $"{t} — {i.Text}" : i.Text;

    /// <summary>The last thing said in a ticket after it was opened: what an agent reading the list needs first.</summary>
    static TestMessage? Last(TestItem i) => i.Thread.Count > 1 ? i.Thread[^1] : null;

    /// <summary>How many lines <c>lista historial</c> shows without a number: the latest.</summary>
    const int HistoryAll = 40;

    static string WhoName(string? who) => who switch
    {
        null or "" => "?",
        TestList.User => "el usuario",
        TestList.SomeAgent => "un agente",
        _ when who.StartsWith("agent:", StringComparison.Ordinal) => "sesión " + who[6..],
        _ => who,
    };

    static string StateName(string? s) => s switch
    {
        TestItem.Pending => "Para probar",
        TestItem.Works => "Anda",
        TestItem.Fails => "No anda",
        TestItem.WorksBadly => "Anda mal",
        TestItem.Improve => "A mejorar",
        TestItem.Request => "Pedido",
        _ => s ?? "",
    };

    static string WhatName(TestEvent e) => e.What switch
    {
        TestEvent.Added => $"lo anotó: {e.Text}",
        TestEvent.Asked => $"lo pidió: {e.Text}",
        TestEvent.Edited => $"lo editó: {e.Text}",
        TestEvent.Moved => "lo volvió a anotar en una copia nueva",
        TestEvent.Took => "lo tomó",
        TestEvent.Released => "lo soltó",
        TestEvent.Fixed => $"mandó un arreglo: {e.Text}",
        TestEvent.FixRefused => "quiso mandar un arreglo, pero ya andaba (no se aplicó)",
        TestEvent.Marked => $"lo marcó: {StateName(e.State)}",
        TestEvent.Commented => "comentó",
        TestEvent.Removed => "lo sacó de la lista",
        TestEvent.Cleared => "lo sacó al limpiar lo que anda",
        TestEvent.Topic => $"le puso el tema: {TopicTitle(e.Note)}",
        TestEvent.Kinded => e.Note is { } k ? $"le puso el tipo: {KindTitle(k)}" : "le sacó el tipo",
        TestEvent.Titled => e.Note is { } t ? $"le puso el título: {t}" : "le sacó el título",
        _ => e.What,
    };

    /// <summary>Each topic (<see cref="TestItem.Topics"/>): what the option says, its title and what goes in it, so an agent picks the same one the bench shows.</summary>
    static readonly (string Topic, string Option, string Title, string About)[] TopicNames =
    [
        (TestItem.TopicTimeline, "timeline", "Timeline", "pistas, clips y su edición, carpetas, marcadores, grilla, bucle"),
        (TestItem.TopicMixer, "mixer", "Mixer", "tiras, faders, medidores, buses, envíos y ruteo"),
        (TestItem.TopicInspector, "inspector", "Inspector", "el panel de la pista elegida y su tira de abajo"),
        (TestItem.TopicPianoRoll, "pianoroll", "Piano roll y MIDI", "notas, herramientas, patrones, partitura, entradas MIDI"),
        (TestItem.TopicInstruments, "instrumentos", "Instrumentos", "Sampler, Granular, teclado virtual, pistas de instrumento"),
        (TestItem.TopicEffects, "efectos", "Efectos", "los efectos nativos de los slots y sus paneles"),
        (TestItem.TopicMastering, "mastering", "Mastering", "la cadena del master, Empuje, Destino, limitador"),
        (TestItem.TopicAutomation, "automatizacion", "Automatización", "carriles, bloques, puntos y curvas"),
        (TestItem.TopicPlugins, "plugins", "Plugins", "VST2 y VST3: ventanas, catálogo, carga, latencia"),
        (TestItem.TopicAudio, "audio", "Audio y transporte", "motor, ASIO, grabación, click, reproducción, rendimiento"),
        (TestItem.TopicVideo, "video", "Video", "cámara, preview, paneles de video"),
        (TestItem.TopicExport, "exportar", "Exportación", "exportar audio, stems y video"),
        (TestItem.TopicAnalysis, "analisis", "Análisis", "tempo, acordes, escala, extraer MIDI, separar en stems"),
        (TestItem.TopicProject, "proyecto", "Proyecto y archivos", "guardar, abrir, biblioteca, importar, deshacer, historial"),
        (TestItem.TopicInterface, "interfaz", "Interfaz", "apariencia, ventanas, menús, atajos, Configuración, idiomas, ayuda"),
        (TestItem.TopicBench, "banco", "Banco y herramientas", "XT-WorkBench, xtlab, skills, instalador"),
    ];

    static string TopicList => string.Join(" | ", TopicNames.Select(t => t.Option));

    /// <summary>The topic an option names; null without one. Also takes the stored id.</summary>
    static string? TopicOption(string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return null;
        string o = option.Trim().ToLowerInvariant();
        return TopicNames.FirstOrDefault(t => t.Option == o || t.Topic == o).Topic
            ?? throw new ArgumentException($"Tema desconocido: {option}. Temas: {TopicList}");
    }

    static string TopicTitle(string? topic) => TopicNames.FirstOrDefault(t => t.Topic == topic).Title ?? "sin tema";

    /// <summary>Each kind of request: what the option says, and its title in <c>lista ver</c>.</summary>
    static readonly (string Kind, string Option, string Title)[] KindNames =
    [
        (TestItem.Bug, "error", "Error"),
        (TestItem.Better, "mejora", "Mejora"),
        (TestItem.Feature, "funcion", "Función nueva"),
        (TestItem.Idea, "idea", "Idea"),
    ];

    static string KindTitle(string kind) => KindNames.FirstOrDefault(k => k.Kind == kind).Title ?? kind;

    static int Number(Args a, string usage) =>
        a.Positional.Count > 1 && int.TryParse(a.Positional[1].TrimStart('#'), out var n) ? n : throw new ArgumentException(usage);

    static string Copy(Args a) => a.Get("copia") ?? throw new ArgumentException("lista: falta --copia nombre");

    static void Show(TestListData d)
    {
        static string Taken(TestItem i) => i.Taken is { } t && TestList.Open(i)
            ? $"  (tomado {t:dd/MM HH:mm} por {(i.TakenBy == TestList.Who ? "esta sesión" : WhoName(i.TakenBy))}{(DateTime.Now - t >= TestList.TakeStale ? ", vencido" : "")})" : "";
        // the user has not seen it yet in the bench
        static string Fresh(TestItem i) => i.Unseen ? "  · nuevo, sin ver" : "";
        var requests = d.Items.Where(i => i.State == TestItem.Request).OrderBy(i => i.Id).ToList();
        Console.WriteLine($"Pedidos ({requests.Count})");
        foreach (var i in requests)
        {
            string kind = i.Kind is { } k ? KindTitle(k) : "Sin tipo";
            Console.WriteLine($"  #{i.Id}  [{kind}] {{{TopicTitle(i.Topic)}}}  {Subject(i)}{Taken(i)}{Fresh(i)}");
            if (Last(i) is { } m) Console.WriteLine($"       Último: {WhoName(m.Who)}, {MessageText(m)}");
        }
        // The workbench's tabs, by its names (TestListWindow.Tabs): A arreglar holds the three verdicts that need work.
        string[] fix = [TestItem.Fails, TestItem.WorksBadly, TestItem.Improve];
        Console.WriteLine($"A arreglar ({d.Items.Count(i => fix.Contains(i.State))})");
        foreach (var (state, title) in new[] { (TestItem.Fails, "No anda"), (TestItem.WorksBadly, "Anda mal"), (TestItem.Improve, "A mejorar") })
            Group(d.Items.Where(i => i.State == state).ToList(), title, "  ");
        Console.WriteLine($"Para probar ({d.Count(TestItem.Pending)})");
        Group(d.Items.Where(TestList.Back).ToList(), "Vuelven de un arreglo", "  ");
        Group(d.Items.Where(i => i.State == TestItem.Pending && !TestList.Back(i)).ToList(), "Primera vez", "  ");
        Console.WriteLine($"Resuelto ({d.Count(TestItem.Works)})");
        Console.WriteLine("La conversación entera de uno: tools\\probar.ps1 -Ticket número");
        int untitled = d.Items.Count(i => i.Title == null && TestList.Open(i));
        if (untitled > 0) Console.WriteLine($"Sin título: {untitled}. Al tomar uno, ponéselo: tools\\probar.ps1 -Titula número \"título\"");
        int loose = d.Items.Count(i => i.Topic == null);
        if (loose > 0) Console.WriteLine($"Sin tema: {loose}. Ponéselo: tools\\probar.ps1 -Clasifica número -Tema tema (los temas: xtlab lista temas)");

        void Group(List<TestItem> items, string title, string indent)
        {
            Console.WriteLine($"{indent}{title} ({items.Count})");
            foreach (var i in items)
            {
                Console.WriteLine($"{indent}  #{i.Id}  {{{TopicTitle(i.Topic)}}}  {Subject(i)}  (intento {i.Tries}, copia {i.Build}){Taken(i)}{Fresh(i)}");
                if (Last(i) is { } m) Console.WriteLine($"{indent}       Último: {WhoName(m.Who)}, {MessageText(m)}");
            }
        }
    }

    static int Check()
    {
        const string first = "20261102-100000-aaaaaaa", second = "20261102-110000-bbbbbbb";
        string dir = Path.Combine(Path.GetTempPath(), "xtlab-lista-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "lista.json");
        int fails = 0;
        void Row(string name, bool ok)
        {
            if (!ok) fails++;
            Console.WriteLine($"  {name,-58} {(ok ? "ok" : "DISTINTO")}");
        }
        string was = TestList.Who;
        TestList.Who = Mine;
        try
        {
            Row("sin archivo, la lista está vacía", TestList.Read(path).Items.Count == 0);
            int a = TestList.Add(path, "Exportar", first), b = TestList.Add(path, "Abrí \"Mixer\" · ñ", first);
            Row("cada ítem lleva su número y queda pendiente", a == 1 && b == 2 && TestList.Read(path).Count(TestItem.Pending) == 2);
            Row("los textos vuelven iguales", TestList.Read(path).Items[1].Text == "Abrí \"Mixer\" · ñ");
            int again = TestList.Add(path, "Exportar", second);
            var d = TestList.Read(path);
            Row("el mismo texto pendiente no se repite y pasa a la copia nueva", again == a && d.Items.Count == 2 && d.Items[0].Build == second);
            Row("una copia vieja no tiene el cambio, una nueva sí",
                TestList.Missing(d.Items[0], first) && !TestList.Missing(d.Items[0], second) && !TestList.Missing(d.Items[1], second));

            TestList.Hold(path, a, TestItem.WorksBadly, " a medias ");
            d = TestList.Read(path);
            Row("una respuesta sin guardar no mueve el ítem", d.Items[0].State == TestItem.Pending && d.Items[0].Draft == TestItem.WorksBadly && d.Items[0].Reply == "a medias");
            TestList.Mark(path, a, TestItem.Fails, " se corta el audio ");
            d = TestList.Read(path);
            Row("el veredicto y la nota quedan, y la respuesta se va", d.Items[0] is { State: TestItem.Fails, Draft: null, Reply: null, Note: "se corta el audio" });
            Row("arreglar lo vuelve a pendiente", TestList.Fixed(path, a, second, "Exportar otra vez") == TestList.FixResult.Done);
            d = TestList.Read(path);
            Row("otro intento, con la nota vieja en la conversación y no en el cuadro",
                d.Items[0] is { State: TestItem.Pending, Tries: 2, Note: null, Text: "Exportar" } && TestList.Back(d.Items[0]));
            Row("la conversación cuenta todo en orden",
                d.Items[0].Thread.Select(m => m.Kind).SequenceEqual([TestMessage.Open, TestMessage.Verdict, TestMessage.Fix])
                && d.Items[0].Thread[1] is { State: TestItem.Fails, Text: "se corta el audio", Try: 1 }
                && d.Items[0].Thread[2] is { Text: "Exportar otra vez", Try: 2, Who: Mine });
            Row("arreglar algo que no está dice que no", TestList.Fixed(path, 99, second) == TestList.FixResult.NotFound);

            int r = TestList.AddRequest(path, "Un pedido", TestItem.Bug, second);
            Row("un pedido lleva número, tipo y no cuenta intentos", r == 3 && TestList.Read(path).Items[^1] is { State: TestItem.Request, Tries: 0, Kind: TestItem.Bug });
            Row("el mismo pedido no se repite y toma el tipo nuevo",
                TestList.AddRequest(path, " Un pedido ", TestItem.Idea, second) == r && TestList.Read(path).Items[^1].Kind == TestItem.Idea);
            Row("editar un pedido", TestList.EditRequest(path, r, "Un pedido mejor", TestItem.Feature)
                && TestList.Read(path).Items[^1] is { Text: "Un pedido mejor", Kind: TestItem.Feature } && !TestList.EditRequest(path, a, "x", TestItem.Bug));
            int topicRev = TestList.Read(path).Items[^1].Rev;
            Row("un tema se pone sin cambiar la versión ni dar \"Nuevo\"", As(TestList.User, () => { TestList.See(path, [r]); return true; })
                && TestList.SetTopic(path, r, TestItem.TopicMixer) && TestList.Read(path).Items[^1] is { Topic: TestItem.TopicMixer, Unseen: false } tp && tp.Rev == topicRev
                && TestList.History(path, r)[^1] is { What: TestEvent.Topic, Note: TestItem.TopicMixer });
            bool unknown = false;
            try { TestList.SetTopic(path, r, "cualquiera"); }
            catch (ArgumentException) { unknown = true; }
            Row("un tema fuera de la lista no entra", unknown && TestList.Read(path).Items[^1].Topic == TestItem.TopicMixer && !TestList.SetTopic(path, 99, null));
            Row("editar el pedido sin tema lo deja; con \"\" lo saca", TestList.EditRequest(path, r, "Un pedido mejor", TestItem.Feature)
                && TestList.Read(path).Items[^1].Topic == TestItem.TopicMixer
                && TestList.EditRequest(path, r, "Un pedido mejor", TestItem.Feature, topic: "") && TestList.Read(path).Items[^1].Topic == null);
            int kindRev = TestList.Read(path).Items[^1].Rev;
            Row("un tipo se pone sin cambiar la versión y queda en el historial", TestList.SetKind(path, r, TestItem.Bug)
                && TestList.Read(path).Items[^1] is { Kind: TestItem.Bug } tk && tk.Rev == kindRev
                && TestList.History(path, r)[^1] is { What: TestEvent.Kinded, Note: TestItem.Bug } && !TestList.SetKind(path, a, TestItem.Bug));
            Row("editar o repetir el pedido sin tipo lo deja", TestList.EditRequest(path, r, "Un pedido mejor", null)
                && TestList.AddRequest(path, "Un pedido mejor", null, second) == r && TestList.Read(path).Items[^1].Kind == TestItem.Bug
                && TestList.SetKind(path, r, TestItem.Feature));
            // the same text again, so no number is spent (the numbers below are counted)
            Row("anotar con tema", TestList.AddRequest(path, "Un pedido mejor", TestItem.Feature, second, TestItem.TopicBench) == r
                && TestList.Read(path).Items[^1].Topic == TestItem.TopicBench
                && TestList.Add(path, "Exportar", second, TestItem.TopicExport) == a && TestList.Read(path).Items[0].Topic == TestItem.TopicExport);
            Row("tomar un pedido", TestList.Take(path, r) == TestList.TakeResult.Taken && TestList.Read(path).Items[^1].Taken != null);
            Row("la misma sesión lo puede volver a tomar", TestList.Take(path, r) == TestList.TakeResult.Taken);
            Row("otra sesión no lo puede tomar y sabe quién lo tiene", As(Other, () => TestList.Take(path, r, out var by) == TestList.TakeResult.Busy && by == Mine));
            Row("lo pendiente no se toma", TestList.Take(path, a) == TestList.TakeResult.NotOpen && TestList.Take(path, 99) == TestList.TakeResult.NotFound);
            Row("soltarlo lo deja libre", TestList.Release(path, r) && TestList.Take(path, r) == TestList.TakeResult.Taken);
            TestList.Mark(path, b, TestItem.Improve, "más grande");
            Row("lo marcado también se toma, una vez", TestList.Take(path, b) == TestList.TakeResult.Taken && As(Other, () => TestList.Take(path, b) == TestList.TakeResult.Busy));
            Row("y se suelta", TestList.Release(path, b) && TestList.Read(path).Items[1].Taken == null);
            TestList.Fixed(path, r, second, "Probar el pedido");
            d = TestList.Read(path);
            Row("hecho, pasa a pendiente como primer intento", d.Items[^1] is { State: TestItem.Pending, Tries: 1, Taken: null, Text: "Un pedido mejor" }
                && d.Items[^1].Thread[0].Text == "Un pedido mejor" && d.Items[^1].Thread[^1].Text == "Probar el pedido");
            Row("un agente pregunta en un ticket: le llega como nuevo", As(TestList.User, () => { TestList.See(path, [r]); return true; })
                && TestList.Comment(path, r, " ¿Va en el mixer? ") && TestList.Read(path).Items[^1] is { Unseen: true, State: TestItem.Pending } t1
                && t1.Thread[^1] is { Kind: TestMessage.Comment, Text: "¿Va en el mixer?" });
            Row("el usuario responde sin cambiar el estado", As(TestList.User, () => TestList.Comment(path, r, "Sí"))
                && TestList.Read(path).Items[^1] is { State: TestItem.Pending, Unseen: true } t2 && t2.Thread[^1].Who == TestList.User);
            Row("un comentario vacío no se guarda", !TestList.Comment(path, r, "  ") && !TestList.Comment(path, 99, "x"));
            int rev = TestList.Read(path).Items[^1].Rev;
            Row("el agente le pone título sin tocar el ticket", TestList.SetTitle(path, r, " Pedido corto ")
                && TestList.Read(path).Items[^1] is { Title: "Pedido corto", Text: "Un pedido mejor" } t3 && t3.Rev == rev
                && TestList.History(path, r)[^1] is { What: TestEvent.Titled, Note: "Pedido corto" });
            Row("un título vacío lo saca", TestList.SetTitle(path, r, " ") && TestList.Read(path).Items[^1].Title == null && !TestList.SetTitle(path, 99, "x"));
            TestList.Remove(path, r);

            TestList.Mark(path, b, TestItem.Works, null);
            Row("lo que anda no vuelve con un arreglo tardío",
                TestList.Fixed(path, b, second, "otra cosa") == TestList.FixResult.Closed && TestList.Read(path).Items[1] is { State: TestItem.Works, Text: "Abrí \"Mixer\" · ñ" });
            TestList.Hold(path, b, TestItem.Works, "nota tarde");
            Row("una respuesta guardada tarde no reabre el veredicto", TestList.Read(path).Items[1] is { State: TestItem.Works, Draft: null, Reply: "nota tarde" });
            TestList.ClearWorking(path);
            int next = TestList.Add(path, "Otra cosa", second);
            d = TestList.Read(path);
            Row("limpiar saca lo que anda y no reusa números", d.Items.Count == 2 && next == 4);
            TestList.Remove(path, next);
            Row("sacar un ítem", TestList.Read(path).Items.Count == 1);

            int n = TestList.Add(path, "Algo de un agente", second);
            Row("lo que anota un agente llega como nuevo", TestList.Read(path).Items[^1].Unseen);
            int seenRev = TestList.Read(path).Items[^1].Rev;
            As(TestList.User, () => { TestList.See(path, [n]); return true; });
            Row("visto, deja de ser nuevo sin cambiar su versión", TestList.Read(path).Items[^1] is { Unseen: false } it && it.Rev == seenRev);
            Row("lo que pide el usuario no es nuevo para él", As(TestList.User, () => TestList.AddRequest(path, "Mío", TestItem.Idea, second) > 0) && !TestList.Read(path).Items[^1].Unseen);
            TestList.Mark(path, n, TestItem.Fails, "no");
            int shown = TestList.Read(path).Items.Find(i => i.Id == n)!.Rev;
            TestList.Fixed(path, n, second, "Algo arreglado");
            Row("un arreglo vuelve como nuevo", TestList.Read(path).Items.Find(i => i.Id == n)!.Unseen);
            bool stale = false;
            try { As(TestList.User, () => { TestList.Mark(path, n, TestItem.Fails, "vieja", shown); return true; }); }
            catch (TestListStaleException) { stale = true; }
            Row("un clic sobre la tarjeta vieja no pisa el arreglo", stale && TestList.Read(path).Items.Find(i => i.Id == n)!.State == TestItem.Pending);
            int now = TestList.Read(path).Items.Find(i => i.Id == n)!.Rev;
            As(TestList.User, () => { TestList.Mark(path, n, TestItem.Works, null, now); return true; });
            Row("con la tarjeta al día, el clic vale", TestList.Read(path).Items.Find(i => i.Id == n)!.State == TestItem.Works);
            var story = TestList.History(path, n);
            Row("el historial cuenta todo, en orden y con quién",
                story.Select(e => e.What).SequenceEqual([TestEvent.Added, TestEvent.Marked, TestEvent.Fixed, TestEvent.Marked])
                && story[0].Who == Mine && story[^1].Who == TestList.User && story[^1].State == TestItem.Works);
            TestList.Fixed(path, n, second);
            Row("un arreglo rechazado también queda", TestList.History(path, n)[^1].What == TestEvent.FixRefused);
            TestList.ClearWorking(path);
            Row("lo sacado queda en el historial", TestList.History(path, n)[^1].What == TestEvent.Cleared && TestList.History(path).Any(e => e.What == TestEvent.Removed && e.Id == r));
            File.AppendAllText(TestList.HistoryPath(path), "{ línea rota\n");
            Row("una línea rota del historial no rompe el resto", TestList.History(path, n).Count == 6);
            string old = Path.Combine(dir, "vieja.json");
            File.WriteAllText(old, """
                {"next":5,"items":[
                  {"id":1,"text":"Volvió","state":"pending","note":"se cortaba","tries":2,"at":"2026-11-03T10:00:00"},
                  {"id":2,"text":"Marcado","state":"fail","note":"no","tries":1,"at":"2026-11-03T10:00:00"},
                  {"id":3,"text":"Escribiendo","state":"pending","note":"a medias","draft":"bad","tries":1,"at":"2026-11-03T10:00:00"},
                  {"id":4,"text":"Pedido","kind":"bug","state":"request","tries":0,"at":"2026-11-03T10:00:00"}]}
                """);
            var mig = TestList.Read(old).Items;
            Row("una lista vieja pasa a tickets: la nota de antes va a la conversación",
                mig[0] is { Note: null, Reply: null } && mig[0].Thread.Select(m => m.Kind).SequenceEqual([TestMessage.Open, TestMessage.Verdict, TestMessage.Fix])
                && mig[0].Thread[1] is { Text: "se cortaba", Try: 1 });
            Row("el veredicto queda con su nota y lo que se escribía, como respuesta",
                mig[1] is { Note: "no" } && mig[1].Thread[^1] is { Kind: TestMessage.Verdict, State: TestItem.Fails }
                && mig[2] is { Note: null, Reply: "a medias", Draft: TestItem.WorksBadly } && mig[3].Thread[0].Who == TestList.User);

            foreach (var i in TestList.Read(path).Items.ToList()) TestList.Remove(path, i.Id);
            TestList.Add(path, "Exportar", second);

            Parallel.For(0, 8, w =>
            {
                for (int i = 0; i < 10; i++) TestList.Add(path, $"sesión {w} cambio {i}", second);
                TestList.Read(path);
            });
            d = TestList.Read(path);
            Row("ocho procesos a la vez no pierden nada", d.Items.Count == 81 && d.Items.Select(i => i.Id).Distinct().Count() == 81);

            File.WriteAllText(path, "{ roto");
            TestList.Add(path, "Después de romperse", second);
            Row("un archivo roto se guarda aparte", File.Exists(path + ".roto") && TestList.Read(path).Items.Count == 1);
        }
        finally
        {
            TestList.Who = was;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        Console.WriteLine(fails == 0 ? "Lista: todo bien." : $"Lista: {fails} comprobaciones fallaron.");
        return fails == 0 ? 0 : 3;
    }

    /// <summary>The two agent sessions the checks play.</summary>
    const string Mine = "agent:aaaaaaaa", Other = "agent:bbbbbbbb";

    /// <summary>Runs as someone else, then back to <see cref="Mine"/>.</summary>
    static bool As(string who, Func<bool> act)
    {
        TestList.Who = who;
        try { return act(); }
        finally { TestList.Who = Mine; }
    }
}
