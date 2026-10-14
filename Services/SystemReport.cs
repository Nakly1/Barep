using System.Text;

namespace Barep.Services;

/// <summary>
/// Informe legible del estado del equipo. Es una foto informativa; para dejar
/// constancia de las piezas con valor probatorio está <see cref="EvidenceService"/>.
/// </summary>
public static class SystemReport
{
    public static string Build(StaticSnapshot s, RealtimeSnapshot r)
    {
        var sb = new StringBuilder();
        var rule = new string('─', 62);

        sb.AppendLine(rule);
        sb.AppendLine("  BAREP — INFORME DEL SISTEMA");
        sb.AppendLine(rule);
        sb.AppendLine($"  Generado   {DateTime.Now:dddd d 'de' MMMM 'de' yyyy, HH:mm:ss}");
        sb.AppendLine($"  Equipo     {Environment.MachineName}");
        sb.AppendLine($"  Usuario    {Environment.UserName}");
        sb.AppendLine($"  Encendido  hace {(int)r.Uptime.TotalDays} d {r.Uptime.Hours} h {r.Uptime.Minutes} min");
        sb.AppendLine();

        Section(sb, "SISTEMA OPERATIVO");
        Field(sb, "Edición", s.Os.Name);
        Field(sb, "Compilación", s.Os.Build);
        Field(sb, "Arquitectura", s.Os.Architecture);
        Field(sb, "Instalado el", s.Os.InstallDate);

        Section(sb, "EQUIPO");
        Field(sb, "Fabricante", s.Board.SystemManufacturer);
        Field(sb, "Modelo", s.Board.SystemModel);
        Field(sb, "Placa base", $"{s.Board.Manufacturer} {s.Board.Product}".Trim());
        Field(sb, "BIOS", $"{s.Board.BiosVendor} {s.Board.BiosVersion}".Trim());

        Section(sb, "PROCESADOR");
        Field(sb, "Modelo", s.Cpu.Name);
        Field(sb, "Núcleos / hilos", $"{s.Cpu.Cores} / {s.Cpu.Threads}");
        Field(sb, "Frecuencia máxima", s.Cpu.MaxMHz > 0 ? $"{s.Cpu.MaxMHz} MHz" : HardwareText.Unknown);
        Field(sb, "Uso en este momento", $"{r.CpuPercent:0.0} %");

        Section(sb, "MEMORIA");
        Field(sb, "Total instalada", $"{s.Ram.TotalMB / 1024.0:0.#} GB");
        Field(sb, "Tecnología", s.Ram.Type);
        Field(sb, "Velocidad", s.Ram.SpeedMHz > 0 ? $"{s.Ram.SpeedMHz} MT/s" : HardwareText.Unknown);
        Field(sb, "Ranuras", $"{s.Ram.SlotsUsed} usadas de {s.Ram.SlotsTotal}");
        Field(sb, "En uso ahora", $"{r.RamUsedMB:N0} MB ({r.RamPercent:0.0} %)");
        Field(sb, "Disponible", $"{r.RamAvailableMB:N0} MB");
        sb.AppendLine();

        foreach (var m in s.Ram.Modules)
        {
            sb.AppendLine($"    · {m.DisplayName}");
            sb.AppendLine($"      Ranura {m.Slot} · parte {m.PartNumber} · serie {m.SerialNumber}");
        }

        Section(sb, "GRÁFICOS");
        foreach (var g in s.Gpus)
        {
            sb.AppendLine($"    · {g.Name}");
            sb.AppendLine($"      VRAM {(g.VramMB > 0 ? $"{g.VramMB / 1024.0:0.#} GB" : HardwareText.Unknown)}" +
                          $" · controlador {g.DriverVersion}");
        }

        Section(sb, "ALMACENAMIENTO");
        foreach (var d in s.Disks)
            sb.AppendLine($"    · {d.Model} — {d.CapacityGB:N0} GB — serie {d.SerialNumber}");
        sb.AppendLine();
        foreach (var v in s.Volumes)
            sb.AppendLine($"      {v.Letter,-4} {v.Label,-16} {v.UsedGB,6:N0} / {v.TotalGB,6:N0} GB  ({v.UsedPercent:0.0} %)");

        Section(sb, "ENERGÍA");
        sb.AppendLine(r.Battery.Present
            ? $"    Batería al {r.Battery.Percent} % — {r.Battery.Status}"
            : "    Sin batería (equipo de sobremesa)");

        Section(sb, "PROCESOS CON MÁS MEMORIA");
        foreach (var p in AppServices.Hardware.GetTopProcesses())
            sb.AppendLine($"    {p.Name,-32} PID {p.Pid,-8} {p.MemoryMB,7:N0} MB");

        sb.AppendLine();
        sb.AppendLine(rule);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine($"  ▸ {title}");
        sb.AppendLine();
    }

    private static void Field(StringBuilder sb, string label, string value) =>
        sb.AppendLine($"    {label,-22}{value}");
}
