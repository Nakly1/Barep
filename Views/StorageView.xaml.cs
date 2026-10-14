using System.Windows;
using System.Windows.Controls;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class StorageView : UserControl
{
    private bool _loaded;

    public StorageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        var s = AppServices.Hardware.GetStatic();
        BuildVolumes(s.Volumes);
        BuildDrives(s.Disks);

        var totalGB = s.Disks.Sum(d => d.CapacityGB);
        SubtitleText.Text = s.Disks.Count > 0
            ? UiKit.Plural(s.Disks.Count, "unidad física", "unidades físicas") +
              $" · {totalGB:N0} GB de capacidad instalada"
            : UiKit.Plural(s.Volumes.Count, "volumen detectado", "volúmenes detectados");
    }

    private void BuildVolumes(IReadOnlyList<DiskInfo> volumes)
    {
        VolumesPanel.Children.Clear();

        if (volumes.Count == 0)
        {
            VolumesPanel.Children.Add(UiKit.EmptyState("No se detectaron volúmenes fijos."));
            return;
        }

        foreach (var v in volumes)
        {
            var brush = UiKit.LoadBrush(v.UsedPercent);
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(v.Label) ? v.Letter : $"{v.Letter}  {v.Label}",
                Style = UiKit.Style("T.Value"),
                FontSize = 13.5
            };
            var amount = new TextBlock
            {
                Text = $"{v.UsedGB:N0} GB de {v.TotalGB:N0} GB  ·  {v.UsedPercent:0}%",
                Style = UiKit.Style("T.Caption"),
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(title, 0);
            Grid.SetColumn(amount, 1);
            header.Children.Add(title);
            header.Children.Add(amount);

            var bar = new ProgressBar
            {
                Style = UiKit.Style("Meter"),
                Maximum = 100,
                Value = v.UsedPercent,
                Foreground = brush
            };

            var footer = new TextBlock
            {
                Text = $"{v.FreeGB:N0} GB libres" +
                       (string.IsNullOrWhiteSpace(v.FileSystem) ? "" : $"  ·  {v.FileSystem}"),
                Style = UiKit.Style("T.Caption"),
                Margin = new Thickness(0, 7, 0, 0)
            };

            block.Children.Add(header);
            block.Children.Add(bar);
            block.Children.Add(footer);
            VolumesPanel.Children.Add(block);
        }
    }

    private void BuildDrives(IReadOnlyList<PhysicalDisk> drives)
    {
        DrivesPanel.Children.Clear();

        if (drives.Count == 0)
        {
            DrivesPanel.Children.Add(UiKit.EmptyState(
                "Windows no expuso el inventario de unidades físicas."));
            return;
        }

        for (int i = 0; i < drives.Count; i++)
        {
            var d = drives[i];
            var block = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = d.Model,
                Style = UiKit.Style("T.Headline"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = d.Model
            };
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            if (d.CapacityGB > 0)
            {
                var chip = UiKit.Chip($"{d.CapacityGB:N0} GB");
                chip.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(chip, 1);
                header.Children.Add(chip);
            }

            var specs = new StackPanel();
            specs.Children.Add(UiKit.SpecRow("Número de serie", d.SerialNumber, mono: true));
            if (!string.IsNullOrWhiteSpace(d.Interface))
                specs.Children.Add(UiKit.SpecRow("Interfaz", d.Interface));
            if (!string.IsNullOrWhiteSpace(d.FirmwareRevision))
                specs.Children.Add(UiKit.SpecRow("Firmware", d.FirmwareRevision, mono: true));

            block.Children.Add(header);
            block.Children.Add(specs);

            DrivesPanel.Children.Add(block);

            if (i < drives.Count - 1)
            {
                DrivesPanel.Children.Add(new Border
                {
                    Style = UiKit.Style("Divider"),
                    Margin = new Thickness(0, 8, 0, 18)
                });
            }
        }
    }
}
