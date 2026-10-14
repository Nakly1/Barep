using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

/// <summary>
/// Pantalla: frecuencia (Hz), resolución, tamaño, HDR y fabricante del panel de
/// cada pantalla, y cambio de Hz con vuelta atrás automática si algo va mal.
/// </summary>
public partial class DisplayView : UserControl
{
    private static readonly CultureInfo Spanish = new("es-ES");
    private bool _loaded;

    public DisplayView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await ReloadAsync();
        };
    }

    private async Task ReloadAsync()
    {
        var displays = await Task.Run(() => DisplayService.GetDisplays());
        DisplaysPanel.Children.Clear();
        SubtitleText.Text = displays.Count switch
        {
            0 => "Windows no ha devuelto información de ninguna pantalla.",
            1 => "1 pantalla conectada.",
            _ => $"{displays.Count} pantallas conectadas."
        };
        foreach (var d in displays) DisplaysPanel.Children.Add(BuildCard(d));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => TweakService.OpenSettings("ms-settings:display");

    // ────────────────────────── Tarjeta ──────────────────────────

    private Border BuildCard(DisplayInfo d)
    {
        var root = new StackPanel();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(Illustration(d));

        var right = new StackPanel();
        Grid.SetColumn(right, 2);

        var title = UiKit.Text(d.Name, "T.Headline");
        title.FontSize = 18;
        right.Children.Add(title);
        var sub = new List<string>();
        if (!string.IsNullOrEmpty(d.Manufacturer)) sub.Add(d.Manufacturer);
        sub.Add(d.Connection);
        if (d.IsPrimary) sub.Add("Principal");
        right.Children.Add(UiKit.Text(string.Join("  ·  ", sub), "T.Secondary", margin: new Thickness(0, 3, 0, 16)));

        // Cifras grandes
        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 16) };
        tiles.Children.Add(Metric("Frecuencia", $"{d.RefreshHz.ToString("0.##", Spanish)} Hz",
            d.MaxHz > Math.Round(d.RefreshHz) ? $"admite hasta {d.MaxHz} Hz" : "la máxima que admite"));
        tiles.Children.Add(Metric("Resolución", $"{d.Width} × {d.Height}",
            d.Width == d.NativeWidth && d.Height == d.NativeHeight ? "nativa" : $"nativa: {d.NativeWidth} × {d.NativeHeight}"));
        tiles.Children.Add(Metric("Tamaño", d.DiagonalInches > 0 ? $"{d.DiagonalInches.ToString("0.#", Spanish)}\"" : "—",
            d.DiagonalInches > 0 ? "pulgadas (diagonal)" : "no informado"));
        tiles.Children.Add(Metric("Escala", $"{d.ScalePercent} %", "tamaño de texto e iconos"));
        right.Children.Add(tiles);

        var specs = new StackPanel();
        UiKit.FillSpecs(specs, new (string, string?)[]
        {
            ("Tipo", d.IsInternal ? "Pantalla integrada del portátil" : $"Monitor externo ({d.Connection})"),
            ("Relación de aspecto", d.AspectRatio),
            ("Color", $"{d.BitsPerColor} bits por color" + (d.BitsPerColor >= 10 ? " (degradados más suaves)" : "")),
            ("HDR", d.HdrSupported ? (d.HdrEnabled ? "Compatible · activado" : "Compatible · desactivado") : "No compatible"),
            ("Panel", string.IsNullOrEmpty(d.ManufacturerCode) ? null : $"{d.Manufacturer} · modelo {d.ManufacturerCode}{d.ProductCode}"),
            ("Fabricado", d.YearOfManufacture?.ToString()),
            ("Tecnología del panel", "Windows no la informa (IPS, VA, OLED…)")
        }.Where(r => r.Item2 is not null), labelWidth: 150);
        right.Children.Add(specs);

        var links = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (!string.IsNullOrEmpty(d.ManufacturerCode))
        {
            var search = new Button { Style = UiKit.Style("BtnLink"), Content = "Buscar el tipo de panel ›" };
            search.Click += (_, _) => Search($"{d.ManufacturerCode}{d.ProductCode} {d.Manufacturer} panel specs IPS OLED");
            links.Children.Add(search);
        }
        if (d.HdrSupported)
        {
            var hdr = new Button { Style = UiKit.Style("BtnLink"), Content = "Ajustes de HDR ›" };
            hdr.Click += (_, _) => TweakService.OpenSettings("ms-settings:display-hdr");
            links.Children.Add(hdr);
        }
        right.Children.Add(links);

        grid.Children.Add(right);
        root.Children.Add(grid);

        // ── Frecuencia: aviso y selector ──
        if (d.SupportedHz.Count > 1)
        {
            root.Children.Add(new Border { Style = UiKit.Style("Divider"), Margin = new Thickness(0, 20, 0, 16) });
            root.Children.Add(RefreshSelector(d));
        }

        return new Border { Style = UiKit.Style("Card"), Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(24), Child = root };
    }

    private static StackPanel Metric(string label, string value, string note)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        s.Children.Add(UiKit.Text(label, "T.Caption"));
        var v = UiKit.Text(value, "T.Metric", margin: new Thickness(0, 4, 0, 2));
        v.FontSize = 22;
        s.Children.Add(v);
        s.Children.Add(UiKit.Text(note, "T.Caption"));
        return s;
    }

    /// <summary>Dibujo del monitor (o de la tapa del portátil) con su resolución y Hz en pantalla.</summary>
    private static UIElement Illustration(DisplayInfo d)
    {
        var aspect = d.NativeHeight > 0 ? d.NativeWidth / (double)d.NativeHeight : 16 / 9.0;
        var w = 230.0;
        var h = Math.Min(160, w / aspect);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var screenText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        screenText.Children.Add(new TextBlock { Text = $"{Math.Round(d.RefreshHz)} Hz", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        screenText.Children.Add(new TextBlock { Text = $"{d.Width} × {d.Height}", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        if (d.HdrSupported)
            screenText.Children.Add(new TextBlock { Text = d.HdrEnabled ? "HDR activado" : "Admite HDR", FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });

        var screen = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10),
            BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(6),
            Background = UiKit.Brush("Br.LensStage"),
            Child = screenText
        };
        stack.Children.Add(screen);

        if (d.IsInternal)
        {
            // Base del portátil
            stack.Children.Add(new Border
            {
                Width = w + 40, Height = 12, CornerRadius = new CornerRadius(0, 0, 8, 8), Margin = new Thickness(0, 1, 0, 0),
                Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(1)
            });
        }
        else
        {
            stack.Children.Add(new Border { Width = 26, Height = 30, Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.Stroke"), BorderThickness = new Thickness(1, 0, 1, 0) });
            stack.Children.Add(new Border { Width = 110, Height = 8, CornerRadius = new CornerRadius(4), Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(1) });
        }
        return stack;
    }

    // ────────────────────────── Cambio de Hz ──────────────────────────

    private StackPanel RefreshSelector(DisplayInfo d)
    {
        var panel = new StackPanel();
        var current = (int)Math.Round(d.RefreshHz);

        panel.Children.Add(UiKit.Text("Frecuencia de actualización", "T.Headline"));
        panel.Children.Add(UiKit.Text("Elige cuántos Hz usar. Si la imagen falla, vuelve sola a la anterior en 15 segundos.",
            "T.Secondary", margin: new Thickness(0, 3, 0, 12)));

        if (d.MaxHz > current)
        {
            var tip = new Border
            {
                Background = UiKit.Brush("Br.AccentTint"), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12)
            };
            var tipGrid = new Grid();
            tipGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tipGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tipText = UiKit.Text($"Tu pantalla admite hasta {d.MaxHz} Hz pero está a {current} Hz. Subirla hace que todo se mueva más fluido" +
                                     (d.IsInternal ? " (gasta un poco más de batería)." : "."), "T.Body");
            tipText.VerticalAlignment = VerticalAlignment.Center;
            tipGrid.Children.Add(tipText);
            var use = new Button { Style = UiKit.Style("BtnPrimary"), Content = $"Usar {d.MaxHz} Hz", Margin = new Thickness(14, 0, 0, 0) };
            use.Click += (_, _) => ApplyHz(d, d.MaxHz, current, panel);
            Grid.SetColumn(use, 1);
            tipGrid.Children.Add(use);
            tip.Child = tipGrid;
            panel.Children.Add(tip);
        }

        var segmented = new Border { Style = UiKit.Style("Segmented") };
        var options = new StackPanel { Orientation = Orientation.Horizontal };
        var group = $"hz-{d.GdiName}";
        foreach (var hz in d.SupportedHz)
        {
            var option = new RadioButton
            {
                Style = UiKit.Style("FilterChip"), GroupName = group, Content = $"{hz} Hz",
                IsChecked = hz == current, MinWidth = 64
            };
            var target = hz;
            option.Checked += (_, _) =>
            {
                if (target != current) ApplyHz(d, target, current, panel);
            };
            options.Children.Add(option);
        }
        segmented.Child = options;
        panel.Children.Add(segmented);
        return panel;
    }

    /// <summary>
    /// Aplica la frecuencia y pide confirmación; si no se confirma en 15 s,
    /// vuelve a la anterior (por si la pantalla se queda en negro).
    /// </summary>
    private void ApplyHz(DisplayInfo d, int hz, int previous, StackPanel panel)
    {
        if (!DisplayService.SetRefreshRate(d.GdiName, hz))
        {
            MessageBox.Show(Window.GetWindow(this)!, $"El controlador de la pantalla no aceptó {hz} Hz.", "Pantalla",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _ = ReloadAsync();
            return;
        }

        var seconds = 15;
        var banner = new Border
        {
            Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 12, 0, 0)
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = UiKit.Text("", "T.Body");
        text.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var keep = new Button { Style = UiKit.Style("BtnPrimary"), Content = "Mantener", Margin = new Thickness(12, 0, 8, 0) };
        var revert = new Button { Style = UiKit.Style("BtnGhost"), Content = "Volver" };
        buttons.Children.Add(keep);
        buttons.Children.Add(revert);
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        banner.Child = g;
        panel.Children.Add(banner);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Update() => text.Text = $"¿Se ve bien a {hz} Hz? Si no respondes, vuelve a {previous} Hz en {seconds} s.";
        async void Finish(bool keepIt)
        {
            timer.Stop();
            if (!keepIt) DisplayService.SetRefreshRate(d.GdiName, previous);
            await ReloadAsync();
        }
        timer.Tick += (_, _) =>
        {
            seconds--;
            if (seconds <= 0) Finish(false);
            else Update();
        };
        keep.Click += (_, _) => Finish(true);
        revert.Click += (_, _) => Finish(false);
        Update();
        timer.Start();
    }

    private static void Search(string query)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={Uri.EscapeDataString(query)}") { UseShellExecute = true });
        }
        catch { }
    }
}
