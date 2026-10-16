using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Barep.Services.Net;

/// <summary>Lo que un aparato anuncia por Bonjour / mDNS.</summary>
public sealed class MdnsInfo
{
    public string? HostName { get; set; }
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Instances { get; } = new();
    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// mDNS (Bonjour): iPhone, Mac, Chromecast, impresoras, altavoces y casi todo
/// lo «inteligente» de casa responden aquí con su nombre («iPhone de Ana»),
/// los servicios que ofrecen y, a veces, su modelo exacto. Todo lo que llega se
/// trata como dato no fiable: se comprueban los límites de cada lectura.
/// </summary>
public static class Mdns
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Servicios que más dicen de un aparato.</summary>
    public static readonly string[] ServiceTypes =
    {
        "_device-info._tcp", "_companion-link._tcp", "_apple-mobdev2._tcp", "_airplay._tcp", "_raop._tcp",
        "_sleep-proxy._udp", "_googlecast._tcp", "_androidtvremote2._tcp", "_amzn-wplay._tcp",
        "_spotify-connect._tcp", "_sonos._tcp", "_ipp._tcp", "_ipps._tcp", "_printer._tcp", "_pdl-datastream._tcp",
        "_scanner._tcp", "_smb._tcp", "_workstation._tcp", "_rdlink._tcp", "_ssh._tcp", "_hap._tcp",
        "_homekit._tcp", "_matter._tcp", "_hue._tcp", "_miio._udp", "_esphomelib._tcp", "_adb-tls-connect._tcp",
        "_adb-tls-pairing._tcp", "_http._tcp", "_nvstream._tcp", "_mediaremotetv._tcp", "_daap._tcp"
    };

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    public static async Task<Dictionary<string, MdnsInfo>> QueryAsync(IPAddress local, IReadOnlyList<IPAddress> hosts,
        TimeSpan listen, CancellationToken ct)
    {
        var result = new Dictionary<string, MdnsInfo>();
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

            // Preguntas con el bit «QU»: que contesten directamente a este puerto.
            var browse = Build(ServiceTypes.Select(t => t + ".local"), TypePtr, unicastResponse: true);
            await udp.SendAsync(browse, browse.Length, Group);

            var reverse = hosts.Select(ReverseName).ToList();
            foreach (var chunk in reverse.Chunk(12))
            {
                var packet = Build(chunk, TypePtr, unicastResponse: true);
                await udp.SendAsync(packet, packet.Length, Group);
            }

            // Y además, directamente a cada aparato: hay quien sólo responde así.
            foreach (var ip in hosts)
            {
                var packet = Build(new[] { ReverseName(ip) }, TypePtr, unicastResponse: false, id: 0x4241);
                try { await udp.SendAsync(packet, packet.Length, new IPEndPoint(ip, 5353)); } catch { }
            }

            // Segunda ronda del navegador de servicios: los que dormían ya despertaron.
            await Task.Delay(400, ct);
            await udp.SendAsync(browse, browse.Length, Group);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(listen);
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                try { Parse(received.Buffer, received.RemoteEndPoint.Address, result); }
                catch { /* paquete mal formado: se ignora */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        finally { udp?.Dispose(); }

        return result;
    }

    private static string ReverseName(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
    }

    private static byte[] Build(IEnumerable<string> names, ushort type, bool unicastResponse, ushort id = 0)
    {
        var list = names.ToList();
        var packet = new List<byte>(12 + list.Count * 40)
        {
            (byte)(id >> 8), (byte)id, 0, 0,
            (byte)(list.Count >> 8), (byte)list.Count, 0, 0, 0, 0, 0, 0
        };

        foreach (var name in list)
        {
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                packet.Add((byte)Math.Min(63, bytes.Length));
                packet.AddRange(bytes.Take(63));
            }
            packet.Add(0);
            packet.Add((byte)(type >> 8));
            packet.Add((byte)type);
            var cls = (ushort)(unicastResponse ? 0x8001 : 0x0001);
            packet.Add((byte)(cls >> 8));
            packet.Add((byte)cls);
        }

        return packet.ToArray();
    }

    // ────────────────────────── Lectura de respuestas ──────────────────────────

    private static void Parse(byte[] data, IPAddress from, Dictionary<string, MdnsInfo> result)
    {
        if (data.Length < 12) return;
        var flags = U16(data, 2);
        if ((flags & 0x8000) == 0) return; // es una pregunta, no una respuesta

        int questions = U16(data, 4);
        int records = U16(data, 6) + U16(data, 8) + U16(data, 10);
        if (records > 400) return;

        var pos = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(data, ref pos);
            pos += 4;
            if (pos > data.Length) return;
        }

