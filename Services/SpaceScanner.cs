using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace Barep.Services;

/// <summary>
/// Recorre una unidad o carpeta y construye el árbol de tamaños que dibuja la
/// Lupa. Usa la enumeración nativa de .NET (NtQueryDirectoryFile por debajo),
/// que devuelve tamaño y atributos en la misma llamada sin abrir cada archivo,
/// y reparte los primeros niveles entre varios hilos.
/// </summary>
public static class SpaceScanner
{
    /// <summary>Archivos que se conservan con fila propia en cada carpeta.</summary>
    private const int FilesKeptPerFolder = 60;

    /// <summary>Profundidad hasta la que las subcarpetas se analizan en paralelo.</summary>
    private const int ParallelDepth = 2;

    // Atributos de los archivos de la nube (OneDrive, iCloud, Google Drive).
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudMask = Pinned | Unpinned | RecallOnOpen | RecallOnDataAccess;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,          // también ocultos y de sistema: es lo que más sorprende
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private readonly record struct Entry(
        string Name, string Path, bool IsDirectory, long Length,
        FileAttributes Attributes, DateTime Modified);

    private sealed class State
    {
        private readonly IProgress<ScanProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReport;
        public long Items;
        public long Bytes;

        public State(IProgress<ScanProgress>? progress) => _progress = progress;

        public void Tick(string path)
        {
            if (_progress is null) return;
            var now = _clock.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastReport);
            if (now - last < 90) return;
            if (Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            _progress.Report(new ScanProgress(path, Interlocked.Read(ref Items), Interlocked.Read(ref Bytes)));
        }
    }

    // ────────────────────────── Entrada pública ──────────────────────────

    public static SpaceNode ScanPath(string path, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var info = new DirectoryInfo(path);
        var isDrive = info.Parent is null;
        var name = isDrive ? DriveTitle(info.FullName) : info.Name;

        var root = new SpaceNode(name, info.FullName,
            isDrive ? SpaceNodeKind.Drive : SpaceNodeKind.Folder, null);
        try { root.Modified = info.LastWriteTime; } catch { }

        var state = new State(progress);
        ScanInto(root, 0, state, ct);
        return root;
    }

    /// <summary>
    /// Analiza sólo las carpetas de las aplicaciones instaladas y las cuelga de
    /// una raíz virtual «Aplicaciones». Mucho más rápido que analizar el disco.
    /// </summary>
    public static SpaceNode ScanApps(IReadOnlyList<InstalledApp> apps,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var root = new SpaceNode("Aplicaciones", "", SpaceNodeKind.AppsRoot, null)
        {
            Modified = DateTime.Now
        };
        var state = new State(progress);
        var nodes = new SpaceNode?[apps.Count];

        Parallel.For(0, apps.Count,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var app = apps[i];
                var location = app.InstallLocation;

                if (!string.IsNullOrEmpty(location) && Directory.Exists(location))
                {
                    var node = new SpaceNode(Path.GetFileName(location.TrimEnd('\\')), location,
                        SpaceNodeKind.Folder, root) { App = app };
                    try { node.Modified = Directory.GetLastWriteTime(location); } catch { }
                    ScanInto(node, ParallelDepth, state, ct); // ya estamos en paralelo por app
                    nodes[i] = node;
                }
                else if (app.EstimatedSizeBytes > 0)
                {
                    nodes[i] = new SpaceNode(app.DisplayName, "", SpaceNodeKind.AppEntry, root)
                    {
                        App = app,
                        Size = app.EstimatedSizeBytes,
                        Modified = app.InstallDate ?? DateTime.MinValue
                    };
                }
            });

