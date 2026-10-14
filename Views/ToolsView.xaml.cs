using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

    private void GcButton_Click(object sender, RoutedEventArgs e)
    {
        GcButton.IsEnabled = false;

        var before = GC.GetTotalMemory(false);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);
        var freedKB = (before - after) / 1024;

        SetStatus(freedKB > 0
            ? $"Memoria compactada. Se devolvieron {freedKB:N0} KB al sistema."
            : "No había memoria que devolver: la aplicación ya estaba compacta.", "Br.Positive");

        GcButton.IsEnabled = true;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Guardar informe del sistema",
            FileName = $"informe-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Documento de texto (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() != true) return;

        ExportButton.IsEnabled = false;
        SetStatus("Reuniendo los datos del equipo…", "Br.TextSecondary");

        try
        {
            var path = dialog.FileName;
            await Task.Run(() =>
            {
                var statics = AppServices.Hardware.GetStatic();
                var realtime = AppServices.Monitor.Latest ?? AppServices.Hardware.GetRealtime();
                File.WriteAllText(path, SystemReport.Build(statics, realtime), Encoding.UTF8);
            });
            SetStatus($"Informe guardado en {path}", "Br.Positive");
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo guardar el informe: {ex.Message}", "Br.Critical");
        }
        finally { ExportButton.IsEnabled = true; }
    }

    private void SetStatus(string message, string brushKey)
    {
        StatusText.Text = message;
        StatusText.Foreground = UiKit.Brush(brushKey);
    }
}
