using System.Windows;
using System.Windows.Controls;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private bool _staticLoaded;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_staticLoaded)
        {
            _staticLoaded = true;
            ApplyStatic(AppServices.Hardware.GetStatic());
            Unloaded += (_, _) => AppServices.Monitor.Sampled -= Apply;
        }

        // Se vuelve a escuchar en cada visita: al salir de la sección se deja de escuchar.
        AppServices.Monitor.Sampled -= Apply;
        AppServices.Monitor.Sampled += Apply;
        if (AppServices.Monitor.Latest is { } latest) Apply(latest);
    }

    private void ApplyStatic(StaticSnapshot s)
    {
        SubtitleText.Text = $"{s.Board.SystemManufacturer} {s.Board.SystemModel} · {s.Os.Name}"
            .Replace($"{HardwareText.Unknown} ", "")
            .Replace($" {HardwareText.Unknown}", "");

        CpuNameText.Text = s.Cpu.Name;
        CpuCoresText.Text = s.Cpu.Cores > 0
            ? $"{s.Cpu.Cores} / {s.Cpu.Threads}"
            : HardwareText.Unknown;
        CpuSpeedText.Text = s.Cpu.MaxMHz > 0
            ? $"{s.Cpu.MaxMHz / 1000.0:0.00} GHz"
            : HardwareText.Unknown;

        var gpu = s.Gpus.FirstOrDefault();
        GpuNameText.Text = gpu?.Name ?? HardwareText.Unavailable;
        GpuSpecs.Children.Clear();
        if (gpu is not null)
        {
            GpuSpecs.Children.Add(UiKit.SpecRow("Memoria de vídeo",
                gpu.VramMB > 0 ? $"{gpu.VramMB / 1024.0:0.#} GB" : HardwareText.Unknown, labelWidth: 116));
            GpuSpecs.Children.Add(UiKit.SpecRow("Controlador", gpu.DriverVersion, labelWidth: 116));
            if (s.Gpus.Count > 1)
                GpuSpecs.Children.Add(UiKit.SpecRow("Otras", string.Join(", ",
                    s.Gpus.Skip(1).Select(g => g.Name)), labelWidth: 116));
        }

        UiKit.FillSpecs(SystemSpecsLeft, new (string, string?)[]
        {
            ("Nombre del equipo", Environment.MachineName),
            ("Usuario", Environment.UserName),
            ("Sistema operativo", s.Os.Name),
            ("Compilación", s.Os.Build),
            ("Arquitectura", s.Os.Architecture),
        });

        UiKit.FillSpecs(SystemSpecsRight, new (string, string?)[]
        {
            ("Fabricante", s.Board.SystemManufacturer),
            ("Modelo", s.Board.SystemModel),
            ("Placa base", $"{s.Board.Manufacturer} {s.Board.Product}".Trim()),
            ("BIOS", $"{s.Board.BiosVendor} {s.Board.BiosVersion}".Trim()),
            ("Windows instalado", s.Os.InstallDate),
        });
    }

    private void Apply(RealtimeSnapshot s)
    {
        var ramBrush = UiKit.LoadBrush(s.RamPercent);
        RamPercentText.Text = $"{s.RamPercent:0.0}";
        RamPercentText.Foreground = ramBrush;
        RamBar.Value = s.RamPercent;
        RamBar.Foreground = ramBrush;
        RamSpark.LineBrush = ramBrush;
        RamSpark.Push(s.RamPercent);
        RamAmountText.Text = $"{s.RamUsedMB / 1024.0:0.0} GB de {s.RamTotalMB / 1024.0:0.0} GB";
        RamFreeText.Text = $"{s.RamAvailableMB / 1024.0:0.0} GB";
        RamUsedText.Text = $"{s.RamUsedMB:N0} MB";
        RamStateText.Text = LoadWord(s.RamPercent);

        var cpuBrush = UiKit.LoadBrush(s.CpuPercent);
        CpuPercentText.Text = $"{s.CpuPercent:0.0}";
        CpuPercentText.Foreground = cpuBrush;
        CpuBar.Value = s.CpuPercent;
        CpuBar.Foreground = cpuBrush;
        CpuSpark.LineBrush = cpuBrush;
        CpuSpark.Push(s.CpuPercent);
        CpuStateText.Text = LoadWord(s.CpuPercent);

        NetDownText.Text = UiKit.FormatSpeed(s.Network.DownKbps);
        NetUpText.Text = UiKit.FormatSpeed(s.Network.UpKbps);

        if (s.Battery.Present)
        {
            var batteryBrush = s.Battery.Percent switch
            {
                > 40 => UiKit.Brush("Br.Accent"),
                > 15 => UiKit.Brush("Br.Warn"),
                _ => UiKit.Brush("Br.Critical")
            };
            BatteryPercentText.Text = $"{s.Battery.Percent} %";
            BatteryPercentText.Foreground = batteryBrush;
            BatteryBar.Visibility = Visibility.Visible;
            BatteryBar.Value = s.Battery.Percent;
            BatteryBar.Foreground = batteryBrush;
            BatteryStatusText.Text = s.Battery.Status;
        }
        else
        {
            BatteryPercentText.Text = "CA";
            BatteryPercentText.Foreground = UiKit.Brush("Br.TextSecondary");
            BatteryBar.Visibility = Visibility.Collapsed;
            BatteryStatusText.Text = "Sin batería. El equipo funciona conectado a la corriente.";
        }

        UptimeText.Text = $"Encendido desde hace {UiKit.FormatUptime(s.Uptime)}";
    }

    private static string LoadWord(double percent) => percent switch
    {
        < 70 => "Holgado",
        < 88 => "Ajustado",
        _ => "Al límite"
    };
}
