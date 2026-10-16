using System.IO;
using System.Net;
using Microsoft.Win32;

namespace Barep.Services.Net;

public enum CheckState { Good, Info, Warn, Bad }

/// <summary>
/// Una comprobación de seguridad. <paramref name="Action"/> dice qué ofrece el
/// botón: «settings:ms-settings:…» abre Configuración, «router» abre el panel
/// del router y «devices» lleva a la lista de aparatos.
/// </summary>
public sealed record SecurityCheck(string Title, string Detail, CheckState State, string? ActionText = null,
    string? Action = null);

/// <summary>
/// Revisión de seguridad de la red y del PC en ella: cifrado del Wi-Fi,
/// cortafuegos, puertos expuestos, protocolos viejos, proxy y archivo hosts,
/// DNS, el router y los aparatos de la red. Sólo lee; no cambia nada.
/// </summary>
public static class SecurityAudit
{
    private static readonly Dictionary<string, string> PublicDns = new()
    {
        ["1.1.1.1"] = "Cloudflare", ["1.0.0.1"] = "Cloudflare", ["8.8.8.8"] = "Google", ["8.8.4.4"] = "Google",
        ["9.9.9.9"] = "Quad9", ["149.112.112.112"] = "Quad9", ["208.67.222.222"] = "OpenDNS",
        ["208.67.220.220"] = "OpenDNS", ["94.140.14.14"] = "AdGuard", ["94.140.15.15"] = "AdGuard",
        ["76.76.2.0"] = "Control D", ["185.228.168.9"] = "CleanBrowsing"
    };

    public static List<SecurityCheck> Run(NetworkMonitor monitor)
    {
        var checks = new List<SecurityCheck>();
        void Add(SecurityCheck? check) { if (check is not null) checks.Add(check); }

        Add(Wifi(monitor.Link, monitor.Wifi));
        Add(Profile(monitor.Wifi));
        Add(Firewall());
        Add(Spoofing(monitor));
        Add(Router(monitor));
        Add(Strangers(monitor));
        Add(RiskyDevices(monitor));
        foreach (var check in ExposedPorts()) checks.Add(check);
        Add(RemoteDesktop());
        Add(Smb1());
        Add(Proxy());
        Add(Hosts());
        Add(Dns(monitor.Link));

        // Primero lo grave.
        return checks.OrderByDescending(c => c.State).ToList();
    }

    // ────────────────────────── Wi-Fi ──────────────────────────

    private static SecurityCheck? Wifi(LocalLink? link, WifiInfo? wifi)
    {
        if (link is null) return null;
        if (!link.Wireless)
            return new("Conectado por cable", "Nadie puede espiar el cable desde fuera de casa: es la conexión más segura.",
                CheckState.Good);
        if (wifi?.Authentication is not { } auth)
            return new("Cifrado del Wi-Fi sin comprobar",
                "Windows no deja leer los datos del Wi-Fi. Activa el permiso de ubicación para las apps de escritorio.",
                CheckState.Info, "Abrir permisos", "settings:ms-settings:privacy-location");

        var a = auth.ToLowerInvariant();
        var cipher = wifi.Cipher?.ToLowerInvariant() ?? "";
        if (a.Contains("open") || a.Contains("abierta") || cipher is "none" or "ninguno")
            return new("Tu Wi-Fi no tiene contraseña",
                "Cualquiera cerca puede conectarse y ver el tráfico que no vaya cifrado. Ponle WPA2 o WPA3 desde el router.",
                CheckState.Bad, "Abrir el router", "router");
        if (a.Contains("wep") || cipher.Contains("wep"))
            return new("Cifrado WEP: se rompe en minutos",
                "WEP está roto desde hace años. Cambia el router a WPA2 o WPA3.", CheckState.Bad, "Abrir el router", "router");
        if (cipher.Contains("tkip") || (a.Contains("wpa-") && !a.Contains("wpa2") && !a.Contains("wpa3")))
            return new("Cifrado WPA antiguo",
                "WPA con TKIP es débil y además frena el Wi-Fi. En el router, elige WPA2-AES (o WPA3).",
                CheckState.Warn, "Abrir el router", "router");
        if (a.Contains("wpa3"))
            return new("Wi-Fi con WPA3", "El cifrado más moderno. Nada que hacer.", CheckState.Good);
        return new("Wi-Fi con WPA2", "Cifrado seguro siempre que la contraseña sea larga y no sea la que venía de fábrica.",
            CheckState.Good);
    }

