using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// wtlab lista: the changes waiting to be tried (<see cref="TestList"/>). <c>tools\probar.ps1</c> runs it to add to the
/// list and to read it. The text can come in <see cref="TextVar"/>: Windows PowerShell breaks quotes inside an argument.
/// </summary>
static class TestListCommands
{
    const string TextVar = "XTREME_PROBAR_TEXTO";

    public static int Run(Args a)
    {
        string action = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "ver";
        if (action == "comprobar") return Check();
        string path = a.Get("archivo") ?? throw new ArgumentException("lista: falta --archivo pruebas\\lista.json");
        string? text = Environment.GetEnvironmentVariable(TextVar) ?? (a.Positional.Count > 1 && action == "agregar" ? a.Positional[1] : a.Get("texto"));
        switch (action)
        {
            case "ver":
                Show(TestList.Read(path));
                return 0;
            case "agregar":
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("lista agregar \"qué probar\" --copia nombre");
                Console.WriteLine($"En la lista: #{TestList.Add(path, text, Copy(a))}");
                return 0;
            case "arreglado":
                int id = a.Positional.Count > 1 && int.TryParse(a.Positional[1].TrimStart('#'), out var n)
                    ? n : throw new ArgumentException("lista arreglado número --copia nombre [--texto \"qué probar\"]");
                if (!TestList.Fixed(path, id, Copy(a), text))
                {
                    Console.Error.WriteLine($"La lista no tiene el #{id}.");
                    return 2;
                }
                Console.WriteLine($"#{id} vuelve a estar para probar.");
                return 0;
            default:
                throw new ArgumentException("lista [ver | agregar \"qué probar\" | arreglado número | comprobar] --archivo x --copia y");
        }
    }

    static string Copy(Args a) => a.Get("copia") ?? throw new ArgumentException("lista: falta --copia nombre");

    static void Show(TestListData d)
    {
        foreach (var (state, title) in new[] { (TestItem.Fails, "No anda"), (TestItem.Pending, "Por probar") })
        {
            var items = d.Items.Where(i => i.State == state).ToList();
            Console.WriteLine($"{title} ({items.Count})");
            foreach (var i in items)
            {
                Console.WriteLine($"  #{i.Id}  {i.Text}  (intento {i.Tries}, copia {i.Build})");
                if (i.Note != null) Console.WriteLine($"       Nota: {i.Note}");
            }
        }
        Console.WriteLine($"Anda ({d.Count(TestItem.Works)})");
    }

    static int Check()
    {
        const string first = "20261102-100000-aaaaaaa", second = "20261102-110000-bbbbbbb";
        string dir = Path.Combine(Path.GetTempPath(), "wtlab-lista-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "lista.json");
        int fails = 0;
        void Row(string name, bool ok)
        {
            if (!ok) fails++;
            Console.WriteLine($"  {name,-58} {(ok ? "ok" : "DISTINTO")}");
        }
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

            TestList.Mark(path, a, TestItem.Fails, " se corta el audio ");
            d = TestList.Read(path);
            Row("el veredicto y la nota quedan", d.Items[0].State == TestItem.Fails && d.Items[0].Note == "se corta el audio");
            Row("arreglar lo vuelve a pendiente", TestList.Fixed(path, a, second, "Exportar otra vez"));
            d = TestList.Read(path);
            Row("con otro intento, la nota y el texto nuevo",
                d.Items[0].State == TestItem.Pending && d.Items[0].Tries == 2 && d.Items[0].Note == "se corta el audio" && d.Items[0].Text == "Exportar otra vez");
            Row("arreglar algo que no está dice que no", !TestList.Fixed(path, 99, second));

            TestList.Mark(path, b, TestItem.Works, null);
            TestList.ClearWorking(path);
            int next = TestList.Add(path, "Otra cosa", second);
            d = TestList.Read(path);
            Row("limpiar saca lo que anda y no reusa números", d.Items.Count == 2 && next == 3);
            TestList.Remove(path, next);
            Row("sacar un ítem", TestList.Read(path).Items.Count == 1);

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
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        Console.WriteLine(fails == 0 ? "Lista: todo bien." : $"Lista: {fails} comprobaciones fallaron.");
        return fails == 0 ? 0 : 3;
    }
}
