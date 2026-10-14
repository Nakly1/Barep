using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Barep.Services;

/// <summary>
/// Única puerta de entrada al hardware. Todas las consultas WMI viven aquí y
/// ninguna lanza: si el equipo o los permisos no dan la información, se
/// devuelve un valor vacío coherente en lugar de romper la interfaz.
/// </summary>
public class HardwareService
{
    private long _cachedTotalMB;

    private readonly PerformanceCounter _cpuCounter =
        new("Processor", "% Processor Time", "_Total", readOnly: true);
    private bool _cpuWarmedUp;

    private long _prevNetBytesRecv;
    private long _prevNetBytesSent;
    private DateTime _prevNetTime = DateTime.MinValue;

    // Lo estático se consulta una vez: WMI es caro y estos datos no cambian
    // mientras el equipo esté encendido.
    private StaticSnapshot? _staticCache;

    // ────────────────────────── Instantáneas ──────────────────────────

    public StaticSnapshot GetStatic(bool forceRefresh = false)
    {
        if (_staticCache is not null && !forceRefresh) return _staticCache;

        _staticCache = new StaticSnapshot(
            Cpu: GetCpu(),
            Gpus: GetGpus(),
            Ram: GetRam(),
            Os: GetOs(),
            Board: GetBoard(),
            Volumes: GetVolumes(),
            Disks: GetPhysicalDisks(),
            Adapters: GetAdapters());

        return _staticCache;
    }

    public RealtimeSnapshot GetRealtime()
    {
        var (used, total, available) = GetRamUsage();
        return new RealtimeSnapshot(
            RamUsedMB: used,
            RamTotalMB: total,
            RamAvailableMB: available,
            CpuPercent: GetCpuUsage(),
            Network: GetNetworkSample(),
            Battery: GetBattery(),
            Uptime: GetUptime(),
            TakenAt: DateTime.Now);
    }

    // ────────────────────────── CPU ──────────────────────────

