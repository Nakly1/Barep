using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Barep.Services.Net;

/// <summary>Ficha UPnP de un aparato: lo que el propio fabricante pone en su descripción.</summary>
public sealed record SsdpInfo(
    string? FriendlyName,
    string? Manufacturer,
    string? ModelName,
    string? ModelNumber,
    string? ModelDescription,
    string? DeviceType,
    string? Server);

/// <summary>
/// UPnP / SSDP: routers, televisores, consolas, impresoras y NAS publican una
/// ficha XML con su marca y su modelo exacto. Se lee sólo de direcciones de la
/// red local y con el XML en modo seguro (sin DTD ni entidades externas).
/// </summary>
public static class Ssdp
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);

    private static readonly HttpClient Lan = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
        MaxResponseContentBufferSize = 512 * 1024
    };

    public static async Task<Dictionary<string, SsdpInfo>> DiscoverAsync(IPAddress local, TimeSpan listen,
        CancellationToken ct)
    {
        var locations = new Dictionary<string, (string Url, string? Server)>();

        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

            foreach (var target in new[] { "ssdp:all", "upnp:rootdevice" })
            {
                var search = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    $"ST: {target}\r\n\r\n");
                await udp.SendAsync(search, search.Length, Group);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(listen);
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                var from = received.RemoteEndPoint.Address;
                if (!LocalNetwork.IsPrivate(from)) continue;

                var text = Encoding.UTF8.GetString(received.Buffer, 0, Math.Min(received.Buffer.Length, 4096));
                var headers = text.Split("\r\n")
                    .Select(l => l.Split(':', 2))
                    .Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim().ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.First()[1].Trim());

                if (!headers.TryGetValue("LOCATION", out var location)) continue;

                // Sólo se sigue la ficha si está en el mismo aparato que contestó.
                if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
                    !IPAddress.TryParse(uri.Host, out var host) || !host.Equals(from)) continue;

                var key = from.ToString();
                headers.TryGetValue("SERVER", out var server);
                var isRoot = headers.TryGetValue("ST", out var st) && st.Contains("rootdevice", StringComparison.OrdinalIgnoreCase);
                if (!locations.ContainsKey(key) || isRoot) locations[key] = (location, server);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        var result = new Dictionary<string, SsdpInfo>();
        var fetches = locations.Select(async pair =>
        {
            var info = await DescribeAsync(pair.Value.Url, pair.Value.Server, ct);
            if (info is not null) lock (result) result[pair.Key] = info;
        });
        await Task.WhenAll(fetches);
        return result;
    }

    private static async Task<SsdpInfo?> DescribeAsync(string url, string? server, CancellationToken ct)
    {
        try
        {
            var xml = await Lan.GetStringAsync(url, ct);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000,
                MaxCharactersFromEntities = 0
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);

            // La primera <device> es el aparato; las anidadas son sus partes.
            var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device is null) return null;

            string? Field(string name)
            {
                var value = device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
                return string.IsNullOrWhiteSpace(value) || value.Length > 120 ? null : value;
            }

            return new SsdpInfo(Field("friendlyName"), Field("manufacturer"), Field("modelName"), Field("modelNumber"),
                Field("modelDescription"), Field("deviceType"), server);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
