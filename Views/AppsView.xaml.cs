using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class AppsView : UserControl
{
    private static readonly CultureInfo Spanish = new("es-ES");

    private List<AppCard> _apps = new();
    private readonly ObservableCollection<AppRow> _rows = new();

    /// <summary>Lo ya medido de cada aplicación: medir una carpeta entera no es gratis.</summary>
    private readonly Dictionary<string, Measurement> _measured = new();

    private CancellationTokenSource? _measureCts;
    private bool _loaded;
    private string? _selectedId;

    private Func<Task>? _dialogPrimary;
    private Func<Task>? _dialogSecondary;
    private List<CheckItem> _checks = new();
    private bool _dialogBusy;

    public AppsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private sealed record Measurement(long Install, List<AppFolder> Data, List<AppCacheSpot> Cache)
    {
        public long DataSize => Data.Sum(d => d.Size);
        public long CacheSize => Cache.Sum(c => c.Size);
        public long Total => Install + DataSize;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        RowsList.ItemsSource = _rows;
        await ReloadAsync();
    }

    // ────────────────────────── Inventario ──────────────────────────

    private async Task ReloadAsync()
    {
        ReloadButton.IsEnabled = false;
        EmptyText.Text = "Leyendo las aplicaciones instaladas…";
        EmptyText.Visibility = Visibility.Visible;

        try { _apps = await Task.Run(AppCatalog.All); }
        catch { _apps = new List<AppCard>(); }

        _measured.Clear();
        ReloadButton.IsEnabled = true;
        Rebuild();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void Rebuild()
    {
        // Vaciar la lista borra la selección, así que se guarda antes.
        var keep = _selectedId;
        var visible = Visible();

        _rows.Clear();
        foreach (var app in visible) _rows.Add(new AppRow(app, Size(app)));

        CountChip.Text = _apps.Count == 0
            ? "—"
            : $"{_apps.Count:N0} aplicaciones · {UiText.Size(_apps.Sum(a => Size(a)))}";

        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible.Count == 0 && _apps.Count > 0)
            EmptyText.Text = SearchBox.Text.Length > 0
                ? $"Ninguna aplicación coincide con «{SearchBox.Text}»."
                : "No hay aplicaciones que mostrar con este filtro.";

        // Si la seleccionada sigue en la lista, se mantiene seleccionada.
        var row = keep is null ? null : _rows.FirstOrDefault(r => r.App.Id == keep);
        if (row is not null) RowsList.SelectedItem = row;
        else ShowDetail(null);
    }

    private List<AppCard> Visible()
    {
        var query = SearchBox.Text.Trim();
        IEnumerable<AppCard> list = _apps;

        if (FilterPrograms.IsChecked == true) list = list.Where(a => a.Origin == AppOrigin.Installer);
        else if (FilterStore.IsChecked == true) list = list.Where(a => a.Origin == AppOrigin.Store);

        if (query.Length > 0)
            list = list.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (a.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        list = SortName.IsChecked == true
            ? list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            : SortDate.IsChecked == true
                ? list.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue).ThenBy(a => a.Name)
                : list.OrderByDescending(a => Size(a)).ThenBy(a => a.Name);

        return list.ToList();
    }

    /// <summary>Lo medido si ya se midió; si no, lo que el instalador declaró.</summary>
    private long Size(AppCard app) =>
        _measured.TryGetValue(app.Id, out var m) && m.Total > 0 ? m.Total : app.ReportedSize;

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loaded) Rebuild();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _loaded) Rebuild();
    }

    // ────────────────────────── Ficha ──────────────────────────

    private AppCard? Selected => (RowsList.SelectedItem as AppRow)?.App;

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var app = Selected;
        _selectedId = app?.Id;
        ShowDetail(app);
        if (app is not null) _ = MeasureAsync(app);
    }

    private void ShowDetail(AppCard? app)
    {
        if (app is null)
        {
            DetailHint.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            return;
        }

        DetailHint.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        DetailTitle.Text = app.Name;
        DetailPublisher.Text = app.Publisher ?? "Fabricante desconocido";
        DetailOrigin.Text = app.OriginLabel;

        var icon = IconFor(app);
        DetailImage.Source = icon;
        DetailImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        DetailGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;

        var measured = _measured.TryGetValue(app.Id, out var m) ? m : null;

        var specs = new List<(string, string?)>();
        if (app.Version is not null) specs.Add(("Versión", app.Version));
        if (app.InstallDate is { } date) specs.Add(("Instalada el", date.ToString("d 'de' MMMM 'de' yyyy", Spanish)));

        specs.Add(("Programa", measured is null
            ? "calculando…"
            : measured.Install > 0
                ? UiText.Size(measured.Install)
                : app.ReportedSize > 0 ? $"≈ {UiText.Size(app.ReportedSize)}" : "no se pudo medir"));

        specs.Add(("Tus datos", measured is null
            ? "calculando…"
            : measured.Data.Count == 0
                ? "no guarda datos aparte"
                : $"{UiText.Size(measured.DataSize)} en {UiText.Plural(measured.Data.Count, "carpeta", "carpetas")}"));

        specs.Add(("Caché", measured is null
            ? "calculando…"
            : measured.CacheSize > 0 ? UiText.Size(measured.CacheSize) : "nada que borrar"));

        UiKit.FillSpecs(DetailSpecs, specs, labelWidth: 104);

        DetailPath.Text = app.InstallLocation ?? (app.Origin == AppOrigin.Store
            ? "Las aplicaciones de la Store viven en una carpeta protegida de Windows."
            : "El instalador no dijo dónde está su carpeta.");

        CacheButton.IsEnabled = measured is null || measured.CacheSize > 0;
        CacheButton.Content = measured is null || measured.CacheSize == 0
            ? "Borrar caché"
            : $"Borrar caché · {UiText.Size(measured.CacheSize)}";

        UninstallButton.IsEnabled = app.CanUninstall;
        UninstallButton.ToolTip = app.CanUninstall
            ? "Abrir el desinstalador y después buscar lo que deje"
            : "Esta aplicación no dice cómo desinstalarse: hazlo desde Configuración de Windows.";

        RevealButton.IsEnabled = app.InstallLocation is { Length: > 0 } location && Directory.Exists(location);
    }

    private async Task<Measurement> MeasureAsync(AppCard app)
    {
        if (_measured.TryGetValue(app.Id, out var cached)) return cached;

        _measureCts?.Cancel();
        var cts = new CancellationTokenSource();
        _measureCts = cts;

        Measurement result;
        try
        {
            result = await Task.Run(() =>
            {
                var install = AppCatalog.InstallSize(app);
                var data = AppCatalog.DataFolders(app);
                var cache = AppCache.Find(data, cts.Token);
                return new Measurement(install, data, cache);
            }, cts.Token);
        }
        catch (OperationCanceledException) { return new Measurement(0, new(), new()); }
        catch { return new Measurement(0, new(), new()); }

        _measured[app.Id] = result;

        // La fila muestra el tamaño real en cuanto se conoce.
        var row = _rows.FirstOrDefault(r => r.App.Id == app.Id);
        row?.SetSize(Size(app));

        if (_selectedId == app.Id) ShowDetail(app);
        return result;
    }

    private static ImageSource? IconFor(AppCard app)
    {
        if (app.Entry is { } entry) return ShellIcons.ForApp(entry, null);

        try
        {
            if (app.InstallLocation is { Length: > 0 } folder && Directory.Exists(folder))
            {
                var exe = Directory.EnumerateFiles(folder, "*.exe").FirstOrDefault();
                if (exe is not null) return ShellIcons.ForFile(exe);
            }
        }
        catch { }
        return null;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.InstallLocation is { Length: > 0 } location) AppManager.Reveal(location);
    }

    // ────────────────────────── Borrar caché ──────────────────────────

    private async void Cache_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } app) return;

        OpenDialog($"Caché de {app.Name}", "Archivos que la aplicación vuelve a crear sola.", IconFor(app));
        SetBusy("Buscando la caché…");

        var measured = await MeasureAsync(app);
        if (measured.Cache.Count == 0 || measured.CacheSize == 0)
        {
            ShowResult("No hay caché que borrar",
                "Esta aplicación no tiene ahora mismo archivos de caché que se puedan eliminar.", ok: true);
            return;
        }

        _checks = measured.Cache
            .Select(spot => new CheckItem(spot.Label, Shorten(spot.Path), spot.Size, spot.Path))
            .ToList();
        ShowChecks($"Nada de esto es tuyo: son copias temporales. {app.Name} volverá a crearlas cuando las necesite.");
        SetButtons($"Borrar {UiText.Size(measured.CacheSize)}", () => CleanAsync(app), "Cancelar", null);
    }

    private async Task CleanAsync(AppCard app)
    {
        var chosen = _checks.Where(c => c.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            DialogFooter.Text = "No has marcado nada.";
            return;
        }

        var spots = _measured.TryGetValue(app.Id, out var m)
            ? m.Cache.Where(s => chosen.Any(c => (string)c.Tag! == s.Path)).ToList()
            : new List<AppCacheSpot>();

        var target = AppCache.AsTarget(app.Name, spots);

        ShowProgress($"Borrando la caché de {app.Name}…");
        var progress = new Progress<CacheCleaner.CleanProgress>(p =>
        {
            ProgressMeter.Value = p.Fraction * 100;
            ProgressText.Text = p.Freed > 0
                ? $"{p.Current} · {UiText.Size(p.Freed)} liberados"
                : p.Current;
        });

        long freed = 0;
        try { freed = await Task.Run(() => CacheCleaner.Clean(new[] { target }, progress)); }
        catch { }

        _measured.Remove(app.Id);
        var row = _rows.FirstOrDefault(r => r.App.Id == app.Id);
        row?.SetSize(Size(app));
        if (_selectedId == app.Id)
        {
            ShowDetail(app);
            _ = MeasureAsync(app);
        }

        ShowResult("Caché borrada",
            freed > 0
                ? $"Se liberaron {UiText.Size(freed)}. La aplicación seguirá funcionando igual; la primera vez " +
                  "puede tardar un poco más en abrir."
                : "No se pudo borrar nada: los archivos estaban en uso. Cierra la aplicación y prueba otra vez.",
            ok: freed > 0);

        Status(freed > 0 ? $"Caché de {app.Name}: {UiText.Size(freed)} liberados." : " ", freed > 0);
    }

    // ────────────────────────── Desinstalar ──────────────────────────

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { CanUninstall: true } app) return;

        OpenDialog($"Antes de quitar {app.Name}", "Esto es lo que encontré enlazado con ella.", IconFor(app));
        SetBusy("Revisando con qué está enlazada…");

        AppLinkReport report;
        try { report = await Task.Run(() => AppLinks.Inspect(app, _apps)); }
        catch
        {
            ShowResult("No se pudo revisar", "Vuelve a intentarlo en un momento.", ok: false);
            return;
        }

        ShowLinks(report.Items);
        DialogFooter.Text = report.HasCare
            ? "Hay cosas que conviene mirar antes de seguir."
            : "No vi nada que se rompa al quitarla.";
        SetButtons("Desinstalar", () => RunUninstallAsync(app), "Cancelar", null);
    }

    private async Task RunUninstallAsync(AppCard app)
    {
        if (app.Origin == AppOrigin.Store)
        {
            SetBusy("Quitando la aplicación…");
            var (process, error) = AppCatalog.RemoveStoreApp(app);
            if (error is not null)
            {
                ShowResult("No se pudo quitar", error, ok: false);
                return;
            }
            if (process is not null)
            {
                try { await process.WaitForExitAsync(); } catch { }
            }
            await FindLeftoversAsync(app);
            return;
        }

        SetBusy("Abriendo el desinstalador de la aplicación…");
        var (uninstaller, problem) = AppManager.StartUninstaller(app.Entry!);
        if (problem is not null)
        {
            ShowResult("No se pudo desinstalar", problem, ok: false);
            return;
        }

        if (uninstaller is null)
        {
            // Algunos desinstaladores lanzan otro programa y se cierran al momento.
            SetBusy("Sigue los pasos del desinstalador en la ventana que se abrió.");
            SetButtons("Buscar restos", () => FindLeftoversAsync(app), "Cerrar", null);
            return;
        }

        SetBusy("Sigue los pasos del desinstalador. Cuando termine, sigo yo.");
        try { await uninstaller.WaitForExitAsync(); } catch { }
        await FindLeftoversAsync(app);
    }

    private async Task FindLeftoversAsync(AppCard app)
    {
        SetBusy("Buscando lo que quedó en el equipo…");

        var still = await Task.Run(() => AppCatalog.StillInstalled(app));
        List<Leftover> leftovers;

        if (app.Origin == AppOrigin.Store)
        {
            var folders = await Task.Run(() => AppCatalog.DataFolders(app));
            leftovers = folders
                .Select(f => new Leftover(f.Path, f.Size, true, "Datos de la aplicación"))
                .ToList();
        }
        else
        {
            var fresh = await Task.Run(AppManager.GetInstalledApps);
            leftovers = await Task.Run(() => AppManager.FindLeftovers(app.Entry!, fresh));
        }

        if (still)
        {
            ShowResult("La aplicación sigue instalada",
                "Parece que el desinstalador no terminó o lo cancelaste. Si crees que sí acabó, vuelve a buscar.",
                ok: false);
            SetButtons("Buscar otra vez", () => FindLeftoversAsync(app), "Cerrar", null);
            return;
        }

        await ReloadAsync();

        if (leftovers.Count == 0)
        {
            ShowResult("Desinstalada sin dejar restos",
                $"{app.Name} ya no está y no encontré carpetas ni accesos directos suyos.", ok: true);
            return;
        }

        _checks = leftovers
            .Select(l => new CheckItem(Path.GetFileName(l.Path.TrimEnd('\\')) is { Length: > 0 } name ? name : l.Path,
                $"{l.Reason} · {Shorten(l.Path)}", l.Size, l.Path))
            .ToList();

        ShowChecks($"{app.Name} se desinstaló, pero dejó esto. Desmarca lo que quieras conservar: " +
                   "lo demás va a la papelera de reciclaje, así que se puede recuperar.");
        SetButtons($"Mover a la papelera ({UiText.Size(leftovers.Sum(l => l.Size))})",
            () => RecycleAsync(app), "Dejarlo así", null);
    }

    private async Task RecycleAsync(AppCard app)
    {
        var chosen = _checks.Where(c => c.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            DialogFooter.Text = "No has marcado nada.";
            return;
        }

        SetBusy("Moviendo a la papelera…");
        var paths = chosen.Select(c => (string)c.Tag!).ToList();
        var removed = await AppManager.RecycleAsync(paths, OwnerHandle());
        var freed = chosen.Where(c => removed.Contains((string)c.Tag!)).Sum(c => c.Size);

        ShowResult(removed.Count == chosen.Count ? "Listo" : "Casi todo",
            removed.Count == 0
                ? "No se pudo borrar nada: puede que algún archivo esté en uso o necesite permisos."
                : $"{UiText.Plural(removed.Count, "elemento borrado", "elementos borrados")} · " +
                  $"{UiText.Size(freed)} liberados." +
                  (removed.Count < chosen.Count ? " El resto estaba en uso o protegido." : ""),
            ok: removed.Count > 0);

        Status(removed.Count > 0 ? $"{app.Name}: {UiText.Size(freed)} liberados." : " ", removed.Count > 0);
    }

    private IntPtr OwnerHandle()
    {
        try
        {
            var window = Window.GetWindow(this);
            return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        }
        catch { return IntPtr.Zero; }
    }

    // ────────────────────────── Ventana de revisión ──────────────────────────

    private void OpenDialog(string title, string subtitle, ImageSource? icon)
    {
        DialogTitle.Text = title;
        DialogSubtitle.Text = subtitle;
        DialogFooter.Text = "";

        DialogImage.Source = icon;
        DialogImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        DialogGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;

        Hide();
        DialogLayer.Visibility = Visibility.Visible;
    }

    private void Hide()
    {
        DialogMessage.Visibility = Visibility.Collapsed;
        DialogLinksScroll.Visibility = Visibility.Collapsed;
        DialogChecks.Visibility = Visibility.Collapsed;
        DialogProgress.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(string message)
    {
        Hide();
        DialogMessage.Text = message;
        DialogMessage.Visibility = Visibility.Visible;
        SetButtons("Cancelar", () => { CloseDialog(); return Task.CompletedTask; }, null, null);
    }

    private void ShowMessage(string message)
    {
        DialogMessage.Text = message;
        DialogMessage.Visibility = Visibility.Visible;
    }

    private void ShowChecks(string message)
    {
        Hide();
        ShowMessage(message);
        foreach (var item in _checks) item.Changed = UpdateChecksFooter;
        DialogChecks.ItemsSource = _checks;
        DialogChecks.Visibility = Visibility.Visible;
        UpdateChecksFooter();
    }

    private void UpdateChecksFooter()
    {
        var chosen = _checks.Where(c => c.IsChecked).ToList();
        DialogFooter.Text = chosen.Count == 0
            ? "Nada marcado"
            : $"{UiText.Plural(chosen.Count, "elemento", "elementos")} · {UiText.Size(chosen.Sum(c => c.Size))}";
    }

    private void ShowLinks(IEnumerable<AppLinkItem> items)
    {
        Hide();
        DialogLinks.ItemsSource = items.Select(i => new LinkRow(i)).ToList();
        DialogLinksScroll.Visibility = Visibility.Visible;
    }

    private void ShowProgress(string message)
    {
        Hide();
        ShowMessage(message);
        ProgressMeter.Value = 0;
        ProgressText.Text = "Preparando…";
        DialogProgress.Visibility = Visibility.Visible;
        DialogPrimary.IsEnabled = false;
        DialogSecondary.Visibility = Visibility.Collapsed;
    }

    private void ShowResult(string title, string message, bool ok)
    {
        Hide();
        DialogTitle.Text = title;
        DialogSubtitle.Text = "";
        DialogFooter.Text = "";
        ShowMessage(message);
        DialogMessage.Foreground = UiKit.Brush(ok ? "Br.TextPrimary" : "Br.Warn");
        SetButtons("Entendido", () => { CloseDialog(); return Task.CompletedTask; }, null, null);
    }

    private void SetButtons(string primaryText, Func<Task> primary, string? secondaryText, Func<Task>? secondary)
    {
        DialogPrimary.Content = primaryText;
        DialogPrimary.IsEnabled = true;
        _dialogPrimary = primary;

        _dialogSecondary = secondary ?? (() => { CloseDialog(); return Task.CompletedTask; });
        DialogSecondary.Content = secondaryText ?? "";
        DialogSecondary.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseDialog()
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        DialogChecks.ItemsSource = null;
        DialogLinks.ItemsSource = null;
        _checks = new List<CheckItem>();
        _dialogPrimary = null;
        _dialogSecondary = null;
        DialogMessage.Foreground = UiKit.Brush("Br.TextPrimary");
    }

    private async void DialogPrimary_Click(object sender, RoutedEventArgs e) => await Run(_dialogPrimary);

    private async void DialogSecondary_Click(object sender, RoutedEventArgs e) => await Run(_dialogSecondary);

    private void DialogClose_Click(object sender, RoutedEventArgs e)
    {
        if (!_dialogBusy) CloseDialog();
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_dialogBusy) CloseDialog();
    }

    private async Task Run(Func<Task>? action)
    {
        if (action is null || _dialogBusy) return;
        _dialogBusy = true;
        DialogPrimary.IsEnabled = false;
        try { await action(); }
        catch { ShowResult("Algo salió mal", "No se pudo completar la operación.", ok: false); }
        finally
        {
            _dialogBusy = false;
            if (DialogLayer.Visibility == Visibility.Visible) DialogPrimary.IsEnabled = true;
        }
    }

    private void Status(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = UiKit.Brush(ok ? "Br.Positive" : "Br.Critical");
    }

    private static string Shorten(string path)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return user.Length > 0 && path.StartsWith(user, StringComparison.OrdinalIgnoreCase)
            ? "…" + path[user.Length..]
            : path;
    }

    // ────────────────────────── Filas ──────────────────────────

    /// <summary>Una aplicación de la lista.</summary>
    public sealed class AppRow : INotifyPropertyChanged
    {
        private long _size;
        private ImageSource? _icon;
        private bool _iconLoaded;

        public AppRow(AppCard app, long size)
        {
            App = app;
            _size = size;
        }

        public AppCard App { get; }

        public string Name => App.Name;

        public string SubText
        {
            get
            {
                var parts = new List<string>();
                if (App.Publisher is { } publisher) parts.Add(publisher);
                if (App.Origin == AppOrigin.Store) parts.Add("Microsoft Store");
                if (App.InstallDate is { } date) parts.Add(date.ToString("MMM yyyy", Spanish));
                if (parts.Count == 0) parts.Add(App.Version ?? "sin datos del fabricante");
                return string.Join(" · ", parts);
            }
        }

        public string Tip => App.InstallLocation ?? App.Name;

        public string SizeText => _size > 0 ? UiText.Size(_size) : "—";

        public void SetSize(long size)
        {
            _size = size;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }

        public ImageSource? Icon
        {
            get
            {
                if (!_iconLoaded)
                {
                    _iconLoaded = true;
                    _icon = IconFor(App);
                }
                return _icon;
            }
        }

        public Visibility ImageVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Fila con casilla de las ventanas de revisión.</summary>
    public sealed class CheckItem : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public CheckItem(string title, string detail, long size, object? tag)
        {
            Title = title;
            Detail = detail;
            Size = size;
            Tag = tag;
        }

        public string Title { get; }
        public string Detail { get; }
        public long Size { get; }
        public string SizeText => UiText.Size(Size);
        public object? Tag { get; }
        public Action? Changed { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                Changed?.Invoke();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Fila de la ventana de vínculos.</summary>
    public sealed class LinkRow
    {
        public LinkRow(AppLinkItem item)
        {
            Title = item.Title;
            Detail = item.Detail;
            Level = item.Level;
        }

        public string Title { get; }
        public string Detail { get; }
        public LinkLevel Level { get; }

        public Geometry Glyph => (Geometry)Application.Current.FindResource("Glyph.Info");

        public Brush GlyphBrush => UiKit.Brush(Level == LinkLevel.Care ? "Br.Warn" : "Br.AccentBright");
    }
}