    /// <summary>Red privada o pública para Windows: decide si otros aparatos pueden ver este PC.</summary>
    private static SecurityCheck? Profile(WifiInfo? wifi)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
            if (root is null) return null;

            int? category = null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var profile = key?.GetValue("ProfileName") as string;
                if (wifi?.Ssid is not null && profile == wifi.Ssid || wifi?.Profile is not null && profile == wifi.Profile)
                {
                    category = key?.GetValue("Category") as int?;
                    break;
                }
            }
            if (category is null) return null;

            return category == 0
                ? new SecurityCheck("Red marcada como pública",
                    "Este PC no se deja ver por otros aparatos. Es lo recomendable fuera de casa.", CheckState.Good)
                : new SecurityCheck("Red marcada como privada",
                    "Otros aparatos de la red pueden ver este PC y sus carpetas compartidas. Bien en casa; si es un café, " +
                    "una universidad o un hotel, cámbiala a pública.",
                    CheckState.Info, "Cambiar", "settings:ms-settings:network-status");
        }
        catch { return null; }
    }

    // ────────────────────────── Cortafuegos ──────────────────────────

    private static SecurityCheck Firewall()
    {
        var off = new List<string>();
        foreach (var (key, name) in new[] { ("DomainProfile", "dominio"), ("StandardProfile", "privada"), ("PublicProfile", "pública") })
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}");
                if (reg?.GetValue("EnableFirewall") is int enabled && enabled == 0) off.Add(name);
            }
            catch { }
        }

        return off.Count == 0
            ? new("Cortafuegos de Windows activo", "Bloquea las conexiones que nadie ha pedido.", CheckState.Good)
            : new("Cortafuegos apagado", $"Está desactivado para la red {string.Join(", ", off)}. Si no usas el de un antivirus, enciéndelo.",
                off.Contains("pública") ? CheckState.Bad : CheckState.Warn, "Abrir seguridad", "settings:windowsdefender://network/");
    }

    // ────────────────────────── Router y aparatos ──────────────────────────

    private static SecurityCheck? Spoofing(NetworkMonitor monitor)
    {
        if (monitor.NetworkId is null) return null;
        var since = DateTime.Now.AddDays(-1);
        var alert = monitor.Store.EventsIn(monitor.NetworkId)
            .FirstOrDefault(e => e.At >= since && e.Kind is NetEventKind.Duplicate or NetEventKind.GatewayChanged);

        return alert is null
            ? new("El router es el de siempre", "Nadie está contestando en su nombre (sin señales de ARP spoofing).", CheckState.Good)
            : new(alert.Name, alert.Detail ?? "", CheckState.Bad, "Ver actividad", "activity");
    }

    private static SecurityCheck? Router(NetworkMonitor monitor)
    {
        var router = monitor.Devices.FirstOrDefault(d => d.IsGateway);
        if (router is null) return null;
        if (!monitor.LastScanWasDeep)
            return new("Router sin revisar", "Pulsa «Analizar la red» para comprobar qué tiene abierto el router.", CheckState.Info);

        var problems = new List<string>();
        var state = CheckState.Good;
        if (router.OpenPorts.Contains(23)) { problems.Add("tiene Telnet abierto, un acceso remoto sin cifrar"); state = CheckState.Bad; }
        if (router.OpenPorts.Contains(80) && !router.OpenPorts.Contains(443))
        {
            problems.Add("su panel sólo funciona sin cifrar (http)");
            if (state < CheckState.Warn) state = CheckState.Warn;
        }
        if (router.UpnpType?.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) == true)
        {
            problems.Add("tiene UPnP activo: los programas pueden abrir puertos hacia internet sin preguntar");
            if (state < CheckState.Info) state = CheckState.Info;
        }

        return problems.Count == 0
            ? new("Router sin puertas raras", "No tiene Telnet ni UPnP a la vista y su panel admite cifrado.", CheckState.Good)
            : new("Revisa el router", "El router " + string.Join("; ", problems) + ".", state, "Abrir el router", "router");
    }

    private static SecurityCheck? Strangers(NetworkMonitor monitor)
    {
        if (monitor.NetworkId is null || monitor.Devices.Count == 0) return null;
        var strangers = monitor.Devices
            .Where(d => !d.IsSelf && !d.IsGateway)
            .Select(d => (Device: d, Known: monitor.Known(d)))
            .Where(x => x.Known is { Trusted: false })
            .ToList();

        return strangers.Count == 0
            ? new("Todos los aparatos son conocidos", "Has marcado como tuyos todo lo que está conectado.", CheckState.Good)
            : new($"{UiText.Plural(strangers.Count, "aparato sin reconocer", "aparatos sin reconocer")}",
                "Revisa la lista y marca como «Es mío» lo que sea de casa. Lo que quede es lo que hay que vigilar.",
                strangers.Count > 6 ? CheckState.Warn : CheckState.Info, "Ver aparatos", "devices");
    }

    private static SecurityCheck? RiskyDevices(NetworkMonitor monitor)
    {
        var risky = monitor.Devices
            .Where(d => !d.IsSelf && (d.OpenPorts.Contains(5555) || d.OpenPorts.Contains(23) ||
                                      d.Services.Contains("_adb-tls-connect._tcp")))
            .ToList();
        if (risky.Count == 0) return null;

        var names = risky.Select(d => $"{DeviceIdentity.DisplayName(d, monitor.Known(d)?.Alias)} ({d.IpText})");
        return new("Aparatos con acceso remoto abierto",
            $"{string.Join(", ", names)} tiene abierta la depuración Android o Telnet: cualquiera en la red podría manejarlo.",
            CheckState.Warn, "Ver aparatos", "devices");
    }

    // ────────────────────────── Este PC ──────────────────────────

    private static IEnumerable<SecurityCheck> ExposedPorts()
    {
        List<Listener> listeners;
        try { listeners = Connections.Listeners(); }
        catch { yield break; }

        var exposed = listeners.Where(l => l.Exposed && !l.Udp).ToList();
        var dangerous = exposed.Where(l => Ports.Describe(l.Port).Risk == PortRisk.High).ToList();

        foreach (var l in dangerous.GroupBy(l => l.Port).Select(g => g.First()))
        {
            var info = Ports.Describe(l.Port);
            yield return new SecurityCheck($"Este PC acepta {info.Name} desde la red",
                $"{l.Process} escucha en el puerto {l.Port}. {info.What}", CheckState.Warn, "Ver conexiones", "connections");
        }

        if (dangerous.Count == 0)
            yield return new SecurityCheck("Sin puertas peligrosas en este PC",
                $"{UiText.Plural(exposed.Select(l => l.Port).Distinct().Count(), "puerto escucha", "puertos escuchan")} hacia la red, " +
                "ninguno de los que suelen atacarse.", CheckState.Good, "Ver conexiones", "connections");
    }

    private static SecurityCheck? RemoteDesktop()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            if (key?.GetValue("fDenyTSConnections") is int deny && deny == 0)
                return new("Escritorio remoto activado",
                    "Se puede manejar este PC desde otro equipo. Si no lo usas, apágalo: es de lo más atacado.",
                    CheckState.Warn, "Desactivar", "settings:ms-settings:remotedesktop");
        }
        catch { }
        return null;
    }

    private static SecurityCheck Smb1()
    {
        var on = false;
        try
        {
            using var server = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
            if (server?.GetValue("SMB1") is int smb1 && smb1 == 1) on = true;
            using var client = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mrxsmb10");
            if (client?.GetValue("Start") is int start && start != 4) on = true;
        }
        catch { }

        return on
            ? new("SMB1 activado", "Es el protocolo por el que se extendió WannaCry. Quítalo en «Características de Windows».",
                CheckState.Bad, "Abrir", "run:optionalfeatures.exe")
            : new("SMB1 desactivado", "El protocolo viejo de compartir archivos está apagado.", CheckState.Good);
    }

    private static SecurityCheck Proxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable") is int e && e == 1;
            var server = key?.GetValue("ProxyServer") as string;
            var script = key?.GetValue("AutoConfigURL") as string;

            if (enabled || !string.IsNullOrWhiteSpace(script))
                return new("El tráfico web pasa por un proxy",
                    $"Configurado: {(enabled ? server : script)}. Si no lo pusiste tú (o tu empresa), puede ser un programa " +
                    "espiando lo que navegas.", CheckState.Warn, "Revisar", "settings:ms-settings:network-proxy");
        }
        catch { }
        return new("Sin proxy", "Tu navegación va directa, sin intermediarios configurados.", CheckState.Good);
    }

    /// <summary>
    /// El archivo hosts manda sobre el DNS. Docker y los entornos de desarrollo
    /// añaden líneas propias (normales); redirigir páginas conocidas no lo es.
    /// </summary>
    private static SecurityCheck? Hosts()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
            if (!File.Exists(path)) return null;

            var entries = File.ReadLines(path)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2)
                .SelectMany(p => p.Skip(1).Select(host => (Ip: p[0], Host: host.ToLowerInvariant())))
                .Where(e => e.Host != "localhost" && !e.Host.EndsWith(".docker.internal") &&
                            !e.Host.EndsWith(".localhost") && !e.Host.EndsWith(".test") && !e.Host.EndsWith(".local"))
                .ToList();

            var sensitive = entries.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.Host,
                @"google|facebook|microsoft|windowsupdate|apple|paypal|banco|bank|bancolombia|davivienda|nequi|whatsapp|instagram|outlook|live\.com|youtube"))
                .ToList();

            if (sensitive.Count > 0)
                return new("El archivo hosts redirige páginas conocidas",
                    $"{string.Join(", ", sensitive.Take(4).Select(s => s.Host))} apunta a {sensitive[0].Ip}. " +
                    "Si no lo hiciste tú (por ejemplo para bloquear anuncios), es una técnica típica de malware.",
                    CheckState.Bad, "Abrir hosts", "file:" + path);
            if (entries.Count > 0)
                return new("Archivo hosts con entradas propias",
                    $"{UiText.Plural(entries.Count, "nombre redirigido", "nombres redirigidos")}, ninguno de páginas conocidas.",
                    CheckState.Info, "Abrir hosts", "file:" + path);
            return new("Archivo hosts limpio", "Ninguna página está redirigida a mano.", CheckState.Good);
        }
        catch { return null; }
    }

    private static SecurityCheck? Dns(LocalLink? link)
    {
        if (link is null || link.Dns.Count == 0) return null;
        var parts = new List<string>();
        var unknown = new List<string>();

        foreach (var dns in link.Dns)
        {
            var text = dns.ToString();
            if (PublicDns.TryGetValue(text, out var provider)) parts.Add($"{provider} ({text})");
            else if (link.Gateway is not null && dns.Equals(link.Gateway)) parts.Add($"el router ({text})");
            else if (LocalNetwork.IsPrivate(dns) || IPAddress.IsLoopback(dns)) parts.Add($"un servidor de la red ({text})");
            else unknown.Add(text);
        }

        if (unknown.Count > 0)
            return new("DNS que no se reconoce",
                $"Este PC traduce las direcciones con {string.Join(", ", unknown)}. Puede ser el de tu operador; si no lo " +
                "configuraste tú, un malware podría usarlo para mandarte a páginas falsas.",
                CheckState.Warn, "Revisar", "settings:ms-settings:network-status");

        return new("DNS de confianza", "Las direcciones se traducen con " + string.Join(" y ", parts) + ".", CheckState.Good);
    }

    public static int Score(IReadOnlyCollection<SecurityCheck> checks)
    {
        var counted = checks.Where(c => c.State != CheckState.Info).ToList();
        return counted.Count == 0 ? 100 : (int)Math.Round(counted.Count(c => c.State == CheckState.Good) * 100.0 / counted.Count);
    }
}
