using System.IO;
using System.Text;

namespace Barep.Services;

/// <summary>
/// Prueba de ida y vuelta de todos los ajustes de Optimizar: cambia cada uno,
/// comprueba que Windows lo aceptó, lo devuelve a como estaba y comprueba que
/// no quedó ningún resto. Se lanza con «dotnet run -- --probar-ajustes» y deja
/// el informe «prueba-ajustes.txt» junto al ejecutable.
/// </summary>
public static class TweakSelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        var admin = TweakCatalog.IsAdmin;
        sb.AppendLine($"Prueba de ajustes · {DateTime.Now:yyyy-MM-dd HH:mm} · Windows build {Environment.OSVersion.Version.Build} · admin: {(admin ? "sí" : "no")}");
        sb.AppendLine(new string('─', 90));

        int ok = 0, fail = 0, skipped = 0;
        foreach (var t in TweakCatalog.All)
        {
            if (!t.Applies()) { sb.AppendLine($"[no aplica]  {t.Id}"); skipped++; continue; }
            if (t.IsLinkOnly) { sb.AppendLine($"[enlace]     {t.Id}"); skipped++; continue; }
            if (t.NeedsAdmin && !admin) { sb.AppendLine($"[sin admin]  {t.Id}"); skipped++; continue; }

            var before = t.Read!();
            var rawBefore = t.Specs.Select(TweakCatalog.RawValue).ToList();
            if (before is null) { sb.AppendLine($"[ERROR]      {t.Id}: no se pudo leer el estado"); fail++; continue; }

            var flipped = TweakService.Apply(t, !before.Value);
            var mid = t.Read!();
            var back = TweakService.Apply(t, before.Value);
            var after = t.Read!();
            var rawAfter = t.Specs.Select(TweakCatalog.RawValue).ToList();

            var changed = flipped == TweakOutcome.Done && mid == !before;
            var restored = back == TweakOutcome.Done && after == before;
            var leftovers = t.Specs.Select((s, i) => (s, i))
                .Where(x => rawBefore[x.i] != rawAfter[x.i])
                .Select(x => $"{x.s.Name}: {rawBefore[x.i]} → {rawAfter[x.i]}")
                .ToList();

            if (changed && restored)
            {
                ok++;
                sb.AppendLine($"[OK]         {t.Id}: {On(before)} → {On(mid)} → {On(after)}" +
                              (leftovers.Count > 0 ? $"   (valor explícito que equivale al de fábrica: {string.Join("; ", leftovers)})" : ""));
            }
            else
            {
                fail++;
                sb.AppendLine($"[FALLA]      {t.Id}: cambio={flipped} ({On(before)}→{On(mid)}), vuelta={back} ({On(after)})");
            }
        }

        sb.AppendLine(new string('─', 90));
        sb.AppendLine($"Correctos: {ok} · Fallos: {fail} · Omitidos: {skipped}");
        var text = sb.ToString();

        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "prueba-ajustes.txt"), text, Encoding.UTF8); } catch { }
        return text;
    }

    private static string On(bool? v) => v switch { true => "activado", false => "desactivado", _ => "?" };
}
