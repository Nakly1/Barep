using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Barep.Services;

public enum PartStatus
{
    /// <summary>La pieza sigue siendo la misma que en el acta.</summary>
    Intact,
    /// <summary>Estaba en el acta y ya no está en el equipo.</summary>
    Missing,
    /// <summary>Está en el equipo pero no figuraba en el acta.</summary>
    Added,
    /// <summary>Mismo hueco, pieza distinta: cambió el serial o el modelo.</summary>
    Changed
}

/// <summary>Una pieza identificable del equipo, tal como queda registrada en el acta.</summary>
public record EvidencePart
{
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Serial o identificador único. Es lo que prueba que la pieza es la misma.</summary>
    public string Identity { get; init; } = "";
    /// <summary>Dónde está montada: ranura, bahía, socket.</summary>
    public string Location { get; init; } = "";
    public Dictionary<string, string> Details { get; init; } = new();

    [JsonIgnore]
    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(Identity) && Identity != HardwareText.Unknown;

    /// <summary>
    /// Clave de emparejamiento entre dos actas. Con serial se compara la pieza
    /// concreta; sin serial, lo mejor que se puede hacer es comparar el hueco.
    /// </summary>
    [JsonIgnore]
    public string MatchKey => HasIdentity
        ? $"{Category}|#{Identity}"
        : $"{Category}|@{Location}|{Name}";
}

/// <summary>Acta de hardware: la foto firmada del equipo en un instante dado.</summary>
public record EvidenceDocument
{
    public string FormatVersion { get; init; } = "1.0";
    public string Application { get; init; } = "Barep";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public string MachineName { get; init; } = "";
    public string UserName { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    /// <summary>Nota del usuario: a quién se entrega el equipo y para qué.</summary>
    public string Note { get; init; } = "";
    public List<EvidencePart> Parts { get; init; } = new();
    /// <summary>SHA-256 del contenido. Si alguien edita el archivo, deja de cuadrar.</summary>
    public string Fingerprint { get; init; } = "";
}

public record EvidenceDiff(
    PartStatus Status,
    string Category,
    string Name,
    string Location,
    string Detail);

public record EvidenceComparison(
    EvidenceDocument Saved,
    IReadOnlyList<EvidenceDiff> Differences,
    int IntactCount,
    bool SavedFileIsAuthentic)
{
    public IReadOnlyList<EvidenceDiff> Alerts =>
        Differences.Where(d => d.Status != PartStatus.Intact).ToList();

    public bool Matches => Alerts.Count == 0;
}

/// <summary>
/// Levanta y verifica actas de hardware.
///
/// Para qué sirve: antes de dejar el equipo en un servicio técnico se genera un
/// acta con el número de serie de cada pieza. Al recogerlo se vuelve a cargar
/// esa acta y la app dice si algo se cambió por otra cosa o directamente falta.
/// </summary>
public class EvidenceService
{
    public const string FileExtension = ".smev.json";

    private readonly HardwareService _hw;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public EvidenceService(HardwareService hardwareService) => _hw = hardwareService;

    public static string DefaultFolder
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = Path.Combine(documents, "Barep", "Evidencias");

            // Las actas hechas con el nombre anterior de la app siguen donde estaban.
            var legacy = Path.Combine(documents, "System Monitor", "Evidencias");
            if (!Directory.Exists(folder) && Directory.Exists(legacy)) return legacy;

            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string SuggestedFileName(string extension) =>
        $"evidencia-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}{extension}";

    // ────────────────────────── Captura ──────────────────────────

