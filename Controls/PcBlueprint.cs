using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Barep.Services;

namespace Barep.Controls;

/// <summary>
/// Plano 2D visto desde arriba (con la tapa quitada) del equipo: un portátil o
/// una placa de sobremesa, con cada pieza real en su sitio típico. Se dibuja en
/// un lienzo fijo de 1000 × 640 y se escala con un Viewbox.
/// </summary>
public sealed class PcBlueprint : Canvas
{
    public const double W = 1000, H = 640;

    public event Action<PcPart?>? PartHovered;
    public event Action<PcPart>? PartClicked;

    private readonly List<(Border Element, PcPart Part)> _parts = new();
    private PcPart? _selected;

    public PcBlueprint()
    {
        Width = W;
        Height = H;
        ClipToBounds = false;
    }

    public void Show(PcLayout layout)
    {
        Children.Clear();
        _parts.Clear();
        if (layout.IsLaptop) DrawLaptop(layout);
        else DrawDesktop(layout);
    }

    public void Select(PcPart? part)
    {
        _selected = part;
        foreach (var (el, p) in _parts) SetHighlight(el, p, ReferenceEquals(p, part));
    }

    // ────────────────────────── Portátil ──────────────────────────

    private void DrawLaptop(PcLayout l)
    {
        // Chasis con la tapa inferior quitada
        Add(new Border
        {
            Width = 960, Height = 600, CornerRadius = new CornerRadius(34),
            Background = GridPattern(), BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(2)
        }, 20, 20);

        // Puertos en los laterales
        foreach (var y in new[] { 110, 160, 210 }) Add(Port(true), 14, y);
        foreach (var y in new[] { 120, 170 }) Add(Port(true), 976, y);

        // Placa base (mitad superior)
        Add(Board(880, 290), 60, 48);

        var dedicated = l.Gpus.Where(g => g.Subtitle == "Gráfica dedicada").ToList();
        var integrated = l.Gpus.Where(g => g.Subtitle != "Gráfica dedicada").ToList();

        // Ventiladores: dos si hay gráfica dedicada, uno si no.
        Add(Fan(150), 72, 118);
        if (dedicated.Count > 0) Add(Fan(150), 778, 118);

        // Tubos de calor del procesador a los ventiladores
        Add(HeatPipe(new Point(200, 195), new Point(350, 195)), 0, 0);
        if (dedicated.Count > 0) Add(HeatPipe(new Point(470, 195), new Point(800, 195)), 0, 0);

        // Procesador (con la gráfica integrada dentro)
        AddPart(l.Cpu, Chip(116, 116, "CPU", l.Cpu.Title, integrated.Count > 0 ? "+ gráfica integrada" : null), 262, 130);
        foreach (var g in integrated) AddPart(g, TagLabel("GPU integrada"), 272, 256);

        // Gráfica dedicada al lado
        var gx = 398;
        foreach (var g in dedicated.Take(1))
        {
            AddPart(g, Chip(104, 104, "GPU", g.Title, null), gx, 136);
            gx += 116;
        }

        // RAM y almacenamiento en la zona derecha de la placa
        var right = dedicated.Count > 0 ? 520 : 470;
        var width = dedicated.Count > 0 ? 250 : 300;
        var y0 = 70.0;
        foreach (var ram in l.Ram.Take(4))
        {
            AddPart(ram, ram.State == SlotState.Soldered ? SolderedRam(width, 38, ram) : Slot(width, 38, ram, "RAM"), right, y0);
            y0 += 48;
        }
        y0 += 8;
        foreach (var disk in l.Storage.Take(Math.Max(1, (int)((330 - y0) / 44))))
        {
            AddPart(disk, Slot(width, 34, disk, disk.Kind == PartKind.SataBay ? "SATA" : "M.2"), right, y0);
            y0 += 44;
        }

        // Batería ocupando la mitad inferior
        if (l.Battery is { } bat) AddPart(bat, Battery(540, 190, bat), 230, 380);
        else Add(Caption("Sin batería detectada", 13, "Br.TextTertiary"), 420, 460);

        // Altavoces
        Add(Speaker(), 80, 410);
        Add(Speaker(), 820, 410);
    }

    // ────────────────────────── Sobremesa ──────────────────────────

