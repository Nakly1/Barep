using System.Windows;
using Barep.Controls;
using Barep.Services;

namespace Barep;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Prueba de ida y vuelta de los ajustes de Optimizar:
        //   dotnet run -- --probar-ajustes
        if (e.Args.Any(a => a.Equals("--probar-ajustes", StringComparison.OrdinalIgnoreCase)))
        {
            var report = TweakSelfTest.Run();
            MessageBox.Show(report.Length > 3000 ? report[..3000] + "\n…" : report,
                "Barep · prueba de ajustes (informe completo en prueba-ajustes.txt)");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ThemeManager.ApplySaved();

        var window = new Barep.MainWindow();
        MainWindow = window;
        window.Show();
    }
}
