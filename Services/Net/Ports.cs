namespace Barep.Services.Net;

public enum PortRisk { None, Low, Medium, High }

/// <summary>Qué es un puerto y si conviene que esté abierto, en palabras normales.</summary>
public sealed record PortInfo(int Port, string Name, string What, PortRisk Risk);

/// <summary>
/// Los puertos más comunes de una red de casa y lo que significa encontrarlos
/// abiertos. Sirve para la ficha de cada aparato y para los puertos de este PC.
/// </summary>
public static class Ports
{
    private static readonly Dictionary<int, PortInfo> Known = new PortInfo[]
    {
        new(21, "FTP", "Compartir archivos sin cifrar: usuario y contraseña viajan a la vista.", PortRisk.High),
        new(22, "SSH", "Acceso remoto por consola. Normal en servidores y NAS si tiene buena contraseña.", PortRisk.Medium),
        new(23, "Telnet", "Acceso remoto antiguo y sin cifrar. No debería estar abierto en ningún aparato.", PortRisk.High),
        new(53, "DNS", "Traduce nombres de páginas a direcciones. Normal en el router.", PortRisk.None),
        new(80, "Web (HTTP)", "Página web sin cifrar. En un router o impresora es su panel de configuración.", PortRisk.Low),
        new(135, "RPC de Windows", "Servicio interno de Windows. Normal en PC con Windows.", PortRisk.Low),
        new(139, "NetBIOS", "Compartir archivos e impresoras al estilo antiguo de Windows.", PortRisk.Medium),
        new(443, "Web segura (HTTPS)", "Página web cifrada. Normal.", PortRisk.None),
        new(445, "Carpetas compartidas (SMB)", "Compartir archivos en Windows. Bien en casa, peligroso en redes públicas.", PortRisk.Medium),
        new(515, "Impresión (LPD)", "Recibe trabajos de impresión.", PortRisk.None),
        new(548, "Carpetas compartidas de Mac", "Compartir archivos en Mac (AFP).", PortRisk.Low),
        new(554, "Vídeo en directo (RTSP)", "Emisión de cámaras de vigilancia. Asegúrate de que tiene contraseña.", PortRisk.Medium),
        new(631, "Impresión (IPP)", "Impresora en red.", PortRisk.None),
        new(1400, "Sonos", "Altavoz Sonos.", PortRisk.None),
        new(1900, "UPnP", "Descubrimiento automático de aparatos.", PortRisk.Low),
        new(3306, "MySQL", "Base de datos. Sólo debería verse desde el propio equipo.", PortRisk.High),
        new(3389, "Escritorio remoto", "Permite manejar el PC a distancia. Blanco habitual de ataques.", PortRisk.High),
        new(5000, "UPnP / NAS / AirPlay", "Panel de NAS, servicio UPnP o AirPlay en Mac.", PortRisk.Low),
        new(5353, "mDNS / Bonjour", "Anuncia el aparato en la red. Normal.", PortRisk.None),
        new(5432, "PostgreSQL", "Base de datos. Sólo debería verse desde el propio equipo.", PortRisk.High),
        new(5555, "Depuración Android (ADB)", "Control total de un Android por la red. Desactívalo si no lo estás usando.", PortRisk.High),
        new(5900, "VNC", "Control remoto de pantalla. Suele tener contraseñas débiles.", PortRisk.High),
        new(7000, "AirPlay", "Recibe vídeo y audio desde iPhone o Mac.", PortRisk.None),
        new(7547, "Gestión del operador (TR-069)", "Tu compañía de internet administra el router por aquí. Normal en routers del operador.", PortRisk.Low),
        new(8008, "Chromecast", "Recibe contenido para reproducir.", PortRisk.None),
        new(8009, "Chromecast", "Recibe contenido para reproducir.", PortRisk.None),
        new(8060, "Roku", "Control del reproductor Roku.", PortRisk.None),
        new(8080, "Web alternativa", "Panel web o servidor de pruebas.", PortRisk.Low),
        new(8443, "Web segura alternativa", "Panel web cifrado.", PortRisk.None),
        new(9100, "Impresión directa", "Impresora en red.", PortRisk.None),
        new(27017, "MongoDB", "Base de datos. Sólo debería verse desde el propio equipo.", PortRisk.High),
        new(32400, "Plex", "Servidor multimedia Plex.", PortRisk.Low),
        new(49152, "UPnP", "Servicio de descubrimiento de aparatos.", PortRisk.Low),
        new(62078, "Sincronización de iPhone", "El iPhone o iPad escucha para sincronizarse con un Mac o PC. Normal.", PortRisk.None)
    }.ToDictionary(p => p.Port);

    /// <summary>Lo que se prueba al pedir los puertos abiertos de un aparato.</summary>
    public static readonly int[] DeviceScan = Known.Keys.Where(p => p != 5353 && p != 1900).Order().ToArray();

    public static PortInfo Describe(int port) =>
        Known.TryGetValue(port, out var info)
            ? info
            : new PortInfo(port, $"Puerto {port}", "Un programa acepta conexiones aquí.", port < 1024 ? PortRisk.Low : PortRisk.None);

    public static string RiskName(PortRisk risk) => risk switch
    {
        PortRisk.High => "Riesgo alto",
        PortRisk.Medium => "Revisar",
        PortRisk.Low => "Bajo",
        _ => "Normal"
    };
}
