using Barep.Services.Net;

namespace Barep.Services;

/// <summary>
/// Servicios compartidos por toda la aplicación. Un localizador estático es
/// suficiente aquí: hay una sola ventana, un solo temporizador y un solo
/// equipo que medir. Cualquier vista puede leer de aquí sin que la ventana
/// principal tenga que inyectarle nada.
/// </summary>
public static class AppServices
{
    public static HardwareService Hardware { get; } = new();
    public static MonitorService Monitor { get; } = new(Hardware);
    public static EvidenceService Evidence { get; } = new(Hardware);
    public static NetworkMonitor Network { get; } = new();
}