        var ptrs = new List<(List<string> Owner, List<string> Target)>();
        var srvs = new List<(string Instance, string Host)>();
        var txts = new List<(string Instance, Dictionary<string, string> Values)>();
        var hostIps = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records; i++)
        {
            var owner = ReadName(data, ref pos);
            if (pos + 10 > data.Length) break;
            var type = U16(data, pos);
            var length = U16(data, pos + 8);
            var start = pos + 10;
            if (start + length > data.Length) break;

            switch (type)
            {
                case TypePtr:
                {
                    var p = start;
                    ptrs.Add((owner, ReadName(data, ref p)));
                    break;
                }
                case TypeSrv when length > 6:
                {
                    var p = start + 6;
                    srvs.Add((Join(owner), Join(ReadName(data, ref p))));
                    break;
                }
                case TypeTxt:
                    txts.Add((Join(owner), ReadTxt(data, start, length)));
                    break;
                case TypeA when length == 4:
                    hostIps[Join(owner)] = new IPAddress(data.AsSpan(start, 4));
                    break;
            }

            pos = start + length;
        }

        // Un Apple TV puede contestar por un Mac dormido («sleep proxy»): lo
        // que anuncia se atribuye a la IP del aparato dueño, no al que habla.
        var instanceHosts = srvs
            .GroupBy(s => s.Instance, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Host, StringComparer.OrdinalIgnoreCase);

        IPAddress Owner(string instance) =>
            instanceHosts.TryGetValue(instance, out var host) && hostIps.TryGetValue(host, out var ip) ? ip : from;

        MdnsInfo Info(IPAddress ip)
        {
            var key = ip.ToString();
            if (!result.TryGetValue(key, out var info)) result[key] = info = new MdnsInfo();
            return info;
        }

        foreach (var (owner, target) in ptrs)
        {
            var ownerName = Join(owner);
            if (ownerName.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReverse(owner, out var ip) && target.Count > 0)
                    Info(ip).HostName = StripLocal(Join(target));
                continue;
            }

            // «_googlecast._tcp.local» → «Salón TV._googlecast._tcp.local»
            if (owner.Count < 3 || !owner[0].StartsWith('_') || target.Count < 4) continue;
            if (owner[1].Equals("_sub", StringComparison.OrdinalIgnoreCase)) continue;
            var service = $"{owner[0]}.{owner[1]}";
            var instanceName = Join(target);
            var info = Info(Owner(instanceName));
            info.Services.Add(service);
            var label = target[0].Trim();
            if (label.Length > 0 && label.Length <= 80 && !info.Instances.Contains(label)) info.Instances.Add(label);
        }

        foreach (var (instance, host) in srvs)
            Info(Owner(instance)).HostName ??= StripLocal(host);

        foreach (var (instance, values) in txts)
        {
            var info = Info(Owner(instance));
            foreach (var (k, v) in values)
                if (info.Txt.Count < 40) info.Txt.TryAdd(k, v);
        }

        foreach (var (host, ip) in hostIps)
            Info(ip).HostName ??= StripLocal(host);
    }

    private static bool TryReverse(List<string> labels, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (labels.Count < 6) return false;
        var parts = labels.Take(4).Reverse().ToArray();
        if (!IPAddress.TryParse(string.Join('.', parts), out var parsed)) return false;
        ip = parsed;
        return true;
    }

    private static string StripLocal(string host)
    {
        host = host.TrimEnd('.');
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host[..^6] : host;
    }

    private static string Join(List<string> labels) => string.Join('.', labels);

    private static int U16(byte[] data, int offset) =>
        offset + 1 < data.Length ? (data[offset] << 8) | data[offset + 1] : 0;

    /// <summary>Nombre DNS con punteros de compresión, sin bucles y sin salirse del paquete.</summary>
    private static List<string> ReadName(byte[] data, ref int pos)
    {
        var labels = new List<string>();
        var cursor = pos;
        var jumped = false;
        var jumps = 0;

        while (cursor < data.Length)
        {
            var length = data[cursor];
            if (length == 0)
            {
                cursor++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= data.Length || ++jumps > 16) { cursor = data.Length; break; }
                var target = ((length & 0x3F) << 8) | data[cursor + 1];
                if (!jumped) pos = cursor + 2;
                jumped = true;
                if (target >= data.Length) break;
                cursor = target;
                continue;
            }

            if (cursor + 1 + length > data.Length || labels.Count > 40) { cursor = data.Length; break; }
            labels.Add(Encoding.UTF8.GetString(data, cursor + 1, length));
            cursor += 1 + length;
        }

        if (!jumped) pos = cursor;
        return labels;
    }

    private static Dictionary<string, string> ReadTxt(byte[] data, int start, int length)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end = start + length;
        var pos = start;
        while (pos < end)
        {
            var size = data[pos];
            pos++;
            if (size == 0) continue;
            if (pos + size > end) break;
            var text = Encoding.UTF8.GetString(data, pos, size);
            pos += size;

            var eq = text.IndexOf('=');
            if (eq <= 0) continue;
            var key = text[..eq].Trim();
            var value = text[(eq + 1)..].Trim();
            if (key.Length is 0 or > 32 || value.Length > 120) continue;
            if (value.Any(char.IsControl)) continue;
            values.TryAdd(key, value);
        }
        return values;
    }
}
