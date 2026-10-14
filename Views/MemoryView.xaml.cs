using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class MemoryView : UserControl
{
    private bool _staticLoaded;

    public MemoryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_staticLoaded) return;
        _staticLoaded = true;

        ApplyStatic(AppServices.Hardware.GetStatic());

        AppServices.Monitor.Sampled += Apply;
        if (AppServices.Monitor.Latest is { } latest) Apply(latest);

        Unloaded += (_, _) => AppServices.Monitor.Sampled -= Apply;
    }

    private void ApplyStatic(StaticSnapshot s)
    {
        var ram = s.Ram;

        TotalText.Text = ram.TotalMB > 0 ? $"{ram.TotalMB / 1024.0:0.#} GB" : "—";
        TypeText.Text = ram.Type == HardwareText.Unknown ? "—" : ram.Type;
        SpeedText.Text = ram.SpeedMHz > 0 ? $"{ram.SpeedMHz} MT/s" : "—";
        SlotsText.Text = ram.SlotsTotal > 0
            ? $"{ram.SlotsUsed} de {ram.SlotsTotal}"
            : ram.SlotsUsed.ToString();

        var free = Math.Max(0, ram.SlotsTotal - ram.SlotsUsed);
        SubtitleText.Text = ram.Modules.Count == 0
            ? "No se pudo leer el inventario de módulos físicos."
            : UiKit.Plural(ram.Modules.Count, "módulo instalado", "módulos instalados") +
              (free > 0
                  ? $" · {UiKit.Plural(free, "ranura libre", "ranuras libres")} para ampliar"
                  : " · todas las ranuras ocupadas");

        BuildModules(ram);
    }

    private void BuildModules(RamSummary ram)
    {
        ModulesPanel.Items.Clear();

        if (ram.Modules.Count == 0)
        {
            ModulesPanel.Items.Add(UiKit.Card(
                UiKit.EmptyState("Windows no expuso el inventario SMBIOS de memoria en este equipo. " +
                                 "El uso en tiempo real sigue siendo correcto.")));
            return;
        }

        var missingSerials = ram.Modules.Count(m => m.SerialNumber == HardwareText.Unknown);
        ModulesHintText.Text = missingSerials == 0
            ? "Todos los módulos informan número de serie"
            : UiKit.Plural(missingSerials, "módulo sin número de serie", "módulos sin número de serie")
              + " en la BIOS";

        // Con un solo módulo, una tarjeta a media anchura queda desequilibrada.
        if (ModulesPanel.ItemsPanel is not null && ram.Modules.Count == 1)
            ModulesPanel.ItemsPanel = SingleColumnPanel();

        for (int i = 0; i < ram.Modules.Count; i++)
            ModulesPanel.Items.Add(BuildModuleCard(ram.Modules[i], i + 1));
    }

    private static ItemsPanelTemplate SingleColumnPanel()
    {
        var factory = new FrameworkElementFactory(typeof(UniformGrid));
        factory.SetValue(UniformGrid.ColumnsProperty, 1);
        return new ItemsPanelTemplate { VisualTree = factory };
    }

    private static Border BuildModuleCard(MemoryModule m, int index)
    {
        var body = new StackPanel();

        // ── Cabecera: distintivo de ranura + nombre comercial ──
        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = UiKit.Brush("Br.AccentTintStrong"),
            BorderBrush = UiKit.Brush("Br.AccentDeep"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = index.ToString(),
                Style = UiKit.Style("T.Value"),
                FontSize = 14,
                Foreground = UiKit.Brush("Br.AccentBright"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = m.DisplayName,
            Style = UiKit.Style("T.Headline"),
            FontSize = 14.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = m.DisplayName
        });
        titles.Children.Add(new TextBlock
        {
            Text = m.Slot,
            Style = UiKit.Style("T.Caption"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(titles, 1);
        header.Children.Add(badge);
        header.Children.Add(titles);

        if (m.FormFactor != HardwareText.Unknown)
        {
            var chip = UiKit.Chip(m.FormFactor);
            chip.VerticalAlignment = VerticalAlignment.Top;
            chip.Margin = new Thickness(10, 2, 0, 0);
            Grid.SetColumn(chip, 2);
            header.Children.Add(chip);
        }

        body.Children.Add(header);
        body.Children.Add(new Border
        {
            Style = UiKit.Style("Divider"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        // ── Ficha técnica ──
        var speed = m.RatedSpeedMHz > 0
            ? m.ConfiguredSpeedMHz > 0 && m.ConfiguredSpeedMHz != m.RatedSpeedMHz
                ? $"{m.RatedSpeedMHz} MT/s · funcionando a {m.ConfiguredSpeedMHz}"
                : $"{m.RatedSpeedMHz} MT/s"
            : HardwareText.Unknown;

        var specs = new StackPanel();
        specs.Children.Add(UiKit.SpecRow("Fabricante", m.Manufacturer, labelWidth: 128));
        specs.Children.Add(UiKit.SpecRow("Número de parte", m.PartNumber, mono: true, labelWidth: 128));
        specs.Children.Add(UiKit.SpecRow("Número de serie", m.SerialNumber, mono: true, labelWidth: 128));
        specs.Children.Add(UiKit.SpecRow("Capacidad", $"{m.CapacityGB:0.#} GB", labelWidth: 128));
        specs.Children.Add(UiKit.SpecRow("Velocidad", speed, labelWidth: 128));
        if (m.VoltageV > 0)
            specs.Children.Add(UiKit.SpecRow("Voltaje", $"{m.VoltageV:0.00} V", labelWidth: 128));
        if (!string.IsNullOrWhiteSpace(m.Bank))
            specs.Children.Add(UiKit.SpecRow("Banco", m.Bank, labelWidth: 128));

        body.Children.Add(specs);

        return new Border
        {
            Style = UiKit.Style("Card"),
            Margin = new Thickness(0, 0, 14, 14),
            Child = body
        };
    }

    private void Apply(RealtimeSnapshot s)
    {
        var brush = UiKit.LoadBrush(s.RamPercent);

        PercentText.Text = $"{s.RamPercent:0.0}";
        PercentText.Foreground = brush;
        UsageBar.Value = s.RamPercent;
        UsageBar.Foreground = brush;
        Spark.LineBrush = brush;
        Spark.Push(s.RamPercent);

        AmountText.Text = $"{s.RamUsedMB / 1024.0:0.0} GB de {s.RamTotalMB / 1024.0:0.0} GB en uso";
        UpdatedText.Text = s.TakenAt.ToString("HH:mm:ss");

        UiKit.FillSpecs(UsageSpecs, new (string, string?)[]
        {
            ("Disponible", $"{s.RamAvailableMB / 1024.0:0.0} GB  ({s.RamAvailableMB:N0} MB)"),
            ("En uso", $"{s.RamUsedMB / 1024.0:0.0} GB  ({s.RamUsedMB:N0} MB)"),
            ("Visible para Windows", $"{s.RamTotalMB / 1024.0:0.0} GB"),
        }, labelWidth: 128);
    }
}
