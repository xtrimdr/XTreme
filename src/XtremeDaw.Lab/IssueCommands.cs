using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>xtlab reporte: the link "Enviar comentarios" opens (<see cref="IssueReport"/>), without opening anything.</summary>
static class IssueCommands
{
    public static int Run(Args a)
    {
        string action = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "";
        if (action == "comprobar") return Check();
        if (action != "enlace" || a.Positional.Count < 2) throw new ArgumentException("reporte [enlace \"título\" [--texto t] [--idea] | comprobar]");
        var kind = a.Flag("idea") ? IssueReport.Kind.Idea : IssueReport.Kind.Bug;
        string url = IssueReport.Url(IssueReport.Title(kind, a.Positional[1]), IssueReport.Body(a.Get("texto") ?? "", []), out bool complete);
        Console.WriteLine(url);
        if (!complete) Console.WriteLine("El texto no entra en el enlace: va sin él.");
        return 0;
    }

    /// <summary>The value of a query parameter, decoded the way GitHub reads it.</summary>
    static string? Param(string url, string name)
    {
        string query = new Uri(url).Query.TrimStart('?');
        foreach (string pair in query.Split('&'))
            if (pair.StartsWith(name + "=", StringComparison.Ordinal)) return Uri.UnescapeDataString(pair[(name.Length + 1)..]);
        return null;
    }

    static int Check()
    {
        int fails = 0;
        void Row(string name, bool ok)
        {
            if (!ok) fails++;
            Console.WriteLine($"  {name,-58} {(ok ? "ok" : "DISTINTO")}");
        }

        Row("cada tipo va en el título", IssueReport.Title(IssueReport.Kind.Bug, " Se corta ") == "[Bug] Se corta" && IssueReport.Title(IssueReport.Kind.Idea, "Otra") == "[Idea] Otra");
        Row("los datos van debajo de una línea, uno por renglón", IssueReport.Body(" Hola\r\nchau ", ["XTreme 1.0", "ASIO: x"]) == "Hola\nchau\n\n---\n- XTreme 1.0\n- ASIO: x");
        Row("sin datos, solo el texto", IssueReport.Body("Hola", []) == "Hola");

        const string title = "[Bug] ¿Clic & corte? #3 100% ñ", body = "Línea 1\n\n- a+b=c & más\n🎸 fin";
        string url = IssueReport.Url(title, body, out bool complete);
        Row("va a la página de issues del repositorio", url.StartsWith("https://github.com/xtrimdr/XTreme/issues/new?title=", StringComparison.Ordinal));
        Row("título y texto vuelven iguales", complete && Param(url, "title") == title && Param(url, "body") == body);
        Row("el enlace no lleva nada sin codificar", new Uri(url).Query.All(c => char.IsAsciiLetterOrDigit(c) || "?&=%-_.~".Contains(c)));

        string fits = new('a', IssueReport.MaxUrl - 200), over = new('ñ', IssueReport.MaxUrl);
        IssueReport.Url("x", fits, out bool shortOk);
        string cut = IssueReport.Url(title, over, out bool longOk);
        Row("un texto que entra va entero", shortOk);
        Row("uno largo no entra: va solo el título y lo avisa", !longOk && Param(cut, "body") == null && Param(cut, "title") == title && cut.Length <= IssueReport.MaxUrl);

        Console.WriteLine(fails == 0 ? "Reporte: todo bien." : $"Reporte: {fails} comprobaciones fallaron.");
        return fails == 0 ? 0 : 3;
    }
}
