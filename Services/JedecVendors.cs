namespace Barep.Services;

/// <summary>
/// Algunas BIOS no escriben el nombre del fabricante de RAM en el SMBIOS y dejan
/// sólo el identificador JEDEC en hexadecimal ("802C", "80CE"…). Esta tabla lo
/// traduce a la marca que el usuario reconoce en la etiqueta del módulo.
/// El primer byte lleva la paridad del banco, así que se comparan las dos
/// mitades en ambos órdenes.
/// </summary>
public static class JedecVendors
{
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2C"] = "Micron",
        ["CE"] = "Samsung",
        ["AD"] = "SK hynix",
        ["98"] = "Kingston",
        ["9E"] = "Corsair",
        ["CB"] = "A-DATA",
        ["CD"] = "G.Skill",
        ["9B"] = "Crucial",
        ["4F"] = "Transcend",
        ["FE"] = "Elpida",
        ["1F"] = "Apacer",
        ["0B"] = "Nanya",
        ["7F"] = "PNY",
        ["94"] = "Smart Modular",
        ["B0"] = "OCZ",
        ["C1"] = "Infineon",
        ["51"] = "Qimonda",
        ["4C"] = "Patriot",
        ["F7"] = "Kingmax",
        ["25"] = "Kingtiger",
        ["BA"] = "PNY",
        ["83"] = "Fairchild",
    };

    /// <summary>
    /// Devuelve el nombre comercial del fabricante, o el valor original limpio
    /// si ya venía como texto legible.
    /// </summary>
    public static string Resolve(object? raw)
    {
        var value = HardwareText.Clean(raw);
        if (value == HardwareText.Unknown) return value;

        // Si contiene letras fuera del rango hexadecimal ya es un nombre real.
        var compact = value.Replace(" ", "").Replace("-", "");
        bool looksHex = compact.Length is 2 or 4 or 6 or 8
                        && compact.All(Uri.IsHexDigit);
        if (!looksHex) return Prettify(value);

        // Se prueban las dos mitades: el ID puede venir como "80CE" o "CE80".
        foreach (var candidate in Candidates(compact))
            if (Ids.TryGetValue(candidate, out var name))
                return name;

        return value;
    }

    private static IEnumerable<string> Candidates(string hex)
    {
        if (hex.Length >= 2)
        {
            yield return hex[^2..];          // últimos dos dígitos
            yield return hex[..2];           // primeros dos
        }
        if (hex.Length >= 4)
        {
            yield return hex.Substring(2, 2);
        }
    }

    /// <summary>Corrige las grafías que las BIOS escriben en mayúsculas o pegadas.</summary>
    private static string Prettify(string value)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["samsung"] = "Samsung",
            ["hynix"] = "SK hynix",
            ["sk hynix"] = "SK hynix",
            ["skhynix"] = "SK hynix",
            ["micron"] = "Micron",
            ["micron technology"] = "Micron",
            ["kingston"] = "Kingston",
            ["corsair"] = "Corsair",
            ["crucial"] = "Crucial",
            ["g skill"] = "G.Skill",
            ["gskill"] = "G.Skill",
            ["g.skill"] = "G.Skill",
            ["adata"] = "A-DATA",
            ["a-data"] = "A-DATA",
            ["team group"] = "Team Group",
            ["teamgroup"] = "Team Group",
            ["patriot"] = "Patriot",
            ["kingmax"] = "Kingmax",
            ["ramaxel"] = "Ramaxel",
            ["nanya"] = "Nanya",
            ["transcend"] = "Transcend",
            ["apacer"] = "Apacer",
        };

        return known.TryGetValue(value.Trim(), out var pretty) ? pretty : value.Trim();
    }
}
