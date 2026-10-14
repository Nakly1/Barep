using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace Barep.Services;

/// <summary>De dónde sale el proceso, para poder separar lo propio de lo del sistema.</summary>
public enum ProcessKind
{
    App,        // tiene ventana: el usuario lo abrió
    Background, // se ejecuta sin ventana (actualizadores, agentes, ayudantes)
    Windows     // forma parte de Windows
}

/// <summary>Un proceso concreto, con su PID.</summary>
public sealed record ProcessItem(
    int Pid,
    string Name,
    string Title,
    string? Path,
    string? Company,
    string? Version,
    string? Services,
    long MemoryBytes,
    double CpuPercent,
    int Threads,
    DateTime? Started,
    bool HasWindow,
    bool Elevated,
    ProcessKind Kind);

/// <summary>
/// Todos los procesos de un mismo programa en una sola fila, como hace el
/// Administrador de tareas: Chrome con veinte pestañas es un programa, no veinte.
/// </summary>
public sealed record ProcessGroup(
    string Key,
    string Title,
    string Name,
    string? Path,
    string? Company,
    string? Version,
    string? Services,
    long MemoryBytes,
    double CpuPercent,
    int Threads,
    bool HasWindow,
    ProcessKind Kind,
    DateTime? Started,
    List<ProcessItem> Items)
{
    public int Count => Items.Count;
}

/// <summary>Una lectura completa de todo lo que está corriendo.</summary>
public sealed record ProcessSnapshot(
    List<ProcessGroup> Groups,
    int ProcessCount,
    int ThreadCount,
    long RamTotalBytes,
    long RamAvailableBytes,
    DateTime TakenAt)
{
    public long RamUsedBytes => Math.Max(0, RamTotalBytes - RamAvailableBytes);
    public double CpuPercent => Math.Min(100, Groups.Sum(g => g.CpuPercent));
}

/// <summary>
/// La lista completa de procesos: los que tienen ventana, los que trabajan en
/// segundo plano sin que se vean y los de Windows. El porcentaje de procesador
/// se calcula comparando dos lecturas, igual que el Administrador de tareas.
/// </summary>
public sealed class ProcessService
{
    private readonly Dictionary<int, (TimeSpan Cpu, long Stamp)> _cpu = new();
    private readonly Dictionary<string, (string? Description, string? Company, string? Version)> _files =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<int, string> _services = new();
    private DateTime _servicesAt = DateTime.MinValue;

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');