    private void DrawDesktop(PcLayout l)
    {
        // Placa base ATX
        Add(Board(560, 610), 190, 15);
        Add(new Border { Width = 540, Height = 590, Background = GridPattern(), CornerRadius = new CornerRadius(10), IsHitTestVisible = false }, 200, 25);

        // Panel trasero de conectores
        Add(new Border
        {
            Width = 90, Height = 180, CornerRadius = new CornerRadius(6),
            Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.Stroke"), BorderThickness = new Thickness(1),
            Child = Caption("Conectores\ntraseros", 11, "Br.TextTertiary", center: true)
        }, 210, 40);

        // Zócalo del procesador
        AddPart(l.Cpu, Chip(150, 150, "CPU", l.Cpu.Title, l.Gpus.Any(g => g.Subtitle != "Gráfica dedicada") ? "+ gráfica integrada" : null), 350, 80);

        // Ranuras de RAM (verticales, a la derecha del procesador)
        var x = 560.0;
        foreach (var ram in l.Ram.Take(4))
        {
            AddPart(ram, VerticalSlot(26, 250, ram), x, 50);
            x += 36;
        }

        // M.2 y PCIe alternados en la mitad inferior
        var m2 = l.Storage.Where(s => s.Kind == PartKind.M2Slot).Take(3).ToList();
        var sata = l.Storage.Where(s => s.Kind == PartKind.SataBay).ToList();
        var dedicated = l.Gpus.Where(g => g.Subtitle == "Gráfica dedicada").ToList();

        var pcie = l.Pcie.Take(3).ToList();
        if (pcie.Count == 0)
            pcie.Add(new PcPart(PartKind.PcieSlot, dedicated.Count > 0 ? SlotState.Occupied : SlotState.Unknown,
                "Ranura PCIe x16", dedicated.Count > 0 ? "Con la tarjeta gráfica" : "Estado desconocido",
                new (string, string?)[] { ("Ranura", "PCIe x16") }));

        var y = 300.0;
        for (int i = 0; i < Math.Max(m2.Count, pcie.Count); i++)
        {
            if (i < m2.Count) { AddPart(m2[i], Slot(230, 30, m2[i], "M.2"), 330, y); y += 50; }
            if (i < pcie.Count)
            {
                if (i == 0 && dedicated.Count > 0)
                    AddPart(dedicated[0], GpuCard(500, 70, dedicated[0]), 215, y);
                else
                    AddPart(pcie[i], Slot(500, 26, pcie[i], "PCIe"), 215, y);
                y += i == 0 && dedicated.Count > 0 ? 90 : 50;
            }
        }

        // Puertos SATA y sus unidades, fuera de la placa
        foreach (var py in new[] { 470, 500, 530, 560 }) Add(Port(false), 720, py);
        var sy = 380.0;
        foreach (var d in sata.Take(4))
        {
            AddPart(d, Slot(220, 44, d, "SATA"), 770, sy);
            sy += 56;
        }
        foreach (var e in l.External.Take(2))
        {
            AddPart(e, Slot(220, 44, e, "USB"), 770, sy);
            sy += 56;
        }
    }

    // ────────────────────────── Piezas ──────────────────────────

    private void Add(UIElement el, double x, double y)
    {
        SetLeft(el, x);
        SetTop(el, y);
        Children.Add(el);
    }

    private void AddPart(PcPart part, Border el, double x, double y)
    {
        el.Cursor = Cursors.Hand;
        el.Tag = part;
        el.ToolTip = part.State switch
        {
            SlotState.Free => "Libre: aquí puedes añadir una pieza",
            SlotState.Unknown => "Windows no informa de esta ranura",
            _ => part.Title
        };
        el.MouseEnter += (_, _) => { SetHighlight(el, part, true); PartHovered?.Invoke(part); };
        el.MouseLeave += (_, _) =>
        {
            if (!ReferenceEquals(part, _selected)) SetHighlight(el, part, false);
            PartHovered?.Invoke(null);
        };
        el.MouseLeftButtonUp += (_, _) => PartClicked?.Invoke(part);
        _parts.Add((el, part));
        Add(el, x, y);

        if (part.State == SlotState.Free) Pulse(el);
    }

    private static void SetHighlight(Border el, PcPart part, bool on)
    {
        el.Effect = on
            ? new DropShadowEffect
            {
                Color = part.State == SlotState.Free ? Color.FromRgb(0x5F, 0xD6, 0xA4) : Color.FromRgb(0x8B, 0x5C, 0xF6),
                BlurRadius = 28, ShadowDepth = 0, Opacity = 0.9
            }
            : null;
    }

