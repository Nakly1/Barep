using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Barep.Services.Net;

/// <summary>
/// La conexión de este PC: por qué adaptador sale, su IP, el router, los DNS,
/// el Wi-Fi al que está unido y las redes que tiene alrededor.
/// </summary>
public static class LocalNetwork
{
    /// <summary>Adaptadores que no son la red de casa: máquinas virtuales, VPN y túneles.</summary>
    private static readonly string[] Virtual =
    {
        "hyper-v", "vethernet", "virtualbox", "vmware", "tap-", "tap ", "tailscale", "wireguard", "zerotier",
        "vpn", "loopback", "bluetooth", "wsl", "docker", "npcap", "pseudo"
    };

    // ────────────────────────── Adaptador ──────────────────────────

    public static LocalLink? Current()
    {
        LocalLink? best = null;
        var bestScore = int.MinValue;

        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return null; }

        foreach (var nic in adapters)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                var unicast = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(u.Address) &&
                                         !u.Address.ToString().StartsWith("169.254."));
                if (unicast is null) continue;

                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var isVirtual = Virtual.Any(v => text.Contains(v));
                var wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

                // Gana el adaptador físico con router: es por donde sale el tráfico de verdad.
                var score = (gateway is not null ? 100 : 0) + (isVirtual ? -200 : 0) +
                            (wireless || nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 20 : 0) +
                            (IsPrivate(unicast.Address) ? 10 : 0);
                if (score <= bestScore) continue;

                var mask = unicast.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                var prefix = unicast.PrefixLength > 0 && unicast.PrefixLength <= 32
                    ? unicast.PrefixLength
                    : PrefixFromMask(mask);

                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                var dhcp = false;
                try { dhcp = props.GetIPv4Properties()?.IsDhcpEnabled == true; } catch { }

                best = new LocalLink(
                    Name: nic.Name,
                    Description: nic.Description,
                    Wireless: wireless,
                    Ip: unicast.Address,
                    Mask: mask,
                    PrefixLength: prefix,
                    Gateway: gateway,
                    Mac: mac.Length == 6 ? FormatMac(mac) : null,
                    SpeedBps: nic.Speed,
                    Dns: props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList(),
                    DnsSuffix: string.IsNullOrWhiteSpace(props.DnsSuffix) ? null : props.DnsSuffix,
                    Dhcp: dhcp);
                bestScore = score;
            }
            catch { }
        }

        return best;
    }

    /// <summary>Las direcciones de la subred a recorrer. Si es enorme, sólo la /24 del propio PC.</summary>
    public static List<IPAddress> Hosts(LocalLink link)
    {
        var prefix = link.PrefixLength is >= 22 and <= 30 ? link.PrefixLength : 24;
        var ip = ToUInt(link.Ip);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ip & mask;
        var broadcast = network | ~mask;

        var list = new List<IPAddress>();
        for (var a = network + 1; a < broadcast; a++) list.Add(FromUInt(a));
        return list;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress FromUInt(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    private static int PrefixFromMask(IPAddress mask)
    {
        var bits = 0;
        foreach (var b in mask.GetAddressBytes())
            for (var i = 7; i >= 0; i--)
                if ((b & (1 << i)) != 0) bits++;
        return bits;
    }

    public static string FormatMac(byte[] mac) =>
        string.Join(":", mac.Select(b => b.ToString("X2")));

    // ────────────────────────── Wi-Fi ──────────────────────────

    /// <summary>
    /// Datos del Wi-Fi actual con netsh. Se pide la salida en UTF-8 para que las
    /// tildes de Windows en español no lleguen rotas, y se aceptan las claves en
    /// inglés y en español.
    /// </summary>
    public static (WifiInfo? Info, bool LocationBlocked) Wifi()
    {
        var output = Netsh("wlan show interfaces");
        if (output is null) return (null, false);
        if (NeedsLocation(output)) return (null, true);

        var map = new Dictionary<string, string>();
        foreach (var (key, value) in Pairs(output))
            map.TryAdd(key, value);

        string? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (map.TryGetValue(k, out var v) && v.Length > 0) return v;
            return null;
        }

        var state = Get("state", "estado");
        var ssid = Get("ssid");
        if (ssid is null) return (null, false);
        if (state is not null && (state.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
                                  state.Contains("desconect", StringComparison.OrdinalIgnoreCase)))
            return (null, false);

        return (new WifiInfo(
            Ssid: ssid,
            Bssid: Get("ap bssid", "bssid"),
            Signal: Percent(Get("signal", "senal")),
            Radio: Get("radio type", "tipo de radio"),
            Band: Get("band", "banda"),
            Channel: Int(Get("channel", "canal")),
            Authentication: Get("authentication", "autenticacion"),
            Cipher: Get("cipher", "cifrado"),
            ReceiveMbps: Number(Get("receive rate (mbps)", "velocidad de recepcion (mbps)")),
            TransmitMbps: Number(Get("transmit rate (mbps)", "velocidad de transmision (mbps)")),
            Profile: Get("profile", "perfil")), false);
    }

    /// <summary>Redes Wi-Fi alrededor, una entrada por punto de acceso.</summary>
    public static WifiScan Nearby()
    {
        var output = Netsh("wlan show networks mode=bssid");
        if (output is null) return new WifiScan(Array.Empty<WifiNetwork>(), false, true);
        if (NeedsLocation(output)) return new WifiScan(Array.Empty<WifiNetwork>(), true, false);
        if (output.Contains("wlansvc", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("no hay ninguna interfaz", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("there is no wireless", StringComparison.OrdinalIgnoreCase))
            return new WifiScan(Array.Empty<WifiNetwork>(), false, true);

        var list = new List<WifiNetwork>();
        string ssid = "";
        string? auth = null, cipher = null;
        string? bssid = null, radio = null, band = null;
        int signal = 0;
        int? channel = null;

        void Flush()
        {
            if (bssid is null) return;
            list.Add(new WifiNetwork(ssid.Length == 0 ? "(red oculta)" : ssid, bssid, signal, auth, cipher, radio,
                band, channel));
            bssid = null; radio = null; band = null; signal = 0; channel = null;
        }

        foreach (var (key, value) in Pairs(output))
        {
            if (key.StartsWith("ssid ") && !key.StartsWith("ssid name"))
            {
                Flush();
                ssid = value;
                auth = null; cipher = null;
            }
            else if (key is "authentication" or "autenticacion") auth = value;
            else if (key is "encryption" or "cipher" or "cifrado") cipher = value;
            else if (key.StartsWith("bssid "))
            {
                Flush();
                bssid = value;
            }
            else if (key is "signal" or "senal") signal = Percent(value) ?? 0;
            else if (key is "radio type" or "tipo de radio") radio = value;
            else if (key is "band" or "banda") band = value;
            else if (key is "channel" or "canal") channel = Int(value);
        }
        Flush();

        list.Sort((a, b) => b.Signal.CompareTo(a.Signal));
        return new WifiScan(list, false, false);
    }

    private static bool NeedsLocation(string output) =>
        output.Contains("location", StringComparison.OrdinalIgnoreCase) &&
        output.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("ubicaci", StringComparison.OrdinalIgnoreCase) &&
        output.Contains("permiso", StringComparison.OrdinalIgnoreCase);

    private static string? Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/d /c chcp 65001 >nul & netsh {arguments}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            if (process is null) return null;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            return text;
        }
        catch { return null; }
    }

    /// <summary>Líneas «Clave : valor» con la clave sin tildes y en minúsculas.</summary>
    private static IEnumerable<(string Key, string Value)> Pairs(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0) colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = Fold(line[..colon].Trim());
            var value = line[(colon + 1)..].Trim().TrimStart(':').Trim();
            if (key.Length == 0) continue;
            yield return (key, value);
        }
    }

    /// <summary>«Señal» → «senal», «Autenticación» → «autenticacion».</summary>
    private static string Fold(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int? Percent(string? value) =>
        value is not null && int.TryParse(value.TrimEnd('%', ' '), out var p) ? p : null;

    private static int? Int(string? value) =>
        value is not null && int.TryParse(value.Split(' ')[0], out var p) ? p : null;

    private static double? Number(string? value) =>
        value is not null && double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var d)
            ? d
            : null;

    /// <summary>Wi-Fi 4/5/6/7 a partir del estándar 802.11.</summary>
    public static string? WifiGeneration(string? radio)
    {
        if (radio is null) return null;
        var r = radio.ToLowerInvariant();
        if (r.Contains("802.11be")) return "Wi-Fi 7";
        if (r.Contains("802.11ax")) return "Wi-Fi 6";
        if (r.Contains("802.11ac")) return "Wi-Fi 5";
        if (r.Contains("802.11n")) return "Wi-Fi 4";
        if (r.Contains("802.11g") || r.Contains("802.11a") || r.Contains("802.11b")) return "Wi-Fi antiguo";
        return null;
    }

    /// <summary>
    /// Windows da la señal en porcentaje; el equivalente aproximado en dBm es
    /// lo que se ve en los foros y en las apps de móvil.
    /// </summary>
    public static int SignalDbm(int percent) => Math.Clamp(percent / 2 - 100, -100, -50);

    // ────────────────────────── IP pública ──────────────────────────

    private static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>
    /// La IP pública y el operador, según el servidor de Cloudflare más cercano.
    /// Es la misma petición que hace su test de velocidad.
    /// </summary>
    public static async Task<PublicInfo?> PublicAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Web.GetAsync("https://speed.cloudflare.com/meta", ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;
                string? S(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : root.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number
                        ? n.ToString()
                        : null;
                return new PublicInfo(S("clientIp"), S("asOrganization"), S("city"), S("country"), S("colo"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        try
        {
            var trace = await Web.GetStringAsync("https://speed.cloudflare.com/cdn-cgi/trace", ct);
            string? Field(string name) => trace.Split('\n')
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2 && p[0] == name)
                .Select(p => p[1].Trim())
                .FirstOrDefault();
            return new PublicInfo(Field("ip"), null, null, Field("loc"), Field("colo"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
