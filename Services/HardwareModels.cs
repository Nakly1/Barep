namespace Barep.Services;

public record CpuInfo(
    string Name,
    int Cores,
    int Threads,
    int MaxMHz,
    string ProcessorId,
    string Socket);

public record GpuInfo(
    string Name,
    long VramMB,
    string DriverVersion,
    string DeviceId);

/// <summary>Un módulo físico de RAM: exactamente lo que se ve al abrir el equipo.</summary>
public record MemoryModule(
    string Slot,
    string Bank,
    string Manufacturer,
    string PartNumber,
    string SerialNumber,
    long CapacityMB,
    string Type,
    int RatedSpeedMHz,
    int ConfiguredSpeedMHz,
    string FormFactor,
    double VoltageV)
{
    public double CapacityGB => CapacityMB / 1024.0;

    /// <summary>Titular legible del módulo, p. ej. «Samsung 8 GB DDR5-5600».</summary>
    public string DisplayName
    {
        get
        {
            var vendor = Manufacturer is { Length: > 0 } and not HardwareText.Unknown
                ? Manufacturer
                : "Módulo";
            var speed = RatedSpeedMHz > 0 ? $"-{RatedSpeedMHz}" : "";
            return $"{vendor} {CapacityGB:0.#} GB {Type}{speed}";
        }
    }
}

public record RamSummary(
    long TotalMB,
    string Type,
    int SpeedMHz,
    int SlotsUsed,
    int SlotsTotal,
    IReadOnlyList<MemoryModule> Modules)
{
    public static RamSummary Empty { get; } =
        new(0, HardwareText.Unknown, 0, 0, 0, Array.Empty<MemoryModule>());
}

public record OsInfo(
    string Name,
    string Build,
    string Architecture,
    string InstallDate);

public record BoardInfo(
    string Manufacturer,
    string Product,
    string SerialNumber,
    string BiosVendor,
    string BiosVersion,
    string BiosSerial,
    string SystemManufacturer,
    string SystemModel,
    string SystemUuid);

public record PhysicalDisk(
    string Model,
    string SerialNumber,
    string Interface,
    string MediaType,
    long CapacityGB,
    string FirmwareRevision);

public record NetSample(double DownKbps, double UpKbps);

public record BatteryInfo(bool Present, int Percent, bool OnAc, string Status);

public record NetworkAdapter(string Name, string MacAddress, string Type);

public record DiskInfo(string Letter, string Label, long TotalGB, long FreeGB, string FileSystem)
{
    public long UsedGB => TotalGB - FreeGB;
    public double UsedPercent => TotalGB > 0 ? Math.Round((double)UsedGB / TotalGB * 100, 1) : 0;
}

public record ProcessRow(string Name, int Pid, long MemoryMB);

/// <summary>Lectura instantánea de todo lo que cambia en el tiempo.</summary>
public record RealtimeSnapshot(
    long RamUsedMB,
    long RamTotalMB,
    long RamAvailableMB,
    double CpuPercent,
    NetSample Network,
    BatteryInfo Battery,
    TimeSpan Uptime,
    DateTime TakenAt)
{
    public double RamPercent => RamTotalMB > 0
        ? Math.Round((double)RamUsedMB / RamTotalMB * 100, 1)
        : 0;
}

/// <summary>Todo lo que no cambia mientras el equipo está encendido.</summary>
public record StaticSnapshot(
    CpuInfo Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    RamSummary Ram,
    OsInfo Os,
    BoardInfo Board,
    IReadOnlyList<DiskInfo> Volumes,
    IReadOnlyList<PhysicalDisk> Disks,
    IReadOnlyList<NetworkAdapter> Adapters);

public static class HardwareText
{
    public const string Unknown = "Sin identificar";
    public const string Unavailable = "No disponible";

    /// <summary>
    /// WMI devuelve con frecuencia relleno inútil en estos campos: cadenas vacías,
    /// «To Be Filled By O.E.M.», ceros o guiones. Todo eso vale lo mismo que nada.
    /// </summary>
    public static string Clean(object? raw, string fallback = Unknown)
    {
        var value = raw?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var normalized = value.Trim().Trim('.', '-', '_');
        if (normalized.Length == 0) return fallback;

        var junk = new[]
        {
            "to be filled by o.e.m.", "tobefilledbyoem", "system serial number",
            "default string", "none", "n/a", "na", "unknown", "not specified",
            "not available", "empty", "manufacturer", "product name",
            "serialnumber", "0", "00000000", "123456789"
        };

        if (junk.Contains(normalized.ToLowerInvariant())) return fallback;
        if (normalized.All(c => c == '0')) return fallback;

        return value.Trim();
    }
}
