using System.IO;
using System.Windows;

namespace Barep.Controls;

/// <summary>
/// Tema oscuro (negro con morado) o claro (blanco con morado). Las vistas usan
/// StaticResource, así que para cambiar de tema se recargan los diccionarios y
/// se vuelve a crear la ventana: es instantáneo y la Lupa conserva su análisis
/// porque lo guarda en disco.
/// </summary>
public static class ThemeManager
{
    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Barep", "tema.txt");

    public static bool IsLight { get; private set; }

    /// <summary>Se llama al arrancar, antes de crear la ventana.</summary>
    public static void ApplySaved()
    {
        var light = false;
        try { light = File.Exists(SettingsFile) && File.ReadAllText(SettingsFile).Trim() == "claro"; } catch { }
        Apply(light);
    }

    public static void Apply(bool light)
    {
        IsLight = light;
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(Load(light ? "Theme/PaletteLight.xaml" : "Theme/Palette.xaml"));
        merged.Add(Load("Theme/Typography.xaml"));
        merged.Add(Load("Theme/Controls.xaml"));
    }

    /// <summary>Cambia de tema, lo recuerda y rehace la ventana en el mismo sitio.</summary>
    public static void Toggle(string? openPage = null)
    {
        Apply(!IsLight);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, IsLight ? "claro" : "oscuro");
        }
        catch { }

        var old = Application.Current.MainWindow;
        var window = new MainWindow { StartPage = openPage };
        if (old is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (old.WindowState == WindowState.Normal)
            {
                window.Left = old.Left;
                window.Top = old.Top;
                window.Width = old.Width;
                window.Height = old.Height;
            }
            else
            {
                window.Left = old.RestoreBounds.Left;
                window.Top = old.RestoreBounds.Top;
                window.WindowState = old.WindowState;
            }
        }

        MainWindow.IsSwitchingTheme = true;
        Application.Current.MainWindow = window;
        window.Show();
        old?.Close();
        MainWindow.IsSwitchingTheme = false;
    }

    private static ResourceDictionary Load(string path) =>
        new() { Source = new Uri($"pack://application:,,,/{path}", UriKind.Absolute) };
}
