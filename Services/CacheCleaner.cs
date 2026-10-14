using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace Barep.Services;

/// <summary>Una categoría de caché que se puede limpiar, ya medida.</summary>
public sealed record CacheTarget(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<(string Folder, string Pattern)> Locations,
    bool CheckedByDefault,
    double MinAgeHours,
    long Size,
    int FileCount);

/// <summary>
/// Cachés que se pueden borrar sin perder nada: los programas las regeneran
/// cuando las necesitan. Nunca toca documentos, contraseñas, historiales ni
/// cookies: de los navegadores sólo se borra la caché de páginas e imágenes.
/// Los archivos en uso se saltan sin avisar.
/// </summary>
public static class CacheCleaner
{
    public const string RecycleBinId = "recycle";

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Common => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private sealed record Definition(string Id, string Title, string Description, bool On, double MinAgeHours,
        Func<IEnumerable<(string, string)>> Locations);

    private static IEnumerable<Definition> Definitions()
    {
        yield return new("temp-user", "Archivos temporales de tu usuario",
            "Restos de instalaciones y programas en %TEMP%. Se dejan los de la última hora.", true, 1,
            () => new[] { (Path.GetTempPath(), "*") });

        yield return new("temp-windows", "Archivos temporales de Windows",
            @"C:\Windows\Temp. Parte puede necesitar permisos de administrador.", true, 1,
            () => new[] { (Path.Combine(WindowsDir, "Temp"), "*") });

        foreach (var (id, name, dataDir) in new[]
                 {
                     ("chrome", "Google Chrome", Path.Combine(Local, "Google", "Chrome", "User Data")),
                     ("edge", "Microsoft Edge", Path.Combine(Local, "Microsoft", "Edge", "User Data")),
                     ("brave", "Brave", Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data")),
                     ("opera", "Opera", Path.Combine(Local, "Opera Software", "Opera Stable")),
                     ("operagx", "Opera GX", Path.Combine(Local, "Opera Software", "Opera GX Stable")),
                     ("vivaldi", "Vivaldi", Path.Combine(Local, "Vivaldi", "User Data"))
                 })
        {
            yield return new($"browser-{id}", $"Caché de {name}",
                "Páginas e imágenes guardadas. No borra contraseñas, historial, pestañas ni sesiones iniciadas.",
                true, 0, () => ChromiumCaches(dataDir));
        }

        yield return new("browser-firefox", "Caché de Firefox",
            "Páginas e imágenes guardadas. No borra contraseñas, historial ni sesiones iniciadas.", true, 0,
            () => SubfolderCaches(Path.Combine(Local, "Mozilla", "Firefox", "Profiles"), "cache2"));

        yield return new("discord", "Caché de Discord",
            "Imágenes, vídeos y stickers ya vistos. Tu cuenta y tus mensajes no se tocan.", true, 0,
            () => new[] { "Cache", "Code Cache", "GPUCache" }
                .Select(d => (Path.Combine(Roaming, "discord", d), "*")));

        yield return new("spotify", "Caché de Spotify",
            "Canciones en caché para reproducir más rápido. Las descargas para escuchar sin conexión se conservan.",
            true, 0, () => new[] { (Path.Combine(Local, "Spotify", "Data"), "*") });

        yield return new("crash", "Informes de errores y volcados",
            "Informes que Windows y los programas generan al cerrarse con un error.", true, 0,
            () => new[]
            {
                (Path.Combine(Local, "CrashDumps"), "*"),
                (Path.Combine(Local, "Microsoft", "Windows", "WER"), "*"),
                (Path.Combine(Common, "Microsoft", "Windows", "WER", "ReportArchive"), "*"),
                (Path.Combine(Common, "Microsoft", "Windows", "WER", "ReportQueue"), "*")
            });

        yield return new("thumbs", "Miniaturas de Windows",
            "La caché de vistas previas del Explorador. Se regenera sola, pero las carpetas con fotos tardarán un poco la primera vez.",
            false, 0, () => new[]
            {
                (Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db"),
                (Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db")
            });

        yield return new("shaders", "Caché de sombreadores (juegos)",
            "DirectX, NVIDIA y AMD. Se regenera, pero los juegos pueden ir a tirones la primera partida tras limpiar.",
            false, 0, () => new[]
            {
                (Path.Combine(Local, "D3DSCache"), "*"),
                (Path.Combine(Local, "NVIDIA", "DXCache"), "*"),
                (Path.Combine(Local, "NVIDIA", "GLCache"), "*"),
                (Path.Combine(Local, "AMD", "DxCache"), "*"),
                (Path.Combine(Local, "AMD", "DxcCache"), "*")
            });

        yield return new("winupdate", "Descargas de Windows Update",
            "Instaladores de actualizaciones ya aplicadas. Necesita permisos de administrador.", false, 24,
            () => new[] { (Path.Combine(WindowsDir, "SoftwareDistribution", "Download"), "*") });
    }

    private static IEnumerable<(string, string)> ChromiumCaches(string dataDir)
    {
        if (!Directory.Exists(dataDir)) yield break;

        // Cachés comunes a todos los perfiles.
        foreach (var shared in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
            yield return (Path.Combine(dataDir, shared), "*");

        // Cada perfil («Default», «Profile 1»…) tiene la suya. Opera guarda el
        // único perfil en la raíz, así que también se mira ahí.
        var profiles = new List<string> { dataDir };
        try
        {
            profiles.AddRange(Directory.EnumerateDirectories(dataDir)
                .Where(d => File.Exists(Path.Combine(d, "Preferences"))));
        }
        catch { }

        foreach (var profile in profiles)
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache", Path.Combine("Service Worker", "ScriptCache") })
                yield return (Path.Combine(profile, cache), "*");
    }

    private static IEnumerable<(string, string)> SubfolderCaches(string root, string cacheName)
    {
        if (!Directory.Exists(root)) yield break;
        string[] profiles;
        try { profiles = Directory.GetDirectories(root); } catch { profiles = Array.Empty<string>(); }
        foreach (var profile in profiles) yield return (Path.Combine(profile, cacheName), "*");
    }

    // ────────────────────────── Medir ──────────────────────────

    /// <summary>Mide todas las categorías y devuelve sólo las que tienen algo que limpiar.</summary>
    public static List<CacheTarget> Measure(CancellationToken ct = default)
    {
        var result = new List<CacheTarget>();

        foreach (var def in Definitions())
        {
            ct.ThrowIfCancellationRequested();
            var locations = def.Locations().Where(l => Directory.Exists(l.Item1)).Distinct().ToList();
            long size = 0;
            int count = 0;
            foreach (var (folder, pattern) in locations)
                foreach (var (_, length) in Files(folder, pattern, def.MinAgeHours))
                {
                    size += length;
                    count++;
                }

            if (size > 0)
                result.Add(new CacheTarget(def.Id, def.Title, def.Description, locations, def.On, def.MinAgeHours,
                    size, count));
        }

        var (binSize, binItems) = RecycleBinInfo();
        if (binSize > 0)
            result.Add(new CacheTarget(RecycleBinId, "Papelera de reciclaje",
                $"{binItems:N0} elementos. Se vacía para siempre: revisa antes que no haya nada que quieras recuperar.",
                Array.Empty<(string, string)>(), false, 0, binSize, (int)Math.Min(binItems, int.MaxValue)));

        result.Sort((a, b) => b.Size.CompareTo(a.Size));
        return result;
    }

    private static IEnumerable<(string Path, long Length)> Files(string folder, string pattern, double minAgeHours)
    {
        var limit = DateTime.UtcNow.AddHours(-minAgeHours);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = pattern == "*",
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        FileSystemEnumerable<(string, long)>? files = null;
        try
        {
            files = new FileSystemEnumerable<(string, long)>(folder,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory &&
                    (minAgeHours <= 0 || e.LastWriteTimeUtc.UtcDateTime < limit) &&
                    (pattern == "*" || FileSystemName.MatchesSimpleExpression(pattern, e.FileName))
            };
        }
        catch { }

        if (files is null) yield break;
        using var enumerator = files.GetEnumerator();
        while (true)
        {
            (string, long) current;
            try
            {
                if (!enumerator.MoveNext()) break;
                current = enumerator.Current;
            }
            catch { break; }
            yield return current;
        }
    }

    // ────────────────────────── Limpiar ──────────────────────────

    /// <summary>Avance de la limpieza: qué se está limpiando, cuánto va (0–1) y cuánto se ha liberado.</summary>
    public readonly record struct CleanProgress(string Current, double Fraction, long Freed);

    /// <summary>Borra lo elegido. Devuelve los bytes que de verdad se liberaron.</summary>
    public static long Clean(IReadOnlyList<CacheTarget> targets, IProgress<CleanProgress>? progress = null)
    {
        long freed = 0, processed = 0;
        var planned = Math.Max(1, targets.Sum(t => t.Size));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -1000;

        void Report(string current, bool force = false)
        {
            if (progress is null) return;
            if (!force && clock.ElapsedMilliseconds - lastReport < 60) return;
            lastReport = clock.ElapsedMilliseconds;
            progress.Report(new CleanProgress(current, Math.Min(1, processed / (double)planned), freed));
        }

        foreach (var target in targets)
        {
            Report(target.Title, force: true);

            if (target.Id == RecycleBinId)
            {
                var (before, _) = RecycleBinInfo();
                try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); } catch { }
                var (after, _) = RecycleBinInfo();
                freed += Math.Max(0, before - after);
                processed += target.Size;
                Report(target.Title, force: true);
                continue;
            }

            long targetProcessed = 0;
            foreach (var (folder, pattern) in target.Locations)
            {
                foreach (var (path, length) in Files(folder, pattern, target.MinAgeHours).ToList())
                {
                    try
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        freed += length;
                    }
                    catch { /* en uso o sin permisos: se deja */ }

                    // El tamaño medido pudo cambiar: no se deja que una categoría pase de su parte.
                    var step = Math.Min(length, Math.Max(0, target.Size - targetProcessed));
                    targetProcessed += step;
                    processed += step;
                    Report(target.Title);
                }
                if (pattern == "*") RemoveEmptyFolders(folder);
            }
            processed += Math.Max(0, target.Size - targetProcessed);
        }

        processed = planned;
        Report("Listo", force: true);
        return freed;
    }

    /// <summary>Quita las subcarpetas que quedaron vacías, nunca la carpeta de la caché en sí.</summary>
    private static void RemoveEmptyFolders(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }
    }

    // ────────────────────────── Papelera ──────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private static (long Size, long Items) RecycleBinInfo()
    {
        try
        {
            var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch { return (0, 0); }
    }
}
