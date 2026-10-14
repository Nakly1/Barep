using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Barep.Services;

/// <summary>
/// Aplicaciones instaladas, desinstalación completa (el desinstalador oficial y
/// después los restos que deja) y borrado seguro a la papelera. También decide
/// qué rutas no se pueden tocar nunca desde la Lupa.
/// </summary>
public static class AppManager
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // ────────────────────────── Inventario ──────────────────────────

    public static List<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Read(RegistryHive.LocalMachine, RegistryView.Registry64, apps, seen);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, apps, seen);
        Read(RegistryHive.CurrentUser, RegistryView.Default, apps, seen);

        apps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return apps;
    }

    private static void Read(RegistryHive hive, RegistryView view, List<InstalledApp> apps, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallPath);
            if (root is null) return;

            foreach (var keyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key is null) continue;

                    var name = Str(key, "DisplayName");
                    var uninstall = Str(key, "UninstallString");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstall)) continue;

                    // Componentes del sistema, parches y actualizaciones no son
                    // aplicaciones que el usuario reconozca como propias.
                    if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (!string.IsNullOrEmpty(Str(key, "ParentKeyName"))) continue;
                    var release = Str(key, "ReleaseType");
                    if (release is "Update" or "Hotfix" or "Security Update") continue;

                    var version = Str(key, "DisplayVersion");
                    if (!seen.Add($"{name}|{version}")) continue;

                    var icon = Str(key, "DisplayIcon");
                    var location = CleanLocation(Str(key, "InstallLocation"))
                                   ?? LocationFromExecutable(icon, name)
                                   ?? LocationFromExecutable(uninstall, name);

                    long sizeKB = key.GetValue("EstimatedSize") is int kb ? kb : 0;

                    apps.Add(new InstalledApp(
                        DisplayName: name.Trim(),
                        Publisher: Str(key, "Publisher")?.Trim(),
                        Version: version,
                        InstallLocation: location,
                        IconPath: icon,
                        UninstallString: uninstall,
                        QuietUninstallString: Str(key, "QuietUninstallString"),
                        EstimatedSizeBytes: sizeKB * 1024,
                        InstallDate: ParseDate(Str(key, "InstallDate")),
                        RegistryKeyPath: $@"{UninstallPath}\{keyName}",
                        RegistryHive: $"{hive}|{view}",
                        HelpLink: Str(key, "HelpLink") ?? Str(key, "URLInfoAbout")));
                }
                catch { /* una clave rota no debe tumbar el inventario */ }
            }
        }
        catch { }
    }

    private static string? Str(RegistryKey key, string name) =>
        key.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static DateTime? ParseDate(string? yyyymmdd) =>
        yyyymmdd is { Length: 8 } &&
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static string? CleanLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = Environment.ExpandEnvironmentVariables(SpaceScanner.Normalize(raw));
        if (!Path.IsPathRooted(path)) return null;
        try { path = Path.GetFullPath(path); } catch { return null; }
        return IsUsableAppFolder(path) ? path : null;
    }

    /// <summary>
    /// Muchos instaladores no rellenan InstallLocation. Si el icono o el
    /// desinstalador son un .exe dentro de la carpeta de la aplicación, esa
    /// carpeta es la ubicación.
    /// </summary>
    private static string? LocationFromExecutable(string? command, string displayName)
    {
        var (file, _) = SplitCommand(command);
        if (file is null) return null;
        file = file.Split(',')[0];
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return null;
        if (file.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
            file.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase) ||
            file.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
            file.Contains(@"\Common Files\", StringComparison.OrdinalIgnoreCase)) return null;

        var dir = Path.GetDirectoryName(file);
        if (dir is null) return null;

        // «…\App\uninstall\unins000.exe» → «…\App»
        var leaf = Path.GetFileName(dir);
        if (leaf.Equals("uninstall", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("uninst", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir);

        if (dir is null || !IsUsableAppFolder(dir)) return null;

        // Sólo se acepta si la carpeta se parece al nombre de la aplicación:
        // un icono prestado de una carpeta compartida no la convierte en suya.
        var folder = Path.GetFileName(dir);
        var tokens = Regex.Split(displayName, @"[^\p{L}\p{N}]+").Where(t => t.Length >= 3);
        return tokens.Any(t => folder.Contains(t, StringComparison.OrdinalIgnoreCase)) ? dir : null;
    }

    private static bool IsUsableAppFolder(string path) =>
        Directory.Exists(path) && !IsProtected(path, out _) &&
        !path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    // ────────────────────────── Desinstalar ──────────────────────────

    /// <summary>
    /// Lanza el desinstalador oficial. Se ejecuta con ShellExecute para que
    /// Windows pueda pedir permisos de administrador si hacen falta.
    /// </summary>
    public static (Process? Process, string? Error) StartUninstaller(InstalledApp app)
    {
        var (file, args) = SplitCommand(app.UninstallString);
        if (file is null) return (null, "La aplicación no declaró cómo desinstalarse.");

        // MsiExec /I abre el asistente de «modificar»; /X desinstala directamente.
        if (Path.GetFileName(file).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            args = Regex.Replace(args, @"/I(?=\s*\{)", "/X", RegexOptions.IgnoreCase);

        try
        {
            var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file) is { Length: > 0 } wd && Directory.Exists(wd)
                    ? wd
                    : Environment.CurrentDirectory
            });
            return (process, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (null, "Cancelaste el permiso de administrador.");
        }
        catch (Exception ex)
        {
            return (null, $"No se pudo abrir el desinstalador: {ex.Message}");
        }
    }

    /// <summary>¿Sigue registrada la aplicación? Si no, el desinstalador terminó.</summary>
    public static bool IsStillInstalled(InstalledApp app)
    {
        try
        {
            var parts = app.RegistryHive.Split('|');
            var hive = Enum.Parse<RegistryHive>(parts[0]);
            var view = Enum.Parse<RegistryView>(parts[1]);
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(app.RegistryKeyPath);
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Separa «"C:\Program Files\App\unins000.exe" /SILENT» en ejecutable y
    /// argumentos, aunque la ruta tenga espacios y no venga entre comillas.
    /// </summary>
    public static (string? File, string Args) SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return (null, "");
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());

        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 1) return (cmd[1..end], cmd[(end + 1)..].Trim());
            return (cmd.Trim('"'), "");
        }

        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());

        var space = cmd.IndexOf(' ');
        return space > 0 ? (cmd[..space], cmd[(space + 1)..].Trim()) : (cmd, "");
    }

    // ────────────────────────── Restos ──────────────────────────

    /// <summary>
    /// Busca lo que suelen dejar los desinstaladores: la carpeta de instalación,
    /// la configuración en AppData y ProgramData y los accesos directos. Sólo
    /// se aceptan coincidencias exactas de nombre para no tocar nada ajeno.
    /// </summary>
    public static List<Leftover> FindLeftovers(InstalledApp app, IEnumerable<InstalledApp> stillInstalled)
    {
        var found = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Carpetas de otras aplicaciones que siguen instaladas: nunca son restos,
        // aunque compartan fabricante o carpeta madre con la desinstalada.
        var others = stillInstalled
            .Where(o => !ReferenceEquals(o, app) && !string.IsNullOrEmpty(o.InstallLocation) &&
                        !string.Equals(o.DisplayName, app.DisplayName, StringComparison.OrdinalIgnoreCase))
            .Select(o => SpaceScanner.Normalize(o.InstallLocation!))
            .ToList();

        bool TouchesOther(string path) => others.Any(o =>
            o.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            o.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase));

        void AddDir(string path, string reason)
        {
            try
            {
                path = SpaceScanner.Normalize(Path.GetFullPath(path));
                if (!Directory.Exists(path) || IsProtected(path, out _) || TouchesOther(path) || !seen.Add(path))
                    return;
                found.Add(new Leftover(path, MeasureFolder(path), true, reason));
            }
            catch { }
        }

        void AddFile(string path, string reason)
        {
            try
            {
                if (!File.Exists(path) || !seen.Add(path)) return;
                found.Add(new Leftover(path, new FileInfo(path).Length, false, reason));
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(app.InstallLocation))
            AddDir(app.InstallLocation, "Carpeta de instalación");

        var names = NameVariants(app);
        var publishers = PublisherVariants(app.Publisher);

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localLow = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");

        var bases = new (string Path, string Reason)[]
        {
            (roaming, "Configuración (AppData\\Roaming)"),
            (local, "Datos locales (AppData\\Local)"),
            (Path.Combine(local, "Programs"), "Programa por usuario"),
            (localLow, "Datos (AppData\\LocalLow)"),
            (common, "Datos compartidos (ProgramData)")
        };

        foreach (var (basePath, reason) in bases)
        {
            foreach (var name in names)
            {
                AddDir(Path.Combine(basePath, name), reason);
                foreach (var publisher in publishers)
                    AddDir(Path.Combine(basePath, publisher, name), reason);
            }
        }

        // Accesos directos del menú Inicio y del escritorio.
        var shortcutRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        foreach (var root in shortcutRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var name in names)
            {
                AddFile(Path.Combine(root, name + ".lnk"), "Acceso directo");
                if (!root.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
                    AddDir(Path.Combine(root, name), "Carpeta del menú Inicio");
            }
        }

        found.Sort((a, b) => b.Size.CompareTo(a.Size));
        return found;
    }

    /// <summary>
    /// Los nombres con los que la aplicación pudo crear sus carpetas: el que
    /// muestra, el mismo sin arquitectura ni versión y el de su carpeta.
    /// </summary>
    public static List<string> NameVariants(InstalledApp app)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim().TrimEnd('.');
            if (s.Length >= 3 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) set.Add(s);
        }

        var name = app.DisplayName;
        Add(name);
        // «Discord 1.0.9», «7-Zip 23.01 (x64)», «Node.js (64-bit)» → «Discord», «7-Zip», «Node.js»
        var noArch = Regex.Replace(name, @"\s*[\(\[](x64|x86|64-bit|32-bit|64 bits|32 bits|amd64|arm64)[\)\]]", "",
            RegexOptions.IgnoreCase);
        Add(noArch);
        Add(Regex.Replace(noArch, @"\s+v?\d+(\.\d+)+.*$", ""));

        if (!string.IsNullOrEmpty(app.InstallLocation))
            Add(Path.GetFileName(app.InstallLocation.TrimEnd('\\')));

        return set.ToList();
    }

    /// <summary>El fabricante tal cual y sin «Inc.», «S.L.» y compañía: muchas carpetas usan esa forma corta.</summary>
    public static List<string> PublisherVariants(string? publisher)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(publisher)) return list;
        var clean = Regex.Replace(publisher, @",?\s+(Inc|LLC|Ltd|GmbH|Corp|Corporation|S\.A\.|Co)\.?$", "",
            RegexOptions.IgnoreCase).Trim();
        foreach (var p in new[] { publisher.Trim(), clean, clean.Split(' ')[0] })
            if (p.Length >= 3 && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !list.Contains(p))
                list.Add(p);
        return list;
    }

    public static long MeasureFolder(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var sizes = new FileSystemEnumerable<long>(path,
                (ref FileSystemEntry e) => e.IsDirectory ? 0 : e.Length, options);
            long total = 0;
            foreach (var s in sizes) total += s;
            return total;
        }
        catch { return 0; }
    }

    // ────────────────────────── Protección ──────────────────────────

    private static readonly Lazy<HashSet<string>> ProtectedRoots = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) set.Add(SpaceScanner.Normalize(p)); }

        foreach (Environment.SpecialFolder f in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
                     Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.Favorites, Environment.SpecialFolder.Startup
                 })
            Add(Environment.GetFolderPath(f));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.GetDirectoryName(profile));                          // C:\Users
        Add(Path.Combine(profile, "Downloads"));
        Add(Path.Combine(profile, "AppData"));
        Add(Path.Combine(profile, "AppData", "LocalLow"));
        Add(Path.Combine(profile, "OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        Add(AppContext.BaseDirectory);
        return set;
    });

    private static readonly string[] RootSystemNames =
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Recovery",
        "Boot", "EFI", "PerfLogs", "System Volume Information", "$Recycle.Bin", "$WinREAgent",
        "Documents and Settings", "Config.Msi", "OneDriveTemp", "$SysReset", "$Windows.~BT", "$Windows.~WS"
    };

    /// <summary>
    /// Rutas que la Lupa muestra pero no deja borrar: la raíz de cada unidad,
    /// Windows entero, las carpetas de sistema de primer nivel y las carpetas
    /// raíz del usuario (Documentos, Descargas…; su contenido sí se puede borrar).
    /// </summary>
    public static bool IsProtected(string path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path)) { reason = "Elemento virtual"; return true; }

        string full;
        try { full = SpaceScanner.Normalize(Path.GetFullPath(path)); }
        catch { reason = "Ruta no válida"; return true; }

        var root = Path.GetPathRoot(full);
        if (root is null || full.Length <= root.TrimEnd('\\').Length)
        {
            reason = "Raíz de la unidad";
            return true;
        }

        var windows = SpaceScanner.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Archivos de Windows";
            return true;
        }

        if (ProtectedRoots.Value.Contains(full))
        {
            reason = "Carpeta del sistema";
            return true;
        }

        if (full.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Aplicaciones de la Microsoft Store";
            return true;
        }

        // Primer nivel de la unidad: carpetas de sistema y archivos como pagefile.sys.
        var parent = Path.GetDirectoryName(full);
        if (parent is not null && SpaceScanner.Normalize(parent).Length <= root.TrimEnd('\\').Length)
        {
            var name = Path.GetFileName(full);
            if (RootSystemNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('$') ||
                File.Exists(full))
            {
                reason = "Carpeta del sistema";
                return true;
            }
        }

        return false;
    }

    // ────────────────────────── Papelera ──────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    private const uint FoDelete = 0x0003;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;

    /// <summary>
    /// Manda las rutas a la papelera de reciclaje con el diálogo de progreso de
    /// Windows, que además pide permiso de administrador si hace falta. Devuelve
    /// las rutas que ya no existen, es decir, las que se borraron de verdad.
    /// </summary>
    public static Task<List<string>> RecycleAsync(IReadOnlyList<string> paths, IntPtr owner)
    {
        var tcs = new TaskCompletionSource<List<string>>();

        // SHFileOperation necesita un hilo STA; uno propio mantiene viva la interfaz.
        var thread = new Thread(() =>
        {
            var removed = new List<string>();
            foreach (var path in paths)
            {
                if (IsProtected(path, out _)) continue;
                try
                {
                    var op = new ShFileOpStruct
                    {
                        hwnd = owner,
                        wFunc = FoDelete,
                        pFrom = path + "\0\0",
                        fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofWantNukeWarning)
                    };
                    SHFileOperation(ref op);
                }
                catch { }

                if (!File.Exists(path) && !Directory.Exists(path)) removed.Add(path);
            }
            tcs.SetResult(removed);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
