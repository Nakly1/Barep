using System.Management;
using System.Runtime.InteropServices;

namespace Barep.Services;

/// <summary>Una pantalla conectada, con lo que Windows sabe de ella.</summary>
public sealed record DisplayInfo(
    string GdiName,                 // \\.\DISPLAY1 — para cambiar la frecuencia
    string Name,                    // «Pantalla integrada» o el nombre del monitor
    bool IsInternal,
    bool IsPrimary,
    string Connection,              // HDMI, DisplayPort, integrada…
    int Width, int Height,          // resolución actual
    int NativeWidth, int NativeHeight,
    double RefreshHz,               // frecuencia actual exacta (p. ej. 143,98)
    IReadOnlyList<int> SupportedHz, // frecuencias posibles a la resolución actual
    int ScalePercent,
    int BitsPerColor,
    bool HdrSupported, bool HdrEnabled,
    double DiagonalInches,
    string ManufacturerCode, string Manufacturer, string ProductCode,
    int? YearOfManufacture,
    string SerialNumber)
{
    public int MaxHz => SupportedHz.Count > 0 ? SupportedHz.Max() : (int)Math.Round(RefreshHz);
    public string AspectRatio
    {
        get
        {
            static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
            var g = Gcd(NativeWidth, NativeHeight);
            if (g == 0) return "";
            var (w, h) = (NativeWidth / g, NativeHeight / g);
            return (w, h) switch { (8, 5) => "16:10", (43, 18) or (64, 27) => "21:9", _ => $"{w}:{h}" };
        }
    }
}

/// <summary>
/// Lee las pantallas con tres fuentes que se complementan: la API de
/// configuración de pantalla de Windows (frecuencia exacta, HDR, tipo de
/// conexión), los modos del controlador (qué frecuencias admite) y el EDID que
/// el propio monitor publica (fabricante, modelo, tamaño, año).
/// </summary>
public static class DisplayService
{
    public static List<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
        var edid = ReadEdid();
        var dpi = ScalesByDevice();

        foreach (var target in QueryTargets())
        {
            var current = CurrentMode(target.GdiName);
            var modes = AllModes(target.GdiName);
            var native = modes.OrderByDescending(m => (long)m.W * m.H).FirstOrDefault();
            var hz = modes.Where(m => m.W == current.W && m.H == current.H && m.Hz > 1)
                .Select(m => m.Hz).Distinct().OrderBy(h => h).ToList();

            var info = edid.FirstOrDefault(e => MatchesPath(e.Instance, target.DevicePath));
            var isInternal = target.OutputTech is 0x80000000 or 6 or 11 or 13;

            var name = !string.IsNullOrWhiteSpace(target.FriendlyName) ? target.FriendlyName
                : !string.IsNullOrWhiteSpace(info?.FriendlyName) ? info!.FriendlyName
                : isInternal ? "Pantalla integrada" : "Monitor";
            if (isInternal && (name == "Monitor" || name.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)))
                name = "Pantalla integrada";

            var refresh = target.RefreshDen > 0 ? target.RefreshNum / (double)target.RefreshDen : current.Hz;
            if (refresh <= 1) refresh = current.Hz;

            var diagonal = info is { WidthCm: > 0, HeightCm: > 0 }
                ? Math.Sqrt(info.WidthCm * info.WidthCm + info.HeightCm * info.HeightCm) / 2.54
                : 0;

