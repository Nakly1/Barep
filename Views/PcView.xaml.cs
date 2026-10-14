using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

/// <summary>
/// Mi PC: un plano del equipo con sus piezas reales y, sobre todo, dónde hay
/// hueco para ampliar (ranuras de RAM o M.2 libres).
/// </summary>
public partial class PcView : UserControl
{
    private PcLayout? _layout;
    private PcPart? _pinned;
    private bool _loaded;

    public PcView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Blueprint.PartHovered += part => ShowDetail(part ?? _pinned);
        Blueprint.PartClicked += part =>
        {
            _pinned = ReferenceEquals(_pinned, part) ? null : part;
            Blueprint.Select(_pinned);
            ShowDetail(part);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        _layout = await Task.Run(() =>
            PcLayoutService.Build(AppServices.Hardware.GetStatic(), AppServices.Hardware.GetBattery()));

        var l = _layout;
        SubtitleText.Text = $"{l.ChassisName} · {l.Model}. Pasa el ratón o haz clic en cada pieza para ver qué es.";
        Blueprint.Show(l);
        BuildSummary(l);
        BuildLegend();

        Disclaimer.Text = $"Es un esquema típico de un {l.ChassisName.ToLowerInvariant()}: Windows sabe qué piezas tienes y qué ranuras " +
                          "están libres, pero no su posición exacta en la placa. Para ver la tuya, busca el manual de servicio de tu modelo.";
    }

    private void BuildSummary(PcLayout l)
    {
        SummaryGrid.Children.Clear();

        var ramTotal = l.Ram.Count(r => r.State != SlotState.Soldered);
        var ramUsed = l.Ram.Count(r => r.State == SlotState.Occupied);
        var soldered = l.Ram.Any(r => r.State == SlotState.Soldered);
        string ramValue, ramNote;
        string ramBrush = "Br.TextPrimary";
        if (ramTotal == 0 && soldered)
        {
            ramValue = "Soldada";
            ramNote = "La memoria va soldada a la placa: no se puede ampliar.";
        }
        else
        {
            ramValue = $"{ramUsed} de {ramTotal} ranuras";
            ramNote = l.FreeRamSlots > 0
                ? $"Tienes {UiKit.Plural(l.FreeRamSlots, "ranura libre", "ranuras libres")}: puedes ampliar la RAM" +
                  (l.MaxRamGB > 0 ? $" hasta {l.MaxRamGB} GB." : ".")
                : "Todas las ranuras están ocupadas: para ampliar hay que cambiar un módulo por otro más grande.";
            if (l.FreeRamSlots > 0) ramBrush = "Br.Positive";
        }
        SummaryGrid.Children.Add(Tile("Memoria RAM", ramValue, ramNote, ramBrush, new Thickness(0, 0, 7, 0)));

        var disks = l.Storage.Count(s => s.State == SlotState.Occupied);
        string storageNote;
        var storageBrush = "Br.TextPrimary";
        if (l.FreeStorageSlots > 0)
        {
            storageNote = $"Hay {UiKit.Plural(l.FreeStorageSlots, "ranura M.2 libre", "ranuras M.2 libres")}: puedes añadir otro SSD.";
            storageBrush = "Br.Positive";
        }
        else if (l.Storage.Any(s => s.State == SlotState.Unknown))
            storageNote = "Windows no informa si hay otra ranura M.2 libre. Revisa el manual de tu modelo.";
        else
            storageNote = "No hay ranuras de almacenamiento libres declaradas.";
        SummaryGrid.Children.Add(Tile("Almacenamiento", UiKit.Plural(disks, "unidad", "unidades"), storageNote, storageBrush,
            new Thickness(7, 0, 7, 0)));

        var dedicated = l.Gpus.FirstOrDefault(g => g.Subtitle == "Gráfica dedicada");
        SummaryGrid.Children.Add(Tile("Procesador y gráfica", l.Cpu.Title,
            dedicated is not null ? $"Gráfica dedicada: {dedicated.Title}" : "Usa la gráfica integrada del procesador.",
            "Br.TextPrimary", new Thickness(7, 0, 0, 0)));
    }

    private static Border Tile(string label, string value, string note, string valueBrush, Thickness margin)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(label, "T.Caption"));
        var v = UiKit.Text(value, "T.Metric", UiKit.Brush(valueBrush), new Thickness(0, 6, 0, 6));
        v.FontSize = 21;
        v.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(v);
        stack.Children.Add(UiKit.Text(note, "T.Secondary"));
        return new Border { Style = UiKit.Style("Card"), Margin = margin, Child = stack };
    }

    private void BuildLegend()
    {
        Legend.Children.Clear();
        Legend.Children.Add(Swatch("Ocupada", UiKit.Brush("Br.AccentGradient"), null, false));
        Legend.Children.Add(Swatch("Libre para ampliar", UiKit.Brush("Br.PositiveTint"), UiKit.Brush("Br.Positive"), true));
        Legend.Children.Add(Swatch("Windows no lo informa", Brushes.Transparent, UiKit.Brush("Br.TextTertiary"), true));
        Legend.Children.Add(Swatch("Soldada (no se cambia)", UiKit.Brush("Br.SurfaceRaised"), UiKit.Brush("Br.AccentDeep"), false));
    }

    private static StackPanel Swatch(string text, Brush fill, Brush? stroke, bool dashed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 6) };
        panel.Children.Add(new Rectangle
        {
            Width = 22, Height = 12, RadiusX = 3, RadiusY = 3, Fill = fill,
            Stroke = stroke, StrokeThickness = stroke is null ? 0 : 1.4,
            StrokeDashArray = dashed ? new DoubleCollection { 3, 2 } : null,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        panel.Children.Add(UiKit.Text(text, "T.Caption"));
        return panel;
    }

    private void ShowDetail(PcPart? part)
    {
        if (part is null)
        {
            DetailKind.Text = "Detalle";
            DetailTitle.Text = "Pasa el ratón por una pieza";
            DetailSubtitle.Text = "Las ranuras verdes están libres: ahí puedes añadir memoria o un disco.";
            DetailSpecs.Children.Clear();
            DetailHint.Visibility = Visibility.Collapsed;
            return;
        }

        DetailKind.Text = part.Kind switch
        {
            PartKind.Cpu => "Procesador",
            PartKind.Gpu => "Gráfica",
            PartKind.RamSlot or PartKind.RamSoldered => "Memoria RAM",
            PartKind.M2Slot => "Ranura M.2",
            PartKind.SataBay => "Unidad SATA",
            PartKind.PcieSlot => "Ranura PCIe",
            PartKind.Battery => "Batería",
            _ => "Pieza"
        };
        DetailTitle.Text = part.Title;
        DetailSubtitle.Text = part.Subtitle;
        DetailSubtitle.Foreground = UiKit.Brush(part.State == SlotState.Free ? "Br.Positive" : "Br.TextSecondary");
        UiKit.FillSpecs(DetailSpecs, part.Specs.Where(sp => !string.IsNullOrWhiteSpace(sp.Value)), labelWidth: 110);

        DetailHint.Text = part.Hint ?? "";
        DetailHint.Visibility = string.IsNullOrEmpty(part.Hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        var model = _layout?.Model ?? "";
        var query = Uri.EscapeDataString($"{model} manual de servicio ranuras RAM M.2");
        try { Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={query}") { UseShellExecute = true }); } catch { }
    }
}
