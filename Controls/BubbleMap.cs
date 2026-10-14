using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Barep.Controls;

public enum BubbleGlyph { Folder, File, Drive, Others, App }

/// <summary>Una burbuja del mapa: el área es proporcional a lo que ocupa.</summary>
public sealed class BubbleItem
{
    public required object Key { get; init; }
    public required string Label { get; init; }
    public required long Size { get; init; }
    public string SizeText { get; init; } = "";
    public ImageSource? Icon { get; init; }

    /// <summary>Miniatura de la foto, si el elemento es una imagen y ya se ha cargado.</summary>
    public ImageSource? Thumbnail { get; set; }
    public BubbleGlyph Glyph { get; init; } = BubbleGlyph.Folder;

    // Ficha flotante
    public string? KindText { get; init; }
    public Brush? KindBrush { get; init; }
    public string? DetailLine { get; init; }
    public string? DateLine { get; init; }

    public bool IsSelected { get; set; }
}

/// <summary>
/// Mapa de burbujas al estilo de la «Lupa» de CleanMyMac: cada carpeta es una
/// esfera de cristal cuya superficie es proporcional a su tamaño, empaquetadas
/// alrededor del centro de mayor a menor.
/// </summary>
public sealed class BubbleMap : Canvas
{
    private const double Gap = 0.045;          // separación entre burbujas, relativa al radio mayor
    private const double MinRadius = 0.19;     // lo mínimo para que se vea y se pueda pulsar
    private const double Padding = 18;

    private IReadOnlyList<BubbleItem> _items = Array.Empty<BubbleItem>();
    private List<(double X, double Y, double R)> _packed = new();
    private readonly Dictionary<object, Grid> _visuals = new();
    private bool _animateNext;
    private object? _highlighted;

    public event Action<BubbleItem>? ItemClicked;
    public event Action<BubbleItem?>? ItemHovered;
    public event Action<BubbleItem, FrameworkElement>? ItemRightClicked;

    public BubbleMap()
    {
        ClipToBounds = false;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Render(animate: false);
    }

    public void SetItems(IReadOnlyList<BubbleItem> items)
    {
        _items = items;
        _packed = Pack(items.Select(i => (double)Math.Max(0, i.Size)).ToList());
        _animateNext = true;
        Render(animate: true);
    }

    /// <summary>Resalta una burbuja desde fuera (al pasar por la fila de la lista).</summary>
    public void Highlight(object? key)
    {
        if (Equals(_highlighted, key)) return;
        if (_highlighted is not null && _visuals.TryGetValue(_highlighted, out var old)) SetHover(old, false);
        _highlighted = key;
        if (key is not null && _visuals.TryGetValue(key, out var now)) SetHover(now, true);
    }

    public void RefreshSelection(Func<object, bool> isSelected)
    {
        foreach (var item in _items)
        {
            item.IsSelected = isSelected(item.Key);
            if (_visuals.TryGetValue(item.Key, out var visual) && visual.Tag is BubbleParts parts)
            {
                parts.Ring.Visibility = item.IsSelected ? Visibility.Visible : Visibility.Collapsed;
                parts.Badge.Visibility = item.IsSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ────────────────────────── Empaquetado ──────────────────────────

    /// <summary>
    /// Empaquetado voraz: cada círculo, de mayor a menor, se coloca tangente a
    /// alguno de los ya colocados en la posición libre más cercana al centro.
    /// Con una docena de burbujas da un racimo compacto y estable.
    /// </summary>
    private static List<(double X, double Y, double R)> Pack(IReadOnlyList<double> sizes)
    {
        var result = new List<(double X, double Y, double R)>();
        if (sizes.Count == 0) return result;

        var max = sizes.Max();
        if (max <= 0) max = 1;

        const int steps = 72;
        foreach (var size in sizes)
        {
            var r = Math.Max(MinRadius, Math.Sqrt(size / max));
            if (result.Count == 0)
            {
                result.Add((0, 0, r));
                continue;
            }

            double bestX = 0, bestY = 0, bestScore = double.MaxValue;
            foreach (var p in result)
            {
                var d = p.R + r + Gap;
                for (int k = 0; k < steps; k++)
                {
                    var a = k * Math.PI * 2 / steps;
                    var x = p.X + d * Math.Cos(a);
                    var y = p.Y + d * Math.Sin(a);

                    var free = true;
                    foreach (var q in result)
                    {
                        var dx = x - q.X;
                        var dy = y - q.Y;
                        var min = q.R + r + Gap * 0.98;
                        if (dx * dx + dy * dy < min * min) { free = false; break; }
                    }
                    if (!free) continue;

                    // El panel es apaisado: se penaliza más crecer en vertical.
                    var score = x * x + (y * 1.45) * (y * 1.45);
                    if (score < bestScore) { bestScore = score; bestX = x; bestY = y; }
                }
            }
            result.Add((bestX, bestY, r));
        }
        return result;
    }

    // ────────────────────────── Dibujo ──────────────────────────

    private sealed record BubbleParts(Ellipse Glass, Ellipse Ring, Border Badge, ScaleTransform Scale, BubbleItem Item,
        ContentControl IconHost, double IconSize);

    /// <summary>Cambia el icono de una burbuja por la miniatura de la foto, cuando ya se ha cargado.</summary>
    public void SetThumbnail(object key, ImageSource image)
    {
        if (!_visuals.TryGetValue(key, out var visual) || visual.Tag is not BubbleParts parts) return;
        parts.Item.Thumbnail = image;
        var size = parts.IconSize * 1.45;
        parts.IconHost.Content = Photo(image, size);
    }

    private static Border Photo(ImageSource image, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(Math.Max(6, size * 0.16)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1.5),
        Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
        Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black }
    };

    private void Render(bool animate)
    {
        Children.Clear();
        _visuals.Clear();
        if (_items.Count == 0 || ActualWidth < 40 || ActualHeight < 40) return;

        animate &= _animateNext;
        _animateNext = false;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y, r) in _packed)
        {
            minX = Math.Min(minX, x - r); maxX = Math.Max(maxX, x + r);
            minY = Math.Min(minY, y - r); maxY = Math.Max(maxY, y + r);
        }

        var w = ActualWidth - Padding * 2;
        var h = ActualHeight - Padding * 2;
        var scale = Math.Min(w / (maxX - minX), h / (maxY - minY));
        // Una sola burbuja no debe comerse el panel entero.
        scale = Math.Min(scale, Math.Min(ActualWidth, ActualHeight) * 0.36 / _packed.Max(p => p.R));

        var cx = ActualWidth / 2 - (minX + maxX) / 2 * scale;
        var cy = ActualHeight / 2 - (minY + maxY) / 2 * scale;

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var (x, y, r) = _packed[i];
            var radius = r * scale;
            if (radius < 6) continue;

            var visual = BuildBubble(item, radius);
            SetLeft(visual, cx + x * scale - radius);
            SetTop(visual, cy + y * scale - radius);
            Children.Add(visual);
            _visuals[item.Key] = visual;

            if (animate) AnimateIn(visual, i);
        }

