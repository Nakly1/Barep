namespace Barep.Services.Net;

/// <summary>
/// Vigila la red mientras Barep está abierto: cada pocos minutos comprueba qué
/// aparatos siguen conectados, apunta quién entra y quién sale y avisa cuando
/// aparece uno que nunca se había visto o cuando el router cambia de identidad.
/// Todos los eventos se lanzan en el hilo de la interfaz.
/// </summary>
public sealed class NetworkMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    private SynchronizationContext? _ui;
    private Timer? _timer;
    private int _busy;

    public NetworkMonitor()
    {
        Store = DeviceStore.Load();
    }

    public DeviceStore Store { get; }
    public LocalLink? Link { get; private set; }
    public WifiInfo? Wifi { get; private set; }
    public bool WifiLocationBlocked { get; private set; }
    public PublicInfo? Public { get; private set; }

    /// <summary>Identificador de la red actual: el nombre del Wi-Fi o, por cable, la IP del router.</summary>
    public string? NetworkId { get; private set; }

    public string? NetworkName => Wifi?.Ssid ?? (Link is null ? null : Link.Wireless ? "Wi-Fi" : "Red por cable");

    public List<NetDevice> Devices { get; private set; } = new();
    public DateTime? LastScan { get; private set; }
    public bool LastScanWasDeep { get; private set; }
    public bool Busy => _busy == 1;

    /// <summary>Después del primer análisis de una red, todo lo que aparezca será «nuevo».</summary>
    public bool FirstInventory { get; private set; }

    public event Action? Updated;
    public event Action<IReadOnlyList<NetDevice>>? NewDevices;
    public event Action<NetScanProgress>? Progress;
    public event Action<string>? Alert;

    public bool AutoWatch
    {
        get => Store.Settings.AutoWatch;
        set
        {
            Store.Settings.AutoWatch = value;
            Store.Save();
            Schedule(value ? Interval : Timeout.InfiniteTimeSpan);
        }
    }

    // ────────────────────────── Arranque ──────────────────────────

    /// <summary>Se llama desde la ventana principal: el primer vistazo, al medio minuto de abrir.</summary>
    public void Start()
    {
        _ui ??= SynchronizationContext.Current;
        if (_timer is not null) return;
        _timer = new Timer(_ => Post(() => _ = ScanAsync(deep: false)), null, Timeout.Infinite, Timeout.Infinite);
        if (AutoWatch) Schedule(TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Schedule(TimeSpan due) => _timer?.Change(due, Timeout.InfiniteTimeSpan);

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    // ────────────────────────── Analizar ──────────────────────────

    /// <param name="deep">
    /// Completo: pregunta a cada aparato su nombre, servicios, ficha y puertos.
    /// Rápido: sólo quién está (y el nombre de los que no se conocían).
    /// </param>
    public async Task ScanAsync(bool deep, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            Link = await Task.Run(LocalNetwork.Current, ct);
            var (wifi, blocked) = Link?.Wireless == true ? await Task.Run(LocalNetwork.Wifi, ct) : (null, false);
            Wifi = wifi;
            WifiLocationBlocked = blocked;

            if (Link is null)
            {
                Devices = new List<NetDevice>();
                NetworkId = null;
                Post(() => Updated?.Invoke());
                return;
            }

            NetworkId = Wifi?.Ssid is { Length: > 0 } ssid ? "wifi:" + ssid : "lan:" + Link.Gateway;
            var network = NetworkId;

            var progress = new Progress<NetScanProgress>(p => Progress?.Invoke(p));
            // Todo el análisis fuera del hilo de la interfaz (incluida la carga de la lista de fabricantes).
            var link = Link;
            var devices = await Task.Run(() => LanScanner.ScanAsync(link, deep, progress,
                d => Store.Find(network, d.Key) is null, ct), ct);

            if (deep)
            {
                try { Public = await LocalNetwork.PublicAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            var fresh = Merge(devices, network);
            Devices = devices;
            LastScan = DateTime.Now;
            LastScanWasDeep = deep;
            Store.Save();

            Post(() =>
            {
                Updated?.Invoke();
                if (fresh.Count > 0) NewDevices?.Invoke(fresh);
            });
        }
        catch (OperationCanceledException) { }
        catch { Post(() => Updated?.Invoke()); }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            if (AutoWatch) Schedule(Interval);
        }
    }

    /// <summary>
    /// Cruza el análisis con lo que ya se conocía: quién es nuevo, quién volvió
    /// y quién se fue. Un aparato se da por desconectado tras fallar dos
    /// análisis seguidos, porque un móvil dormido a veces tarda en contestar.
    /// </summary>
    private List<NetDevice> Merge(List<NetDevice> devices, string network)
    {
        var now = DateTime.Now;
        var gap = Interval * 2.5;
        var known = Store.InNetwork(network);
        FirstInventory = known.Count == 0;
        var fresh = new List<NetDevice>();
        var seen = new HashSet<string>();

        foreach (var device in devices)
        {
            seen.Add(device.Key);
            var entry = Store.Find(network, device.Key);

            if (entry is null)
            {
                entry = new KnownDevice
                {
                    Key = device.Key,
                    Mac = device.Mac,
                    Network = network,
                    FirstSeen = now,
                    // El propio PC y el router son de confianza desde el principio.
                    Trusted = device.IsSelf || device.IsGateway
                };
                Store.Add(entry);

                if (!FirstInventory && !device.IsSelf)
                {
                    fresh.Add(device);
                    Store.Log(new NetEvent
                    {
                        At = now, Kind = NetEventKind.FirstSeen, Key = device.Key, Network = network,
                        Name = DeviceIdentity.DisplayName(device, null),
                        Detail = $"Se conectó por primera vez · {device.IpText}"
                    });
                }
            }
            else
            {
                // Un análisis rápido no pregunta nombres a quien ya se conocía.
                if (device.Model is null && entry.Model is not null) { device.Model = entry.Model; device.ModelSource ??= DeviceIdentity.SourceName; }
                if (device.Brand is null && entry.Brand is not null) device.Brand = entry.Brand;
                if (device.FriendlyName is null && entry.Name is not null && !deviceNameIsGeneric(entry.Name)) device.FriendlyName = entry.Name;
                if ((device.Kind == DeviceKind.Unknown || device.KindIsGuess) && entry.Kind != DeviceKind.Unknown)
                {
                    device.Kind = entry.Kind;
                    device.KindIsGuess = false;
                }

                if (!entry.Online && !device.IsSelf)
                    Store.Log(new NetEvent
                    {
                        At = now, Kind = NetEventKind.Joined, Key = device.Key, Network = network,
                        Name = entry.Alias ?? DeviceIdentity.DisplayName(device, null),
                        Detail = $"Se conectó · {device.IpText}"
                    });
            }

            entry.Online = true;
            entry.Misses = 0;
            entry.LastSeen = now;
            entry.LastIp = device.IpText;
            entry.Kind = device.Kind;
            entry.Brand = device.Brand ?? entry.Brand;
            entry.Model = device.Model ?? entry.Model;
            entry.Vendor = device.Vendor ?? entry.Vendor;
            entry.RandomMac = device.RandomMac;
            entry.Name = DeviceIdentity.DisplayName(device, null);
            DeviceStore.Touch(entry, now, gap);
        }

        foreach (var entry in known.Where(k => k.Online && !seen.Contains(k.Key)))
        {
            if (++entry.Misses < 2) continue;
            entry.Online = false;
            Store.Log(new NetEvent
            {
                At = entry.LastSeen, Kind = NetEventKind.Left, Key = entry.Key, Network = network,
                Name = entry.Alias ?? entry.Name ?? entry.Key, Detail = "Se desconectó"
            });
        }

        CheckGateway(devices, network, now);
        return fresh;

        static bool deviceNameIsGeneric(string name) =>
            name.StartsWith("Aparato en ", StringComparison.Ordinal) ||
            name.StartsWith("Móvil o tableta", StringComparison.Ordinal);
    }

    /// <summary>
    /// Suplantación del router (ARP spoofing): otro aparato contesta con la MAC
    /// del router, o la MAC del router cambia sin que se haya cambiado de router.
    /// </summary>
    private void CheckGateway(List<NetDevice> devices, string network, DateTime now)
    {
        var gateway = devices.FirstOrDefault(d => d.IsGateway);
        if (gateway?.Mac is not { } mac) return;

        var clones = devices.Where(d => !d.IsGateway && d.Mac == mac).ToList();
        if (clones.Count > 0)
        {
            var message = $"{string.Join(", ", clones.Select(c => c.IpText))} contesta con la misma MAC que el router. " +
                          "Es la señal típica de alguien interceptando el tráfico de la red.";
            Store.Log(new NetEvent
            {
                At = now, Kind = NetEventKind.Duplicate, Key = gateway.Key, Network = network,
                Name = "Posible suplantación del router", Detail = message
            });
            Post(() => Alert?.Invoke(message));
        }

        if (Store.Settings.Gateways.TryGetValue(network, out var previous) && previous != mac)
        {
            var message = $"La MAC del router pasó de {previous} a {mac}. Si no has cambiado de router, " +
                          "alguien podría estar haciéndose pasar por él.";
            Store.Log(new NetEvent
            {
                At = now, Kind = NetEventKind.GatewayChanged, Key = gateway.Key, Network = network,
                Name = "El router cambió", Detail = message
            });
            Post(() => Alert?.Invoke(message));
        }

        Store.Settings.Gateways[network] = mac;
    }

    // ────────────────────────── Acciones del usuario ──────────────────────────

    public KnownDevice? Known(NetDevice device) => Store.Find(NetworkId, device.Key);

    public void Rename(NetDevice device, string? alias)
    {
        if (Known(device) is not { } entry) return;
        entry.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        Store.Save();
        Post(() => Updated?.Invoke());
    }

    public void SetTrusted(NetDevice device, bool trusted)
    {
        if (Known(device) is not { } entry) return;
        entry.Trusted = trusted;
        Store.Save();
        Post(() => Updated?.Invoke());
    }

    public void TrustAll()
    {
        foreach (var entry in Store.InNetwork(NetworkId)) entry.Trusted = true;
        Store.Save();
        Post(() => Updated?.Invoke());
    }
}
