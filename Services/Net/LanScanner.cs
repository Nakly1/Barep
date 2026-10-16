using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Barep.Services.Net;

/// <summary>Avance de un análisis: en qué fase va y cuánto lleva (0–1).</summary>
public readonly record struct NetScanProgress(string Stage, double Fraction);

/// <summary>
/// Encuentra los aparatos de la red local. Primero pregunta a cada dirección
/// por ARP (lo contestan incluso los móviles que ignoran el ping) y después, en
/// el análisis completo, reúne todo lo que cada aparato dice de sí mismo: su
/// nombre, sus servicios, su ficha UPnP y qué puertos tiene abiertos.
/// </summary>
public static class LanScanner
{
    /// <summary>Puertos que delatan qué es un aparato sin necesidad de más.</summary>
    public static readonly int[] FingerprintPorts =
        { 62078, 5555, 8008, 8009, 9100, 631, 554, 445, 3389, 22, 80, 443, 7000, 1400, 8060, 23 };

    /// <param name="enrich">
    /// En el análisis rápido, a qué aparatos preguntar su nombre (normalmente,
    /// sólo a los que no se conocían). En el completo se pregunta a todos.
    /// </param>
    public static async Task<List<NetDevice>> ScanAsync(LocalLink link, bool deep,
        IProgress<NetScanProgress>? progress = null, Func<NetDevice, bool>? enrich = null, CancellationToken ct = default)
    {
        var hosts = LocalNetwork.Hosts(link);
        progress?.Report(new NetScanProgress("Buscando aparatos conectados…", 0.02));

        // 1. Ping a todo: rápido y además deja la tabla ARP de Windows al día.
        var pings = await PingSweepAsync(hosts, ct);
        progress?.Report(new NetScanProgress("Preguntando a cada dirección quién es…", 0.2));

        // 2. ARP a todo: la única forma fiable de ver los móviles.
        var macs = await Task.Run(() => ArpSweep(hosts, link.Ip, ct), ct);
        progress?.Report(new NetScanProgress("Identificando aparatos…", deep ? 0.45 : 0.85));

        var devices = new Dictionary<string, NetDevice>();
        foreach (var (ip, mac) in macs)
        {
            var device = new NetDevice(ip) { Mac = mac };
            if (pings.TryGetValue(ip, out var ms)) device.PingMs = ms;
            devices[ip.ToString()] = device;
        }
        foreach (var (ip, ms) in pings)
            if (!devices.ContainsKey(ip.ToString()))
                devices[ip.ToString()] = new NetDevice(ip) { PingMs = ms };

        // Este PC siempre está, aunque no se conteste a sí mismo.
        devices[link.Ip.ToString()] = new NetDevice(link.Ip) { Mac = link.Mac, IsSelf = true, PingMs = 0 };

        foreach (var device in devices.Values)
        {
            device.IsGateway = link.Gateway is not null && device.Ip.Equals(link.Gateway);
            device.VendorFull = Oui.Lookup(device.Mac);
            device.RandomMac = Oui.IsRandomized(device.Mac);
        }

        // 3. Lo que cada aparato cuenta de sí mismo.
        var targets = devices.Values
            .Where(d => !d.IsSelf && (deep || enrich?.Invoke(d) == true))
            .ToList();

        if (targets.Count > 0)
        {
            var names = ResolveNamesAsync(targets, ct);
            var netbios = NetbiosAsync(targets, link.Ip, ct);
            var mdns = Mdns.QueryAsync(link.Ip, targets.Select(t => t.Ip).ToList(), TimeSpan.FromSeconds(deep ? 3 : 2), ct);
            var ssdp = deep
                ? Ssdp.DiscoverAsync(link.Ip, TimeSpan.FromSeconds(3), ct)
                : Task.FromResult(new Dictionary<string, SsdpInfo>());
            var ports = deep ? ProbeAsync(targets, FingerprintPorts, 450, ct) : Task.CompletedTask;
            var router = deep && link.Gateway is not null
                ? WebTitleAsync(link.Gateway, ct)
                : Task.FromResult<string?>(null);

            progress?.Report(new NetScanProgress("Leyendo nombres y servicios…", deep ? 0.6 : 0.9));
            await Task.WhenAll(names, netbios, mdns, ssdp, ports, router);

            foreach (var (ip, info) in mdns.Result)
            {
                if (!devices.TryGetValue(ip, out var d)) continue;
                d.MdnsHost ??= info.HostName;
                foreach (var s in info.Services) d.Services.Add(s);
                foreach (var i in info.Instances) if (!d.MdnsInstances.Contains(i)) d.MdnsInstances.Add(i);
                foreach (var (k, v) in info.Txt) d.Txt.TryAdd(k, v);
            }

            foreach (var (ip, info) in ssdp.Result)
            {
                if (!devices.TryGetValue(ip, out var d)) continue;
                d.UpnpName = info.FriendlyName;
                d.UpnpManufacturer = info.Manufacturer;
                d.UpnpModel = JoinModel(info.ModelName, info.ModelNumber);
                d.UpnpType = info.DeviceType;
            }

            if (router.Result is { } title && link.Gateway is not null &&
                devices.TryGetValue(link.Gateway.ToString(), out var gateway))
                gateway.WebTitle = title;
        }

        foreach (var device in devices.Values) DeviceIdentity.Identify(device);
        progress?.Report(new NetScanProgress("Listo", 1));

        return devices.Values
            .OrderByDescending(d => d.IsGateway)
            .ThenByDescending(d => d.IsSelf)
            .ThenBy(d => LocalNetwork.ToUInt(d.Ip))
            .ToList();
    }

