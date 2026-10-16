using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Barep.Controls;
using Barep.Services;
using Barep.Services.Net;

namespace Barep.Views;

public partial class NetworkView : UserControl
{
    private static readonly CultureInfo Spanish = new("es-ES");

    private readonly ObservableCollection<DeviceRow> _rows = new();
    private string? _selectedKey;
    private bool _hooked;
    private bool _updating;
    private CancellationTokenSource? _speedCts;
    private CancellationTokenSource? _portsCts;
    private List<SecurityCheck> _checks = new();
    private WifiScan? _nearby;
    private DateTime _connectionsAt;
    private string? _aliasKey;
    private bool _boostLoaded;
    private CancellationTokenSource? _dnsCts;

    private static NetworkMonitor Net => AppServices.Network;

    public NetworkView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ────────────────────────── Ciclo de vida ──────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            DeviceList.ItemsSource = _rows;
        }

        Net.Updated += OnUpdated;
        Net.Progress += OnProgress;
        Net.NewDevices += OnNewDevices;
        Net.Alert += OnAlert;

        WatchSwitch.IsChecked = Net.AutoWatch;
        RefreshAll();

        // La primera visita hace el análisis completo; luego basta con el botón.
        if ((Net.LastScan is null || !Net.LastScanWasDeep) && !Net.Busy) _ = ScanAsync();
        else if (Net.Busy) ShowBusy(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Net.Updated -= OnUpdated;
        Net.Progress -= OnProgress;
        Net.NewDevices -= OnNewDevices;
        Net.Alert -= OnAlert;
    }

    private void OnUpdated()
    {
        ShowBusy(Net.Busy);
        RefreshAll();
    }

    private void OnNewDevices(IReadOnlyList<NetDevice> devices) => RefreshAll();

    private void OnAlert(string message)
    {
        AlertTitle.Text = "Algo raro con el router";
        AlertText.Text = message;
        AlertBanner.Visibility = Visibility.Visible;
    }

    private void OnProgress(NetScanProgress p)
    {
        ShowBusy(p.Fraction < 1);
        ScanProgressText.Text = p.Stage;
        ScanProgressBar.Value = p.Fraction * 100;
    }

    private void ShowBusy(bool busy)
    {
        ScanProgressBox.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LastScanText.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.IsEnabled = !busy;
    }

    private async Task ScanAsync()
    {
        ShowBusy(true);
        ScanProgressText.Text = "Preparando…";
        ScanProgressBar.Value = 0;
        await Net.ScanAsync(deep: true);
        ShowBusy(false);
        RefreshAll();
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void RefreshAll()
    {
        RefreshHeader();
        RefreshDevices();
        RefreshSummary();
        RefreshActivity();
        RefreshWifiCurrent();
        RefreshSpeedHistory();
        _ = RefreshSecurityAsync();
    }

    private void RefreshHeader()
    {
        LastScanText.Text = Net.LastScan is { } at ? "Analizada " + Ago(at) : "Sin analizar";
        HeaderSubtitle.Text = Net.Link is null
            ? "No hay conexión de red ahora mismo."
            : $"Conectado a {Net.NetworkName}. Quién está en tu red, qué tan rápida va y qué tan protegida está.";
    }

    // ────────────────────────── Pestañas ──────────────────────────

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        PanelSummary.Visibility = Vis(TabSummary.IsChecked == true);
        PanelDevices.Visibility = Vis(TabDevices.IsChecked == true);
        PanelActivity.Visibility = Vis(TabActivity.IsChecked == true);
        PanelSpeed.Visibility = Vis(TabSpeed.IsChecked == true);
        PanelWifi.Visibility = Vis(TabWifi.IsChecked == true);
        PanelSecurity.Visibility = Vis(TabSecurity.IsChecked == true);
        PanelConnections.Visibility = Vis(TabConnections.IsChecked == true);

        if (TabWifi.IsChecked == true && _nearby is null) _ = RefreshNearbyAsync();
        if (TabWifi.IsChecked == true && !_boostLoaded) _ = RefreshBoostAsync();
        if (TabConnections.IsChecked == true && (DateTime.Now - _connectionsAt).TotalSeconds > 20) _ = RefreshConnectionsAsync();
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void GoDevices_Click(object sender, RoutedEventArgs e) => TabDevices.IsChecked = true;
    private void GoActivity_Click(object sender, RoutedEventArgs e) => TabActivity.IsChecked = true;

    // ────────────────────────── Datos combinados ──────────────────────────

    /// <summary>
    /// Los aparatos conectados ahora más los que se conocen de antes y no están:
    /// así se ve también quién se fue.
    /// </summary>
    private List<DeviceRow> AllRows()
    {
        var known = Net.Store.InNetwork(Net.NetworkId);
        var inventoryAt = known.Count == 0 ? DateTime.MaxValue : known.Min(k => k.FirstSeen).AddMinutes(2);
        var rows = new List<DeviceRow>();
        var live = new HashSet<string>();

        foreach (var device in Net.Devices)
        {
            live.Add(device.Key);
            rows.Add(new DeviceRow(device, Net.Known(device), online: true, inventoryAt));
        }

        foreach (var entry in known.Where(k => !live.Contains(k.Key)))
        {
            if (!IPAddress.TryParse(entry.LastIp, out var ip)) continue;
            var ghost = new NetDevice(ip)
            {
                Mac = entry.Mac, Kind = entry.Kind, Brand = entry.Brand, Model = entry.Model,
                FriendlyName = entry.Name, Vendor = entry.Vendor, RandomMac = entry.RandomMac
            };
            rows.Add(new DeviceRow(ghost, entry, online: false, inventoryAt));
        }

        return rows
            .OrderByDescending(r => r.Online)
            .ThenByDescending(r => r.Device.IsGateway)
            .ThenByDescending(r => r.Device.IsSelf)
            .ThenByDescending(r => r.IsNew)
            .ThenBy(r => LocalNetwork.ToUInt(r.Device.Ip))
            .ToList();
    }

    // ────────────────────────── Resumen ──────────────────────────

    private void RefreshSummary()
    {
        var link = Net.Link;
        var wifi = Net.Wifi;

        SumNet.Text = Net.NetworkName ?? "Sin conexión";
        if (wifi?.Signal is { } signal)
        {
            SumNetSignal.Visibility = Visibility.Visible;
            SumNetSignal.Value = signal;
            SumNetSignal.Foreground = UiKit.Brush(signal >= 60 ? "Br.Positive" : signal >= 35 ? "Br.Warn" : "Br.Critical");
            SumNetCaption.Text = string.Join(" · ", new[]
            {
                $"Señal {signal} %", wifi.Band, LocalNetwork.WifiGeneration(wifi.Radio)
            }.Where(s => !string.IsNullOrEmpty(s)));
        }
        else
        {
            SumNetSignal.Visibility = Visibility.Collapsed;
            SumNetCaption.Text = link is null ? "Revisa el cable o el Wi-Fi" :
                link.Wireless ? "Wi-Fi" : $"Cable · {LinkSpeed(link.SpeedBps)}";
        }

        var rows = AllRows();
        var online = rows.Count(r => r.Online);
        var fresh = rows.Where(r => r.Online && r.IsNew).ToList();
        var strangers = rows.Count(r => r.Online && r.Known is { Trusted: false });
        SumDevices.Text = Net.LastScan is null ? "—" : online.ToString();
        SumDevicesCaption.Text = Net.LastScan is null ? "Analizando…" :
            fresh.Count > 0 ? $"{UiText.Plural(fresh.Count, "nuevo", "nuevos")} · {strangers} sin reconocer" :
            strangers > 0 ? $"{UiText.Plural(strangers, "sin reconocer", "sin reconocer")}" : "Todos conocidos";

        var last = Net.Store.Speeds.LastOrDefault();
        SumSpeed.Text = last is null ? "—" : $"{last.DownMbps:0} / {last.UpMbps:0}";
        SumSpeedCaption.Text = last is null ? "Sin medir todavía" : $"Mbps bajada / subida · {Ago(last.At)}";

        // Avisos
        if (fresh.Count > 0)
        {
            NewBanner.Visibility = Visibility.Visible;
            NewTitle.Text = fresh.Count == 1 ? "Hay un aparato nuevo en tu red" : $"Hay {fresh.Count} aparatos nuevos en tu red";
            NewText.Text = string.Join(", ", fresh.Take(4).Select(f => $"{f.Name} ({f.IpText})")) +
                           ". Nunca se habían conectado antes. Si son tuyos, márcalos; si no, cambia la contraseña del Wi-Fi.";
        }
        else if (Net.FirstInventory && Net.LastScan is not null && strangers > 0)
        {
            NewBanner.Visibility = Visibility.Visible;
            NewTitle.Text = "Primer análisis de esta red";
            NewText.Text = $"Encontré {UiText.Plural(online, "aparato", "aparatos")}. A partir de ahora te aviso cuando " +
                           "aparezca uno que no estaba. Si todo lo de hoy es tuyo, márcalo de una vez.";
        }
        else NewBanner.Visibility = Visibility.Collapsed;

        var since = DateTime.Now.AddDays(-1);
        var alert = Net.Store.EventsIn(Net.NetworkId)
            .FirstOrDefault(ev => ev.At >= since && ev.Kind is NetEventKind.Duplicate or NetEventKind.GatewayChanged);
        if (alert is not null)
        {
            AlertTitle.Text = alert.Name;
            AlertText.Text = alert.Detail;
            AlertBanner.Visibility = Visibility.Visible;
        }

        // Ficha de la conexión
        var specs = new List<(string, string?)>();
        if (link is not null)
        {
            var router = Net.Devices.FirstOrDefault(d => d.IsGateway);
            specs.Add(("Este PC", $"{link.Ip}  ·  {link.Name}"));
            specs.Add(("Router", link.Gateway is null ? "—" :
                router?.Model is { } model ? $"{link.Gateway}  ·  {model}" : link.Gateway.ToString()));
            specs.Add(("IP pública", Net.Public?.Ip ?? "se ve al analizar"));
            if (Net.Public?.Isp is { } isp) specs.Add(("Operador", isp));
            if (Net.Public?.City is { } city) specs.Add(("Sale a internet en", city + (Net.Public.Country is { } c ? $" ({c})" : "")));
            specs.Add(("DNS", link.Dns.Count == 0 ? "—" : string.Join(", ", link.Dns)));
            specs.Add(("Enlace", link.Wireless
                ? wifi?.ReceiveMbps is { } rx ? $"{rx:0} Mbps con el router" : "Wi-Fi"
                : LinkSpeed(link.SpeedBps)));
            specs.Add(("Dirección", link.Dhcp ? "Automática (DHCP)" : "Fija"));
        }
        else specs.Add(("Estado", "Sin conexión de red"));
        UiKit.FillSpecs(SumSpecs, specs, labelWidth: 132);

        SumEvents.Children.Clear();
        var events = Net.Store.EventsIn(Net.NetworkId).Take(7).ToList();
        if (events.Count == 0)
            SumEvents.Children.Add(UiKit.EmptyState("Todavía no hay movimientos. Se apuntan mientras Barep está abierto."));
        foreach (var ev in events) SumEvents.Children.Add(EventRow(ev, compact: true));
    }

    private static string LinkSpeed(long bps) => bps switch
    {
        <= 0 => "—",
        >= 1_000_000_000 => $"{bps / 1_000_000_000.0:0.#} Gbps",
        _ => $"{bps / 1_000_000:0} Mbps"
    };

    // ────────────────────────── Aparatos ──────────────────────────

    private void RefreshDevices()
    {
        var query = DeviceSearch.Text.Trim();
        IEnumerable<DeviceRow> rows = AllRows();

        if (DevFilterOnline.IsChecked == true) rows = rows.Where(r => r.Online);
        else if (DevFilterNew.IsChecked == true) rows = rows.Where(r => r.IsNew);
        else if (DevFilterStrangers.IsChecked == true) rows = rows.Where(r => r.Known is { Trusted: false });

        if (query.Length > 0)
            rows = rows.Where(r =>
                r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.SubText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                r.IpText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.Device.Mac?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        var list = rows.ToList();
        var keep = _selectedKey;

        _updating = true;
        _rows.Clear();
        foreach (var row in list) _rows.Add(row);
        _updating = false;

        DeviceEmpty.Visibility = Vis(list.Count == 0);
        DeviceEmpty.Text = Net.LastScan is null ? "Buscando aparatos en tu red…" :
            query.Length > 0 ? $"Nada coincide con «{query}»." : "No hay aparatos con este filtro.";

        var selected = keep is null ? null : _rows.FirstOrDefault(r => r.Key == keep);
        if (selected is not null) DeviceList.SelectedItem = selected;
        ShowDevice(selected);
    }

    private void DeviceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        DeviceSearchHint.Visibility = Vis(DeviceSearch.Text.Length == 0);
        if (IsLoaded) RefreshDevices();
    }

    private void DeviceFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshDevices();
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var row = DeviceList.SelectedItem as DeviceRow;
        if (row?.Key != _selectedKey) DetailPorts.Children.Clear();
        _selectedKey = row?.Key;
        ShowDevice(row);
    }

    private DeviceRow? SelectedRow => _rows.FirstOrDefault(r => r.Key == _selectedKey);

    private void ShowDevice(DeviceRow? row)
    {
        if (row is null)
        {
            DeviceHint.Visibility = Visibility.Visible;
            DeviceDetail.Visibility = Visibility.Collapsed;
            return;
        }

        DeviceHint.Visibility = Visibility.Collapsed;
        DeviceDetail.Visibility = Visibility.Visible;

        var d = row.Device;
        var known = row.Known;

        DetailGlyph.Data = row.Glyph;
        DetailGlyph.Fill = row.GlyphBrush;
        DetailTile.Background = row.TileBrush;
        DetailName.Text = row.Name;
        DetailSub.Text = string.Join(" · ", new[] { d.Brand, d.Model is { } m && d.Brand is { } b && m.StartsWith(b) ? m[(b.Length)..].Trim() : d.Model }
            .Where(s => !string.IsNullOrWhiteSpace(s)).DefaultIfEmpty(DeviceIdentity.KindName(d.Kind)));

        DetailChips.Children.Clear();
        void Chip(string text, string fg, string bg) =>
            DetailChips.Children.Add(Spaced(UiKit.Chip(text, UiKit.Brush(fg), UiKit.Brush(bg))));
        Chip(DeviceIdentity.KindName(d.Kind) + (d.KindIsGuess ? " (probable)" : ""), "Br.AccentBright", "Br.AccentTint");
        if (row.Online) Chip("Conectado", "Br.Positive", "Br.PositiveTint");
        else Chip("Desconectado", "Br.TextSecondary", "Br.SurfaceRaised");
        if (d.IsSelf) Chip("Este PC", "Br.AccentBright", "Br.AccentTint");
        else if (row.IsNew) Chip("Nuevo", "Br.Warn", "Br.WarnTint");
        else if (known is { Trusted: true }) Chip("Es tuyo", "Br.Positive", "Br.PositiveTint");
        else Chip("Sin reconocer", "Br.Warn", "Br.WarnTint");
        if (d.RandomMac) Chip("Dirección privada", "Br.TextSecondary", "Br.SurfaceRaised");

        // Mientras escribes no se pisa lo escrito aunque la lista se refresque.
        if (_aliasKey != row.Key || !AliasBox.IsKeyboardFocusWithin)
        {
            _updating = true;
            AliasBox.Text = known?.Alias ?? "";
            _updating = false;
            AliasSave.IsEnabled = false;
            _aliasKey = row.Key;
        }
        AliasHint.Visibility = Vis(AliasBox.Text.Length == 0);
        AliasBox.IsEnabled = known is not null;

        var specs = new List<(string, string?)>
        {
            ("Tipo", DeviceIdentity.KindName(d.Kind) + (d.KindIsGuess ? " (deducido)" : "")),
            ("Marca", d.Brand),
            ("Modelo", d.Model ?? (d.Kind == DeviceKind.Phone && d.Brand is null ? "No lo publica" : null)),
            ("De dónde sale", d.ModelSource),
            ("IP", d.IpText),
            ("MAC", d.Mac is null ? null : d.Mac + (d.RandomMac ? "  (privada)" : "")),
            ("Tarjeta de red", d.VendorFull ?? (d.RandomMac ? "Oculta por la dirección privada" : null)),
            ("Nombre en la red", d.MdnsHost ?? d.HostName ?? d.NetbiosName)
        };
        if (d.Services.Count > 0)
            specs.Add(("Ofrece", string.Join(", ", d.Services.Select(ServiceName).Where(s => s is not null).Distinct().Take(6))));
        if (d.PingMs is { } ping && !d.IsSelf) specs.Add(("Responde en", ping == 0 ? "< 1 ms" : $"{ping} ms"));
        if (known is not null)
        {
            specs.Add(("Primera vez", known.FirstSeen.ToString("d MMM yyyy, HH:mm", Spanish)));
            specs.Add(("Última vez", row.Online ? "Ahora" : Ago(known.LastSeen)));
            specs.Add(("Hoy conectado", Duration(DeviceStore.OnlineSince(known, DateTime.Today))));
        }
        UiKit.FillSpecs(DetailSpecs, specs.Where(s => !string.IsNullOrWhiteSpace(s.Item2)), labelWidth: 118);

        DrawTimeline(known);

        TrustButton.Content = known is { Trusted: true } ? "No es mío" : "Es mío";
        TrustButton.Style = UiKit.Style(known is { Trusted: true } ? "BtnGhost" : "BtnPrimary");
        TrustButton.Visibility = Vis(known is not null && !d.IsSelf);
        PortsButton.IsEnabled = row.Online;
        PhotosButton.Visibility = Vis(d.Model is not null || (d.Brand is not null && d.Kind != DeviceKind.Unknown));
        RouterButton.Visibility = Vis(d.IsGateway);
        ForgetButton.Visibility = Vis(!row.Online && known is not null);
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 0, 6, 6);
        return element;
    }

    /// <summary>Barra de 24 h dividida en cuartos de hora: relleno donde estuvo conectado.</summary>
    private void DrawTimeline(KnownDevice? known)
    {
        DetailTimeline.Children.Clear();
        DetailTimeline.ColumnDefinitions.Clear();
        const int slots = 96;
        for (var i = 0; i < slots; i++)
            DetailTimeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var track = new Border { Background = UiKit.Brush("Br.Track"), CornerRadius = new CornerRadius(3) };
        Grid.SetColumnSpan(track, slots);
        DetailTimeline.Children.Add(track);
        if (known is null) return;

        var today = DateTime.Today;
        for (var i = 0; i < slots; i++)
        {
            var from = today.AddMinutes(i * 15);
            var to = from.AddMinutes(15);
            if (!known.Sessions.Any(s => s.From < to && s.To.AddMinutes(1) > from)) continue;
            var block = new Border { Background = UiKit.Brush("Br.AccentGradient") };
            Grid.SetColumn(block, i);
            DetailTimeline.Children.Add(block);
        }

        var now = (int)((DateTime.Now - today).TotalMinutes / 15);
        if (now is >= 0 and < slots)
        {
            var marker = new Border { Width = 2, Background = UiKit.Brush("Br.TextPrimary"), HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(marker, now);
            DetailTimeline.Children.Add(marker);
        }
    }

    private void AliasBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AliasHint.Visibility = Vis(AliasBox.Text.Length == 0);
        if (!_updating) AliasSave.IsEnabled = true;
    }

    private void AliasBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveAlias();
    }

    private void AliasSave_Click(object sender, RoutedEventArgs e) => SaveAlias();

    private void SaveAlias()
    {
        if (SelectedRow is not { } row) return;
        Net.Rename(row.Device, AliasBox.Text);
        Status($"Guardado: «{(string.IsNullOrWhiteSpace(AliasBox.Text) ? row.Name : AliasBox.Text.Trim())}».", ok: true);
    }

    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { Known: { } known } row) return;
        Net.SetTrusted(row.Device, !known.Trusted);
    }

    private void TrustAll_Click(object sender, RoutedEventArgs e)
    {
        Net.TrustAll();
        Status("Todos los aparatos conectados quedan marcados como tuyos. Te aviso si aparece otro.", ok: true);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        Net.Store.Forget(Net.NetworkId, row.Key);
        _selectedKey = null;
        RefreshAll();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        try
        {
            Clipboard.SetText(row.IpText);
            Status($"IP {row.IpText} copiada.", ok: true);
        }
        catch { }
    }

    private void Photos_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow?.Device is not { } d) return;
        var query = string.Join(' ', new[] { d.Brand, d.Model ?? DeviceIdentity.KindName(d.Kind) }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        Open("https://www.google.com/search?tbm=isch&q=" + Uri.EscapeDataString(query));
    }

    private void Router_Click(object sender, RoutedEventArgs e) => OpenRouter();

    private static void OpenRouter()
    {
        if (Net.Link?.Gateway is { } gateway) Open($"http://{gateway}/");
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    private async void Ports_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow?.Device is not { } device) return;

        _portsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _portsCts = cts;

        DetailPorts.Children.Clear();
        DetailPorts.Children.Add(UiKit.Text($"Probando {Ports.DeviceScan.Length} puertos de {device.IpText}…", "T.Caption"));
        PortsButton.IsEnabled = false;

        var probe = new NetDevice(device.Ip);
        try { await LanScanner.ProbeAsync(new[] { probe }, Ports.DeviceScan, 700, cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { PortsButton.IsEnabled = true; }

        if (cts.IsCancellationRequested) return;
        foreach (var port in probe.OpenPorts) device.OpenPorts.Add(port);

        DetailPorts.Children.Clear();
        DetailPorts.Children.Add(UiKit.Text("PUERTOS ABIERTOS", "T.Label", margin: new Thickness(0, 6, 0, 8)));
        if (probe.OpenPorts.Count == 0)
        {
            DetailPorts.Children.Add(UiKit.Text("Ninguno de los habituales. No acepta conexiones de la red: bien cerrado.",
                "T.Caption", UiKit.Brush("Br.Positive")));
            return;
        }

        foreach (var port in probe.OpenPorts.Order())
            DetailPorts.Children.Add(PortRow(Ports.Describe(port), null));
    }

    private static FrameworkElement PortRow(PortInfo info, string? owner)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = UiKit.Text(info.Port.ToString(), "T.Mono");
        number.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(number);

        var text = new StackPanel();
        text.Children.Add(UiKit.Text(owner is null ? info.Name : $"{info.Name} · {owner}", "T.Value"));
        var what = UiKit.Text(info.What, "T.Caption", margin: new Thickness(0, 2, 8, 0));
        what.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(what);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var (fg, bg) = info.Risk switch
        {
            PortRisk.High => ("Br.Critical", "Br.CriticalTint"),
            PortRisk.Medium => ("Br.Warn", "Br.WarnTint"),
            _ => ("Br.TextSecondary", "Br.SurfaceRaised")
        };
        var chip = UiKit.Chip(Ports.RiskName(info.Risk), UiKit.Brush(fg), UiKit.Brush(bg));
        chip.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(chip, 2);
        grid.Children.Add(chip);
        return grid;
    }

    private static string? ServiceName(string service) => service.ToLowerInvariant() switch
    {
        "_googlecast._tcp" => "Google Cast",
        "_airplay._tcp" => "AirPlay",
        "_raop._tcp" => "AirPlay (audio)",
        "_companion-link._tcp" => "Continuidad de Apple",
        "_apple-mobdev2._tcp" => "Sincronización de iPhone",
        "_ipp._tcp" or "_ipps._tcp" or "_printer._tcp" or "_pdl-datastream._tcp" => "Impresión",
        "_scanner._tcp" => "Escáner",
        "_spotify-connect._tcp" => "Spotify Connect",
        "_smb._tcp" => "Carpetas compartidas",
        "_workstation._tcp" => "Equipo en red",
        "_rdlink._tcp" => "Escritorio remoto",
        "_ssh._tcp" => "SSH",
        "_hap._tcp" or "_homekit._tcp" => "HomeKit",
        "_matter._tcp" => "Matter",
        "_hue._tcp" => "Philips Hue",
        "_miio._udp" => "Xiaomi Home",
        "_esphomelib._tcp" => "ESPHome",
        "_adb-tls-connect._tcp" or "_adb-tls-pairing._tcp" => "Depuración Android por Wi-Fi",
        "_http._tcp" => "Página web",
        "_sonos._tcp" => "Sonos",
        "_amzn-wplay._tcp" => "Fire TV",
        "_androidtvremote2._tcp" => "Android TV",
        "_mediaremotetv._tcp" => "Apple TV",
        "_nvstream._tcp" => "NVIDIA GameStream",
        "_daap._tcp" => "Música compartida",
        _ => null
    };

    // ────────────────────────── Actividad ──────────────────────────

    private void WatchSwitch_Click(object sender, RoutedEventArgs e)
    {
        Net.AutoWatch = WatchSwitch.IsChecked == true;
        Status(Net.AutoWatch ? "Vigilancia activada: cada 3 minutos." : "Vigilancia apagada. Sólo se analiza cuando pulses el botón.",
            ok: Net.AutoWatch);
    }

    private void RefreshActivity()
    {
        var online = AllRows().Where(r => r.Online && !r.Device.IsSelf).ToList();
        NowCaption.Text = Net.LastScan is null ? "Analizando…" : UiText.Plural(online.Count, "aparato conectado", "aparatos conectados");

        NowList.Children.Clear();
        foreach (var row in online.Take(30))
        {
            var since = row.Known?.Sessions.LastOrDefault()?.From;
            var line = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Path { Data = row.Glyph, Fill = row.GlyphBrush, Width = 15, Height = 15, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 0, 0) };
            line.Children.Add(icon);

            var text = new StackPanel();
            text.Children.Add(UiKit.Text(row.Name, "T.Value"));
            var today = row.Known is null ? TimeSpan.Zero : DeviceStore.OnlineSince(row.Known, DateTime.Today);
            text.Children.Add(UiKit.Text(
                (since is { } s ? $"Desde las {s:HH:mm}" : "Ahora") + $" · hoy {Duration(today)}" + (row.IsNew ? " · nuevo" : ""),
                "T.Caption", row.IsNew ? UiKit.Brush("Br.Warn") : null));
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            NowList.Children.Add(line);
        }

        EventList.Children.Clear();
        var events = Net.Store.EventsIn(Net.NetworkId).Take(150).ToList();
        if (events.Count == 0)
        {
            EventList.Children.Add(UiKit.EmptyState("Aún no hay nada. En cuanto un aparato entre o salga, aparece aquí."));
            return;
        }

        DateTime? day = null;
        foreach (var ev in events)
        {
            if (day != ev.At.Date)
            {
                day = ev.At.Date;
                EventList.Children.Add(UiKit.Text(DayName(ev.At), "T.Label",
                    margin: new Thickness(0, EventList.Children.Count == 0 ? 0 : 10, 0, 8)));
            }
            EventList.Children.Add(EventRow(ev, compact: false));
        }
    }

    private static FrameworkElement EventRow(NetEvent ev, bool compact)
    {
        var (verb, brush) = ev.Kind switch
        {
            NetEventKind.FirstSeen => ("se conectó por primera vez", "Br.Warn"),
            NetEventKind.Joined => ("se conectó", "Br.Positive"),
            NetEventKind.Left => ("se desconectó", "Br.TextTertiary"),
            _ => ("", "Br.Critical")
        };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, compact ? 9 : 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Ellipse
        {
            Width = 8, Height = 8, Fill = UiKit.Brush(brush),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0)
        });

        var text = new StackPanel();
        var title = verb.Length == 0 ? ev.Name : $"{ev.Name} {verb}";
        var titleBlock = UiKit.Text(title, "T.Value");
        titleBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        if (ev.Kind is NetEventKind.Duplicate or NetEventKind.GatewayChanged) titleBlock.Foreground = UiKit.Brush("Br.Critical");
        text.Children.Add(titleBlock);
        if (!compact || ev.Kind is NetEventKind.Duplicate or NetEventKind.GatewayChanged)
        {
            var detail = ev.Kind is NetEventKind.Duplicate or NetEventKind.GatewayChanged ? ev.Detail : ev.Detail?.Split('·').LastOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                var block = UiKit.Text(detail, "T.Caption", margin: new Thickness(0, 2, 10, 0));
                block.TextWrapping = TextWrapping.Wrap;
                text.Children.Add(block);
            }
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var when = UiKit.Text(compact ? Ago(ev.At) : ev.At.ToString("HH:mm", Spanish), "T.Caption");
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);
        return grid;
    }

    private static string DayName(DateTime at)
    {
        if (at.Date == DateTime.Today) return "HOY";
        if (at.Date == DateTime.Today.AddDays(-1)) return "AYER";
        return at.ToString("dddd d 'de' MMMM", Spanish).ToUpperInvariant();
    }

    // ────────────────────────── Velocidad ──────────────────────────

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (_speedCts is not null)
        {
            _speedCts.Cancel();
            return;
        }

        var cts = new CancellationTokenSource();
        _speedCts = cts;
        SpeedButton.Content = "Detener";
        SpeedVerdict.Visibility = Visibility.Collapsed;
        SpeedDown.Text = SpeedUp.Text = SpeedPing.Text = SpeedJitter.Text = "—";
        SpeedMeter.Value = 0;
        SpeedPhaseText.Text = "Conectando con el servidor más cercano…";

        var progress = new Progress<SpeedProgress>(p =>
        {
            switch (p.Phase)
            {
                case SpeedPhase.Latency:
                    SpeedPhaseText.Text = "Midiendo el ping…";
                    SpeedPing.Text = $"{p.PingMs:0}";
                    SpeedMeter.Value = p.Fraction * 10;
                    break;
                case SpeedPhase.Download:
                    SpeedPhaseText.Text = "Midiendo la bajada…";
                    SpeedDown.Text = Mbps(p.Mbps);
                    SpeedMeter.Value = 10 + p.Fraction * 45;
                    break;
                case SpeedPhase.Upload:
                    SpeedPhaseText.Text = "Midiendo la subida…";
                    SpeedUp.Text = Mbps(p.Mbps);
                    SpeedMeter.Value = 55 + p.Fraction * 45;
                    break;
            }
        });

        try
        {
            var publicTask = Net.Public is null ? LocalNetwork.PublicAsync(cts.Token) : Task.FromResult(Net.Public);
            var result = await SpeedTest.RunAsync(progress, cts.Token);
            var info = await publicTask;

            result.Isp = info?.Isp;
            result.Server = info?.City is { } city ? $"{city} ({info.Colo})" : info?.Colo;
            result.Network = Net.NetworkName;
            Net.Store.AddSpeed(result);

            SpeedDown.Text = Mbps(result.DownMbps);
            SpeedUp.Text = Mbps(result.UpMbps);
            SpeedPing.Text = $"{result.PingMs:0}";
            SpeedJitter.Text = $"{result.JitterMs:0}";
            SpeedMeter.Value = 100;
            SpeedPhaseText.Text = "Terminado.";
            SpeedVerdict.Text = SpeedTest.Verdict(result);
            SpeedVerdict.Visibility = Visibility.Visible;
            SpeedServer.Text = string.Join(" · ", new[]
            {
                result.Isp is null ? null : "Operador: " + result.Isp,
                result.Server is null ? null : "Servidor de Cloudflare en " + result.Server,
                info?.Ip is null ? null : "IP pública " + info.Ip
            }.Where(s => s is not null));

            RefreshSpeedHistory();
            RefreshSummary();
        }
        catch (OperationCanceledException)
        {
            SpeedPhaseText.Text = "Test detenido.";
            SpeedMeter.Value = 0;
        }
        catch (Exception ex)
        {
            SpeedPhaseText.Text = "No se pudo completar el test: " +
                                  (ex is HttpRequestException ? "no hay conexión con el servidor." : ex.Message);
        }
        finally
        {
            _speedCts = null;
            SpeedButton.Content = "Empezar";
        }
    }

    private static string Mbps(double value) => value >= 100 ? $"{value:0}" : $"{value:0.0}";

    private void RefreshSpeedHistory()
    {
        SpeedHistory.Children.Clear();
        var history = Net.Store.Speeds.AsEnumerable().Reverse().Take(15).ToList();
        if (history.Count == 0)
        {
            SpeedHistory.Children.Add(UiKit.EmptyState("Todavía no has medido la velocidad."));
            return;
        }

        var best = Math.Max(1, history.Max(h => h.DownMbps));
        foreach (var r in history)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

            var when = new StackPanel();
            when.Children.Add(UiKit.Text(r.At.ToString("d MMM · HH:mm", Spanish), "T.Value"));
            when.Children.Add(UiKit.Text(r.Network ?? "", "T.Caption"));
            grid.Children.Add(when);

            var bar = new ProgressBar { Style = UiKit.Style("Meter"), Maximum = 100, Value = r.DownMbps * 100 / best,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);

            var numbers = UiKit.Text($"↓ {Mbps(r.DownMbps)}   ↑ {Mbps(r.UpMbps)} Mbps   ·   {r.PingMs:0} ms", "T.Value");
            numbers.HorizontalAlignment = HorizontalAlignment.Right;
            numbers.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(numbers, 2);
            grid.Children.Add(numbers);

            SpeedHistory.Children.Add(grid);
        }
    }

    // ────────────────────────── Wi-Fi ──────────────────────────

    private void RefreshWifiCurrent()
    {
        var wifi = Net.Wifi;
        var link = Net.Link;
        LocationBanner.Visibility = Vis(Net.WifiLocationBlocked || _nearby?.LocationBlocked == true);

        if (wifi is null)
        {
            WifiName.Text = link is null ? "Sin conexión" : link.Wireless ? "Wi-Fi" : "Conectado por cable";
            WifiSignal.Value = link is { Wireless: false } ? 100 : 0;
            WifiSignalText.Text = link is { Wireless: false } ? "Cable" : "—";
            UiKit.FillSpecs(WifiSpecs, new (string, string?)[]
            {
                ("Adaptador", link?.Description),
                ("Velocidad", link is null ? null : LinkSpeed(link.SpeedBps))
            }.Where(s => s.Item2 is not null), labelWidth: 112);
            WifiAdvice.Text = link is { Wireless: false }
                ? "Por cable no hay señal que mejorar: es la conexión más estable."
                : "";
            return;
        }

        WifiName.Text = wifi.Ssid ?? "Wi-Fi";
        var signal = wifi.Signal ?? 0;
        WifiSignal.Value = signal;
        WifiSignal.Foreground = UiKit.Brush(signal >= 60 ? "Br.Positive" : signal >= 35 ? "Br.Warn" : "Br.Critical");
        WifiSignalText.Text = $"{signal} %  ·  {LocalNetwork.SignalDbm(signal)} dBm aprox.";

        var generation = LocalNetwork.WifiGeneration(wifi.Radio);
        UiKit.FillSpecs(WifiSpecs, new (string, string?)[]
        {
            ("Seguridad", string.Join(" · ", new[] { wifi.Authentication, wifi.Cipher }.Where(s => s is not null))),
            ("Estándar", generation is null ? wifi.Radio : $"{generation} ({wifi.Radio})"),
            ("Banda", wifi.Band),
            ("Canal", wifi.Channel?.ToString()),
            ("Enlace", wifi.ReceiveMbps is { } rx ? $"{rx:0} Mbps bajada · {wifi.TransmitMbps:0} subida" : null),
            ("Punto de acceso", wifi.Bssid),
            ("Adaptador", link?.Description)
        }.Where(s => !string.IsNullOrWhiteSpace(s.Item2)), labelWidth: 112);

        var advice = new List<string>();
        if (signal < 35) advice.Add("La señal es débil: acércate al router, quita obstáculos o pon un repetidor o un sistema mesh.");
        else if (signal < 60) advice.Add("Señal aceptable. Si notas cortes, prueba a acercarte al router.");
        else advice.Add("Señal buena.");
        if (wifi.Band?.Contains("2.4") == true || wifi.Band?.Contains("2,4") == true || wifi.Channel is >= 1 and <= 14)
            advice.Add("Estás en 2,4 GHz: llega más lejos pero es más lento y más concurrido. Si tu router tiene 5 GHz, úsala cerca de él.");
        if (generation is "Wi-Fi 4" or "Wi-Fi antiguo")
            advice.Add("El estándar es antiguo: un router o un adaptador Wi-Fi 6 iría bastante más rápido.");
        WifiAdvice.Text = string.Join(" ", advice);
    }

    private void WifiRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshNearbyAsync();

    private void LocationSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-location");

    private async Task RefreshNearbyAsync()
    {
        NearbyList.Children.Clear();
        NearbyList.Children.Add(UiKit.EmptyState("Buscando redes…"));
        _nearby = await Task.Run(LocalNetwork.Nearby);
        RefreshWifiCurrent();

        NearbyList.Children.Clear();
        if (_nearby.NoWifi)
        {
            NearbyList.Children.Add(UiKit.EmptyState("Este PC no tiene Wi-Fi o está apagado."));
            ChannelAdvice.Text = "";
            return;
        }
        if (_nearby.LocationBlocked)
        {
            NearbyList.Children.Add(UiKit.EmptyState("Hace falta el permiso de ubicación para ver las redes (arriba)."));
            ChannelAdvice.Text = "";
            return;
        }

        var groups = _nearby.Networks
            .GroupBy(n => n.Ssid)
            .Select(g => (Best: g.OrderByDescending(n => n.Signal).First(), Count: g.Count()))
            .OrderByDescending(g => g.Best.Ssid == Net.Wifi?.Ssid)
            .ThenByDescending(g => g.Best.Signal)
            .ToList();

        ChannelAdvice.Text = ChannelText(_nearby.Networks);
        RouterTips.Text = RouterTipsText();

        foreach (var (net, count) in groups.Take(40))
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            var text = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(UiKit.Text(net.Ssid, "T.Value"));
            if (net.Ssid == Net.Wifi?.Ssid)
            {
                var mine = UiKit.Chip("Tu red");
                mine.Margin = new Thickness(8, 0, 0, 0);
                title.Children.Add(mine);
            }
            text.Children.Add(title);
            text.Children.Add(UiKit.Text(string.Join(" · ", new[]
            {
                net.Band, net.Channel is { } ch ? $"canal {ch}" : null, LocalNetwork.WifiGeneration(net.Radio),
                count > 1 ? $"{count} puntos de acceso" : null
            }.Where(s => s is not null)), "T.Caption", margin: new Thickness(0, 2, 0, 0)));
            grid.Children.Add(text);

            var (label, fg, bg) = SecurityLabel(net.Authentication, net.Cipher);
            var chip = UiKit.Chip(label, UiKit.Brush(fg), UiKit.Brush(bg));
            chip.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);

            var bar = new ProgressBar { Style = UiKit.Style("Meter"), Maximum = 100, Value = net.Signal,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Señal {net.Signal} %",
                Foreground = UiKit.Brush(net.Signal >= 60 ? "Br.Positive" : net.Signal >= 35 ? "Br.Warn" : "Br.Critical") };
            Grid.SetColumn(bar, 2);
            grid.Children.Add(bar);

            NearbyList.Children.Add(grid);
        }

        if (groups.Count == 0) NearbyList.Children.Add(UiKit.EmptyState("No se ve ninguna red."));
    }

    private static (string Label, string Fg, string Bg) SecurityLabel(string? auth, string? cipher)
    {
        var a = (auth ?? "").ToLowerInvariant();
        var c = (cipher ?? "").ToLowerInvariant();
        if (a.Contains("open") || a.Contains("abierta") || c is "none" or "ninguno") return ("Abierta", "Br.Critical", "Br.CriticalTint");
        if (a.Contains("wep") || c.Contains("wep")) return ("WEP (rota)", "Br.Critical", "Br.CriticalTint");
        if (a.Contains("wpa3")) return ("WPA3", "Br.Positive", "Br.PositiveTint");
        if (a.Contains("wpa2")) return ("WPA2", "Br.TextSecondary", "Br.SurfaceRaised");
        if (a.Contains("wpa")) return ("WPA (antigua)", "Br.Warn", "Br.WarnTint");
        return (auth ?? "—", "Br.TextSecondary", "Br.SurfaceRaised");
    }

    /// <summary>
    /// En 2,4 GHz sólo los canales 1, 6 y 11 no se pisan entre sí: se cuenta
    /// cuántas redes caen cerca de cada uno y se recomienda el más libre.
    /// </summary>
    private string ChannelText(IReadOnlyList<WifiNetwork> networks)
    {
        var low = networks.Where(n => n.Channel is >= 1 and <= 14).ToList();
        if (low.Count == 0) return "";

        int Crowd(int channel) => low.Count(n => Math.Abs(n.Channel!.Value - channel) <= 2);
        var options = new[] { 1, 6, 11 }.Select(ch => (Channel: ch, Crowd: Crowd(ch))).OrderBy(x => x.Crowd).ToList();
        var best = options[0];

        var mine = Net.Wifi?.Channel;
        if (mine is >= 1 and <= 14)
        {
            var myCrowd = Crowd(mine.Value) - 1;
            return myCrowd <= best.Crowd
                ? $"Tu red usa el canal {mine} de 2,4 GHz, de los más despejados ahora mismo."
                : $"Tu red usa el canal {mine} de 2,4 GHz y lo comparte con {myCrowd} redes. El más libre ahora es el {best.Channel}: " +
                  "puedes cambiarlo en la configuración Wi-Fi del router.";
        }
        return $"En 2,4 GHz el canal más libre ahora mismo es el {best.Channel} ({UiText.Plural(best.Crowd, "red", "redes")} cerca).";
    }

    // ────────────────────────── Acelerar ──────────────────────────

    private async Task RefreshBoostAsync()
    {
        _boostLoaded = true;
        RouterTips.Text = RouterTipsText();
        WifiPowerSwitch.IsEnabled = false;
        var (ac, dc) = await Task.Run(WifiBoost.PowerSaving);

        if (ac is null || dc is null)
        {
            WifiPowerState.Text = "No disponible";
            return;
        }

        var max = ac == 0 && dc == 0;
        WifiPowerSwitch.IsChecked = max;
        WifiPowerSwitch.IsEnabled = true;
        WifiPowerState.Text = max ? "Siempre a tope" : "Ahorra con batería";
        WifiPowerState.Foreground = UiKit.Brush(max ? "Br.Positive" : "Br.TextTertiary");
    }

    private async void WifiPower_Click(object sender, RoutedEventArgs e)
    {
        var on = WifiPowerSwitch.IsChecked == true;
        WifiPowerSwitch.IsEnabled = false;
        var ok = await Task.Run(() => WifiBoost.SetMaxPerformance(on));
        await RefreshBoostAsync();
        Status(ok
            ? on ? "El Wi-Fi ya no ahorra energía: menos ping y menos cortes." : "El Wi-Fi vuelve a ahorrar energía con batería."
            : "Windows no dejó cambiar el ahorro de energía del Wi-Fi.", ok);
    }

    private async void DnsTest_Click(object sender, RoutedEventArgs e)
    {
        if (Net.Link is not { } link) return;

        _dnsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _dnsCts = cts;

        DnsTestButton.IsEnabled = false;
        DnsTestButton.Content = "Midiendo…";
        DnsResults.Children.Clear();
        DnsResults.Children.Add(UiKit.EmptyState("Preguntando a cada servidor…"));

        List<DnsTiming> timings;
        try { timings = await WifiBoost.BenchmarkAsync(link.Dns, cts.Token); }
        catch { timings = new(); }
        finally
        {
            DnsTestButton.IsEnabled = true;
            DnsTestButton.Content = "Medir otra vez";
        }
        if (cts.IsCancellationRequested) return;
        RenderDns(timings, link);
    }

    private void RenderDns(List<DnsTiming> timings, LocalLink link)
    {
        DnsResults.Children.Clear();
        if (timings.All(t => t.Milliseconds is null))
        {
            DnsResults.Children.Add(UiKit.EmptyState("Ningún servidor contestó. Comprueba la conexión a internet."));
            return;
        }

        var fastest = timings.First(t => t.Milliseconds is not null);
        var current = timings.FirstOrDefault(t => t.Current);

        foreach (var t in timings)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(UiKit.Text(t.Name, "T.Value"));
            text.Children.Add(UiKit.Text("  " + t.Server, "T.Mono", UiKit.Brush("Br.TextTertiary")));
            if (ReferenceEquals(t, fastest))
            {
                var chip = UiKit.Chip("El más rápido", UiKit.Brush("Br.Positive"), UiKit.Brush("Br.PositiveTint"));
                chip.Margin = new Thickness(10, 0, 0, 0);
                text.Children.Add(chip);
            }
            grid.Children.Add(text);

            var ms = UiKit.Text(t.Milliseconds is { } v ? $"{v:0} ms" : "sin respuesta", "T.Value",
                UiKit.Brush(ReferenceEquals(t, fastest) ? "Br.Positive" : "Br.TextSecondary"));
            ms.HorizontalAlignment = HorizontalAlignment.Right;
            ms.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ms, 1);
            grid.Children.Add(ms);

            var provider = WifiBoost.Providers.FirstOrDefault(p => p.Ip == t.Server.ToString());
            if (!t.Current && provider.Name is not null)
            {
                var use = new Button { Content = "Usar", Style = UiKit.Style("BtnGhost"), Padding = new Thickness(14, 5, 14, 5),
                    HorizontalAlignment = HorizontalAlignment.Right };
                use.Click += async (_, _) => await UseDnsAsync(link, provider.Name, provider.Ip, provider.Secondary);
                Grid.SetColumn(use, 2);
                grid.Children.Add(use);
            }
            DnsResults.Children.Add(grid);
        }

        string advice;
        if (current is null) advice = "";
        else if (ReferenceEquals(current, fastest)) advice = "Ya usas el más rápido.";
        else if (current.Milliseconds is not { } now) advice = $"Tu DNS no respondió: prueba con {fastest.Name}.";
        else
        {
            var gain = now - fastest.Milliseconds!.Value;
            advice = gain >= 10
                ? $"{fastest.Name} te responde {gain:0} ms antes que el tuyo: merece la pena cambiar."
                : "La diferencia es pequeña: puedes dejarlo como está.";
        }
        if (advice.Length > 0) DnsResults.Children.Add(UiKit.Text(advice, "T.Caption", margin: new Thickness(0, 2, 0, 6)));

        var reset = new Button { Content = "Volver al DNS automático (el del router)", Style = UiKit.Style("BtnLink"),
            HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += async (_, _) => await UseDnsAsync(link, null, null, null);
        DnsResults.Children.Add(reset);
    }

    private async Task UseDnsAsync(LocalLink link, string? name, string? primary, string? secondary)
    {
        var ok = await WifiBoost.SetDnsAsync(link.Name, primary, secondary);
        if (!ok)
        {
            Status("No se cambió el DNS: hace falta aceptar el permiso de administrador.", ok: false);
            return;
        }

        Status(name is null ? "DNS automático restaurado." : $"Ahora usas el DNS de {name}.", ok: true);
        await Net.ScanAsync(deep: false);
        DnsTest_Click(this, new RoutedEventArgs());
    }

    private string RouterTipsText()
    {
        var tips = new List<string>
        {
            "Pon el router en alto, en el centro de la casa y lejos del microondas, de espejos y de paredes gruesas."
        };
        if (Net.Wifi?.Channel is >= 1 and <= 14)
            tips.Add("Cerca del router, conéctate a la red de 5 GHz: es mucho más rápida. La de 2,4 GHz sólo conviene lejos de él.");
        if (_nearby is { Networks.Count: > 0 } && ChannelText(_nearby.Networks) is { Length: > 0 } channel) tips.Add(channel);
        tips.Add("Si lleva semanas sin apagarse, reinícialo: muchos routers se van ralentizando.");
        tips.Add("Si a alguna habitación llega poca señal, un sistema mesh o un repetidor lo arregla mejor que cualquier ajuste.");
        tips.Add("Para jugar o hacer videollamadas, el cable de red siempre gana al Wi-Fi.");
        return string.Join("\n", tips.Select(t => "•  " + t));
    }

    // ────────────────────────── Seguridad ──────────────────────────

    private async Task RefreshSecurityAsync()
    {
        try { _checks = await Task.Run(() => SecurityAudit.Run(Net)); }
        catch { return; }

        var score = SecurityAudit.Score(_checks);
        var bad = _checks.Count(c => c.State == CheckState.Bad);
        var warn = _checks.Count(c => c.State == CheckState.Warn);

        ScoreText.Text = score.ToString();
        ScoreMeter.Value = score;
        ScoreMeter.Foreground = UiKit.Brush(score >= 85 ? "Br.Positive" : score >= 65 ? "Br.Warn" : "Br.Critical");
        ScoreTitle.Text = bad > 0 ? "Hay cosas importantes que arreglar" :
            warn > 0 ? "Bien, con detalles por mejorar" : "Tu red está bien protegida";
        ScoreCaption.Text = bad + warn == 0
            ? $"{UiText.Plural(_checks.Count, "comprobación", "comprobaciones")} sin problemas."
            : string.Join(" · ", new[]
            {
                bad > 0 ? UiText.Plural(bad, "problema grave", "problemas graves") : null,
                warn > 0 ? UiText.Plural(warn, "aviso", "avisos") : null
            }.Where(s => s is not null)) + ". Empieza por lo de arriba.";

        SumSecurity.Text = $"{score}";
        SumSecurityCaption.Text = bad + warn == 0 ? "Todo en orden" : ScoreCaption.Text.Split('.')[0];

        SecurityList.Children.Clear();
        foreach (var check in _checks) SecurityList.Children.Add(CheckRow(check));
    }

    private void SecurityRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshSecurityAsync();

    private FrameworkElement CheckRow(SecurityCheck check)
    {
        var (glyph, brush) = check.State switch
        {
            CheckState.Good => ("Glyph.Check", "Br.Positive"),
            CheckState.Info => ("Glyph.Info", "Br.AccentBright"),
            CheckState.Warn => ("Glyph.Info", "Br.Warn"),
            _ => ("Glyph.Info", "Br.Critical")
        };

        var card = new Border { Style = UiKit.Style("Card"), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(18, 14, 18, 14) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Path
        {
            Data = (Geometry)FindResource(glyph), Width = 16, Height = 16, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
        };
        if (check.State == CheckState.Good)
        {
            icon.Stroke = UiKit.Brush(brush);
            icon.StrokeThickness = 2.6;
            icon.StrokeStartLineCap = icon.StrokeEndLineCap = PenLineCap.Round;
            icon.StrokeLineJoin = PenLineJoin.Round;
        }
        else icon.Fill = UiKit.Brush(brush);
        grid.Children.Add(icon);

        var text = new StackPanel();
        text.Children.Add(UiKit.Text(check.Title, "T.Value"));
        var detail = UiKit.Text(check.Detail, "T.Caption", margin: new Thickness(0, 3, 16, 0));
        detail.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (check.ActionText is { } label && check.Action is { } action)
        {
            var button = new Button { Content = label, Style = UiKit.Style("BtnGhost"), Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => RunAction(action);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        card.Child = grid;
        return card;
    }

    private void RunAction(string action)
    {
        if (action.StartsWith("settings:")) Open(action["settings:".Length..]);
        else if (action == "router") OpenRouter();
        else if (action == "devices") TabDevices.IsChecked = true;
        else if (action == "activity") TabActivity.IsChecked = true;
        else if (action == "connections") TabConnections.IsChecked = true;
        else if (action.StartsWith("run:")) Open(action["run:".Length..]);
        else if (action.StartsWith("file:"))
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{action["file:".Length..]}\"") { UseShellExecute = true }); }
            catch { }
        }
    }

    // ────────────────────────── Conexiones ──────────────────────────

    private void ConnectionsRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshConnectionsAsync();

    private async Task RefreshConnectionsAsync()
    {
        _connectionsAt = DateTime.Now;
        ConnCaption.Text = "Leyendo…";

        List<TcpConnection> connections;
        List<Listener> listeners;
        try
        {
            connections = await Task.Run(Connections.Tcp);
            listeners = await Task.Run(Connections.Listeners);
        }
        catch
        {
            ConnCaption.Text = "No se pudieron leer las conexiones.";
            return;
        }

        var internet = connections.Where(c => c.Established && c.ToInternet).ToList();
        var groups = internet.GroupBy(c => c.Pid)
            .Select(g => (First: g.First(), Items: g.ToList()))
            .OrderByDescending(g => g.Items.Count)
            .ToList();

        ConnCaption.Text = $"{UiText.Plural(internet.Count, "conexión", "conexiones")} de " +
                           $"{UiText.Plural(groups.Count, "programa", "programas")} ahora mismo.";

        ConnList.Children.Clear();
        if (groups.Count == 0) ConnList.Children.Add(UiKit.EmptyState("Ningún programa está conectado a internet ahora."));

        foreach (var (first, items) in groups.Take(30))
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var image = ProcessesView.Icon(first.Path);
            if (image is not null)
                grid.Children.Add(new Image { Source = image, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top });

            var text = new StackPanel();
            text.Children.Add(UiKit.Text(first.Process, "T.Value"));
            var hosts = UiKit.Text(string.Join(", ", items.Select(i => i.RemoteIp.ToString()).Distinct().Take(3)), "T.Caption",
                margin: new Thickness(0, 2, 10, 0));
            hosts.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(hosts);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var count = UiKit.Text(items.Count.ToString(), "T.Value", UiKit.Brush("Br.TextSecondary"));
            count.ToolTip = "Conexiones abiertas";
            Grid.SetColumn(count, 2);
            grid.Children.Add(count);
            ConnList.Children.Add(grid);

            _ = ResolveHostsAsync(hosts, items.Select(i => i.RemoteIp).Distinct().Take(4).ToList());
        }

        var exposed = listeners
            .Where(l => l.Exposed && (!l.Udp || Ports.Describe(l.Port).Name != $"Puerto {l.Port}"))
            .GroupBy(l => (l.Port, l.Udp))
            .Select(g => g.First())
            .OrderByDescending(l => Ports.Describe(l.Port).Risk)
            .ThenBy(l => l.Port)
            .ToList();

        ListenCaption.Text = exposed.Count == 0
            ? "Nada de este PC acepta conexiones de otros aparatos."
            : "Aceptan conexiones desde otros aparatos de la red. Lo marcado en rojo conviene cerrarlo si no lo usas.";

        ListenList.Children.Clear();
        foreach (var l in exposed.Take(40))
            ListenList.Children.Add(PortRow(Ports.Describe(l.Port), l.Process + (l.Udp ? " (UDP)" : "")));
    }

    private static async Task ResolveHostsAsync(TextBlock target, List<IPAddress> ips)
    {
        var names = new List<string>();
        foreach (var ip in ips)
        {
            var host = await Connections.HostOfAsync(ip, CancellationToken.None);
            names.Add(host ?? ip.ToString());
        }
        target.Text = string.Join(", ", names.Distinct());
    }

    // ────────────────────────── Apoyo ──────────────────────────

    private void Status(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = UiKit.Brush(ok ? "Br.Positive" : "Br.Critical");
    }

    public static string Ago(DateTime at)
    {
        var elapsed = DateTime.Now - at;
        if (elapsed.TotalMinutes < 1) return "hace un momento";
        if (elapsed.TotalHours < 1) return $"hace {(int)elapsed.TotalMinutes} min";
        if (at.Date == DateTime.Today) return $"hace {(int)elapsed.TotalHours} h";
        if (at.Date == DateTime.Today.AddDays(-1)) return $"ayer a las {at:HH:mm}";
        return at.ToString("d MMM, HH:mm", Spanish);
    }

    private static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "menos de un minuto";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min";
        return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string GlyphKey(DeviceKind kind) => kind switch
    {
        DeviceKind.Router => "Dev.Router",
        DeviceKind.Phone => "Dev.Phone",
        DeviceKind.Tablet => "Dev.Tablet",
        DeviceKind.Computer => "Dev.Laptop",
        DeviceKind.Tv => "Dev.Tv",
        DeviceKind.Console => "Dev.Console",
        DeviceKind.Printer => "Dev.Printer",
        DeviceKind.Speaker => "Dev.Speaker",
        DeviceKind.Camera => "Dev.Camera",
        DeviceKind.Watch => "Dev.Watch",
        DeviceKind.SmartHome => "Dev.SmartHome",
        DeviceKind.Storage => "Dev.Storage",
        _ => "Dev.Unknown"
    };

    // ────────────────────────── Fila de aparato ──────────────────────────

    public sealed class DeviceRow : INotifyPropertyChanged
    {
        public DeviceRow(NetDevice device, KnownDevice? known, bool online, DateTime inventoryAt)
        {
            Device = device;
            Known = known;
            Online = online;
            IsNew = known is not null && !device.IsSelf && known.FirstSeen > inventoryAt &&
                    DateTime.Now - known.FirstSeen < TimeSpan.FromDays(2) && !known.Trusted;
        }

        public NetDevice Device { get; }
        public KnownDevice? Known { get; }
        public bool Online { get; }
        public bool IsNew { get; }

        public string Key => Device.Key;
        public string Name => DeviceIdentity.DisplayName(Device, Known?.Alias);
        public string IpText => Device.IpText;

        public string SubText
        {
            get
            {
                var parts = new List<string?>();
                if (Device.IsGateway) parts.Add("Tu router");
                var model = Device.Model;
                if (Device.Brand is not null && (model is null || !model.StartsWith(Device.Brand))) parts.Add(Device.Brand);
                if (model is not null && model != Name) parts.Add(model);
                if (parts.Count == 0 || (Device.Kind != DeviceKind.Unknown && parts.All(p => p == Device.Brand)))
                    parts.Add(DeviceIdentity.KindName(Device.Kind) + (Device.KindIsGuess ? " (probable)" : ""));
                if (Device.RandomMac && Device.Brand is null) parts.Add("dirección privada");
                return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
        }

        public Geometry Glyph => (Geometry)Application.Current.FindResource(GlyphKey(Device.Kind));

        public Brush GlyphBrush => UiKit.Brush(!Online ? "Br.TextTertiary" : IsNew ? "Br.Warn" : "Br.AccentBright");
        public Brush TileBrush => UiKit.Brush(!Online ? "Br.SurfaceRaised" : IsNew ? "Br.WarnTint" : "Br.AccentTint");

        public string Badge => Device.IsSelf ? "Tú" : Device.IsGateway ? "Router" : IsNew ? "Nuevo" :
            Known is { Trusted: false } && Online ? "Sin reconocer" : "";
        public Visibility BadgeVisibility => Badge.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Brush BadgeBrush => UiKit.Brush(IsNew || Badge == "Sin reconocer" ? "Br.WarnTint" : "Br.AccentTint");
        public Brush BadgeForeground => UiKit.Brush(IsNew || Badge == "Sin reconocer" ? "Br.Warn" : "Br.AccentBright");

        public string StateText => Online ? "Conectado" : Known is null ? "Desconectado" : Ago(Known.LastSeen);
        public Brush StateBrush => UiKit.Brush(Online ? "Br.Positive" : "Br.TextTertiary");

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
