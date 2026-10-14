using System.IO;
using System.IO.Enumeration;

namespace Barep.Services;

/// <summary>Una carpeta de caché encontrada dentro de los datos de una aplicación.</summary>
public sealed record AppCacheSpot(string Path, long Size, int Files, string Label);

/// <summary>
/// La caché de una aplicación concreta: lo que guarda para ir más rápido y
/// vuelve a crear cuando lo necesita. No se toca nada de la carpeta del
/// programa ni las carpetas de datos reales (sesiones, cookies, bases de
/// datos, documentos): sólo las carpetas que son caché por definición.
/// </summary>
public static class AppCache
{
    /// <summary>Nombres de carpeta que son caché, registros o temporales en cualquier programa.</summary>
    private static readonly Dictionary<string, string> Cacheable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cache"] = "Caché",
        ["caches"] = "Caché",
        ["cache_data"] = "Caché",
        ["code cache"] = "Caché de código",
        ["codecache"] = "Caché de código",
        ["gpucache"] = "Caché de la gráfica",
        ["dawncache"] = "Caché de la gráfica",
        ["graphitedawncache"] = "Caché de la gráfica",
        ["shadercache"] = "Caché de sombreadores",
        ["grshadercache"] = "Caché de sombreadores",
        ["scriptcache"] = "Caché de scripts",
        ["cachestorage"] = "Caché de páginas web",
        ["inetcache"] = "Caché de internet",
        ["localcache"] = "Caché local",
        ["mediacache"] = "Caché de vídeo y audio",
        ["imagecache"] = "Caché de imágenes",
        ["thumbnails"] = "Miniaturas",
        ["blob_storage"] = "Archivos temporales",
        ["crashpad"] = "Informes de errores",
        ["crashes"] = "Informes de errores",
        ["crashreports"] = "Informes de errores",
        ["crashdumps"] = "Informes de errores",
        ["logs"] = "Registros",
        ["log"] = "Registros",
        ["temp"] = "Temporales",
        ["tmp"] = "Temporales",
        ["tempstate"] = "Temporales"
    };

    /// <summary>Carpetas en las que no se entra a buscar: ahí vive lo que sí importa.</summary>
    private static readonly string[] Skip =
    {
        "local storage", "session storage", "indexeddb", "databases", "cookies", "extensions", "user data",
        "settings", "profiles", "saves", "savegames", "backup", "downloads"
    };

    /// <summary>Busca las carpetas de caché dentro de las carpetas de datos de la aplicación.</summary>
    public static List<AppCacheSpot> Find(IEnumerable<AppFolder> dataFolders, CancellationToken ct = default)
    {
        var spots = new List<AppCacheSpot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in dataFolders)
        {
            ct.ThrowIfCancellationRequested();
            Walk(folder.Path, 0, spots, seen, ct);
        }

        spots.Sort((a, b) => b.Size.CompareTo(a.Size));
        return spots;
    }

    private static void Walk(string path, int depth, List<AppCacheSpot> spots, HashSet<string> seen,
        CancellationToken ct)
    {
        if (depth > 4) return;

        string[] children;
        try { children = Directory.GetDirectories(path); }
        catch { return; }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(child);

            if (Cacheable.TryGetValue(name, out var label))
            {
                if (!seen.Add(SpaceScanner.Normalize(child))) continue;
                var (size, files) = Measure(child);
                if (size > 0) spots.Add(new AppCacheSpot(SpaceScanner.Normalize(child), size, files, label));
                continue; // dentro de una caché ya no hace falta mirar más
            }

            if (Skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Walk(child, depth + 1, spots, seen, ct);
        }
    }

    public static (long Size, int Files) Measure(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var lengths = new FileSystemEnumerable<long>(path,
                (ref FileSystemEntry e) => e.Length, options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory
            };

            long size = 0;
            int files = 0;
            foreach (var length in lengths) { size += length; files++; }
            return (size, files);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Convierte las carpetas encontradas en un objetivo de limpieza, para
    /// reutilizar el borrado con barra de progreso que ya usa la Lupa.
    /// </summary>
    public static CacheTarget AsTarget(string appName, IReadOnlyList<AppCacheSpot> spots) =>
        new(Id: "app-cache",
            Title: $"Caché de {appName}",
            Description: "Archivos que la aplicación vuelve a crear cuando los necesita.",
            Locations: spots.Select(s => (s.Path, "*")).ToList(),
            CheckedByDefault: true,
            MinAgeHours: 0,
            Size: spots.Sum(s => s.Size),
            FileCount: spots.Sum(s => s.Files));
}
