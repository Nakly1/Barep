using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Barep.Services.Net;

/// <summary>
/// Fabricante de la tarjeta de red a partir de la MAC. Los primeros dígitos de
/// cada MAC los asigna el IEEE a un fabricante; la lista completa va dentro del
/// ejecutable (Assets/oui.txt.gz) para que funcione sin internet.
/// </summary>
public static class Oui
{
    private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(60000, StringComparer.Ordinal);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Barep.oui.txt.gz");
            if (stream is null) return map;
            using var zip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(zip, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var tab = line.IndexOf('\t');
                if (tab is >= 6 and <= 9) map[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Bloques de 36, 28 y 24 bits: los grandes fabricantes tienen el corto,
    /// los pequeños comparten uno y se distinguen por más dígitos.
    /// </summary>
    public static string? Lookup(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length < 6) return null;

        var table = Table.Value;
        if (hex.Length >= 9 && table.TryGetValue(hex[..9], out var small)) return small;
        if (hex.Length >= 7 && table.TryGetValue(hex[..7], out var medium)) return medium;
        return table.TryGetValue(hex[..6], out var large) ? large : null;
    }

    /// <summary>
    /// MAC aleatoria: el segundo bit del primer byte marca una dirección
    /// «administrada localmente». iPhone y Android la usan por defecto en cada
    /// red Wi-Fi para que no se les pueda seguir de una red a otra.
    /// </summary>
    public static bool IsRandomized(string? mac)
    {
        if (mac is null || mac.Length < 2) return false;
        return byte.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber, null, out var first) &&
               (first & 0x02) != 0;
    }
}