    public CpuInfo GetCpu()
    {
        foreach (var obj in Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, ProcessorId, SocketDesignation FROM Win32_Processor"))
        {
            return new CpuInfo(
                Name: HardwareText.Clean(obj["Name"], HardwareText.Unavailable),
                Cores: ToInt(obj["NumberOfCores"]),
                Threads: ToInt(obj["NumberOfLogicalProcessors"]),
                MaxMHz: ToInt(obj["MaxClockSpeed"]),
                ProcessorId: HardwareText.Clean(obj["ProcessorId"]),
                Socket: HardwareText.Clean(obj["SocketDesignation"]));
        }

        return new CpuInfo(HardwareText.Unavailable, Environment.ProcessorCount, Environment.ProcessorCount,
            0, HardwareText.Unknown, HardwareText.Unknown);
    }

    public double GetCpuUsage()
    {
        try
        {
            // La primera lectura de un PerformanceCounter siempre es 0: necesita
            // dos muestras para calcular un delta.
            if (!_cpuWarmedUp)
            {
                _cpuCounter.NextValue();
                _cpuWarmedUp = true;
                return 0;
            }
            return Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch { return 0; }
    }

    // ────────────────────────── GPU ──────────────────────────

    public List<GpuInfo> GetGpus()
    {
        var gpus = new List<GpuInfo>();

        foreach (var obj in Query(
            "SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID FROM Win32_VideoController"))
        {
            var name = obj["Name"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Microsoft Display", StringComparison.OrdinalIgnoreCase))
                continue;

            long vramMB = 0;
            if (obj["AdapterRAM"] is not null)
            {
                try
                {
                    // AdapterRAM es uint32 y se desborda a partir de 4 GB.
                    vramMB = Convert.ToUInt32(obj["AdapterRAM"]) / (1024 * 1024);
                }
                catch { }
            }

            if (vramMB == 0 || vramMB >= 4094)
            {
                var regVram = GetVramFromRegistry();
                if (regVram > 0) vramMB = regVram;
            }

            gpus.Add(new GpuInfo(
                Name: name,
                VramMB: vramMB,
                DriverVersion: HardwareText.Clean(obj["DriverVersion"]),
                DeviceId: HardwareText.Clean(obj["PNPDeviceID"])));
        }

        return gpus;
    }

    private static long GetVramFromRegistry()
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is null) return 0;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;
                using var subKey = baseKey.OpenSubKey(subKeyName);
                var val = subKey?.GetValue("HardwareInformation.qwMemorySize");
                if (val is byte[] { Length: 8 } bytes)
                    return BitConverter.ToInt64(bytes, 0) / (1024 * 1024);
                if (val is long lv && lv > 0)
                    return lv / (1024 * 1024);
            }
        }
        catch { }
        return 0;
    }

    // ────────────────────────── Memoria ──────────────────────────

    public RamSummary GetRam()
    {
        var modules = new List<MemoryModule>();
        long totalMB = 0;

        foreach (var obj in Query(
            "SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, SerialNumber, Capacity, " +
            "Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor, ConfiguredVoltage " +
            "FROM Win32_PhysicalMemory"))
        {
            var capacityMB = ToLong(obj["Capacity"]) / (1024 * 1024);
            totalMB += capacityMB;

            modules.Add(new MemoryModule(
                Slot: HardwareText.Clean(obj["DeviceLocator"], $"Ranura {modules.Count + 1}"),
                Bank: HardwareText.Clean(obj["BankLabel"], ""),
                Manufacturer: JedecVendors.Resolve(obj["Manufacturer"]),
                PartNumber: HardwareText.Clean(obj["PartNumber"]),
                SerialNumber: HardwareText.Clean(obj["SerialNumber"]),
                CapacityMB: capacityMB,
                Type: MemoryTypeName(ToInt(obj["SMBIOSMemoryType"])),
                RatedSpeedMHz: ToInt(obj["Speed"]),
                ConfiguredSpeedMHz: ToInt(obj["ConfiguredClockSpeed"]),
                FormFactor: FormFactorName(ToInt(obj["FormFactor"])),
                VoltageV: ToInt(obj["ConfiguredVoltage"]) / 1000.0));
        }

        _cachedTotalMB = totalMB;

        var dominantType = modules
            .Select(m => m.Type)
            .FirstOrDefault(t => t != HardwareText.Unknown) ?? HardwareText.Unknown;

        var topSpeed = modules.Count > 0 ? modules.Max(m => m.RatedSpeedMHz) : 0;

        return new RamSummary(
            TotalMB: totalMB,
            Type: dominantType,
            SpeedMHz: topSpeed,
            SlotsUsed: modules.Count,
            SlotsTotal: GetMemorySlotCount(modules.Count),
            Modules: modules);
    }

    /// <summary>Ranuras físicas de la placa, para saber si queda sitio para ampliar.</summary>
    private int GetMemorySlotCount(int fallback)
    {
        foreach (var obj in Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
        {
            var devices = ToInt(obj["MemoryDevices"]);
            if (devices > 0) return devices;
        }
        return fallback;
    }

    private static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR",
        31 => "LPDDR2",
        32 => "LPDDR3",
        33 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => HardwareText.Unknown
    };

    private static string FormFactorName(int formFactor) => formFactor switch
    {
        7 => "SIMM",
        8 => "DIMM",
        9 => "TSOP",
        11 => "RIMM",
        12 => "SODIMM",
        13 => "SRIMM",
        _ => HardwareText.Unknown
    };

    public (long UsedMB, long TotalMB, long AvailableMB) GetRamUsage()
    {
        foreach (var obj in Query(
            "SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
        {
            long freeMB = ToLong(obj["FreePhysicalMemory"]) / 1024;
            long totalMB = ToLong(obj["TotalVisibleMemorySize"]) / 1024;
            if (_cachedTotalMB == 0) _cachedTotalMB = totalMB;
            return (totalMB - freeMB, totalMB, freeMB);
        }
        return (_cachedTotalMB, _cachedTotalMB, 0);
    }

    // ────────────────────────── Sistema y placa ──────────────────────────

    public OsInfo GetOs()
    {
        foreach (var obj in Query(
            "SELECT Caption, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem"))
        {
            var install = HardwareText.Clean(obj["InstallDate"], "");
            if (install.Length >= 8)
            {
                install = DateTime.TryParseExact(install[..8], "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed.ToString("dd/MM/yyyy")
                    : HardwareText.Unknown;
            }
            else install = HardwareText.Unknown;

            return new OsInfo(
                Name: HardwareText.Clean(obj["Caption"], "Windows"),
                Build: HardwareText.Clean(obj["BuildNumber"]),
                Architecture: HardwareText.Clean(obj["OSArchitecture"], "64-bit"),
                InstallDate: install);
        }
        return new OsInfo("Windows", HardwareText.Unknown, "64-bit", HardwareText.Unknown);
    }

    public BoardInfo GetBoard()
    {
        string mbVendor = HardwareText.Unknown, mbProduct = HardwareText.Unknown, mbSerial = HardwareText.Unknown;
        foreach (var obj in Query("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard"))
        {
            mbVendor = HardwareText.Clean(obj["Manufacturer"]);
            mbProduct = HardwareText.Clean(obj["Product"]);
            mbSerial = HardwareText.Clean(obj["SerialNumber"]);
            break;
        }

        string biosVendor = HardwareText.Unknown, biosVersion = HardwareText.Unknown, biosSerial = HardwareText.Unknown;
        foreach (var obj in Query("SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber FROM Win32_BIOS"))
        {
            biosVendor = HardwareText.Clean(obj["Manufacturer"]);
            biosVersion = HardwareText.Clean(obj["SMBIOSBIOSVersion"]);
            biosSerial = HardwareText.Clean(obj["SerialNumber"]);
            break;
        }

        string sysVendor = HardwareText.Unknown, sysModel = HardwareText.Unknown, sysUuid = HardwareText.Unknown;
        foreach (var obj in Query("SELECT Vendor, Name, UUID FROM Win32_ComputerSystemProduct"))
        {
            sysVendor = HardwareText.Clean(obj["Vendor"]);
            sysModel = HardwareText.Clean(obj["Name"]);
            sysUuid = HardwareText.Clean(obj["UUID"]);
            break;
        }

        return new BoardInfo(mbVendor, mbProduct, mbSerial,
            biosVendor, biosVersion, biosSerial,
            sysVendor, sysModel, sysUuid);
    }

    public TimeSpan GetUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // ────────────────────────── Almacenamiento ──────────────────────────

    public List<DiskInfo> GetVolumes()
    {
        var disks = new List<DiskInfo>();
        foreach (var obj in Query(
            "SELECT DeviceID, VolumeName, Size, FreeSpace, FileSystem FROM Win32_LogicalDisk WHERE DriveType=3"))
        {
            var total = ToLong(obj["Size"]) / (1024L * 1024 * 1024);
            if (total <= 0) continue;

            disks.Add(new DiskInfo(
                Letter: HardwareText.Clean(obj["DeviceID"], "?"),
                Label: HardwareText.Clean(obj["VolumeName"], ""),
                TotalGB: total,
                FreeGB: ToLong(obj["FreeSpace"]) / (1024L * 1024 * 1024),
                FileSystem: HardwareText.Clean(obj["FileSystem"], "")));
        }
        return disks;
    }

    /// <summary>Unidades físicas con su número de serie: la pieza que se roba.</summary>
    public List<PhysicalDisk> GetPhysicalDisks()
    {
        var drives = new List<PhysicalDisk>();
        foreach (var obj in Query(
            "SELECT Model, SerialNumber, InterfaceType, MediaType, Size, FirmwareRevision FROM Win32_DiskDrive"))
        {
            drives.Add(new PhysicalDisk(
                Model: HardwareText.Clean(obj["Model"]),
                SerialNumber: HardwareText.Clean(obj["SerialNumber"]),
                Interface: HardwareText.Clean(obj["InterfaceType"], ""),
                MediaType: HardwareText.Clean(obj["MediaType"], ""),
                CapacityGB: ToLong(obj["Size"]) / (1024L * 1024 * 1024),
                FirmwareRevision: HardwareText.Clean(obj["FirmwareRevision"], "")));
        }
        return drives;
    }

    // ────────────────────────── Procesos ──────────────────────────

    public List<ProcessRow> GetTopProcesses(int count = 12)
    {
        try
        {
            return Process.GetProcesses()
                .Select(p =>
                {
                    try { return new ProcessRow(p.ProcessName, p.Id, p.WorkingSet64 / (1024 * 1024)); }
                    catch { return new ProcessRow(p.ProcessName, p.Id, 0); }
                })
                .OrderByDescending(p => p.MemoryMB)
                .Take(count)
                .ToList();
        }
        catch { return new List<ProcessRow>(); }
    }

    public (bool Ok, string Message) KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(2000);
            return (true, $"Proceso «{name}» (PID {pid}) finalizado.");
        }
        catch (ArgumentException)
        {
            return (false, $"El proceso (PID {pid}) ya no existe.");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo finalizar el PID {pid}: {ex.Message}");
        }
    }

    // ────────────────────────── Red ──────────────────────────

    public List<NetworkAdapter> GetAdapters()
    {
        var adapters = new List<NetworkAdapter>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var mac = string.Join(":", ni.GetPhysicalAddress()
                    .GetAddressBytes().Select(b => b.ToString("X2")));
                if (string.IsNullOrEmpty(mac)) continue;

                adapters.Add(new NetworkAdapter(ni.Name, mac, ni.NetworkInterfaceType.ToString()));
            }
        }
        catch { }
        return adapters;
    }

    public NetSample GetNetworkSample()
    {
        try
        {
            long bytesRecv = 0, bytesSent = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var stats = ni.GetIPStatistics();
                bytesRecv += stats.BytesReceived;
                bytesSent += stats.BytesSent;
            }

            var now = DateTime.UtcNow;
            if (_prevNetTime == DateTime.MinValue)
            {
                _prevNetBytesRecv = bytesRecv;
                _prevNetBytesSent = bytesSent;
                _prevNetTime = now;
                return new NetSample(0, 0);
            }

            var seconds = (now - _prevNetTime).TotalSeconds;
            if (seconds <= 0) return new NetSample(0, 0);

            var down = (bytesRecv - _prevNetBytesRecv) * 8.0 / 1000.0 / seconds;
            var up = (bytesSent - _prevNetBytesSent) * 8.0 / 1000.0 / seconds;

            _prevNetBytesRecv = bytesRecv;
            _prevNetBytesSent = bytesSent;
            _prevNetTime = now;

            return new NetSample(Math.Max(0, down), Math.Max(0, up));
        }
        catch { return new NetSample(0, 0); }
    }

    // ────────────────────────── Batería ──────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    public BatteryInfo GetBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s))
                return new BatteryInfo(false, 0, false, "—");

            // BatteryFlag 128 = el equipo no tiene batería.
            if ((s.BatteryFlag & 128) != 0)
                return new BatteryInfo(false, 0, s.ACLineStatus == 1, "Sin batería");

            var onAc = s.ACLineStatus == 1;
            var percent = s.BatteryLifePercent == 255 ? 0 : s.BatteryLifePercent;
            var status = onAc ? "Cargando" : "Con batería";
            if ((s.BatteryFlag & 8) != 0) status = "Cargando";
            return new BatteryInfo(true, percent, onAc, status);
        }
        catch { return new BatteryInfo(false, 0, false, "—"); }
    }

    // ────────────────────────── Utilidades ──────────────────────────

    /// <summary>
    /// Ejecuta una consulta WQL y materializa el resultado. Si WMI falla —servicio
    /// detenido, clase ausente en esta edición de Windows, permisos— se devuelve
    /// una lista vacía y quien llama usa sus valores por defecto.
    /// </summary>
    private static List<ManagementObject> Query(string wql)
    {
        var rows = new List<ManagementObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var results = searcher.Get();
            foreach (var item in results)
                if (item is ManagementObject obj) rows.Add(obj);
        }
        catch { }
        return rows;
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value); }
        catch { return 0; }
    }

    private static long ToLong(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value); }
        catch { return 0; }
    }
}
