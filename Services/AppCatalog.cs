using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Barep.Services;

/// <summary>De dónde viene la aplicación: un instalador clásico o la tienda de Microsoft.</summary>
public enum AppOrigin { Installer, Store }

/// <summary>Una aplicación instalada, venga del registro o de la Microsoft Store.</summary>
public sealed record AppCard(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    DateTime? InstallDate,
    long ReportedSize,
    AppOrigin Origin,
    InstalledApp? Entry,
    string? PackageFullName,
    string? PackageFamily)
{
    public bool CanUninstall => Origin == AppOrigin.Store
        ? PackageFullName is not null
        : Entry?.CanUninstall == true;

    public string OriginLabel => Origin == AppOrigin.Store ? "Microsoft Store" : "Programa instalado";
}

/// <summary>Carpetas de datos de una aplicación, ya medidas.</summary>
public sealed record AppFolder(string Path, long Size, string Reason);

/// <summary>
/// La lista de aplicaciones del equipo: los programas del registro de Windows
/// más las aplicaciones de la Microsoft Store, con sus carpetas de datos y la
/// caché que se puede borrar sin perder nada.
/// </summary>
public static class AppCatalog
{
    private const string StorePackages =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Paquetes que no son aplicaciones: bibliotecas, idiomas y piezas del sistema.</summary>
    private static readonly Regex NotAnApp = new(
        @"Framework|VCLibs|NET\.Native|UI\.Xaml|Runtime|ResourceDefault|LanguagePack|WebExperience|" +
        @"DesktopAppInstaller|Wallet|VP9|HEIF|HEVC|WebMedia|AV1|MPEG|RawImage|OneCore|Client\.CBS|" +
        @"SecHealthUI|Windows\.Photos\.Addon|StorePurchaseApp",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ────────────────────────── Inventario ──────────────────────────

    public static List<AppCard> All()
    {
        var cards = new List<AppCard>();

        foreach (var app in AppManager.GetInstalledApps())
            cards.Add(new AppCard(
                Id: app.RegistryHive + "|" + app.RegistryKeyPath,
                Name: app.DisplayName,
                Publisher: app.Publisher,
                Version: app.Version,
                InstallLocation: app.InstallLocation,
                InstallDate: app.InstallDate,
                ReportedSize: app.EstimatedSizeBytes,
                Origin: AppOrigin.Installer,
                Entry: app,
                PackageFullName: null,
                PackageFamily: null));

        cards.AddRange(StoreApps(cards));
        cards.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return cards;
    }

    private static List<AppCard> StoreApps(List<AppCard> existing)
    {
        var cards = new List<AppCard>();
        var seen = new HashSet<string>(existing.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(StorePackages);
            if (root is null) return cards;

            foreach (var packageName in root.GetSubKeyNames())
            {
                try
                {
                    // «Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe»
                    var parts = packageName.Split('_');
                    if (parts.Length < 5) continue;
                    if (NotAnApp.IsMatch(parts[0])) continue;

                    using var key = root.OpenSubKey(packageName);
                    if (key is null) continue;

                    // Sólo los paquetes con aplicaciones salen en el menú Inicio.
                    using var apps = key.OpenSubKey("Applications");
                    if (apps is null || apps.SubKeyCount == 0) continue;

                    var family = parts[0] + "_" + parts[^1];
                    var name = Clean(key.GetValue("DisplayName") as string) ?? PrettyPackage(parts[0]);
                    if (name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                        name = PrettyPackage(parts[0]);
                    if (!seen.Add(name)) continue;

                    var folder = Clean(key.GetValue("PackageRootFolder") as string);
                    var dataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family);

                    cards.Add(new AppCard(
                        Id: packageName,
                        Name: name,
                        Publisher: Clean(key.GetValue("PublisherDisplayName") as string) ?? Publisher(parts[0]),
                        Version: parts[1],
                        InstallLocation: folder is not null && Directory.Exists(folder) ? folder : null,
                        InstallDate: InstalledWhen(dataFolder),
                        ReportedSize: 0,
                        Origin: AppOrigin.Store,
                        Entry: null,
                        PackageFullName: packageName,
                        PackageFamily: family));
                }
                catch { /* un paquete roto no debe tumbar la lista */ }
            }
        }
        catch { }

        return cards;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? InstalledWhen(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetCreationTime(folder) : null; }
        catch { return null; }
    }

    private static string? Publisher(string packageName)
    {
        var dot = packageName.IndexOf('.');
        return dot > 1 ? packageName[..dot] : null;
    }

    /// <summary>«Microsoft.WindowsCalculator» → «Windows Calculator».</summary>
    private static string PrettyPackage(string packageName)
    {
        var name = packageName;
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) name = name[(dot + 1)..];

        var builder = new StringBuilder(name.Length + 6);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) builder.Append(' ');
            builder.Append(name[i]);
        }
        return builder.ToString();
    }

    // ────────────────────────── Carpetas de datos ──────────────────────────

    /// <summary>
    /// Dónde guarda la aplicación su configuración y sus datos. Es lo que queda
    /// en el equipo cuando el desinstalador termina, y lo que hay que medir para
    /// saber lo que ocupa de verdad.
    /// </summary>
    public static List<AppFolder> DataFolders(AppCard app)
    {
        var folders = new List<AppFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string reason)
        {
            try
            {
                if (!Directory.Exists(path) || !seen.Add(SpaceScanner.Normalize(path))) return;
                folders.Add(new AppFolder(SpaceScanner.Normalize(path), AppManager.MeasureFolder(path), reason));
            }
            catch { }
        }

        if (app.Origin == AppOrigin.Store)
        {
            if (app.PackageFamily is { } family)
                Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", family), "Datos de la aplicación");
            return folders;
        }

        if (app.Entry is not { } entry) return folders;

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localLow = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");

        foreach (var name in AppManager.NameVariants(entry))
        {
            Add(Path.Combine(roaming, name), "Configuración (AppData\\Roaming)");
            Add(Path.Combine(local, name), "Datos locales (AppData\\Local)");
            Add(Path.Combine(localLow, name), "Datos (AppData\\LocalLow)");
            Add(Path.Combine(common, name), "Datos compartidos (ProgramData)");

            foreach (var publisher in AppManager.PublisherVariants(entry.Publisher))
            {
                Add(Path.Combine(roaming, publisher, name), "Configuración (AppData\\Roaming)");
                Add(Path.Combine(local, publisher, name), "Datos locales (AppData\\Local)");
                Add(Path.Combine(common, publisher, name), "Datos compartidos (ProgramData)");
            }
        }

        folders.Sort((a, b) => b.Size.CompareTo(a.Size));
        return folders;
    }

    /// <summary>Lo que ocupa la carpeta del programa, medido de verdad.</summary>
    public static long InstallSize(AppCard app) =>
        app.InstallLocation is { Length: > 0 } path && Directory.Exists(path)
            ? AppManager.MeasureFolder(path)
            : 0;

    // ────────────────────────── Desinstalar de la Store ──────────────────────────

    /// <summary>
    /// Las aplicaciones de la Store se quitan con Remove-AppxPackage, que es lo
    /// mismo que hace Windows desde Configuración: no deja restos en el registro.
    /// </summary>
    public static (Process? Process, string? Error) RemoveStoreApp(AppCard app)
    {
        if (app.PackageFullName is not { Length: > 0 } package)
            return (null, "No se pudo identificar el paquete de la aplicación.");

        try
        {
            var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Remove-AppxPackage -Package '{package}'\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return (process, null);
        }
        catch (Exception ex)
        {
            return (null, $"No se pudo quitar la aplicación: {ex.Message}");
        }
    }

    /// <summary>¿Sigue instalada? Para saber si el desinstalador terminó bien.</summary>
    public static bool StillInstalled(AppCard app)
    {
        if (app.Origin == AppOrigin.Installer)
            return app.Entry is not null && AppManager.IsStillInstalled(app.Entry);

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(StorePackages);
            using var key = root?.OpenSubKey(app.PackageFullName ?? "");
            return key is not null;
        }
        catch { return false; }
    }
}
