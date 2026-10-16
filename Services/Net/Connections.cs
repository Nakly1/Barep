using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Barep.Services.Net;

/// <summary>Una conexión TCP de un programa de este PC.</summary>
public sealed record TcpConnection(
    int Pid,
    string Process,
    string? Path,
    IPAddress LocalIp,
    int LocalPort,
    IPAddress RemoteIp,
    int RemotePort,
    int State)
{
    public bool Established => State == 5;
    public bool ToInternet => !IPAddress.IsLoopback(RemoteIp) && !LocalNetwork.IsPrivate(RemoteIp) &&
                              !RemoteIp.Equals(IPAddress.Any);
}

/// <summary>Un programa de este PC esperando conexiones (un «puerto abierto»).</summary>
public sealed record Listener(int Pid, string Process, string? Path, IPAddress Ip, int Port, bool Udp)
{
    /// <summary>Se puede llegar a él desde otros aparatos de la red, no sólo desde el propio PC.</summary>
    public bool Exposed => !IPAddress.IsLoopback(Ip);
}

/// <summary>
/// Con quién habla cada programa del PC y qué puertos tiene abiertos, leído de
/// las tablas TCP/UDP de Windows con el proceso dueño de cada línea.
/// </summary>
public static class Connections
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass,
        uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow
    {
        public uint LocalAddr, LocalPort, Pid;
    }

    public static List<TcpConnection> Tcp()
    {
        var names = new Dictionary<int, (string, string?)>();
        return ReadTable<TcpRow>(GetExtendedTcpTable, TcpTableOwnerPidAll)
            .Where(r => r.State != 2) // las que escuchan van aparte
            .Select(r =>
            {
                var (name, path) = Owner((int)r.Pid, names);
                return new TcpConnection((int)r.Pid, name, path, new IPAddress(r.LocalAddr), Port(r.LocalPort),
                    new IPAddress(r.RemoteAddr), Port(r.RemotePort), (int)r.State);
            })
            .ToList();
    }

    public static List<Listener> Listeners()
    {
        var names = new Dictionary<int, (string, string?)>();
        var tcp = ReadTable<TcpRow>(GetExtendedTcpTable, TcpTableOwnerPidAll)
            .Where(r => r.State == 2)
            .Select(r =>
            {
                var (name, path) = Owner((int)r.Pid, names);
                return new Listener((int)r.Pid, name, path, new IPAddress(r.LocalAddr), Port(r.LocalPort), false);
            });

        var udp = ReadTable<UdpRow>(GetExtendedUdpTable, UdpTableOwnerPid)
            .Select(r =>
            {
                var (name, path) = Owner((int)r.Pid, names);
                return new Listener((int)r.Pid, name, path, new IPAddress(r.LocalAddr), Port(r.LocalPort), true);
            });

        return tcp.Concat(udp)
            .GroupBy(l => (l.Port, l.Udp, l.Pid, l.Exposed))
            .Select(g => g.First())
            .OrderBy(l => l.Port)
            .ToList();
    }

    private delegate uint TableReader(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    private static List<T> ReadTable<T>(TableReader reader, int tableClass) where T : struct
    {
        var rows = new List<T>();
        var size = 0;
        reader(IntPtr.Zero, ref size, true, AfInet, tableClass, 0);
        if (size <= 0) return rows;

        // La tabla puede crecer entre las dos llamadas: se deja margen.
        size += 4096;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (reader(buffer, ref size, true, AfInet, tableClass, 0) != 0) return rows;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<T>();
            var max = (size - 4) / rowSize;
            for (var i = 0; i < Math.Min(count, max); i++)
                rows.Add(Marshal.PtrToStructure<T>(buffer + 4 + i * rowSize));
        }
        catch { }
        finally { Marshal.FreeHGlobal(buffer); }
        return rows;
    }

    /// <summary>Los puertos vienen en orden de red (big-endian) en los 16 bits bajos.</summary>
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static (string Name, string? Path) Owner(int pid, Dictionary<int, (string, string?)> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;

        (string, string?) result = pid switch
        {
            0 => ("Sistema (inactivo)", null),
            4 => ("Windows (núcleo)", null),
            _ => ("Proceso " + pid, null)
        };

        if (pid > 4)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var path = ProcessService.PathOf(pid);
                var name = process.ProcessName;
                if (path is not null)
                {
                    try
                    {
                        var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                        if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
                    }
                    catch { }
                }
                result = (name, path);
            }
            catch { }
        }

        cache[pid] = result;
        return result;
    }

    // ────────────────────────── Nombres de los servidores ──────────────────────────

    private static readonly Dictionary<string, string?> HostCache = new();

    /// <summary>
    /// «140.82.112.26» → «github.com». Se pregunta una vez por dirección y se
    /// recuerda; si no hay nombre, se queda la IP.
    /// </summary>
    public static async Task<string?> HostOfAsync(IPAddress ip, CancellationToken ct)
    {
        var key = ip.ToString();
        lock (HostCache)
            if (HostCache.TryGetValue(key, out var cached)) return cached;

        string? host = null;
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(2000, ct)) == lookup && lookup.Status == TaskStatus.RanToCompletion)
                host = Domain(lookup.Result.HostName);
        }
        catch { }

        lock (HostCache) HostCache[key] = host;
        return host;
    }

    /// <summary>«lb-140-82-112-26-iad.github.com» → «github.com»; «server-1-2-3-4.cloudfront.net» → «cloudfront.net».</summary>
    private static string? Domain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _)) return null;
        var parts = host.TrimEnd('.').Split('.');
        if (parts.Length <= 2) return host.TrimEnd('.');

        // Dominios de dos niveles como «.com.co» o «.co.uk».
        var second = parts[^2];
        var take = second.Length <= 3 && parts[^1].Length == 2 && parts.Length >= 3 ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }
}
