using System.Windows.Threading;

namespace Barep.Services;

/// <summary>
/// Latido de la aplicación. Toma una lectura del sistema cada pocos segundos
/// fuera del hilo de interfaz y la reparte a las vistas que estén escuchando,
/// de modo que exista un único temporizador y un único momento de verdad.
/// </summary>
public class MonitorService
{
    private readonly HardwareService _hw;
    private readonly DispatcherTimer _timer = new();
    private bool _busy;

    public MonitorService(HardwareService hardwareService)
    {
        _hw = hardwareService;
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) => await SampleAsync();
    }

    /// <summary>Se dispara en el hilo de interfaz con cada lectura nueva.</summary>
    public event Action<RealtimeSnapshot>? Sampled;

    public RealtimeSnapshot? Latest { get; private set; }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public async Task SampleAsync()
    {
        // Una lectura WMI lenta no debe encolar la siguiente.
        if (_busy) return;
        _busy = true;
        try
        {
            var snapshot = await Task.Run(_hw.GetRealtime);
            Latest = snapshot;
            Sampled?.Invoke(snapshot);
        }
        catch
        {
            // Un fallo puntual de WMI no debe tumbar la app: se reintenta al
            // siguiente tick.
        }
        finally { _busy = false; }
    }
}
