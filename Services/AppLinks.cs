using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Barep.Services;

/// <summary>Cuánto hay que fijarse en un vínculo: dato informativo o algo a tener en cuenta.</summary>
public enum LinkLevel { Info, Care }

/// <summary>Algo del equipo que depende de la aplicación o está conectado con ella.</summary>
public sealed record AppLinkItem(string Title, string Detail, LinkLevel Level);

/// <summary>Todo lo que se encontró enlazado con la aplicación.</summary>
public sealed record AppLinkReport(List<AppLinkItem> Items, List<AppFolder> Data, bool Running, long DataSize)
{
    public bool HasCare => Items.Any(i => i.Level == LinkLevel.Care);
}

/// <summary>
/// Antes de borrar una aplicación «por completo» conviene saber con qué está
/// enlazada: si está abierta, si arranca con Windows, si instaló un servicio,
/// si otros programas viven dentro de su carpeta o si es la que abre cierto
/// tipo de archivos. Todo se lee, nada se cambia.
/// </summary>
public static class AppLinks
{
    /// <summary>Programas que otros necesitan para funcionar: quitarlos rompe cosas ajenas.</summary>
    private static readonly Regex Runtime = new(
        @"Visual C\+\+|\.NET|dotnet|Java|JRE|JDK|DirectX|Redistributable|Runtime|Framework|Python|Node\.js|" +
        @"WebView2|Visual Studio Tools|OpenAL|PhysX|Mono|Electron",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static AppLinkReport Inspect(AppCard app, IReadOnlyList<AppCard> all, CancellationToken ct = default)
    {
        var items = new List<AppLinkItem>();
        var location = Normalize(app.InstallLocation);
        var tokens = Tokens(app.Name);

        var running = Running(app, location, tokens, items);
        ct.ThrowIfCancellationRequested();

        if (app.Origin == AppOrigin.Store)
        {
            items.Add(new AppLinkItem("Viene de la Microsoft Store",
                "Windows la quita entera, sin dejar nada en el registro. Puedes volver a instalarla cuando quieras.",
                LinkLevel.Info));
        }
        else
        {
            Services(location, items);
            ct.ThrowIfCancellationRequested();
            Startup(location, tokens, items);
            ct.ThrowIfCancellationRequested();
            Tasks(location, items);
            ct.ThrowIfCancellationRequested();
            Associations(location, items);
            ct.ThrowIfCancellationRequested();
            Neighbours(app, all, location, items);
        }

        if (Runtime.IsMatch(app.Name))
            items.Add(new AppLinkItem("Otros programas pueden necesitarla",
                "Parece un complemento o un motor que otras aplicaciones usan para funcionar. Si lo quitas, " +
                "alguna puede dejar de abrirse; se vuelve a instalar descargándolo otra vez.", LinkLevel.Care));

        var data = AppCatalog.DataFolders(app);
        var dataSize = data.Sum(d => d.Size);
        if (data.Count > 0)
            items.Add(new AppLinkItem("Deja datos tuyos en el equipo",
                $"{UiText.Plural(data.Count, "carpeta", "carpetas")} con tu configuración y tus archivos de la app " +
                $"({UiText.Size(dataSize)}). Al desinstalar te los enseño para que decidas si borrarlos.",
                LinkLevel.Info));

        if (items.Count == 0)
            items.Add(new AppLinkItem("No se encontró nada enlazado",
                "No está abierta, no arranca con Windows y no vi servicios, tareas ni otros programas que " +
                "dependan de ella.", LinkLevel.Info));

        return new AppLinkReport(items, data, running, dataSize);
    }

    // ────────────────────────── Está abierta ──────────────────────────

    private static bool Running(AppCard app, string? location, List<string> tokens, List<AppLinkItem> items)
    {
        var names = new List<string>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var path = ProcessService.PathOf(process.Id);
                    var inside = path is not null && location is not null &&
                                 Normalize(path)!.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase);

                    // Sin ruta sólo se acepta si el nombre del proceso es una
                    // palabra del nombre de la aplicación: «spotify» → Spotify.
                    var byName = path is null && tokens.Any(t =>
                        process.ProcessName.Equals(t, StringComparison.OrdinalIgnoreCase));

                    if ((inside || byName) && !names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        names.Add(process.ProcessName);
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
        }
        catch { }

        if (names.Count == 0) return false;

        items.Add(new AppLinkItem("Se está ejecutando ahora mismo",
            $"{UiText.Plural(names.Count, "proceso abierto", "procesos abiertos")}: {string.Join(", ", names.Take(6))}. " +
            "El desinstalador pedirá cerrarlos; guarda lo que tengas a medias.", LinkLevel.Care));
        return true;
    }

    // ────────────────────────── Servicios ──────────────────────────

