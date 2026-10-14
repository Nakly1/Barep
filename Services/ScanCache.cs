using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Barep.Services;

/// <summary>Resumen de un análisis guardado, sin cargar el árbol entero.</summary>
public sealed record CachedScanInfo(string SourceKey, DateTime ScannedAt, long Size, string FilePath);

/// <summary>
/// Guarda cada análisis de la Lupa en disco para no tener que repetirlo: al
/// volver a la sección, al pulsar «Volver a empezar» o al reabrir la app se
/// abre al instante. Formato binario propio comprimido; un disco de sistema
/// entero ocupa unos pocos MB.
/// </summary>
public static class ScanCache
{
    private const int Magic = 0x4C555041; // «LUPA»
    private const int Version = 1;
    public const string AppsKey = "apps";

    private static readonly object Gate = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Barep", "Lupa");

    /// <summary>Clave de origen: la ruta normalizada o «apps».</summary>
    public static string KeyFor(string? path) =>
        path is null ? AppsKey : SpaceScanner.Normalize(path).ToUpperInvariant();

    private static string FileFor(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Folder, $"{hash}.lupa");
    }

    // ────────────────────────── Guardar ──────────────────────────

    public static void Save(string key, SpaceNode root, DateTime scannedAt)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var target = FileFor(key);
                var temp = target + ".tmp";

                using (var file = File.Create(temp))
                using (var zip = new GZipStream(file, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(new BufferedStream(zip, 1 << 16), Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(key);
                    w.Write(scannedAt.ToBinary());
                    w.Write(root.Size);
                    WriteNode(w, root, parentPath: null);
                }
                File.Move(temp, target, overwrite: true);
            }
            catch { /* la caché es una comodidad: si falla, se vuelve a analizar */ }
        }
    }

    private static void WriteNode(BinaryWriter w, SpaceNode n, string? parentPath)
    {
        w.Write((byte)n.Kind);
        w.Write(n.Name);

        // La ruta casi siempre es «padre\nombre»: sólo se escribe cuando no lo es.
        var implied = parentPath is { Length: > 0 } && n.FullPath.Length > 0 &&
                      n.FullPath.Equals(Path.Combine(parentPath, n.Name), StringComparison.Ordinal);
        w.Write(implied);
        if (!implied) w.Write(n.FullPath);

        w.Write(n.Size);
        w.Write(n.ItemCount);
        w.Write(n.Modified.Ticks);
        w.Write(n.CloudOnlyCount);

        // Los «N archivos más» se vuelven a leer del disco si se abren.
        var children = n.Kind == SpaceNodeKind.Aggregate ? new List<SpaceNode>() : n.Children;
        w.Write(children.Count);
        foreach (var c in children) WriteNode(w, c, n.FullPath);
    }

    // ────────────────────────── Leer ──────────────────────────

    public static List<CachedScanInfo> List()
    {
        var list = new List<CachedScanInfo>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.lupa"))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    using var zip = new GZipStream(stream, CompressionMode.Decompress);
                    using var r = new BinaryReader(zip, Encoding.UTF8);
                    if (r.ReadInt32() != Magic || r.ReadInt32() != Version) continue;
                    var key = r.ReadString();
                    var at = DateTime.FromBinary(r.ReadInt64());
                    var size = r.ReadInt64();
                    list.Add(new CachedScanInfo(key, at, size, file));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static (SpaceNode Root, DateTime ScannedAt)? Load(string key)
    {
        lock (Gate)
        {
            try
            {
                var path = FileFor(key);
                if (!File.Exists(path)) return null;

                using var stream = File.OpenRead(path);
                using var zip = new GZipStream(stream, CompressionMode.Decompress);
                using var r = new BinaryReader(new BufferedStream(zip, 1 << 16), Encoding.UTF8);
                if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
                if (r.ReadString() != key) return null;
                var at = DateTime.FromBinary(r.ReadInt64());
                r.ReadInt64();
                return (ReadNode(r, null), at);
            }
            catch { return null; }
        }
    }

    private static SpaceNode ReadNode(BinaryReader r, SpaceNode? parent)
    {
        var kind = (SpaceNodeKind)r.ReadByte();
        var name = r.ReadString();
        var implied = r.ReadBoolean();
        var fullPath = implied ? Path.Combine(parent!.FullPath, name) : r.ReadString();

        var node = new SpaceNode(name, fullPath, kind, parent)
        {
            Size = r.ReadInt64(),
            ItemCount = r.ReadInt64(),
            Modified = new DateTime(r.ReadInt64()),
            CloudOnlyCount = r.ReadInt64()
        };

        var count = r.ReadInt32();
        node.Children.Capacity = count;
        for (int i = 0; i < count; i++) node.Children.Add(ReadNode(r, node));
        return node;
    }

    public static void Delete(string key)
    {
        try { File.Delete(FileFor(key)); } catch { }
    }
}