    /// <summary>
    /// Procesos que no son ejecutables normales: Windows no deja leer su ruta ni
    /// su descripción, así que el nombre se pone a mano para que signifiquen algo.
    /// </summary>
    private static readonly Dictionary<string, string> Pseudo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Idle"] = "Procesador en reposo",
        ["System"] = "Núcleo de Windows",
        ["Registry"] = "Registro de Windows",
        ["Secure System"] = "Sistema seguro (virtualización)",
        ["Memory Compression"] = "Memoria comprimida",
        ["vmmem"] = "Máquinas virtuales y WSL",
        ["vmmemWSL"] = "Linux en Windows (WSL)"
    };

    /// <summary>Lectura completa. Pesa unos milisegundos: conviene llamarla fuera del hilo de interfaz.</summary>
    public ProcessSnapshot Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var services = Services();
        var items = new List<ProcessItem>(400);
        var alive = new HashSet<int>();
        int threads = 0;

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { processes = Array.Empty<Process>(); }

        foreach (var p in processes)
        {
            try
            {
                var pid = p.Id;
                alive.Add(pid);

                string name;
                try { name = p.ProcessName; } catch { continue; }

                long memory = 0;
                try { memory = p.WorkingSet64; } catch { }

                int count = 0;
                try { count = p.Threads.Count; } catch { }
                threads += count;

                DateTime? started = null;
                try { started = p.StartTime; } catch { }

                var hasWindow = false;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }

                var path = PathOf(pid);
                var (description, company, version) = FileFacts(path);

                var title = Pseudo.TryGetValue(name, out var friendly)
                    ? friendly
                    : Clean(description) ?? Pretty(name);

                var kind = path is null || path.StartsWith(WindowsDir + "\\", StringComparison.OrdinalIgnoreCase)
                    ? ProcessKind.Windows
                    : hasWindow
                        ? ProcessKind.App
                        : ProcessKind.Background;

                items.Add(new ProcessItem(
                    Pid: pid,
                    Name: name,
                    Title: title,
                    Path: path,
                    Company: Clean(company),
                    Version: Clean(version),
                    Services: services.GetValueOrDefault(pid),
                    MemoryBytes: memory,
                    CpuPercent: CpuShare(pid, p, now),
                    Threads: count,
                    Started: started,
                    HasWindow: hasWindow,
                    Elevated: path is null,
                    Kind: kind));
            }
            catch { /* el proceso pudo cerrarse mientras se leía */ }
            finally { try { p.Dispose(); } catch { } }
        }

        foreach (var dead in _cpu.Keys.Where(k => !alive.Contains(k)).ToList()) _cpu.Remove(dead);

        var (total, available) = Ram();
        return new ProcessSnapshot(Group(items), items.Count, threads, total, available, DateTime.Now);
    }

    private double CpuShare(int pid, Process p, long now)
    {
        TimeSpan cpu;
        try { cpu = p.TotalProcessorTime; }
        catch { return 0; }

        double percent = 0;
        if (_cpu.TryGetValue(pid, out var previous) && now > previous.Stamp)
        {
            var seconds = (now - previous.Stamp) / (double)Stopwatch.Frequency;
            var busy = (cpu - previous.Cpu).TotalSeconds;
            if (seconds > 0.1 && busy > 0)
                percent = Math.Min(100, busy / (seconds * Environment.ProcessorCount) * 100);
        }

        _cpu[pid] = (cpu, now);
        return percent;
    }

    // ────────────────────────── Agrupar ──────────────────────────

    private static List<ProcessGroup> Group(List<ProcessItem> items)
    {
        var groups = new List<ProcessGroup>();

        foreach (var set in items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var list = set.OrderByDescending(i => i.MemoryBytes).ToList();

            // La ficha se toma del proceso con ventana si lo hay: es el que lleva
            // el nombre bonito y la versión del programa.
            var lead = list.FirstOrDefault(i => i.HasWindow) ?? list[0];
            var services = list.Select(i => i.Services).FirstOrDefault(s => s is not null);

            groups.Add(new ProcessGroup(
                Key: set.Key.ToLowerInvariant(),
                Title: lead.Title,
                Name: lead.Name,
                Path: list.Select(i => i.Path).FirstOrDefault(p => p is not null),
                Company: list.Select(i => i.Company).FirstOrDefault(c => c is not null),
                Version: lead.Version ?? list.Select(i => i.Version).FirstOrDefault(v => v is not null),
                Services: services,
                MemoryBytes: list.Sum(i => i.MemoryBytes),
                CpuPercent: list.Sum(i => i.CpuPercent),
                Threads: list.Sum(i => i.Threads),
                HasWindow: list.Any(i => i.HasWindow),
                Kind: list.Any(i => i.HasWindow) ? ProcessKind.App : list.Min(i => i.Kind),
                Started: list.Where(i => i.Started is not null).Select(i => i.Started).Min(),
                Items: list));
        }

        groups.Sort((a, b) => b.MemoryBytes.CompareTo(a.MemoryBytes));
        return groups;
    }

    // ────────────────────────── Datos del ejecutable ──────────────────────────

    private (string? Description, string? Company, string? Version) FileFacts(string? path)
    {
        if (path is null) return (null, null, null);
        if (_files.TryGetValue(path, out var cached)) return cached;

        (string?, string?, string?) facts = (null, null, null);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            facts = (info.FileDescription ?? info.ProductName, info.CompanyName, info.FileVersion);
        }
        catch { }

        _files[path] = facts;
        return facts;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>«GoogleUpdateCore» → «Google Update Core», para cuando no hay descripción.</summary>
    private static string Pretty(string processName)
    {
        var text = processName.Replace('_', ' ').Replace('-', ' ');
        var builder = new StringBuilder(text.Length + 6);
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && (char.IsLower(text[i - 1]) ||
                (i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1]))))
                builder.Append(' ');
            builder.Append(text[i]);
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 ? processName : char.ToUpper(result[0]) + result[1..];
    }

    // ────────────────────────── Servicios ──────────────────────────

    /// <summary>
    /// Qué servicio de Windows hay detrás de cada proceso: sin esto, media docena
    /// de «svchost» no le dicen nada a nadie. Se relee cada medio minuto.
    /// </summary>
    private Dictionary<int, string> Services()
    {
        if ((DateTime.Now - _servicesAt).TotalSeconds < 30) return _services;
        _servicesAt = DateTime.Now;

        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, DisplayName FROM Win32_Service WHERE State = 'Running'");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    if (pid <= 0) continue;
                    var name = obj["DisplayName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    map[pid] = map.TryGetValue(pid, out var existing) ? $"{existing}, {name}" : name;
                }
                catch { }
                finally { obj.Dispose(); }
            }
        }
        catch { return _services; }

        _services = map;
        return map;
    }

    // ────────────────────────── Finalizar ──────────────────────────

    /// <summary>Cierra todos los procesos de un programa. Devuelve cuántos se cerraron.</summary>
    public static (int Closed, string Message) Kill(ProcessGroup group)
    {
        int closed = 0;
        string? problem = null;

        foreach (var item in group.Items)
        {
            try
            {
                using var p = Process.GetProcessById(item.Pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(1500);
                closed++;
            }
            catch (ArgumentException) { /* ya no existía */ }
            catch (Exception ex) { problem ??= ex.Message; }
        }

        if (closed > 0)
            return (closed, closed == 1
                ? $"«{group.Title}» se cerró."
                : $"«{group.Title}»: {closed} procesos cerrados.");

        return (0, problem is null
            ? $"«{group.Title}» ya no se estaba ejecutando."
            : $"Windows no deja cerrar «{group.Title}»: {problem}");
    }

    // ────────────────────────── Interop ──────────────────────────

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// La ruta del ejecutable. Se pide con el permiso mínimo porque
    /// Process.MainModule falla en cuanto el proceso es de 64 bits y la app no,
    /// o pertenece a otro usuario.
    /// </summary>
    public static string? PathOf(int pid)
    {
        if (pid <= 4) return null;
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return null;

            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch { return null; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

    private static (long Total, long Available) Ram()
    {
        try
        {
            var status = new MemoryStatus();
            if (GlobalMemoryStatusEx(status))
                return ((long)status.TotalPhysical, (long)status.AvailablePhysical);
        }
        catch { }
        return (0, 0);
    }

    /// <summary>Abre la carpeta del ejecutable en el Explorador.</summary>
    public static void Reveal(string? path)
    {
        if (path is not null && File.Exists(path)) AppManager.Reveal(path);
    }
}