    /// <summary>Las ranuras libres «respiran» para que se encuentren de un vistazo.</summary>
    private static void Pulse(UIElement el) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, new Duration(TimeSpan.FromSeconds(1.1)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

    private static Brush GridPattern()
    {
        var line = UiKit.Brush("Br.StrokeSoft");
        var drawing = new GeometryDrawing(null, new Pen(line, 1),
            new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(24, 0)), new LineGeometry(new Point(0, 0), new Point(0, 24)) } });
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Opacity = 0.9
        };
    }

    private static Border Board(double w, double h) => new()
    {
        Width = w, Height = h, CornerRadius = new CornerRadius(14),
        Background = UiKit.Brush("Br.AccentTint"),
        BorderBrush = UiKit.Brush("Br.AccentDeep"), BorderThickness = new Thickness(1.2),
        IsHitTestVisible = false
    };

    private static TextBlock Caption(string text, double size, string brush, bool center = false, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = UiKit.Brush(brush),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = center ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    /// <summary>Un chip cuadrado (CPU/GPU) con su nombre.</summary>
    private static Border Chip(double w, double h, string badge, string title, string? extra)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        stack.Children.Add(new TextBlock { Text = badge, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxHeight = 42,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0)
        });
        if (extra is not null)
            stack.Children.Add(new TextBlock { Text = extra, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });

        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12),
            Background = UiKit.Brush("Br.AccentGradient"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1.5),
            Child = stack
        };
    }

    private static Border TagLabel(string text) => new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
        Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.AccentDeep"), BorderThickness = new Thickness(1),
        Child = Caption(text, 11, "Br.AccentBright")
    };

    /// <summary>Una ranura horizontal (RAM, M.2, PCIe, SATA) según su estado.</summary>
    private static Border Slot(double w, double h, PcPart part, string kind)
    {
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(10, 0, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new TextBlock { Text = kind, FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var text = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        grid.Children.Add(badge);
        System.Windows.Controls.Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var border = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(7), Child = grid };

        switch (part.State)
        {
            case SlotState.Occupied:
                border.Background = UiKit.Brush("Br.AccentGradient");
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
                border.BorderThickness = new Thickness(1);
                badge.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
                text.Foreground = Brushes.White;
                text.FontWeight = FontWeights.SemiBold;
                text.Text = $"{part.Title}  ·  {part.Subtitle}";
                break;
            case SlotState.Free:
                border.Background = UiKit.Brush("Br.PositiveTint");
                border.BorderBrush = UiKit.Brush("Br.Positive");
                border.BorderThickness = new Thickness(1.6);
                badge.Foreground = UiKit.Brush("Br.Positive");
                text.Foreground = UiKit.Brush("Br.Positive");
                text.FontWeight = FontWeights.SemiBold;
                text.Text = "+  Libre · " + part.Subtitle;
                Dashed(border);
                break;
            default:
                border.Background = Brushes.Transparent;
                border.BorderBrush = UiKit.Brush("Br.TextTertiary");
                border.BorderThickness = new Thickness(1.2);
                badge.Foreground = UiKit.Brush("Br.TextTertiary");
                text.Foreground = UiKit.Brush("Br.TextTertiary");
                text.Text = "?  " + part.Title;
                Dashed(border);
                break;
        }
        return border;
    }

    private static Border VerticalSlot(double w, double h, PcPart part)
    {
        var text = new TextBlock
        {
            Text = part.State switch { SlotState.Free => "LIBRE", SlotState.Occupied => part.Title, _ => "?" },
            FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            LayoutTransform = new RotateTransform(-90), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = h - 16
        };
        var border = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(5), Child = text };
        if (part.State == SlotState.Occupied)
        {
            border.Background = UiKit.Brush("Br.AccentGradient");
            text.Foreground = Brushes.White;
        }
        else if (part.State == SlotState.Free)
        {
            border.Background = UiKit.Brush("Br.PositiveTint");
            border.BorderBrush = UiKit.Brush("Br.Positive");
            border.BorderThickness = new Thickness(1.6);
            text.Foreground = UiKit.Brush("Br.Positive");
        }
        else
        {
            border.BorderBrush = UiKit.Brush("Br.TextTertiary");
            border.BorderThickness = new Thickness(1.2);
            text.Foreground = UiKit.Brush("Br.TextTertiary");
        }
        return border;
    }

    private static Border SolderedRam(double w, double h, PcPart part)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        for (int i = 0; i < 4; i++)
            panel.Children.Add(new Border { Width = 18, Height = h - 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), Background = UiKit.Brush("Br.AccentDeep") });
        panel.Children.Add(Caption($"{part.Title} soldada", 11.5, "Br.TextPrimary", bold: true));
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(7), Child = panel,
            Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.AccentDeep"), BorderThickness = new Thickness(1)
        };
    }

    private static Border GpuCard(double w, double h, PcPart gpu)
    {
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(new Ellipse { Width = 52, Height = 52, Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 0) });
        grid.Children.Add(new Ellipse { Width = 52, Height = 52, Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 0) });
        grid.Children.Add(new TextBlock { Text = gpu.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w - 170 });
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10), Child = grid,
            Background = UiKit.Brush("Br.AccentGradient"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1.2)
        };
    }

    private static Border Battery(double w, double h, PcPart bat)
    {
        var cells = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(14) };
        for (int i = 0; i < 4; i++)
            cells.Children.Add(new Border { Margin = new Thickness(5), CornerRadius = new CornerRadius(8), Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.Stroke"), BorderThickness = new Thickness(1) });
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(cells);
        var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(Caption("Batería", 16, "Br.TextPrimary", center: true, bold: true));
        label.Children.Add(Caption(bat.Subtitle, 12, "Br.TextSecondary", center: true));
        grid.Children.Add(label);
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(14), Child = grid,
            Background = UiKit.Brush("Br.Surface"), BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(1.5)
        };
    }

    private static UIElement Fan(double size)
    {
        var grid = new System.Windows.Controls.Grid { Width = size, Height = size, IsHitTestVisible = false };
        grid.Children.Add(new Ellipse { Stroke = UiKit.Brush("Br.StrokeStrong"), StrokeThickness = 2, Fill = UiKit.Brush("Br.Surface") });
        for (int i = 0; i < 7; i++)
        {
            var blade = new Path
            {
                Data = Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M {size / 2},{size / 2} Q {size * 0.62},{size * 0.18} {size / 2},{size * 0.06}")),
                Stroke = UiKit.Brush("Br.TextTertiary"), StrokeThickness = 2,
                RenderTransform = new RotateTransform(i * 360.0 / 7, size / 2, size / 2)
            };
            grid.Children.Add(blade);
        }
        grid.Children.Add(new Ellipse { Width = size * 0.26, Height = size * 0.26, Fill = UiKit.Brush("Br.SurfaceRaised"), Stroke = UiKit.Brush("Br.StrokeStrong"), StrokeThickness = 1.5 });
        return grid;
    }

    private static UIElement HeatPipe(Point a, Point b) => new Line
    {
        X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, Stroke = UiKit.Brush("Br.Track"), IsHitTestVisible = false
    };

    private static UIElement Speaker() => new Border
    {
        Width = 100, Height = 150, CornerRadius = new CornerRadius(14), IsHitTestVisible = false,
        Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.Stroke"), BorderThickness = new Thickness(1),
        Child = Caption("Altavoz", 11, "Br.TextTertiary", center: true)
    };

    private static UIElement Port(bool vertical) => new Border
    {
        Width = vertical ? 10 : 26, Height = vertical ? 30 : 14, CornerRadius = new CornerRadius(3), IsHitTestVisible = false,
        Background = UiKit.Brush("Br.SurfaceRaised"), BorderBrush = UiKit.Brush("Br.StrokeStrong"), BorderThickness = new Thickness(1)
    };

    /// <summary>Borde discontinuo: un Border no lo admite, así que se superpone un rectángulo.</summary>
    private static void Dashed(Border border)
    {
        var brush = border.BorderBrush;
        var thickness = border.BorderThickness.Left;
        border.BorderThickness = new Thickness(0);
        var content = border.Child;
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(new Rectangle
        {
            Stroke = brush, StrokeThickness = thickness, StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = border.CornerRadius.TopLeft, RadiusY = border.CornerRadius.TopLeft
        });
        border.Child = null;
        grid.Children.Add(content);
        border.Child = grid;
    }
}