        foreach (var node in nodes)
        {
            if (node is null) continue;
            root.Children.Add(node);
            root.Size += node.Size;
            root.ItemCount += node.ItemCount + 1;
        }
        root.SortChildren();
        return root;
    }

    /// <summary>
    /// Marca con su aplicación las carpetas que coinciden con la ubicación de
    /// instalación de algún programa registrado.
    /// </summary>
    public static void AttachApps(SpaceNode root, IReadOnlyList<InstalledApp> apps)
    {
        var byPath = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            if (!string.IsNullOrEmpty(app.InstallLocation))
                byPath.TryAdd(Normalize(app.InstallLocation), app);

        var stack = new Stack<SpaceNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Kind == SpaceNodeKind.Folder && byPath.TryGetValue(Normalize(node.FullPath), out var app))
                node.App = app;
            else if (node.Kind == SpaceNodeKind.AppEntry)
                node.App = apps.FirstOrDefault(a => a.DisplayName == node.Name);
            foreach (var child in node.Children)
                if (child.Kind is SpaceNodeKind.Folder or SpaceNodeKind.AppEntry) stack.Push(child);
        }
    }

    /// <summary>
    /// Abre el grupo «N archivos más»: vuelve a leer la carpeta y cuelga de él
    /// los archivos pequeños que no tenían fila propia.
    /// </summary>
    public static void ExpandAggregate(SpaceNode aggregate)
    {
        if (aggregate.Kind != SpaceNodeKind.Aggregate || aggregate.Children.Count > 0) return;
        var parent = aggregate.Parent;
        if (parent is null || string.IsNullOrEmpty(parent.FullPath)) return;

        var shown = new HashSet<string>(
            parent.Children.Where(c => c.Kind == SpaceNodeKind.File).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            var files = new FileSystemEnumerable<Entry>(parent.FullPath,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.ToFullPath(), e.IsDirectory,
                    e.Length, e.Attributes, e.LastWriteTimeUtc.LocalDateTime), Options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory
            };

            long size = 0;
            foreach (var f in files)
            {
                if (shown.Contains(f.Name)) continue;
                var cloudOnly = (f.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0;
                var length = cloudOnly ? 0 : f.Length;
                aggregate.Children.Add(new SpaceNode(f.Name, f.Path, SpaceNodeKind.File, aggregate)
                {
                    Size = length,
                    Modified = f.Modified
                });
                size += length;
            }

            // Si la carpeta cambió desde el análisis, el grupo refleja lo que hay ahora.
            var delta = size - aggregate.Size;
            for (var p = aggregate; p is not null; p = p.Parent) p.Size = Math.Max(0, p.Size + delta);
            aggregate.ItemCount = aggregate.Children.Count;
            aggregate.SortChildren();
        }
        catch { }
    }

    public static string Normalize(string path) => path.Trim().Trim('"').TrimEnd('\\', '/');

    public static string DriveTitle(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            var label = drive.IsReady ? drive.VolumeLabel : "";
            var letter = drive.Name.TrimEnd('\\');
            return string.IsNullOrWhiteSpace(label) ? $"Disco local ({letter})" : $"{label} ({letter})";
        }
        catch { return root; }
    }

    // ────────────────────────── Recorrido ──────────────────────────

    private static void ScanInto(SpaceNode node, int depth, State state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dirs = new List<SpaceNode>();
        var files = new List<Entry>();
        long filesBytes = 0, cloudOnly = 0;

        try
        {
            var enumerable = new FileSystemEnumerable<Entry>(node.FullPath,
                (ref FileSystemEntry e) => new Entry(
                    e.FileName.ToString(),
                    e.ToFullPath(),
                    e.IsDirectory,
                    e.Length,
                    e.Attributes,
                    e.LastWriteTimeUtc.LocalDateTime),
                Options);

            foreach (var entry in enumerable)
            {
                if (entry.IsDirectory)
                {
                    // Uniones y enlaces simbólicos («Documents and Settings»,
                    // «Application Data»…) apuntan a carpetas que ya se cuentan en
                    // otro sitio. Las carpetas de la nube también son puntos de
                    // análisis, pero ésas sí hay que recorrerlas.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 &&
                        (entry.Attributes & CloudMask) == 0)
                        continue;

                    dirs.Add(new SpaceNode(entry.Name, entry.Path, SpaceNodeKind.Folder, node)
                    {
                        Modified = entry.Modified
                    });
                }
                else
                {
                    var isCloudOnly = (entry.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0;
                    if (isCloudOnly) cloudOnly++;
                    else filesBytes += entry.Length;

                    files.Add(isCloudOnly ? entry with { Length = 0 } : entry);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Carpeta protegida o desaparecida a mitad del análisis: cuenta como vacía.
        }

        Interlocked.Add(ref state.Items, files.Count + dirs.Count);
        Interlocked.Add(ref state.Bytes, filesBytes);
        state.Tick(node.FullPath);

        if (depth < ParallelDepth && dirs.Count > 1)
        {
            Parallel.ForEach(dirs,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                d => ScanInto(d, depth + 1, state, ct));
        }
        else
        {
            foreach (var d in dirs) ScanInto(d, depth + 1, state, ct);
        }

        // Archivos: los grandes con fila propia; el resto, agrupados en una sola.
        files.Sort((a, b) => b.Length.CompareTo(a.Length));
        var kept = Math.Min(files.Count, FilesKeptPerFolder);
        for (int i = 0; i < kept; i++)
        {
            var f = files[i];
            node.Children.Add(new SpaceNode(f.Name, f.Path, SpaceNodeKind.File, node)
            {
                Size = f.Length,
                Modified = f.Modified
            });
        }
        if (files.Count > kept)
        {
            long rest = 0;
            for (int i = kept; i < files.Count; i++) rest += files[i].Length;
            var others = files.Count - kept;
            node.Children.Add(new SpaceNode(
                others == 1 ? "1 archivo más" : $"{others:N0} archivos más", "",
                SpaceNodeKind.Aggregate, node)
            {
                Size = rest,
                ItemCount = others
            });
        }

        long size = filesBytes, items = files.Count, cloud = cloudOnly;
        foreach (var d in dirs)
        {
            node.Children.Add(d);
            size += d.Size;
            items += d.ItemCount + 1;
            cloud += d.CloudOnlyCount;
        }

        node.Size = size;
        node.ItemCount = items;
        node.CloudOnlyCount = cloud;
        node.SortChildren();
    }
}
