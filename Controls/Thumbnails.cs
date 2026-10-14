using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Barep.Controls;

/// <summary>
/// Miniaturas y vistas previas de fotos, decodificadas fuera del hilo de la
/// interfaz y ya reducidas al tamaño que se van a pintar: una foto de 12 MP
/// abierta a tamaño real pesaría 48 MB en memoria por cada fila.
/// </summary>
public static class Thumbnails
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".jxr", ".wdp"
    };

    private static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mp3", ".wav", ".m4a", ".wma", ".aac", ".flac"
    };

    private const int CacheLimit = 600;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();

    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => MediaExt.Contains(Path.GetExtension(path));

    /// <summary>Miniatura cuadrada pequeña (filas y burbujas). null si no es una imagen legible.</summary>
    public static async Task<ImageSource?> SmallAsync(string path, int pixels = 120)
    {
        if (!IsImage(path)) return null;
        var key = $"{pixels}|{path}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = await Task.Run(() => Decode(path, pixels));

        Cache[key] = image;
        Order.Enqueue(key);
        while (Order.Count > CacheLimit) Cache.Remove(Order.Dequeue());
        return image;
    }

    /// <summary>Imagen grande para el visor, con sus dimensiones originales.</summary>
    public static Task<(ImageSource? Image, int Width, int Height)> LargeAsync(string path, int maxPixels) =>
        Task.Run(() =>
        {
            var (w, h) = Dimensions(path);
            var decodeWidth = w > 0 && h > 0 && h > w ? (int)(maxPixels * (double)w / h) : maxPixels;
            return (Decode(path, w > 0 && w < decodeWidth ? 0 : decodeWidth), w, h);
        });

    private static ImageSource? Decode(string path, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // suelta el archivo en cuanto lee
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static (int Width, int Height) Dimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (0, 0); }
    }

    // ────────────────────────── Abrir ──────────────────────────

    /// <summary>Abre con el programa predeterminado de Windows.</summary>
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    /// <summary>El diálogo «Abrir con…» de Windows para elegir el programa.</summary>
    public static void OpenWith(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}")
            {
                UseShellExecute = false
            });
        }
        catch { }
    }
}