    public EvidenceDocument Capture(string note = "")
    {
        var s = _hw.GetStatic(forceRefresh: true);
        var parts = new List<EvidencePart>();

        parts.Add(new EvidencePart
        {
            Category = "Procesador",
            Name = s.Cpu.Name,
            Identity = s.Cpu.ProcessorId,
            Location = s.Cpu.Socket,
            Details = new()
            {
                ["Núcleos"] = s.Cpu.Cores.ToString(),
                ["Hilos"] = s.Cpu.Threads.ToString(),
                ["Frecuencia máxima"] = s.Cpu.MaxMHz > 0 ? $"{s.Cpu.MaxMHz} MHz" : HardwareText.Unknown
            }
        });

        foreach (var m in s.Ram.Modules)
        {
            parts.Add(new EvidencePart
            {
                Category = "Memoria RAM",
                Name = m.DisplayName,
                Identity = m.SerialNumber,
                Location = m.Slot,
                Details = new()
                {
                    ["Fabricante"] = m.Manufacturer,
                    ["Número de parte"] = m.PartNumber,
                    ["Capacidad"] = $"{m.CapacityGB:0.#} GB",
                    ["Tipo"] = m.Type,
                    ["Velocidad nominal"] = m.RatedSpeedMHz > 0 ? $"{m.RatedSpeedMHz} MT/s" : HardwareText.Unknown,
                    ["Formato"] = m.FormFactor,
                    ["Banco"] = m.Bank
                }
            });
        }

        foreach (var g in s.Gpus)
        {
            parts.Add(new EvidencePart
            {
                Category = "Tarjeta gráfica",
                Name = g.Name,
                Identity = g.DeviceId,
                Location = "",
                Details = new()
                {
                    ["VRAM"] = g.VramMB > 0 ? $"{g.VramMB / 1024.0:0.#} GB" : HardwareText.Unknown,
                    ["Controlador"] = g.DriverVersion
                }
            });
        }

        foreach (var d in s.Disks)
        {
            parts.Add(new EvidencePart
            {
                Category = "Almacenamiento",
                Name = d.Model,
                Identity = d.SerialNumber,
                Location = d.Interface,
                Details = new()
                {
                    ["Capacidad"] = d.CapacityGB > 0 ? $"{d.CapacityGB} GB" : HardwareText.Unknown,
                    ["Interfaz"] = d.Interface,
                    ["Firmware"] = d.FirmwareRevision
                }
            });
        }

        parts.Add(new EvidencePart
        {
            Category = "Placa base",
            Name = $"{s.Board.Manufacturer} {s.Board.Product}".Trim(),
            Identity = s.Board.SerialNumber,
            Location = "",
            Details = new()
            {
                ["Fabricante"] = s.Board.Manufacturer,
                ["Modelo"] = s.Board.Product,
                ["BIOS"] = $"{s.Board.BiosVendor} {s.Board.BiosVersion}".Trim(),
                ["Serial BIOS"] = s.Board.BiosSerial,
                ["UUID del equipo"] = s.Board.SystemUuid
            }
        });

        foreach (var a in s.Adapters)
        {
            parts.Add(new EvidencePart
            {
                Category = "Adaptador de red",
                Name = a.Name,
                Identity = a.MacAddress,
                Location = a.Type,
                Details = new() { ["Dirección MAC"] = a.MacAddress, ["Tipo"] = a.Type }
            });
        }

        var doc = new EvidenceDocument
        {
            CreatedAt = DateTime.Now,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            OperatingSystem = $"{s.Os.Name} (build {s.Os.Build}, {s.Os.Architecture})",
            Note = note.Trim(),
            Parts = parts
        };

        return doc with { Fingerprint = ComputeFingerprint(doc) };
    }

    // ────────────────────────── Persistencia ──────────────────────────

