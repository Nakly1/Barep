using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Barep.Services;

namespace Barep.Controls;

/// <summary>
/// Piezas de interfaz que se construyen en código porque su número depende del
/// equipo: un módulo de RAM por ranura, una fila por disco, una por proceso.
/// Todo lo demás vive en XAML.
/// </summary>
public static class UiKit
{
    public static Brush Brush(string resourceKey) =>
        (Brush)Application.Current.FindResource(resourceKey);

    public static Style Style(string resourceKey) =>
        (Style)Application.Current.FindResource(resourceKey);

    /// <summary>Verde/ámbar/rojo del sistema, pero en la clave morada de la app.</summary>
    public static Brush LoadBrush(double percent) => percent switch
    {
        < 70 => Brush("Br.Accent"),
        < 88 => Brush("Br.Warn"),
        _ => Brush("Br.Critical")
    };

    /// <summary>Fila «etiqueta · valor» de una ficha técnica.</summary>
    public static Grid SpecRow(string label, string? value, bool mono = false, double labelWidth = 132)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock { Text = label, Style = Style("T.Label") };

        var text = string.IsNullOrWhiteSpace(value) ? HardwareText.Unknown : value;
        var valueBlock = new TextBlock
        {
            Text = text,
            Style = Style(mono ? "T.Mono" : "T.Value"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text.Length > 28 ? text : null
        };

        if (text == HardwareText.Unknown || text == HardwareText.Unavailable)
            valueBlock.Foreground = Brush("Br.TextTertiary");

        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        return row;
    }

    /// <summary>Vuelca una ficha técnica completa dentro de un panel existente.</summary>
    public static void FillSpecs(Panel target, IEnumerable<(string Label, string? Value)> rows,
        double labelWidth = 132)
    {
        target.Children.Clear();
        foreach (var (label, value) in rows)
            target.Children.Add(SpecRow(label, value, labelWidth: labelWidth));
    }

    /// <summary>Píldora de estado: el chip morado del sistema, o un color propio.</summary>
    public static Border Chip(string text, Brush? foreground = null, Brush? background = null, Brush? border = null)
    {
        var chip = new Border
        {
            Style = Style("Chip"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (background is not null) chip.Background = background;
        if (border is not null) chip.BorderBrush = border;

        var label = new TextBlock { Text = text, Style = Style("ChipText") };
        if (foreground is not null) label.Foreground = foreground;

        chip.Child = label;
        return chip;
    }

    public static Border Card(UIElement content, string styleKey = "Card", Thickness? margin = null)
    {
        return new Border
        {
            Style = Style(styleKey),
            Child = content,
            Margin = margin ?? new Thickness(0)
        };
    }

    public static TextBlock Text(string text, string styleKey, Brush? foreground = null,
        Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, Style = Style(styleKey) };
        if (foreground is not null) block.Foreground = foreground;
        if (margin is not null) block.Margin = margin.Value;
        return block;
    }

    public static TextBlock EmptyState(string message) =>
        Text(message, "T.Secondary", Brush("Br.TextTertiary"));

    /// <summary>Bytes a la unidad que toque: 832 MB, 49,3 GB, 1,2 TB.</summary>
    public static string FormatSize(long bytes) => UiText.Size(bytes);

    /// <summary>845.312 → «845K»; 1.234.567 → «1,2 M». Para recuentos de ítems.</summary>
    public static string CompactCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#} M",
        >= 10_000 => $"{count / 1000:N0}K",
        _ => $"{count:N0}"
    };

    /// <summary>
    /// «1 módulo» / «2 módulos». Escribir "módulo(s)" es más rápido de programar
    /// y peor de leer; la app habla como una persona.
    /// </summary>
    public static string Plural(int count, string singular, string plural) =>
        UiText.Plural(count, singular, plural);

    public static string FormatSpeed(double kbps) => kbps < 1000
        ? $"{kbps:0} Kb/s"
        : $"{kbps / 1000.0:0.00} Mb/s";

    public static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min";
        if (t.TotalHours >= 1) return $"{t.Hours} h {t.Minutes} min";
        return $"{t.Minutes} min {t.Seconds} s";
    }
}