        if (_highlighted is not null && _visuals.TryGetValue(_highlighted, out var hi)) SetHover(hi, true);
    }

    private Grid BuildBubble(BubbleItem item, double radius)
    {
        var d = radius * 2;
        var scale = new ScaleTransform(1, 1);
        var root = new Grid
        {
            Width = d,
            Height = d,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale
        };

        var isOthers = item.Glyph == BubbleGlyph.Others;
        var glass = new Ellipse
        {
            Fill = isOthers ? OthersFill : GlassFill,
            Stroke = RimBrush,
            StrokeThickness = radius > 60 ? 1.4 : 1.1
        };
        root.Children.Add(glass);

        // Brillo especular superior: lo que hace que parezca cristal y no un disco plano.
        root.Children.Add(new Ellipse
        {
            Width = d * 0.62,
            Height = d * 0.30,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, d * 0.06, 0, 0),
            Fill = SheenBrush,
            IsHitTestVisible = false
        });

        var ring = new Ellipse
        {
            Stroke = UiKit.Brush("Br.AccentBright"),
            StrokeThickness = 2.4,
            Margin = new Thickness(-3),
            Visibility = item.IsSelected ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false
        };
        root.Children.Add(ring);

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Margin = new Thickness(0, radius > 44 ? radius * 0.08 : 0, 0, 0)
        };

        var iconSize = Math.Clamp(radius * 0.52, 14, 64);
        var iconHost = new ContentControl
        {
            Content = item.Thumbnail is not null ? Photo(item.Thumbnail, iconSize * 1.45) : BuildIcon(item, iconSize),
            HorizontalAlignment = HorizontalAlignment.Center,
            Focusable = false
        };
        content.Children.Add(iconHost);

        if (radius >= 40)
        {
            content.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Text"),
                FontSize = Math.Clamp(radius * 0.15, 10.5, 15),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = d * 0.78,
                Margin = new Thickness(0, Math.Clamp(radius * 0.06, 3, 9), 0, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = item.SizeText,
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Text"),
                FontSize = Math.Clamp(radius * 0.13, 10, 14),
                FontWeight = FontWeights.Medium,
                Foreground = SizeBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        root.Children.Add(content);

        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = UiKit.Brush("Br.AccentGradient"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, radius * 0.12, radius * 0.12, 0),
            Visibility = item.IsSelected ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new Path
            {
                Data = Geometry.Parse("M5,12.5 L10,17 L19,7.5"),
                Stroke = Brushes.White,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = 9,
                Height = 9
            }
        };
        root.Children.Add(badge);

        root.Tag = new BubbleParts(glass, ring, badge, scale, item, iconHost, iconSize);
        root.ToolTip = BuildTooltip(item);
        ToolTipService.SetInitialShowDelay(root, 220);
        ToolTipService.SetPlacement(root, System.Windows.Controls.Primitives.PlacementMode.Mouse);

        root.MouseEnter += (_, _) =>
        {
            SetHover(root, true);
            ItemHovered?.Invoke(item);
        };
        root.MouseLeave += (_, _) =>
        {
            if (!Equals(_highlighted, item.Key)) SetHover(root, false);
            ItemHovered?.Invoke(null);
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ItemClicked?.Invoke(item);
        };
        root.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ItemRightClicked?.Invoke(item, root);
        };

        return root;
    }

    private static UIElement BuildIcon(BubbleItem item, double size)
    {
        if (item.Icon is not null)
        {
            var image = new Image
            {
                Source = item.Icon,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black }
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        var (geometry, fill) = item.Glyph switch
        {
            BubbleGlyph.File => ("Glyph.File", "Br.GlyphFile"),
            BubbleGlyph.Drive => ("Glyph.Drive", "Br.GlyphFolder"),
            BubbleGlyph.Others => ("Glyph.Others", "Br.GlyphOthers"),
            _ => ("Glyph.Folder", "Br.GlyphFolder")
        };

        return new Path
        {
            Data = (Geometry)Application.Current.FindResource(geometry),
            Fill = UiKit.Brush(fill),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black }
        };
    }

    private static object BuildTooltip(BubbleItem item)
    {
        var panel = new StackPanel { MaxWidth = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = UiKit.Brush("Br.TextPrimary")
        });
        if (!string.IsNullOrEmpty(item.KindText))
            panel.Children.Add(new TextBlock
            {
                Text = item.KindText,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = item.KindBrush ?? UiKit.Brush("Br.AccentBright")
            });
        if (!string.IsNullOrEmpty(item.DetailLine))
            panel.Children.Add(new TextBlock
            {
                Text = item.DetailLine,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = UiKit.Brush("Br.TextSecondary")
            });
        if (!string.IsNullOrEmpty(item.DateLine))
            panel.Children.Add(new TextBlock
            {
                Text = item.DateLine,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = UiKit.Brush("Br.TextSecondary")
            });
        return panel;
    }

    // ────────────────────────── Interacción ──────────────────────────

    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(170));

    private static void SetHover(Grid visual, bool on)
    {
        if (visual.Tag is not BubbleParts parts) return;

        var target = on ? 1.05 : 1.0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, HoverDuration) { EasingFunction = ease });
        parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, HoverDuration) { EasingFunction = ease });

        parts.Glass.Fill = on
            ? GlassFillHover
            : parts.Item.Glyph == BubbleGlyph.Others ? OthersFill : GlassFill;
        parts.Glass.Stroke = on ? RimBrushHover : RimBrush;
        visual.Effect = on
            ? new DropShadowEffect
            {
                Color = Color.FromRgb(0x8B, 0x5C, 0xF6),
                BlurRadius = 38,
                ShadowDepth = 0,
                Opacity = 0.8
            }
            : null;
        SetZIndex(visual, on ? 1000 : 0);
    }

    private static void AnimateIn(Grid visual, int index)
    {
        if (visual.Tag is not BubbleParts parts) return;

        var begin = TimeSpan.FromMilliseconds(35 * index);
        var duration = new Duration(TimeSpan.FromMilliseconds(520));
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        visual.Opacity = 0;
        visual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(320))) { BeginTime = begin });

        var grow = new DoubleAnimation(0.55, 1, duration) { BeginTime = begin, EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            // Liberar la animación para que el hover pueda volver a escalar.
            parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        parts.Scale.ScaleX = parts.Scale.ScaleY = 1;
        parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
    }

    // ────────────────────────── Materiales ──────────────────────────

    private static readonly Brush GlassFill = Glass(
        (0.00, "#3DC4B5FD"), (0.55, "#2E7C3AED"), (0.86, "#4A6D28D9"), (1.00, "#B38B5CF6"));

    private static readonly Brush GlassFillHover = Glass(
        (0.00, "#5AD8CCFF"), (0.55, "#447C3AED"), (0.86, "#6A7C3AED"), (1.00, "#E0A78BFA"));

    private static readonly Brush OthersFill = Glass(
        (0.00, "#26A79FBC"), (0.60, "#1C4B3F6B"), (0.88, "#335B4B8A"), (1.00, "#808B7FB8"));

    private static readonly Brush RimBrush = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0xCC, 0xE9, 0xDE, 0xFF), 0),
            new(Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6), 0.55),
            new(Color.FromArgb(0x99, 0xA7, 0x8B, 0xFA), 1)
        }, new Point(0.15, 0), new Point(0.85, 1)));

    private static readonly Brush RimBrushHover = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0xFF, 0xF5, 0xF0, 0xFF), 0),
            new(Color.FromArgb(0x99, 0xA7, 0x8B, 0xFA), 0.55),
            new(Color.FromArgb(0xFF, 0xC4, 0xB5, 0xFD), 1)
        }, new Point(0.15, 0), new Point(0.85, 1)));

    private static readonly Brush SheenBrush = Frozen(new RadialGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0),
            new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
        }));

    private static readonly Brush SizeBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0xE4, 0xDB, 0xFA)));

    private static Brush Glass(params (double Offset, string Color)[] stops)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.36, 0.28),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        return Frozen(brush);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