    public void Save(EvidenceDocument doc, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions), Encoding.UTF8);
    }

    public EvidenceDocument Load(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var doc = JsonSerializer.Deserialize<EvidenceDocument>(json, JsonOptions)
                  ?? throw new InvalidDataException("El archivo no contiene un acta válida.");
        if (doc.Parts.Count == 0)
            throw new InvalidDataException("El acta no registra ninguna pieza.");
        return doc;
    }

    /// <summary>
    /// Recalcula la huella del acta. Si no coincide con la guardada, el archivo
    /// fue editado después de generarlo y no sirve como prueba.
    /// </summary>
    public bool IsAuthentic(EvidenceDocument doc) =>
        string.Equals(doc.Fingerprint, ComputeFingerprint(doc), StringComparison.OrdinalIgnoreCase);

    private static string ComputeFingerprint(EvidenceDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append(doc.FormatVersion).Append('|')
          .Append(doc.CreatedAt.ToString("O")).Append('|')
          .Append(doc.MachineName).Append('|')
          .Append(doc.UserName).Append('|')
          .Append(doc.OperatingSystem).Append('|')
          .Append(doc.Note).Append('|');

        // Orden estable: la huella no puede depender del orden de enumeración de WMI.
        foreach (var p in doc.Parts.OrderBy(p => p.MatchKey, StringComparer.Ordinal))
        {
            sb.Append(p.Category).Append('~').Append(p.Name).Append('~')
              .Append(p.Identity).Append('~').Append(p.Location).Append('~');
            foreach (var kv in p.Details.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            sb.Append('|');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    public static string FormatFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return HardwareText.Unknown;
        // En bloques de cuatro para poder leerlo en voz alta o copiarlo a mano.
        var chunks = Enumerable.Range(0, fingerprint.Length / 4)
            .Select(i => fingerprint.Substring(i * 4, 4));
        return string.Join(" ", chunks);
    }

    // ────────────────────────── Comparación ──────────────────────────

    public EvidenceComparison CompareWithCurrent(EvidenceDocument saved)
    {
        var current = Capture(saved.Note);

        var savedByKey = saved.Parts
            .GroupBy(p => p.MatchKey)
            .ToDictionary(g => g.Key, g => g.First());
        var currentByKey = current.Parts
            .GroupBy(p => p.MatchKey)
            .ToDictionary(g => g.Key, g => g.First());

        var diffs = new List<EvidenceDiff>();
        var unmatchedSaved = new List<EvidencePart>();
        var claimed = new HashSet<string>();
        int intact = 0;

        // Paso 1 · emparejar por identidad: misma pieza, mismo serial.
        foreach (var (key, savedPart) in savedByKey)
        {
            if (!currentByKey.TryGetValue(key, out var currentPart))
            {
                unmatchedSaved.Add(savedPart);
                continue;
            }

            claimed.Add(key);
            var changes = DescribeChanges(savedPart, currentPart);

            if (changes.Length == 0)
            {
                intact++;
                diffs.Add(new EvidenceDiff(PartStatus.Intact, savedPart.Category,
                    savedPart.Name, savedPart.Location,
                    savedPart.HasIdentity ? $"Serial {savedPart.Identity}" : "Coincide con el acta"));
            }
            else
            {
                diffs.Add(new EvidenceDiff(PartStatus.Changed, savedPart.Category,
                    savedPart.Name, savedPart.Location, changes));
            }
        }

        // Paso 2 · las que no tienen pareja pueden haber sido sustituidas: si en
        // su misma ranura hay ahora otra pieza sin reclamar, es un cambiazo, y
        // eso es una sola incidencia, no una que falta más otra que sobra.
        foreach (var savedPart in unmatchedSaved)
        {
            var replacement = string.IsNullOrEmpty(savedPart.Location)
                ? null
                : current.Parts.FirstOrDefault(p =>
                    p.Category == savedPart.Category &&
                    p.Location == savedPart.Location &&
                    !claimed.Contains(p.MatchKey) &&
                    !savedByKey.ContainsKey(p.MatchKey));

            if (replacement is not null)
            {
                claimed.Add(replacement.MatchKey);

                var detail = $"En el acta figuraba «{savedPart.Name}»" +
                             (savedPart.HasIdentity ? $" con serial {savedPart.Identity}" : "") +
                             $". Ahora hay «{replacement.Name}»" +
                             (replacement.HasIdentity ? $" con serial {replacement.Identity}" : "") + ".";

                diffs.Add(new EvidenceDiff(PartStatus.Changed, savedPart.Category,
                    savedPart.Name, savedPart.Location, detail));
                continue;
            }

            diffs.Add(new EvidenceDiff(PartStatus.Missing, savedPart.Category,
                savedPart.Name, savedPart.Location,
                savedPart.HasIdentity
                    ? $"No se encuentra en el equipo. Serial registrado: {savedPart.Identity}"
                    : "No se encuentra en el equipo."));
        }

        // Paso 3 · lo que queda sin reclamar es material añadido.
        foreach (var (key, currentPart) in currentByKey)
        {
            if (savedByKey.ContainsKey(key) || claimed.Contains(key)) continue;
            diffs.Add(new EvidenceDiff(PartStatus.Added, currentPart.Category,
                currentPart.Name, currentPart.Location,
                currentPart.HasIdentity
                    ? $"No figuraba en el acta. Serial {currentPart.Identity}"
                    : "No figuraba en el acta."));
        }

        var order = new Dictionary<PartStatus, int>
        {
            [PartStatus.Missing] = 0,
            [PartStatus.Changed] = 1,
            [PartStatus.Added] = 2,
            [PartStatus.Intact] = 3
        };

        diffs = diffs
            .OrderBy(d => order[d.Status])
            .ThenBy(d => d.Category, StringComparer.CurrentCulture)
            .ThenBy(d => d.Location, StringComparer.CurrentCulture)
            .ToList();

        return new EvidenceComparison(saved, diffs, intact, IsAuthentic(saved));
    }

    private static string DescribeChanges(EvidencePart before, EvidencePart after)
    {
        var changes = new List<string>();

        if (!string.Equals(before.Name, after.Name, StringComparison.OrdinalIgnoreCase))
            changes.Add($"modelo: «{before.Name}» → «{after.Name}»");

        foreach (var (field, oldValue) in before.Details)
        {
            if (!after.Details.TryGetValue(field, out var newValue)) continue;
            if (string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase)) continue;
            // El controlador de vídeo cambia con cada actualización: no es un robo.
            if (field is "Controlador" or "Firmware" or "BIOS") continue;
            changes.Add($"{field.ToLowerInvariant()}: «{oldValue}» → «{newValue}»");
        }

        return string.Join(" · ", changes);
    }

    // ────────────────────────── Informe legible ──────────────────────────

    /// <summary>Versión imprimible del acta, para firmar y dejar constancia en papel.</summary>
    public string BuildReport(EvidenceDocument doc)
    {
        var sb = new StringBuilder();
        var line = new string('─', 66);

        sb.AppendLine(line);
        sb.AppendLine("  ACTA DE HARDWARE — BAREP");
        sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine($"  Equipo          {doc.MachineName}");
        sb.AppendLine($"  Usuario         {doc.UserName}");
        sb.AppendLine($"  Sistema         {doc.OperatingSystem}");
        sb.AppendLine($"  Fecha y hora    {doc.CreatedAt:dddd d 'de' MMMM 'de' yyyy, HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(doc.Note))
            sb.AppendLine($"  Motivo          {doc.Note}");
        sb.AppendLine();
        sb.AppendLine($"  Huella SHA-256");
        sb.AppendLine($"  {FormatFingerprint(doc.Fingerprint)}");
        sb.AppendLine();
        sb.AppendLine(line);
        sb.AppendLine($"  PIEZAS REGISTRADAS: {doc.Parts.Count}");
        sb.AppendLine(line);

        foreach (var group in doc.Parts.GroupBy(p => p.Category))
        {
            sb.AppendLine();
            sb.AppendLine($"  ▸ {group.Key.ToUpperInvariant()}");
            sb.AppendLine();

            foreach (var part in group)
            {
                sb.AppendLine($"    {part.Name}");
                if (!string.IsNullOrWhiteSpace(part.Location))
                    sb.AppendLine($"      {"Ubicación",-22}{part.Location}");
                sb.AppendLine($"      {"Número de serie",-22}{(part.HasIdentity ? part.Identity : "no informado por la BIOS")}");
                foreach (var (field, value) in part.Details)
                {
                    if (string.IsNullOrWhiteSpace(value) || value == HardwareText.Unknown) continue;
                    sb.AppendLine($"      {field,-22}{value}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine(line);
        sb.AppendLine("  Este acta se generó leyendo el SMBIOS y WMI del equipo.");
        sb.AppendLine("  Guarde el archivo .smev.json junto a este documento: permite");
        sb.AppendLine("  verificar automáticamente que las piezas son las mismas.");
        sb.AppendLine();
        sb.AppendLine("  Entrega:  ______________________   Recepción:  ______________________");
        sb.AppendLine(line);

        return sb.ToString();
    }

    public string BuildComparisonReport(EvidenceComparison comparison)
    {
        var sb = new StringBuilder();
        var line = new string('─', 66);

        sb.AppendLine(line);
        sb.AppendLine("  VERIFICACIÓN DE HARDWARE — BAREP");
        sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine($"  Acta original   {comparison.Saved.CreatedAt:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"  Verificado el   {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"  Integridad      {(comparison.SavedFileIsAuthentic ? "el acta no fue alterada" : "AVISO: el archivo del acta fue modificado")}");
        sb.AppendLine();
        sb.AppendLine(comparison.Matches
            ? $"  RESULTADO: todo coincide. {comparison.IntactCount} piezas verificadas."
            : $"  RESULTADO: {comparison.Alerts.Count} discrepancia(s) sobre {comparison.Differences.Count} piezas.");
        sb.AppendLine();
        sb.AppendLine(line);

        foreach (var d in comparison.Differences)
        {
            var mark = d.Status switch
            {
                PartStatus.Missing => "[ FALTA   ]",
                PartStatus.Changed => "[ CAMBIÓ  ]",
                PartStatus.Added => "[ NUEVA   ]",
                _ => "[ OK      ]"
            };
            sb.AppendLine($"  {mark} {d.Category} · {d.Name}");
            if (!string.IsNullOrWhiteSpace(d.Location))
                sb.AppendLine($"              Ubicación: {d.Location}");
            sb.AppendLine($"              {d.Detail}");
        }

        sb.AppendLine(line);
        return sb.ToString();
    }
}
