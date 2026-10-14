using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Barep.Controls;

/// <summary>
/// Gráfico de línea compacto para una sola serie 0–100. Dibuja por
/// <see cref="OnRender"/> en vez de crear formas: con un punto nuevo cada dos
/// segundos, reconstruir el árbol visual sería un desperdicio.
/// </summary>
public class Sparkline : FrameworkElement
{
    private readonly Queue<double> _values = new();

    public static readonly DependencyProperty CapacityProperty =
        DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(Sparkline),
            new PropertyMetadata(60));

    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty =
        DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty =
        DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(Sparkline),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public void Push(double value)
    {
        _values.Enqueue(Math.Clamp(value, 0, 100));
        while (_values.Count > Capacity) _values.Dequeue();
        InvalidateVisual();
    }

    public void Clear()
    {
        _values.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        // Fondo transparente pero presente: sin él, WPF no entrega los eventos
        // de ratón sobre el elemento y el tooltip del contenedor no aparece.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (ShowGrid && GridBrush is not null)
        {
            var pen = new Pen(GridBrush, 1);
            pen.Freeze();
            for (int i = 1; i <= 3; i++)
            {
                var y = Math.Round(h * i / 4.0) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }

        if (_values.Count < 2) return;

        var samples = _values.ToArray();
        var step = w / Math.Max(1, Capacity - 1);
        var startX = w - step * (samples.Length - 1);

        var figure = new PathFigure { StartPoint = new Point(startX, ToY(samples[0], h)) };
        for (int i = 1; i < samples.Length; i++)
            figure.Segments.Add(new LineSegment(new Point(startX + step * i, ToY(samples[i], h)), true));

        var stroke = new PathGeometry(new[] { figure });
        stroke.Freeze();

        // El relleno reusa la misma silueta cerrada contra la base.
        var areaFigure = figure.Clone();
        areaFigure.Segments.Add(new LineSegment(new Point(startX + step * (samples.Length - 1), h), false));
        areaFigure.Segments.Add(new LineSegment(new Point(startX, h), false));
        areaFigure.IsClosed = true;
        var area = new PathGeometry(new[] { areaFigure });
        area.Freeze();

        if (LineBrush is SolidColorBrush solid)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x59, solid.Color.R, solid.Color.G, solid.Color.B), 0),
                    new GradientStop(Color.FromArgb(0x00, solid.Color.R, solid.Color.G, solid.Color.B), 1)
                }
            };
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        var linePen = new Pen(LineBrush, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, stroke);

        // Punto vivo en la última muestra.
        var last = new Point(startX + step * (samples.Length - 1), ToY(samples[^1], h));
        dc.DrawEllipse(LineBrush, null, last, 2.6, 2.6);
    }

    private static double ToY(double value, double height) =>
        height - value / 100.0 * (height - 3) - 1.5;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Ocupa lo que le den: siempre vive dentro de una tarjeta con tamaño propio.
        var width = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 48 : availableSize.Height;
        return new Size(width, height);
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "Sparkline ({0} muestras)", _values.Count);
}
