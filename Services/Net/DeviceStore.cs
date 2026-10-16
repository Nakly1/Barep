using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Barep.Services.Net;

/// <summary>
/// La memoria del módulo de red: qué aparatos se han visto en cada red, cuándo
/// estuvieron conectados, lo que ha pasado y los tests de velocidad. Vive en
/// %LocalAppData%\Barep\Red y no sale nunca del equipo.
/// </summary>
public sealed class DeviceStore
{
    private const int MaxEvents = 500;
    private const int MaxSessions = 300;
    private const int MaxSpeeds = 40;

    private readonly object _gate = new();

    public List<KnownDevice> Devices { get; set; } = new();
    public List<NetEvent> Events { get; set; } = new();
    public List<SpeedResult> Speeds { get; set; } = new();
    public NetSettings Settings { get; set; } = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Barep", "Red");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DeviceStore Load()
    {
        var store = new DeviceStore();
        store.Devices = Read<List<KnownDevice>>("dispositivos.json") ?? new();
        store.Events = Read<List<NetEvent>>("eventos.json") ?? new();
        store.Speeds = Read<List<SpeedResult>>("velocidad.json") ?? new();
        store.Settings = Read<NetSettings>("ajustes.json") ?? new();
        return store;
    }

    private static T? Read<T>(string file)
    {
        try
        {
            var path = Path.Combine(Folder, file);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch { return default; } // un archivo dañado no debe impedir abrir la app
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Write("dispositivos.json", Devices);
                Write("eventos.json", Events);
                Write("velocidad.json", Speeds);
                Write("ajustes.json", Settings);
            }
            catch { }
        }
    }

    private static void Write<T>(string file, T value)
    {
        var path = Path.Combine(Folder, file);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    // ────────────────────────── Consultas ──────────────────────────

    public KnownDevice? Find(string? network, string key)
    {
        lock (_gate)
            return Devices.FirstOrDefault(d => d.Key == key && d.Network == network);
    }

    public List<KnownDevice> InNetwork(string? network)
    {
        lock (_gate)
            return Devices.Where(d => d.Network == network).ToList();
    }

    public List<NetEvent> EventsIn(string? network)
    {
        lock (_gate)
            return Events.Where(e => e.Network == network || e.Network is null)
                .OrderByDescending(e => e.At)
                .ToList();
    }

    public void Add(KnownDevice device)
    {
        lock (_gate) Devices.Add(device);
    }

    public void Log(NetEvent e)
    {
        lock (_gate)
        {
            Events.Add(e);
            if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
        }
    }

    public void AddSpeed(SpeedResult result)
    {
        lock (_gate)
        {
            Speeds.Add(result);
            if (Speeds.Count > MaxSpeeds) Speeds.RemoveRange(0, Speeds.Count - MaxSpeeds);
        }
        Save();
    }

    public void Forget(string? network, string key)
    {
        lock (_gate)
        {
            Devices.RemoveAll(d => d.Key == key && d.Network == network);
            Events.RemoveAll(e => e.Key == key && e.Network == network);
        }
        Save();
    }

    /// <summary>Alarga la sesión abierta o empieza otra si hubo un hueco.</summary>
    public static void Touch(KnownDevice device, DateTime now, TimeSpan gap)
    {
        var last = device.Sessions.LastOrDefault();
        if (last is not null && now - last.To <= gap) last.To = now;
        else device.Sessions.Add(new Presence { From = now, To = now });

        if (device.Sessions.Count > MaxSessions) device.Sessions.RemoveRange(0, device.Sessions.Count - MaxSessions);
    }

    /// <summary>Cuánto tiempo estuvo conectado un aparato desde una fecha.</summary>
    public static TimeSpan OnlineSince(KnownDevice device, DateTime since)
    {
        var total = TimeSpan.Zero;
        foreach (var s in device.Sessions)
        {
            var from = s.From < since ? since : s.From;
            if (s.To > from) total += s.To - from;
        }
        return total;
    }
}
