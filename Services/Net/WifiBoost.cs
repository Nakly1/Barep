using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Barep.Services.Net;

/// <summary>Velocidad de respuesta de un servidor DNS.</summary>
public sealed record DnsTiming(string Name, IPAddress Server, double? Milliseconds, bool Current);

/// <summary>
/// Lo que se puede hacer desde el PC para que internet por Wi-Fi vaya más
/// ligero: quitar el ahorro de energía del adaptador (que lo «duerme» entre
/// paquetes y sube el ping) y usar el DNS que responda más rápido. El resto
/// (canal, banda, distancia) depende del router y se explica aparte.
/// </summary>
public static class WifiBoost
{
    // Opciones de energía › Configuración del adaptador inalámbrico › Modo de ahorro de energía.
    private const string Subgroup = "19cab1e1-6a54-4f5f-b3f6-9d6e3b0b1fb6";
    private const string Setting = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>0 = máximo rendimiento … 3 = máximo ahorro, enchufado y con batería.</summary>
    public static (int? Ac, int? Dc) PowerSaving()
    {
        var output = Run("powercfg", $"/q SCHEME_CURRENT {Subgroup} {Setting}");
        if (output is null) return (null, null);

        // Las dos últimas cifras «0x…» son el valor actual enchufado y con batería (en cualquier idioma).
        var values = Regex.Matches(output, @"0x([0-9a-fA-F]{8})")
            .Select(m => Convert.ToInt32(m.Groups[1].Value, 16))
            .ToList();
        return values.Count >= 2 ? (values[^2], values[^1]) : (null, null);
    }

    public static bool MaxPerformance() => PowerSaving() is (0, 0);

    /// <summary>Activa o devuelve el ahorro de energía del Wi-Fi (por defecto de Windows: medio con batería).</summary>
    public static bool SetMaxPerformance(bool on)
    {
        var dc = on ? 0 : 2;
        var ok = Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {Subgroup} {Setting} 0") is not null &&
                 Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {Subgroup} {Setting} {dc}") is not null &&
                 Run("powercfg", "/setactive SCHEME_CURRENT") is not null;
        return ok && MaxPerformance() == on;
    }

    // ────────────────────────── DNS ──────────────────────────

    public static readonly (string Name, string Ip, string Secondary)[] Providers =
    {
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9", "9.9.9.9", "149.112.112.112")
    };

    private static readonly string[] Domains =
        { "www.google.com", "www.youtube.com", "www.whatsapp.com", "www.wikipedia.org", "www.instagram.com" };

    /// <summary>Mide cuánto tarda cada DNS en contestar (la mediana de varias consultas).</summary>
    public static async Task<List<DnsTiming>> BenchmarkAsync(IReadOnlyList<IPAddress> current, CancellationToken ct)
    {
        var servers = current.Where(c => c.AddressFamily == AddressFamily.InterNetwork)
            .Take(1)
            .Select(c => (Name: "El que usas ahora", Ip: c, Current: true))
            .Concat(Providers.Select(p => (p.Name, Ip: IPAddress.Parse(p.Ip), Current: false)))
            .GroupBy(s => s.Ip)
            .Select(g => g.First())
            .ToList();

        var results = await Task.WhenAll(servers.Select(async s =>
        {
            var samples = new List<double>();
            foreach (var domain in Domains.Concat(Domains))
            {
                var ms = await QueryAsync(s.Ip, domain, ct);
                if (ms is not null) samples.Add(ms.Value);
            }
            samples.Sort();
            double? median = samples.Count == 0 ? null : samples[samples.Count / 2];
            var name = s.Current && Providers.FirstOrDefault(p => p.Ip == s.Ip.ToString()).Name is { } known
                ? $"{known} (el que usas ahora)"
                : s.Name;
            return new DnsTiming(name, s.Ip, median, s.Current);
        }));

        return results.OrderBy(r => r.Milliseconds ?? double.MaxValue).ToList();
    }

    private static async Task<double?> QueryAsync(IPAddress server, string domain, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var id = (ushort)Random.Shared.Next(1, 65535);
            var packet = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in domain.Split('.'))
            {
                packet.Add((byte)label.Length);
                packet.AddRange(Encoding.ASCII.GetBytes(label));
            }
            packet.AddRange(new byte[] { 0, 0, 1, 0, 1 });

            var clock = Stopwatch.StartNew();
            await udp.SendAsync(packet.ToArray(), packet.Count, new IPEndPoint(server, 53));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (reply.Buffer.Length >= 2 && ((reply.Buffer[0] << 8) | reply.Buffer[1]) == id)
                    return clock.Elapsed.TotalMilliseconds;
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Cambia el DNS del adaptador. Necesita permiso de administrador, así que
    /// Windows lo pregunta una vez. <paramref name="primary"/> nulo vuelve al automático.
    /// </summary>
    public static async Task<bool> SetDnsAsync(string adapter, string? primary, string? secondary)
    {
        var name = adapter.Replace("\"", "");
        var command = primary is null
            ? $"netsh interface ipv4 set dnsservers name=\"{name}\" source=dhcp"
            : $"netsh interface ipv4 set dnsservers name=\"{name}\" static {primary} primary validate=no" +
              (secondary is null ? "" : $" & netsh interface ipv4 add dnsservers name=\"{name}\" {secondary} index=2 validate=no");
        command += " & ipconfig /flushdns";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c {command}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return false;
            await process.WaitForExitAsync();
            return true;
        }
        catch { return false; } // el usuario dijo que no en el aviso de Windows
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