    private static string? JoinModel(string? name, string? number)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.IsNullOrWhiteSpace(number) ? null : number.Trim();
        if (string.IsNullOrWhiteSpace(number) || name.Contains(number, StringComparison.OrdinalIgnoreCase))
            return name.Trim();
        return $"{name.Trim()} {number.Trim()}";
    }

    // ────────────────────────── Ping ──────────────────────────

    private static async Task<Dictionary<IPAddress, int>> PingSweepAsync(List<IPAddress> hosts, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<IPAddress, int>();
        using var gate = new SemaphoreSlim(96);

        await Task.WhenAll(hosts.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, 700);
                if (reply.Status == IPStatus.Success) result[ip] = (int)reply.RoundtripTime;
            }
            catch { }
            finally { gate.Release(); }
        }));

        return new Dictionary<IPAddress, int>(result);
    }

    // ────────────────────────── ARP ──────────────────────────

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);

    /// <summary>
    /// SendARP bloquea hasta tres segundos por dirección vacía, así que se
    /// reparte entre muchos hilos propios en vez de ahogar el pool de tareas.
    /// </summary>
    private static Dictionary<IPAddress, string> ArpSweep(List<IPAddress> hosts, IPAddress source, CancellationToken ct)
    {
        var queue = new ConcurrentQueue<IPAddress>(hosts);
        var found = new ConcurrentDictionary<IPAddress, string>();
        var src = BitConverter.ToUInt32(source.GetAddressBytes(), 0);

        var threads = Enumerable.Range(0, Math.Min(96, Math.Max(1, hosts.Count))).Select(_ => new Thread(() =>
        {
            while (!ct.IsCancellationRequested && queue.TryDequeue(out var ip))
            {
                if (ip.Equals(source)) continue;
                try
                {
                    var mac = new byte[6];
                    var length = mac.Length;
                    var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
                    if (SendARP(dest, src, mac, ref length) == 0 && length == 6 &&
                        mac.Any(b => b != 0) && !mac.All(b => b == 0xFF))
                        found[ip] = LocalNetwork.FormatMac(mac);
                }
                catch { }
            }
        }) { IsBackground = true, Name = "Barep ARP" }).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        return new Dictionary<IPAddress, string>(found);
    }

    /// <summary>Comprueba sólo unas direcciones concretas (los aparatos ya conocidos, por ejemplo).</summary>
    public static Task<Dictionary<IPAddress, string>> ArpAsync(List<IPAddress> hosts, IPAddress source,
        CancellationToken ct) => Task.Run(() => ArpSweep(hosts, source, ct), ct);

    // ────────────────────────── Nombres ──────────────────────────

    /// <summary>Nombre por DNS inverso: muchos routers apuntan el nombre que da cada móvil al conectarse.</summary>
    private static async Task ResolveNamesAsync(List<NetDevice> devices, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(24);
        await Task.WhenAll(devices.Select(async d =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var lookup = Dns.GetHostEntryAsync(d.Ip);
                var done = await Task.WhenAny(lookup, Task.Delay(1500, ct));
                if (done == lookup && lookup.Status == TaskStatus.RanToCompletion)
                {
                    var name = CleanHost(lookup.Result.HostName);
                    if (name is not null && name != d.IpText) d.HostName = name;
                }
            }
            catch { }
            finally { gate.Release(); }
        }));
    }

    /// <summary>«Galaxy-S23.lan» → «Galaxy-S23». Descarta lo que sólo es la IP escrita al revés.</summary>
    public static string? CleanHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var name = host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(name, out _)) return null;
        if (Regex.IsMatch(name, @"^\d{1,3}[-.]\d{1,3}[-.]\d{1,3}[-.]\d{1,3}")) return null;

        var dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Petición de estado NetBIOS (puerto 137): la contestan los PC con Windows
    /// y muchos NAS con su nombre de equipo.
    /// </summary>
    private static async Task NetbiosAsync(List<NetDevice> devices, IPAddress local, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(32);
        await Task.WhenAll(devices.Select(async d =>
        {
            await gate.WaitAsync(ct);
            try { d.NetbiosName = await NetbiosNameAsync(d.Ip, local, ct); }
            catch { }
            finally { gate.Release(); }
        }));
    }

    private static readonly byte[] NbstatQuery = BuildNbstat();

    private static byte[] BuildNbstat()
    {
        var packet = new List<byte> { 0x13, 0x37, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 };
        // Nombre «*» relleno con ceros y codificado en mitades de byte («CK» + «AA»×15).
        packet.Add((byte)('A' + ('*' >> 4)));
        packet.Add((byte)('A' + ('*' & 0x0F)));
        for (var i = 0; i < 15; i++) { packet.Add((byte)'A'); packet.Add((byte)'A'); }
        packet.AddRange(new byte[] { 0x00, 0x00, 0x21, 0x00, 0x01 });
        return packet.ToArray();
    }

    private static async Task<string?> NetbiosNameAsync(IPAddress ip, IPAddress local, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(local, 0));
        await udp.SendAsync(NbstatQuery, NbstatQuery.Length, new IPEndPoint(ip, 137));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(900);
        UdpReceiveResult result;
        try { result = await udp.ReceiveAsync(timeout.Token); }
        catch { return null; }

        var data = result.Buffer;
        // Cabecera (12) + nombre (34) + tipo, clase, TTL, longitud (10) + número de nombres (1).
        const int countOffset = 56;
        if (data.Length <= countOffset) return null;
        int count = data[countOffset];
        for (var i = 0; i < count; i++)
        {
            var offset = countOffset + 1 + i * 18;
            if (offset + 18 > data.Length) break;
            var suffix = data[offset + 15];
            var flags = (data[offset + 16] << 8) | data[offset + 17];
            var isGroup = (flags & 0x8000) != 0;
            if (suffix != 0x00 || isGroup) continue;
            var name = Encoding.ASCII.GetString(data, offset, 15).Trim().Trim('\0');
            if (name.Length > 0 && name.All(c => c >= 0x20 && c < 0x7F)) return name;
        }
        return null;
    }

    // ────────────────────────── Puertos ──────────────────────────

    /// <summary>Intenta conectar a cada puerto: si acepta, hay algo escuchando.</summary>
    public static async Task ProbeAsync(IEnumerable<NetDevice> devices, IReadOnlyCollection<int> ports, int timeoutMs,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(160);
        var jobs = devices.SelectMany(d => ports.Select(p => (Device: d, Port: p))).ToList();

        await Task.WhenAll(jobs.Select(async job =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await IsOpenAsync(job.Device.Ip, job.Port, timeoutMs, ct))
                    lock (job.Device.OpenPorts) job.Device.OpenPorts.Add(job.Port);
            }
            finally { gate.Release(); }
        }));
    }

    public static async Task<bool> IsOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(ip, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    // ────────────────────────── Página del router ──────────────────────────

    private static readonly HttpClient Lan = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        // Los routers usan certificados hechos por ellos mismos. Este cliente
        // sólo lee el título de la página del router de la red local: nunca
        // envía datos ni se usa para nada de internet.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
        MaxResponseContentBufferSize = 256 * 1024
    };

    /// <summary>
    /// El título de la página de acceso del router y su «realm»: casi todos
    /// ponen ahí la marca y el modelo. Sólo se lee, nunca se envía nada.
    /// </summary>
    public static async Task<string?> WebTitleAsync(IPAddress router, CancellationToken ct)
    {
        foreach (var scheme in new[] { "http", "https" })
        {
            try
            {
                using var response = await Lan.GetAsync($"{scheme}://{router}/", HttpCompletionOption.ResponseHeadersRead, ct);
                var realm = response.Headers.WwwAuthenticate.Select(h => h.Parameter).FirstOrDefault(p => p is not null);
                if (realm is not null)
                {
                    var match = Regex.Match(realm, "realm=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (match.Success) return Tidy(match.Groups[1].Value);
                }

                var html = await ReadLimitedAsync(response, 64 * 1024, ct);
                var title = Regex.Match(html, @"<title[^>]*>\s*([^<]{2,80}?)\s*</title>", RegexOptions.IgnoreCase);
                if (title.Success) return Tidy(WebUtility.HtmlDecode(title.Groups[1].Value));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }

    private static string? Tidy(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length < 2) return null;
        // Títulos que no dicen nada.
        if (Regex.IsMatch(text, @"^(login|log in|iniciar sesi.n|index|home|web ?ui|router|loading|redirect\w*)$",
                RegexOptions.IgnoreCase)) return null;
        return text;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[limit];
        var total = 0;
        int read;
        while (total < limit && (read = await stream.ReadAsync(buffer.AsMemory(total, limit - total), ct)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
