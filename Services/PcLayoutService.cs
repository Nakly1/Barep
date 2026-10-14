using System.Management;

namespace Barep.Services;

public enum PartKind { Cpu, Gpu, RamSlot, RamSoldered, M2Slot, SataBay, PcieSlot, Battery }

/// <summary>Ocupado, libre, desconocido (Windows no lo informa) o soldado a la placa.</summary>
public enum SlotState { Occupied, Free, Unknown, Soldered }

/// <summary>Una pieza del plano: lo que se dibuja y lo que se cuenta al pasar el ratón.</summary>
public sealed record PcPart(
    PartKind Kind,
    SlotState State,
    string Title,
    string Subtitle,
    IReadOnlyList<(string Label, string? Value)> Specs,
    string? Hint = null);

public sealed record PcLayout(
    bool IsLaptop,
    string ChassisName,
    string Model,
    PcPart Cpu,
    IReadOnlyList<PcPart> Gpus,
    IReadOnlyList<PcPart> Ram,
    long MaxRamGB,
    IReadOnlyList<PcPart> Storage,
    IReadOnlyList<PcPart> Pcie,
    PcPart? Battery,
    IReadOnlyList<PcPart> External,
    bool StorageSlotsReported)
{
    public int FreeRamSlots => Ram.Count(r => r.State == SlotState.Free);
    public int FreeStorageSlots => Storage.Count(s => s.State == SlotState.Free);
}