            result.Add(new DisplayInfo(
                GdiName: target.GdiName,
                Name: name,
                IsInternal: isInternal,
                IsPrimary: current.X == 0 && current.Y == 0,
                Connection: ConnectionName(target.OutputTech),
                Width: current.W, Height: current.H,
                NativeWidth: native.W > 0 ? native.W : current.W,
                NativeHeight: native.H > 0 ? native.H : current.H,
                RefreshHz: refresh,
                SupportedHz: hz,
                ScalePercent: dpi.TryGetValue(target.GdiName, out var s) ? s : 100,
                BitsPerColor: target.BitsPerColor,
                HdrSupported: target.HdrSupported,
                HdrEnabled: target.HdrEnabled,
                DiagonalInches: diagonal,
                ManufacturerCode: info?.ManufacturerCode ?? (target.EdidManufacturer ?? ""),
                Manufacturer: VendorName(info?.ManufacturerCode ?? target.EdidManufacturer ?? ""),
                ProductCode: info?.ProductCode ?? (target.EdidProduct > 0 ? target.EdidProduct.ToString("X4") : ""),
                YearOfManufacture: info?.Year,
                SerialNumber: info?.Serial ?? ""));
        }

        return result.OrderByDescending(d => d.IsPrimary).ToList();
    }

    private static bool MatchesPath(string wmiInstance, string devicePath)
    {
        // WMI:  DISPLAY\BOE0A1B\4&2ea6e9d&0&UID8388688_0
        // Ruta: \\?\DISPLAY#BOE0A1B#4&2ea6e9d&0&UID8388688#{e6f07b5f-…}
        if (string.IsNullOrEmpty(wmiInstance) || string.IsNullOrEmpty(devicePath)) return false;
        var path = devicePath.Replace(@"\\?\", "");
        var brace = path.IndexOf("#{", StringComparison.Ordinal);
        if (brace > 0) path = path[..brace];
        path = path.Replace('#', '\\');
        return wmiInstance.StartsWith(path, StringComparison.OrdinalIgnoreCase);
    }

    private static string ConnectionName(uint tech) => tech switch
    {
        0x80000000 => "Integrada",
        11 => "Integrada (eDP)",
        6 => "Integrada (LVDS)",
        13 => "Integrada",
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        12 => "UDI",
        15 => "Inalámbrica (Miracast)",
        16 => "USB / virtual",
        _ => "Desconocida"
    };

    /// <summary>Los fabricantes de paneles y monitores más habituales según su código EDID.</summary>
    public static string VendorName(string code) => code.ToUpperInvariant() switch
    {
        "BOE" => "BOE",
        "AUO" => "AU Optronics",
        "LGD" => "LG Display",
        "SDC" => "Samsung Display",
        "CMN" => "Innolux",
        "SHP" => "Sharp",
        "IVO" => "InfoVision",
        "NCP" => "Nanjing Panda",
        "CSO" => "CSOT (TCL)",
        "TMA" => "Tianma",
        "GSM" => "LG",
        "SAM" => "Samsung",
        "DEL" => "Dell",
        "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo",
        "ACR" => "Acer",
        "AUS" or "ACI" => "ASUS",
        "MSI" => "MSI",
        "AOC" => "AOC",
        "PHL" => "Philips",
        "BNQ" => "BenQ",
        "VSC" => "ViewSonic",
        "GBT" => "Gigabyte",
        "XMI" => "Xiaomi",
        "HKC" => "HKC",
        "APP" => "Apple",
        "SNY" => "Sony",
        "" => "",
        _ => code
    };

    // ────────────────────────── Cambiar frecuencia ──────────────────────────

    /// <summary>Cambia la frecuencia de una pantalla. Devuelve false si el controlador no la acepta.</summary>
    public static bool SetRefreshRate(string gdiName, int hz)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettings(gdiName, EnumCurrentSettings, ref mode)) return false;
        mode.dmDisplayFrequency = hz;
        mode.dmFields = DmDisplayFrequency;

        // Primero se prueba sin aplicar: si el controlador no lo admite, no se toca nada.
        if (ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, CdsTest, IntPtr.Zero) != 0) return false;
        return ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == 0;
    }

    // ────────────────────────── Modos del controlador ──────────────────────────

    private readonly record struct Mode(int W, int H, int Hz, int X, int Y);

    private static Mode CurrentMode(string device)
    {
        var dm = NewDevMode();
        return EnumDisplaySettings(device, EnumCurrentSettings, ref dm)
            ? new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmPositionX, dm.dmPositionY)
            : default;
    }

    private static List<Mode> AllModes(string device)
    {
        var list = new List<Mode>();
        var dm = NewDevMode();
        for (int i = 0; i < 2000 && EnumDisplaySettings(device, i, ref dm); i++)
            list.Add(new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, 0, 0));
        return list;
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private const int EnumCurrentSettings = -1;
    private const int DmDisplayFrequency = 0x400000;
    private const int CdsUpdateRegistry = 0x1;
    private const int CdsTest = 0x2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

    // ────────────────────────── Escala (PPP) ──────────────────────────

    private static Dictionary<string, int> ScalesByDevice()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi) && GetDpiForMonitor(hMon, 0, out var dpiX, out _) == 0)
                    map[mi.szDevice] = (int)Math.Round(dpiX / 96.0 * 100);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return map;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int rcLeft, rcTop, rcRight, rcBottom;
        public int wkLeft, wkTop, wkRight, wkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ────────────────────────── API de configuración de pantalla ──────────────────────────

    private sealed record Target(
        string GdiName, string FriendlyName, string DevicePath, uint OutputTech,
        uint RefreshNum, uint RefreshDen, bool HdrSupported, bool HdrEnabled, int BitsPerColor,
        string? EdidManufacturer, int EdidProduct);

    private const int QdcOnlyActivePaths = 2;
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(int flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(int flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes,
        IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

    /// <summary>
    /// Recorre las rutas activas (adaptador → pantalla). Los paquetes se leen por
    /// desplazamiento sobre memoria nativa: así no dependemos de declarar las
    /// uniones de la API en C#, que es donde suelen colarse errores.
    /// </summary>
    private static List<Target> QueryTargets()
    {
        var list = new List<Target>();
        IntPtr paths = IntPtr.Zero, modes = IntPtr.Zero;
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var numPaths, out var numModes) != 0) return Fallback();
            paths = Marshal.AllocHGlobal((int)numPaths * PathInfoSize);
            modes = Marshal.AllocHGlobal((int)numModes * ModeInfoSize);
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
                return Fallback();

            for (int i = 0; i < numPaths; i++)
            {
                var p = paths + i * PathInfoSize;
                var srcLow = Marshal.ReadInt32(p, 0);
                var srcHigh = Marshal.ReadInt32(p, 4);
                var srcId = Marshal.ReadInt32(p, 8);
                var tgtLow = Marshal.ReadInt32(p, 20);
                var tgtHigh = Marshal.ReadInt32(p, 24);
                var tgtId = Marshal.ReadInt32(p, 28);
                var outputTech = (uint)Marshal.ReadInt32(p, 36);
                var refreshNum = (uint)Marshal.ReadInt32(p, 48);
                var refreshDen = (uint)Marshal.ReadInt32(p, 52);

                var gdi = SourceName(srcLow, srcHigh, srcId);
                if (string.IsNullOrEmpty(gdi)) continue;
                var (friendly, devicePath, edidMan, edidProd) = TargetName(tgtLow, tgtHigh, tgtId);
                var (hdrSupported, hdrEnabled, bits) = AdvancedColor(tgtLow, tgtHigh, tgtId);

                list.Add(new Target(gdi, friendly, devicePath, outputTech, refreshNum, refreshDen,
                    hdrSupported, hdrEnabled, bits, edidMan, edidProd));
            }
        }
        catch
        {
            return Fallback();
        }
        finally
        {
            if (paths != IntPtr.Zero) Marshal.FreeHGlobal(paths);
            if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes);
        }
        return list.Count > 0 ? list : Fallback();
    }

    /// <summary>Si la API moderna falla, al menos se listan las pantallas con la clásica.</summary>
    private static List<Target> Fallback()
    {
        var list = new List<Target>();
        var screens = new HashSet<string>(ScalesByDevice().Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var name in screens)
            list.Add(new Target(name, "", "", 0xFFFFFFFF, 0, 0, false, false, 8, null, 0));
        return list;
    }

    private static IntPtr Packet(int type, int size, int low, int high, int id)
    {
        var packet = Marshal.AllocHGlobal(size);
        for (int o = 0; o < size; o += 4) Marshal.WriteInt32(packet, o, 0);
        Marshal.WriteInt32(packet, 0, type);
        Marshal.WriteInt32(packet, 4, size);
        Marshal.WriteInt32(packet, 8, low);
        Marshal.WriteInt32(packet, 12, high);
        Marshal.WriteInt32(packet, 16, id);
        return packet;
    }

    private static string SourceName(int low, int high, int id)
    {
        const int size = 20 + 64;
        var packet = Packet(1, size, low, high, id);
        try
        {
            return DisplayConfigGetDeviceInfo(packet) == 0 ? Marshal.PtrToStringUni(packet + 20) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(packet); }
    }

    private static (string Friendly, string Path, string? EdidManufacturer, int EdidProduct) TargetName(int low, int high, int id)
    {
        const int size = 20 + 4 + 4 + 4 + 4 + 128 + 256;
        var packet = Packet(2, size, low, high, id);
        try
        {
            if (DisplayConfigGetDeviceInfo(packet) != 0) return ("", "", null, 0);
            var flags = Marshal.ReadInt32(packet, 20);
            var manufacturerId = (ushort)Marshal.ReadInt16(packet, 28);
            var productId = (ushort)Marshal.ReadInt16(packet, 30);
            var friendly = Marshal.PtrToStringUni(packet + 36) ?? "";
            var path = Marshal.PtrToStringUni(packet + 36 + 128) ?? "";
            var edidValid = (flags & 0x4) != 0;   // bit 2: edidIdsValid
            return (friendly, path, edidValid ? DecodePnpId(manufacturerId) : null, edidValid ? productId : 0);
        }
        finally { Marshal.FreeHGlobal(packet); }
    }

    /// <summary>El código de fabricante del EDID son tres letras de 5 bits cada una, en big-endian.</summary>
    private static string DecodePnpId(ushort raw)
    {
        var v = (ushort)((raw >> 8) | (raw << 8));
        char C(int shift) => (char)('A' - 1 + ((v >> shift) & 0x1F));
        return new string(new[] { C(10), C(5), C(0) });
    }

    private static (bool Supported, bool Enabled, int Bits) AdvancedColor(int low, int high, int id)
    {
        const int size = 20 + 4 + 4 + 4;
        var packet = Packet(9, size, low, high, id);
        try
        {
            if (DisplayConfigGetDeviceInfo(packet) != 0) return (false, false, 8);
            var value = Marshal.ReadInt32(packet, 20);
            var bits = Marshal.ReadInt32(packet, 28);
            return ((value & 0x1) != 0, (value & 0x2) != 0, bits is > 0 and <= 16 ? bits : 8);
        }
        finally { Marshal.FreeHGlobal(packet); }
    }

    // ────────────────────────── EDID por WMI ──────────────────────────

    private sealed record EdidInfo(string Instance, string FriendlyName, string ManufacturerCode, string ProductCode,
        string Serial, int? Year, double WidthCm, double HeightCm);

    private static List<EdidInfo> ReadEdid()
    {
        var list = new List<EdidInfo>();
        var sizes = new Dictionary<string, (double W, double H)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams");
            foreach (ManagementObject o in s.Get())
                sizes[(string)o["InstanceName"]] = (Convert.ToDouble(o["MaxHorizontalImageSize"]),
                    Convert.ToDouble(o["MaxVerticalImageSize"]));
        }
        catch { }

        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi",
                "SELECT InstanceName, ManufacturerName, ProductCodeID, SerialNumberID, UserFriendlyName, YearOfManufacture FROM WmiMonitorID");
            foreach (ManagementObject o in s.Get())
            {
                var instance = (string)o["InstanceName"];
                sizes.TryGetValue(instance, out var size);
                int? year = o["YearOfManufacture"] is { } y && Convert.ToInt32(y) > 1990 ? Convert.ToInt32(y) : null;
                list.Add(new EdidInfo(instance, Decode(o["UserFriendlyName"]), Decode(o["ManufacturerName"]),
                    Decode(o["ProductCodeID"]), Decode(o["SerialNumberID"]), year, size.W, size.H));
            }
        }
        catch { }
        return list;
    }

    private static string Decode(object? raw) => raw is ushort[] chars
        ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim()
        : "";
}
