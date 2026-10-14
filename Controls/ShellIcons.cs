using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Barep.Services;

namespace Barep.Controls;

/// <summary>
/// Iconos reales de Windows para aplicaciones y archivos, a 48 px (la lista
/// «extra grande» del shell) para que se vean nítidos dentro de las burbujas.
/// Se cachean: por ruta los ejecutables, por extensión todo lo demás.
/// Debe llamarse desde el hilo de la interfaz (STA).
/// </summary>
public static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] OwnIconExtensions = { ".exe", ".ico", ".lnk", ".msc", ".cpl", ".scr", ".url" };

    /// <summary>Palabras que delatan un ejecutable auxiliar, no el programa principal.</summary>
    private static readonly Regex HelperExe = new(
        @"unins|uninstall|update|crash|helper|setup|install|elevat|notif|report|service|launcher_?stub|squirrel|repair|diag",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ImageSource? ForNode(SpaceNode node)
    {
        switch (node.Kind)
        {
            case SpaceNodeKind.File:
                return ForFile(node.FullPath);

            case SpaceNodeKind.AppEntry:
                return node.App is null ? null : ForApp(node.App, null);

            case SpaceNodeKind.Folder:
                if (node.App is not null) return ForApp(node.App, node);
                return LooksLikeAppFolder(node) ? FromExecutable(MainExecutable(node)) : null;

            default:
                return null;
        }
    }

    public static ImageSource? ForApp(InstalledApp app, SpaceNode? folder)
    {
        var key = "app:" + app.DisplayName;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;

        if (!string.IsNullOrWhiteSpace(app.IconPath))
        {
            var (file, _) = AppManager.SplitCommand(app.IconPath);
            if (file is not null)
            {
                var index = 0;
                var comma = file.LastIndexOf(',');
                if (comma > 2 && int.TryParse(file[(comma + 1)..], out var parsed))
                {
                    index = parsed;
                    file = file[..comma];
                }
                file = file.Trim('"');
                if (File.Exists(file))
                    image = index == 0 ? Extract(file, false) : ExtractIndexed(file, index);
            }
        }

        if (image is null && folder is not null)
            image = FromExecutable(MainExecutable(folder));

        if (image is null && !string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
        {
            try
            {
                var exe = Directory.EnumerateFiles(app.InstallLocation, "*.exe")
                    .Where(f => !HelperExe.IsMatch(Path.GetFileName(f)))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();
                image = FromExecutable(exe);
            }
            catch { }
        }

        Cache[key] = image;
        return image;
    }

    public static ImageSource? ForFile(string path)
    {
        var ext = Path.GetExtension(path);
        var perFile = OwnIconExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        var key = perFile ? path : "ext:" + (ext.Length == 0 ? "(none)" : ext);

        if (Cache.TryGetValue(key, out var cached)) return cached;
        var image = perFile ? Extract(path, false) : Extract(ext.Length == 0 ? "file" : "file" + ext, true);
        Cache[key] = image;
        return image;
    }

    /// <summary>
    /// Carpetas que cuelgan directamente de Program Files o de
    /// AppData\Local\Programs: suelen ser programas aunque no estén registrados.
    /// </summary>
    public static bool LooksLikeAppFolder(SpaceNode node)
    {
        if (node.Kind != SpaceNodeKind.Folder || node.Parent is null) return false;
        var parent = node.Parent.FullPath.TrimEnd('\\');
        return parent.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
               || parent.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase)
               || parent.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>El .exe que mejor representa a la carpeta: el que se llama como ella o el más grande.</summary>
    public static string? MainExecutable(SpaceNode folder)
    {
        var exes = folder.Children
            .Where(c => c.Kind == SpaceNodeKind.File &&
                        c.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !HelperExe.IsMatch(c.Name))
            .ToList();
        if (exes.Count == 0) return null;

        var folderName = Regex.Replace(folder.Name, @"[^\p{L}\p{N}]", "");
        var named = exes.FirstOrDefault(e =>
        {
            var stem = Regex.Replace(Path.GetFileNameWithoutExtension(e.Name), @"[^\p{L}\p{N}]", "");
            return stem.Length >= 3 && (folderName.Contains(stem, StringComparison.OrdinalIgnoreCase) ||
                                        stem.Contains(folderName, StringComparison.OrdinalIgnoreCase));
        });
        return (named ?? exes.OrderByDescending(e => e.Size).First()).FullPath;
    }

    /// <summary>Datos de versión del ejecutable principal (producto, fabricante, versión).</summary>
    public static FileVersionInfo? VersionOf(string? exe)
    {
        if (exe is null) return null;
        try { return FileVersionInfo.GetVersionInfo(exe); } catch { return null; }
    }

    private static ImageSource? FromExecutable(string? exe)
    {
        if (exe is null) return null;
        if (Cache.TryGetValue(exe, out var cached)) return cached;
        var image = Extract(exe, false);
        Cache[exe] = image;
        return image;
    }

    // ────────────────────────── Interop del shell ──────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi,
        uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList? ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiSysIconIndex = 0x4000;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const int ShilExtraLarge = 0x2;
    private const int IldTransparent = 0x1;

    private static Guid _iidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private static IImageList? _extraLarge;
    private static bool _extraLargeTried;

    private static ImageSource? Extract(string path, bool byExtension)
    {
        try
        {
            var info = new ShFileInfo();
            var flags = ShgfiSysIconIndex | (byExtension ? ShgfiUseFileAttributes : 0);
            var ok = SHGetFileInfo(path, byExtension ? FileAttributeNormal : 0, ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (ok == IntPtr.Zero) return null;

            if (!_extraLargeTried)
            {
                _extraLargeTried = true;
                try { SHGetImageList(ShilExtraLarge, ref _iidImageList, out _extraLarge); }
                catch { _extraLarge = null; }
            }

            var hIcon = IntPtr.Zero;
            if (_extraLarge is not null && _extraLarge.GetIcon(info.iIcon, IldTransparent, ref hIcon) == 0 &&
                hIcon != IntPtr.Zero)
                return ToImage(hIcon);

            // Respaldo: el icono de 32 px de toda la vida.
            info = new ShFileInfo();
            SHGetFileInfo(path, byExtension ? FileAttributeNormal : 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon | (byExtension ? ShgfiUseFileAttributes : 0));
            return info.hIcon != IntPtr.Zero ? ToImage(info.hIcon) : null;
        }
        catch { return null; }
    }

    private static ImageSource? ExtractIndexed(string file, int index)
    {
        try
        {
            var large = new IntPtr[1];
            if (ExtractIconEx(file, index, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                return ToImage(large[0]);
        }
        catch { }
        return Extract(file, false);
    }

    private static ImageSource? ToImage(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally { DestroyIcon(hIcon); }
    }
}
