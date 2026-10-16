using System.Net;

namespace Barep.Services.Net;

/// <summary>Qué es un aparato de la red, hasta donde se puede saber desde fuera.</summary>
public enum DeviceKind
{
    Unknown,
    Router,
    Phone,
    Tablet,
    Computer,
    Tv,
    Console,
    Printer,
    Speaker,
    Camera,
    Watch,
    SmartHome,
    Storage
}

/// <summary>
/// Un aparato encontrado en la red durante un análisis. Se va rellenando por
/// capas (ARP, nombres, mDNS, UPnP, puertos) y al final se identifica.
/// </summary>
public sealed class NetDevice
{
    public NetDevice(IPAddress ip) => Ip = ip;

    public IPAddress Ip { get; }
    public string? Mac { get; set; }

    /// <summary>Dirección privada (aleatoria): la usan los móviles para no ser rastreados entre redes.</summary>
    public bool RandomMac { get; set; }

    public string? Vendor { get; set; }
    public string? VendorFull { get; set; }

    public string? HostName { get; set; }
    public string? NetbiosName { get; set; }
    public string? MdnsHost { get; set; }
    public List<string> MdnsInstances { get; } = new();
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? UpnpName { get; set; }
    public string? UpnpManufacturer { get; set; }
    public string? UpnpModel { get; set; }
    public string? UpnpType { get; set; }
    public string? WebTitle { get; set; }

    public HashSet<int> OpenPorts { get; } = new();
    public int? PingMs { get; set; }

    public bool IsGateway { get; set; }
    public bool IsSelf { get; set; }

    // Resultado de la identificación
    public DeviceKind Kind { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? FriendlyName { get; set; }
    public string? ModelSource { get; set; }
    public bool KindIsGuess { get; set; }

    /// <summary>Clave estable: la MAC. Sin MAC (otra subred, VPN) se usa la IP.</summary>
    public string Key => Mac ?? "ip:" + Ip;

    public string IpText => Ip.ToString();
}

/// <summary>La conexión por la que este PC sale a la red.</summary>
public sealed record LocalLink(
    string Name,
    string Description,
    bool Wireless,
    IPAddress Ip,
    IPAddress Mask,
    int PrefixLength,
    IPAddress? Gateway,
    string? Mac,
    long SpeedBps,
    IReadOnlyList<IPAddress> Dns,
    string? DnsSuffix,
    bool Dhcp);

/// <summary>Datos del Wi-Fi al que está conectado el PC.</summary>
public sealed record WifiInfo(
    string? Ssid,
    string? Bssid,
    int? Signal,
    string? Radio,
    string? Band,
    int? Channel,
    string? Authentication,
    string? Cipher,
    double? ReceiveMbps,
    double? TransmitMbps,
    string? Profile);

/// <summary>Un punto de acceso Wi-Fi que se ve desde aquí.</summary>
public sealed record WifiNetwork(
    string Ssid,
    string? Bssid,
    int Signal,
    string? Authentication,
    string? Cipher,
    string? Radio,
    string? Band,
    int? Channel);

/// <summary>Resultado de pedir a Windows las redes Wi-Fi: puede que falte el permiso de ubicación.</summary>
public sealed record WifiScan(IReadOnlyList<WifiNetwork> Networks, bool LocationBlocked, bool NoWifi);

/// <summary>Dirección pública, operador y ciudad que ve internet.</summary>
public sealed record PublicInfo(string? Ip, string? Isp, string? City, string? Country, string? Colo);

// ────────────────────────── Lo que se guarda en disco ──────────────────────────

/// <summary>Un tramo de tiempo en el que el aparato estuvo conectado.</summary>
public sealed class Presence
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

/// <summary>Un aparato que ya se vio alguna vez en esta red.</summary>
public sealed class KnownDevice
{
    public string Key { get; set; } = "";
    public string? Mac { get; set; }
    public string? Alias { get; set; }
    public bool Trusted { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public string? LastIp { get; set; }
    public string? Name { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public DeviceKind Kind { get; set; }
    public string? Vendor { get; set; }
    public bool RandomMac { get; set; }
    public string? Network { get; set; }
    public List<Presence> Sessions { get; set; } = new();

    /// <summary>Análisis seguidos en los que no apareció: se da por desconectado a partir de dos.</summary>
    public int Misses { get; set; }

    public bool Online { get; set; }
}

public enum NetEventKind { FirstSeen, Joined, Left, GatewayChanged, Duplicate }

/// <summary>Algo que pasó en la red: un aparato nuevo, uno que entra o sale, una alerta.</summary>
public sealed class NetEvent
{
    public DateTime At { get; set; }
    public NetEventKind Kind { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public string? Network { get; set; }
}

/// <summary>Preferencias del módulo y lo que hace falta recordar de cada red.</summary>
public sealed class NetSettings
{
    public bool AutoWatch { get; set; } = true;

    /// <summary>MAC del router de cada red: si cambia sin motivo, alguien puede estar suplantándolo.</summary>
    public Dictionary<string, string> Gateways { get; set; } = new();
}

/// <summary>Un test de velocidad terminado.</summary>
public sealed class SpeedResult
{
    public DateTime At { get; set; }
    public double DownMbps { get; set; }
    public double UpMbps { get; set; }
    public double PingMs { get; set; }
    public double JitterMs { get; set; }
    public string? Server { get; set; }
    public string? Isp { get; set; }
    public string? Network { get; set; }
}