    private static void Services(string? location, List<AppLinkItem> items)
    {
        if (location is null) return;
        var found = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DisplayName, PathName, State FROM Win32_Service");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                try
                {
                    var path = obj["PathName"]?.ToString();
                    if (path is null) continue;
                    var (file, _) = AppManager.SplitCommand(path);
                    if (file is null || Normalize(file) is not { } normalized) continue;
                    if (!normalized.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase)) continue;

                    var name = obj["DisplayName"]?.ToString() ?? "servicio sin nombre";
                    var state = obj["State"]?.ToString();
                    found.Add(state == "Running" ? $"{name} (en marcha)" : name);
                }
                catch { }
                finally { obj.Dispose(); }
            }
        }
        catch { return; }

        if (found.Count > 0)
            items.Add(new AppLinkItem("Instaló un servicio de Windows",
                $"{string.Join(", ", found.Take(5))}. Se quita con la aplicación; si queda, no hace nada.",
                LinkLevel.Care));
    }

    // ────────────────────────── Arranque de Windows ──────────────────────────

    private static void Startup(string? location, List<string> tokens, List<AppLinkItem> items)
    {
        var found = new List<string>();

        void Scan(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name)?.ToString();
                    if (value is null) continue;
                    var (file, _) = AppManager.SplitCommand(value);
                    var normalized = Normalize(file);

                    var inside = location is not null && normalized is not null &&
                                 normalized.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase);
                    var byName = tokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

                    if ((inside || byName) && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                        found.Add(name);
                }
            }
            catch { }
        }

        Scan(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        Scan(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        try
        {
            using var wow = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            Scan(wow, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        }
        catch { }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                 })
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (tokens.Any(t => stem.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
                        !found.Contains(stem, StringComparer.OrdinalIgnoreCase))
                        found.Add(stem);
                }
            }
            catch { }
        }

        if (found.Count > 0)
            items.Add(new AppLinkItem("Se abre al iniciar Windows",
                $"Entradas de arranque: {string.Join(", ", found.Take(5))}. Dejarán de ejecutarse al quitarla.",
                LinkLevel.Info));
    }

    // ────────────────────────── Tareas programadas ──────────────────────────

    private static void Tasks(string? location, List<AppLinkItem> items)
    {
        if (location is null) return;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        var found = new List<string>();

        try
        {
            if (!Directory.Exists(root)) return;
            // System.Management también declara EnumerationOptions: hay que decir cuál.
            foreach (var file in Directory.EnumerateFiles(root, "*", new System.IO.EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 256 * 1024) continue;
                    var text = File.ReadAllText(file);
                    if (text.Contains(location, StringComparison.OrdinalIgnoreCase))
                        found.Add(Path.GetFileName(file));
                }
                catch { }
                if (found.Count >= 6) break;
            }
        }
        catch { return; }

        if (found.Count > 0)
            items.Add(new AppLinkItem("Tiene tareas programadas",
                $"{string.Join(", ", found)}. Suelen ser actualizadores automáticos; se van con la aplicación.",
                LinkLevel.Info));
    }

    // ────────────────────────── Tipos de archivo ──────────────────────────

    private static void Associations(string? location, List<AppLinkItem> items)
    {
        if (location is null) return;
        var extensions = new List<string>();

        try
        {
            using var classes = Registry.ClassesRoot;
            var progIds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            bool PointsHere(string progId)
            {
                if (progIds.TryGetValue(progId, out var cached)) return cached;

                var result = false;
                try
                {
                    using var command = classes.OpenSubKey($@"{progId}\shell\open\command");
                    var value = command?.GetValue(null)?.ToString();
                    var (file, _) = AppManager.SplitCommand(value);
                    var normalized = Normalize(file);
                    result = normalized is not null &&
                             normalized.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase);
                }
                catch { }

                progIds[progId] = result;
                return result;
            }

            foreach (var name in classes.GetSubKeyNames())
            {
                if (name.Length < 2 || name[0] != '.') continue;
                try
                {
                    using var key = classes.OpenSubKey(name);
                    var progId = key?.GetValue(null)?.ToString();
                    if (string.IsNullOrWhiteSpace(progId)) continue;
                    if (PointsHere(progId)) extensions.Add(name.ToLowerInvariant());
                }
                catch { }
                if (extensions.Count >= 12) break;
            }
        }
        catch { return; }

        if (extensions.Count > 0)
            items.Add(new AppLinkItem("Es la que abre estos archivos",
                $"{string.Join(", ", extensions)}. Al quitarla, Windows te preguntará con qué abrirlos.",
                LinkLevel.Care));
    }

    // ────────────────────────── Programas vecinos ──────────────────────────

    private static void Neighbours(AppCard app, IReadOnlyList<AppCard> all, string? location,
        List<AppLinkItem> items)
    {
        if (location is null) return;

        var inside = new List<string>();
        var sameFolder = new List<string>();
        var parent = Normalize(Path.GetDirectoryName(location));

        foreach (var other in all)
        {
            if (ReferenceEquals(other, app) || other.Id == app.Id) continue;
            var otherLocation = Normalize(other.InstallLocation);
            if (otherLocation is null) continue;

            if (otherLocation.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase))
                inside.Add(other.Name);
            else if (parent is not null && otherLocation.Equals(location, StringComparison.OrdinalIgnoreCase))
                sameFolder.Add(other.Name);
        }

        if (inside.Count > 0)
            items.Add(new AppLinkItem("Dentro de su carpeta hay más programas",
                $"{string.Join(", ", inside.Take(6))}" + (inside.Count > 6 ? $" y {inside.Count - 6} más" : "") +
                ". Si borras la carpeta entera, también se van. Pasa con las tiendas de juegos y sus juegos.",
                LinkLevel.Care));

        if (sameFolder.Count > 0)
            items.Add(new AppLinkItem("Comparte carpeta con otro programa",
                $"{string.Join(", ", sameFolder.Take(4))}. Esa carpeta no se borra para no llevárselo por delante.",
                LinkLevel.Care));
    }

    // ────────────────────────── Apoyo ──────────────────────────

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return SpaceScanner.Normalize(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))); }
        catch { return null; }
    }

    /// <summary>Palabras del nombre con las que se puede reconocer la aplicación sin confundirla con otra.</summary>
    private static List<string> Tokens(string name) =>
        Regex.Split(name, @"[^\p{L}\p{N}]+")
            .Where(t => t.Length >= 4 && !t.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
}