/// <summary>
/// Reúne lo necesario para dibujar el plano del equipo: tipo de chasis,
/// ranuras de RAM (usadas, libres o memoria soldada), unidades con su tipo de
/// conexión real (NVMe, SATA, USB) y las ranuras M.2/PCIe que la BIOS declara.
/// Windows no conoce la posición física de cada pieza: el plano es un esquema
/// típico para ese tipo de equipo, no una foto de la placa.
/// </summary>
public static class PcLayoutService
{
    private static readonly int[] LaptopChassis = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };

    public static PcLayout Build(StaticSnapshot s, BatteryInfo battery)
    {
        var (isLaptop, chassisName) = Chassis(battery.Present);

        // ── Procesador y gráficas ──
        var cpu = new PcPart(PartKind.Cpu, SlotState.Occupied, ShortCpu(s.Cpu.Name), "Procesador",
            new (string, string?)[]
            {
                ("Modelo", s.Cpu.Name),
                ("Núcleos / hilos", $"{s.Cpu.Cores} / {s.Cpu.Threads}"),
                ("Frecuencia máxima", s.Cpu.MaxMHz > 0 ? $"{s.Cpu.MaxMHz / 1000.0:0.0#} GHz" : null),
                ("Zócalo", s.Cpu.Socket)
            },
            isLaptop ? "En portátiles el procesador va soldado a la placa: no se puede cambiar." : null);

        var gpus = s.Gpus.Select(g =>
        {
            var integrated = IsIntegrated(g.Name);
            return new PcPart(PartKind.Gpu, SlotState.Occupied, g.Name, integrated ? "Gráfica integrada" : "Gráfica dedicada",
                new (string, string?)[]
                {
                    ("Modelo", g.Name),
                    ("Memoria de vídeo", g.VramMB > 0 ? $"{g.VramMB / 1024.0:0.#} GB" : null),
                    ("Controlador", g.DriverVersion),
                    ("Tipo", integrated ? "Integrada en el procesador" : "Independiente")
                },
                integrated ? "Va dentro del procesador y usa parte de la RAM." : null);
        }).ToList();

        // ── RAM: ranuras usadas, libres y memoria soldada ──
        var raw = RawMemory();
        var ram = new List<PcPart>();
        foreach (var m in s.Ram.Modules)
        {
            raw.TryGetValue(m.Slot, out var ff);
            var soldered = m.Type.StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase) || ff is 9 or 10 or 16;
            ram.Add(new PcPart(soldered ? PartKind.RamSoldered : PartKind.RamSlot,
                soldered ? SlotState.Soldered : SlotState.Occupied,
                $"{m.CapacityGB:0.#} GB {m.Type}",
                m.Manufacturer is { Length: > 0 } and not HardwareText.Unknown ? m.Manufacturer : "Módulo de memoria",
                new (string, string?)[]
                {
                    ("Ranura", m.Slot),
                    ("Fabricante", m.Manufacturer),
                    ("Capacidad", $"{m.CapacityGB:0.#} GB"),
                    ("Tipo", m.Type),
                    ("Velocidad", m.RatedSpeedMHz > 0 ? $"{m.RatedSpeedMHz} MT/s" : null),
                    ("Formato", soldered ? "Soldada a la placa" : m.FormFactor),
                    ("Número de parte", m.PartNumber)
                },
                soldered ? "Esta memoria va soldada: no se puede quitar ni cambiar." : null));
        }

        var maxRam = MaxRamGB();
        var totalSlots = Math.Max(s.Ram.SlotsTotal, s.Ram.Modules.Count);
        var free = totalSlots - s.Ram.Modules.Count;
        var type = s.Ram.Modules.FirstOrDefault()?.Type ?? "";
        var formFactor = isLaptop ? "SODIMM" : "DIMM";
        for (int i = 0; i < free; i++)
        {
            ram.Add(new PcPart(PartKind.RamSlot, SlotState.Free, "Ranura libre", $"Admite {type} {formFactor}".Trim(),
                new (string, string?)[]
                {
                    ("Estado", "Vacía: puedes añadir un módulo"),
                    ("Tipo compatible", $"{type} {formFactor}".Trim()),
                    ("Máximo del equipo", maxRam > 0 ? $"{maxRam} GB en total" : null)
                },
                "Lo ideal es un módulo igual al que ya tienes (misma capacidad y velocidad) para que funcionen en doble canal."));
        }

        // ── Almacenamiento: unidades reales y ranuras que declara la BIOS ──
        var disks = PhysicalDisks(s.Disks);
        var internalDisks = disks.Where(d => !d.External).ToList();
        var storage = new List<PcPart>();
        foreach (var d in internalDisks)
        {
            var isM2 = d.Bus == "NVMe";
            storage.Add(new PcPart(isM2 ? PartKind.M2Slot : PartKind.SataBay, SlotState.Occupied,
                d.Model, $"{(d.SizeGB >= 1000 ? $"{d.SizeGB / 1000.0:0.#} TB" : $"{d.SizeGB:N0} GB")} · {d.Media} {d.Bus}",
                new (string, string?)[]
                {
                    ("Modelo", d.Model),
                    ("Capacidad", $"{d.SizeGB:N0} GB"),
                    ("Tipo", d.Media),
                    ("Conexión", isM2 ? "M.2 NVMe (PCIe)" : d.Bus),
                    ("Número de serie", d.Serial)
                }));
        }

        var slots = SystemSlots();
        var m2Declared = slots.Where(x => x.IsM2).ToList();
        var reported = m2Declared.Count > 0;
        if (reported)
        {
            // La BIOS dice cuántas M.2 hay y cuáles están vacías.
            var usedM2 = storage.Count(p => p.Kind == PartKind.M2Slot);
            var declaredFree = m2Declared.Where(x => x.Usage == 3).ToList();
            foreach (var slot in declaredFree)
                storage.Add(new PcPart(PartKind.M2Slot, SlotState.Free, "Ranura M.2 libre", slot.Name,
                    new (string, string?)[] { ("Ranura", slot.Name), ("Estado", "Vacía según la BIOS") },
                    "Aquí cabe un SSD M.2 NVMe adicional. Revisa el tamaño que admite (normalmente 2280)."));

            if (declaredFree.Count == 0)
            {
                // Declara las ranuras pero no si están en uso: las que sobran se marcan como «por confirmar».
                foreach (var slot in m2Declared.Skip(usedM2))
                    storage.Add(new PcPart(PartKind.M2Slot, SlotState.Unknown, "Ranura M.2", slot.Name,
                        new (string, string?)[] { ("Ranura", slot.Name), ("Estado", "La BIOS no indica si está ocupada") },
                        "Probablemente libre: tus unidades ya ocupan las demás."));
            }
        }
        else
        {
            storage.Add(new PcPart(PartKind.M2Slot, SlotState.Unknown, "¿Otra ranura M.2?", "Windows no lo informa",
                new (string, string?)[] { ("Estado", "Desconocido") },
                "La BIOS de este equipo no declara sus ranuras M.2. Busca el manual de tu modelo o mira dentro: " +
                (isLaptop ? "muchos portátiles traen una segunda ranura vacía." : "la mayoría de placas actuales traen 2 o más.")));
        }

        // ── PCIe (sólo sobremesa) ──
        var pcie = new List<PcPart>();
        if (!isLaptop)
        {
            foreach (var slot in slots.Where(x => x.IsPcie))
            {
                pcie.Add(new PcPart(PartKind.PcieSlot, slot.Usage switch { 3 => SlotState.Free, 4 => SlotState.Occupied, _ => SlotState.Unknown },
                    slot.Name, slot.Usage == 3 ? "Libre" : slot.Usage == 4 ? "En uso" : "Estado desconocido",
                    new (string, string?)[] { ("Ranura", slot.Name), ("Estado", slot.Usage == 3 ? "Libre" : slot.Usage == 4 ? "En uso" : "Desconocido") }));
            }
        }

        var bat = battery.Present
            ? new PcPart(PartKind.Battery, SlotState.Occupied, "Batería", $"{battery.Percent} % · {battery.Status}",
                new (string, string?)[] { ("Carga", $"{battery.Percent} %"), ("Estado", battery.Status) })
            : null;

        var external = disks.Where(d => d.External).Select(d => new PcPart(PartKind.SataBay, SlotState.Occupied, d.Model,
            $"{d.SizeGB:N0} GB · {d.Bus}", new (string, string?)[] { ("Modelo", d.Model), ("Conexión", d.Bus) })).ToList();

        var model = $"{s.Board.SystemManufacturer} {s.Board.SystemModel}".Replace(HardwareText.Unknown, "").Trim();
        return new PcLayout(isLaptop, chassisName, model.Length > 0 ? model : "Tu equipo", cpu, gpus, ram, maxRam,
            storage, pcie, bat, external, reported);
    }

    public static bool IsIntegrated(string gpu) =>
        gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !gpu.Contains("Arc A", StringComparison.OrdinalIgnoreCase)
        || gpu.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
        || gpu.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
        || gpu.Contains("Vega", StringComparison.OrdinalIgnoreCase) && !gpu.Contains("RX Vega", StringComparison.OrdinalIgnoreCase)
        || gpu.Contains("Radeon 7", StringComparison.OrdinalIgnoreCase) && gpu.Contains("M", StringComparison.Ordinal)
        || gpu.Contains("Adreno", StringComparison.OrdinalIgnoreCase);

    private static string ShortCpu(string name) => name
        .Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Replace("  ", " ")
        .Split('@')[0].Trim();

    // ────────────────────────── WMI ──────────────────────────

    private static (bool IsLaptop, string Name) Chassis(bool hasBattery)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject o in s.Get())
            {
                if (o["ChassisTypes"] is ushort[] { Length: > 0 } types)
                {
                    var t = types[0];
                    if (LaptopChassis.Contains(t)) return (true, t is 30 or 31 or 32 ? "Tableta / convertible" : "Portátil");
                    if (t is 3 or 4 or 5 or 6 or 7 or 15 or 16 or 35 or 36) return (false, t is 35 or 36 ? "Mini PC" : "Sobremesa");
                }
            }
        }
        catch { }
        return hasBattery ? (true, "Portátil") : (false, "Sobremesa");
    }

    private static Dictionary<string, int> RawMemory()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceLocator, FormFactor FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
                if (o["DeviceLocator"] is string loc) map[loc.Trim()] = Convert.ToInt32(o["FormFactor"] ?? 0);
        }
        catch { }
        return map;
    }

    private static long MaxRamGB()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject o in s.Get())
            {
                ulong kb = 0;
                try { kb = Convert.ToUInt64(o["MaxCapacityEx"] ?? 0); } catch { }
                if (kb == 0) kb = Convert.ToUInt64(o["MaxCapacity"] ?? 0);
                var gb = (long)(kb / 1024 / 1024);
                if (gb is >= 2 and <= 4096) return gb;
            }
        }
        catch { }
        return 0;
    }

    private sealed record DiskRow(string Model, long SizeGB, string Bus, string Media, string Serial, bool External);

    private static List<DiskRow> PhysicalDisks(IReadOnlyList<PhysicalDisk> fallback)
    {
        var list = new List<DiskRow>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, Size, BusType, MediaType, SerialNumber FROM MSFT_PhysicalDisk");
            foreach (ManagementObject o in s.Get())
            {
                var bus = Convert.ToInt32(o["BusType"] ?? 0);
                var media = Convert.ToInt32(o["MediaType"] ?? 0);
                list.Add(new DiskRow(
                    HardwareText.Clean(o["FriendlyName"]),
                    (long)(Convert.ToUInt64(o["Size"] ?? 0) / 1_000_000_000),
                    bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 12 => "Tarjeta SD", 8 => "RAID", 10 => "SAS", 3 => "ATA", _ => "Otra" },
                    media switch { 4 => "SSD", 3 => "Disco duro", 5 => "Memoria SCM", _ => bus == 17 ? "SSD" : "Unidad" },
                    HardwareText.Clean(o["SerialNumber"]),
                    bus is 7 or 12));
            }
        }
        catch { }

        if (list.Count == 0)
        {
            foreach (var d in fallback)
            {
                var nvme = d.Model.Contains("NVMe", StringComparison.OrdinalIgnoreCase);
                list.Add(new DiskRow(d.Model, d.CapacityGB, nvme ? "NVMe" : d.Interface == "USB" ? "USB" : "SATA",
                    nvme || d.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD" : "Unidad",
                    d.SerialNumber, d.Interface == "USB"));
            }
        }
        return list;
    }

    private sealed record SystemSlot(string Name, int Usage, bool IsM2, bool IsPcie);

    /// <summary>Win32_SystemSlot: lo que la BIOS declara (M.2, PCIe…) y si está en uso (3 libre, 4 en uso).</summary>
    private static List<SystemSlot> SystemSlots()
    {
        var list = new List<SystemSlot>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SlotDesignation, CurrentUsage FROM Win32_SystemSlot");
            foreach (ManagementObject o in s.Get())
            {
                var name = (o["SlotDesignation"] as string ?? "").Trim();
                if (name.Length == 0) continue;
                var usage = Convert.ToInt32(o["CurrentUsage"] ?? 2);
                var upper = name.ToUpperInvariant();
                var wireless = upper.Contains("WLAN") || upper.Contains("WIFI") || upper.Contains("WI-FI") ||
                               upper.Contains("WWAN") || upper.Contains("E KEY") || upper.Contains("E-KEY") ||
                               upper.Contains("BT");
                var isM2 = !wireless && (upper.Contains("M.2") || upper.Contains("M2") || upper.Contains("NGFF") ||
                                         upper.Contains("SSD"));
                var isPcie = !isM2 && !wireless && (upper.Contains("PCI") || upper.Contains("PEG") || upper.Contains("X16") ||
                                                    upper.Contains("X1"));
                list.Add(new SystemSlot(name, usage, isM2, isPcie));
            }
        }
        catch { }
        return list;
    }
}
