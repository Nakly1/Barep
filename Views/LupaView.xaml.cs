using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

/// <summary>
/// La Lupa: mapa de burbujas del disco al estilo de CleanMyMac. Tres pantallas
/// (inicio, análisis, mapa) y un diálogo propio para revisar, borrar y
/// desinstalar sin salir de la vista.
/// </summary>
public partial class LupaView : UserControl
{
    private const int MaxBubbles = 12;
    private static readonly CultureInfo Spanish = new("es-ES");
    private static readonly object OthersKey = new();

    private Task<List<InstalledApp>>? _appsTask;
    private SpaceNode? _root;
    private SpaceNode? _current;
    private readonly Stack<SpaceNode> _back = new();
    private readonly Stack<SpaceNode> _forward = new();
    private readonly HashSet<SpaceNode> _selected = new();
    private CancellationTokenSource? _cts;
    private bool _loaded;

    // Diálogo
    private Func<Task>? _dialogPrimary;
    private Func<Task>? _dialogSecondary;
    private List<CheckRow> _dialogRows = new();
    private Action? _dialogRowsChanged;
    private InstalledApp? _uninstalling;

    // Análisis guardados: en memoria los de esta sesión, en disco los de antes.
    private readonly Dictionary<string, (SpaceNode Root, DateTime At)> _memoryCache = new();
    private Dictionary<string, CachedScanInfo> _diskCache = new();
    private string? _rootKey;
    private DateTime _rootScannedAt;

    // Visor
    private List<SpaceNode> _previewList = new();
    private int _previewIndex;
    private int _previewVersion;
    private bool _mediaPlaying;

    public LupaView()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        Map.ItemClicked += OnBubbleClicked;
        Map.ItemRightClicked += (item, target) =>
        {
            if (item.Key is SpaceNode node) ShowContextMenu(node, target);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        BuildSources();

        // El inventario de aplicaciones se lee en segundo plano desde el principio:
        // así los iconos y la ficha de cada programa están listos al terminar el análisis.
        _ = EnsureAppsAsync();
        _ = LoadDiskCacheListAsync();
    }

    private async Task LoadDiskCacheListAsync()
    {
        var list = await Task.Run(() => ScanCache.List());
        _diskCache = list.ToDictionary(i => i.SourceKey, i => i);
        UpdateCachedUi();
    }

    private Task<List<InstalledApp>> EnsureAppsAsync(bool refresh = false)
    {
        if (_appsTask is null || refresh || _appsTask.IsFaulted)
            _appsTask = Task.Run(() => AppManager.GetInstalledApps());
        return _appsTask;
    }

    // ══════════════════════════ Inicio ══════════════════════════

    private void BuildSources()
    {
        SourcesPanel.Children.Clear();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        RadioButton? first = null;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var used = drive.TotalSize - drive.TotalFreeSpace;
                var percent = drive.TotalSize > 0 ? used * 100.0 / drive.TotalSize : 0;

                var option = SourceOption(
                    (Geometry)FindResource("Glyph.Drive"),
                    SpaceScanner.DriveTitle(drive.RootDirectory.FullName),
                    $"{UiKit.FormatSize(used)} de {UiKit.FormatSize(drive.TotalSize)} usados",
                    percent);
                option.Tag = drive.RootDirectory.FullName;
                SourcesPanel.Children.Add(option);

                if (drive.RootDirectory.FullName.Equals(systemRoot, StringComparison.OrdinalIgnoreCase) || first is null)
                    first = option;
            }
            catch { /* unidad que desaparece mientras se lee */ }
        }

        var apps = SourceOption((Geometry)FindResource("Glyph.Stack"), "Aplicaciones instaladas",
            "Cada programa con su icono, su peso y desinstalación completa", null);
        apps.Tag = "apps";
        SourcesPanel.Children.Add(apps);

        foreach (var option in SourcesPanel.Children.OfType<RadioButton>())
            option.Checked += (_, _) => UpdateCachedUi();

        (first ?? apps).IsChecked = true;
        UpdateCachedUi();
    }

    private string? SelectedSourceKey() =>
        SourcesPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag
            ? ScanCache.KeyFor(tag == "apps" ? null : tag)
            : null;

    private (DateTime At, long Size)? CachedFor(string? key)
    {
        if (key is null) return null;
        if (_memoryCache.TryGetValue(key, out var mem)) return (mem.At, mem.Root.Size);
        if (_diskCache.TryGetValue(key, out var disk)) return (disk.ScannedAt, disk.Size);
        return null;
    }

    /// <summary>Si ya hay un análisis de lo elegido, se ofrece abrirlo en vez de repetirlo.</summary>
    private void UpdateCachedUi()
    {
        var cached = CachedFor(SelectedSourceKey());
        if (cached is { } c)
        {
            OpenCachedButton.Visibility = Visibility.Visible;
            AnalyzeButton.Content = "Analizar de nuevo";
            AnalyzeButton.Style = UiKit.Style("Btn");
            CachedText.Text = $"Último análisis {Ago(c.At)} · {UiKit.FormatSize(c.Size)}. " +
                              "Ábrelo al instante o analiza otra vez para ver los cambios.";
            CachedText.Visibility = Visibility.Visible;
        }
        else
        {
            OpenCachedButton.Visibility = Visibility.Collapsed;
            AnalyzeButton.Content = "Analizar";
            AnalyzeButton.Style = UiKit.Style("BtnPrimary");
            CachedText.Visibility = Visibility.Collapsed;
        }
    }

    private static string Ago(DateTime at)
    {
        var span = DateTime.Now - at;
        if (span.TotalMinutes < 1) return "hace un momento";
        if (span.TotalMinutes < 60) return $"hace {(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"hace {(int)span.TotalHours} h";
        if (span.TotalDays < 2) return "ayer";
        return $"el {at.ToString("d 'de' MMMM", Spanish)}";
    }

    private async void OpenCached_Click(object sender, RoutedEventArgs e)
    {
        var key = SelectedSourceKey();
        if (key is null) return;

        if (_memoryCache.TryGetValue(key, out var mem))
        {
            ShowRoot(mem.Root, key, mem.At);
            return;
        }

        ScanTitle.Text = "Abriendo el análisis guardado…";
        ScanPathText.Text = " ";
        ScanCountText.Text = " ";
        ShowPanel(ScanPanel);
        StartOrbit();
        try
        {
            var apps = await EnsureAppsAsync();
            var loaded = await Task.Run(() =>
            {
                var result = ScanCache.Load(key);
                if (result is { } r) SpaceScanner.AttachApps(r.Root, apps);
                return result;
            });

            if (loaded is { } l)
            {
                _memoryCache[key] = (l.Root, l.ScannedAt);
                ShowRoot(l.Root, key, l.ScannedAt);
            }
            else
            {
                _diskCache.Remove(key);
                ShowPanel(StartPanel);
                UpdateCachedUi();
                StartStatus.Text = "No se pudo abrir el análisis guardado. Analiza de nuevo.";
                StartStatus.Visibility = Visibility.Visible;
            }
        }
        finally { StopOrbit(); }
    }

    private void ShowRoot(SpaceNode root, string key, DateTime scannedAt)
    {
        _root = root;
        _rootKey = key;
        _rootScannedAt = scannedAt;
        _selected.Clear();
        _back.Clear();
        _forward.Clear();
        FooterMessage.Text = "";
        ShowPanel(BrowsePanel);
        Navigate(root, push: false);
    }

    /// <summary>Guarda el árbol (tras analizar o borrar) sin bloquear la interfaz.</summary>
    private void SaveCurrent()
    {
        if (_root is null || _rootKey is null) return;
        var (root, key, at) = (_root, _rootKey, _rootScannedAt);
        _memoryCache[key] = (root, at);
        _ = Task.Run(() =>
        {
            ScanCache.Save(key, root, at);
            return ScanCache.List();
        }).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
                _diskCache = t.Result.ToDictionary(i => i.SourceKey, i => i);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_rootKey is null || _root is null) return;
        _ = StartScanAsync(_rootKey == ScanCache.AppsKey ? null : _root.FullPath);
    }

    private static RadioButton SourceOption(Geometry glyph, string title, string caption, double? percent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new System.Windows.Shapes.Path
        {
            Data = glyph,
            Fill = UiKit.Brush("Br.GlyphFolder"),
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(UiKit.Text(title, "T.Value"));
        text.Children.Add(UiKit.Text(caption, "T.Caption", margin: new Thickness(0, 2, 0, 0)));
        if (percent is { } p)
        {
            text.Children.Add(new ProgressBar
            {
                Style = UiKit.Style("Meter"),
                Height = 4,
                Maximum = 100,
                Value = p,
                Foreground = UiKit.LoadBrush(p),
                Margin = new Thickness(0, 7, 0, 0)
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new RadioButton { Style = UiKit.Style("SourceOption"), Content = grid };
    }

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var chosen = SourcesPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (chosen?.Tag is not string tag) return;
        _ = StartScanAsync(tag == "apps" ? null : tag);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Elige la carpeta que quieres analizar",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _ = StartScanAsync(dialog.FolderName);
    }

    // ══════════════════════════ Análisis ══════════════════════════

    private async Task StartScanAsync(string? path)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        StartStatus.Visibility = Visibility.Collapsed;
        ScanTitle.Text = path is null
            ? "Midiendo tus aplicaciones…"
            : "Visualizando tu espacio de almacenamiento…";
        ScanPathText.Text = path ?? "Leyendo el registro de programas instalados";
        ScanCountText.Text = " ";
        ShowPanel(ScanPanel);
        StartOrbit();

        var progress = new Progress<ScanProgress>(p =>
        {
            if (ct.IsCancellationRequested) return;
            ScanPathText.Text = p.CurrentPath;
            ScanCountText.Text = $"{p.Items:N0} elementos · {UiKit.FormatSize(p.Bytes)}";
        });

        try
        {
            var apps = await EnsureAppsAsync();
            var root = await Task.Run(() =>
            {
                if (path is null) return SpaceScanner.ScanApps(DistinctLocations(apps), progress, ct);

                var node = SpaceScanner.ScanPath(path, progress, ct);
                SpaceScanner.AttachApps(node, apps);
                return node;
            }, ct);

            ShowRoot(root, ScanCache.KeyFor(path), DateTime.Now);
            SaveCurrent();
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            // Si se detuvo un «Actualizar», se vuelve al análisis que ya había.
            if (_root is not null && _current is not null) ShowPanel(BrowsePanel);
            else ShowPanel(StartPanel);
        }
        catch (Exception ex)
        {
            ShowPanel(StartPanel);
            StartStatus.Text = $"No se pudo analizar: {ex.Message}";
            StartStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            StopOrbit();
        }
    }

    /// <summary>
    /// Si una aplicación está instalada dentro de la carpeta de otra (p. ej.
    /// componentes de Office), se cuenta una sola vez: la carpeta exterior.
    /// </summary>
    private static List<InstalledApp> DistinctLocations(IEnumerable<InstalledApp> apps)
    {
        var chosen = new List<string>();
        var result = new List<InstalledApp>();
        foreach (var app in apps.OrderBy(a => a.InstallLocation?.Length ?? int.MaxValue))
        {
            if (string.IsNullOrEmpty(app.InstallLocation))
            {
                result.Add(app);
                continue;
            }
            var path = SpaceScanner.Normalize(app.InstallLocation);
            if (chosen.Any(c => path.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                                path.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            chosen.Add(path);
            result.Add(app);
        }
        return result;
    }

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException ||
        ex is AggregateException agg && agg.Flatten().InnerExceptions.All(i => i is OperationCanceledException);

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void StartOrbit() =>
        OrbitRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2.6)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });

    private void StopOrbit() => OrbitRotation.BeginAnimation(RotateTransform.AngleProperty, null);

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { StartPanel, ScanPanel, BrowsePanel })
            p.Visibility = ReferenceEquals(p, panel) ? Visibility.Visible : Visibility.Collapsed;

        panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260))));
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        // El análisis no se pierde: queda guardado y se ofrece al volver a elegirlo.
        if (_root is not null && _rootKey is not null) _memoryCache[_rootKey] = (_root, _rootScannedAt);
        _root = null;
        _current = null;
        _selected.Clear();
        ItemsList.ItemsSource = null;
        Map.SetItems(Array.Empty<BubbleItem>());
        BuildSources();
        ShowPanel(StartPanel);
    }

    // ══════════════════════════ Navegación ══════════════════════════

    private void Navigate(SpaceNode node, bool push = true)
    {
        if (push && _current is not null && !ReferenceEquals(_current, node))
        {
            _back.Push(_current);
            _forward.Clear();
        }
        if (node.Kind == SpaceNodeKind.Aggregate) SpaceScanner.ExpandAggregate(node);
        _current = node;
        Render();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (_back.Count == 0 || _current is null) return;
        _forward.Push(_current);
        _current = _back.Pop();
        Render();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_forward.Count == 0 || _current is null) return;
        _back.Push(_current);
        _current = _forward.Pop();
        Render();
    }

    /// <summary>Los botones laterales del ratón también navegan, como en el Explorador.</summary>
    private void Browse_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Forward_Click(sender, e); e.Handled = true; }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var path = _current?.FolderPath;
        if (string.IsNullOrEmpty(path)) path = _current?.App?.InstallLocation;
        if (!string.IsNullOrEmpty(path)) AppManager.Reveal(path);
    }

    // ══════════════════════════ Dibujo ══════════════════════════

    private void Render()
    {
        var node = _current;
        if (node is null) return;

        // Cabecera
        HeaderName.Text = node.DisplayName;
        HeaderName.ToolTip = string.IsNullOrEmpty(node.FullPath) ? null : node.FullPath;
        HeaderMeta.Text = node.Kind == SpaceNodeKind.AppEntry
            ? $"{UiKit.FormatSize(node.Size)}  |  tamaño declarado"
            : $"{UiKit.FormatSize(node.Size)}  |  {UiKit.CompactCount(node.ItemCount)} ítems";
        if (ReferenceEquals(node, _root))
            HeaderMeta.Text += $"  |  analizado {Ago(_rootScannedAt)}";
        SetIcon(HeaderImage, HeaderGlyph, node);

        ShowAppCard(node);

        // Lista
        var rows = node.Children.Select(c => new SpaceRow(c, node.Size, this)).ToList();
        ItemsList.ItemsSource = rows;
        if (rows.Count > 0) ItemsList.ScrollIntoView(rows[0]);
        ListCountText.Text = UiKit.Plural(rows.Count, "elemento", "elementos");

        EmptyListText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = node.Kind == SpaceNodeKind.AppEntry
            ? "Windows no indica en qué carpeta está esta aplicación. Aun así puedes desinstalarla desde aquí."
            : "Esta carpeta está vacía o Windows no deja leerla.";

        // Burbujas
        var bubbles = BuildBubbles(node);
        Map.SetItems(bubbles);
        Map.RefreshSelection(k => k is SpaceNode n && _selected.Contains(n));
        MapEmptyText.Visibility = bubbles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MapEmptyText.Text = "Nada que dibujar aquí";
        _ = LoadBubbleThumbnailsAsync(node, bubbles);

        BuildBreadcrumb(node);
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        RevealButton.IsEnabled = !string.IsNullOrEmpty(node.FolderPath) || node.App?.InstallLocation is not null;
        SelectAllButton.IsEnabled = rows.Any(r => r.CanSelect);

        UpdateVolume();
        UpdateSelectionFooter();
    }

    /// <summary>Las fotos se ven como fotos: su miniatura sustituye al icono al cargarse.</summary>
    private async Task LoadBubbleThumbnailsAsync(SpaceNode node, IEnumerable<BubbleItem> bubbles)
    {
        foreach (var bubble in bubbles)
        {
            if (bubble.Key is not SpaceNode { Kind: SpaceNodeKind.File } file || !Thumbnails.IsImage(file.FullPath))
                continue;
            var thumb = await Thumbnails.SmallAsync(file.FullPath, 160);
            if (!ReferenceEquals(_current, node)) return;
            if (thumb is not null) Map.SetThumbnail(file, thumb);
        }
    }

    private List<BubbleItem> BuildBubbles(SpaceNode node)
    {
        var items = new List<BubbleItem>();

        if (node.Kind == SpaceNodeKind.AppEntry)
        {
            items.Add(ToBubble(node));
            return items;
        }

        var visible = node.Children.Where(c => c.Size > 0).ToList();
        foreach (var child in visible.Take(MaxBubbles))
            items.Add(ToBubble(child));

        if (visible.Count > MaxBubbles)
        {
            var rest = visible.Skip(MaxBubbles).ToList();
            var size = rest.Sum(r => r.Size);
            items.Add(new BubbleItem
            {
                Key = OthersKey,
                Label = "Otros elementos",
                Size = size,
                SizeText = UiKit.FormatSize(size),
                Glyph = BubbleGlyph.Others,
                KindText = UiKit.Plural(rest.Count, "elemento más pequeño", "elementos más pequeños"),
                DetailLine = $"Tamaño: {UiKit.FormatSize(size)}",
                DateLine = "Están al final de la lista"
            });
        }
        return items;
    }

    private BubbleItem ToBubble(SpaceNode n)
    {
        var isProtected = !string.IsNullOrEmpty(n.FullPath) && AppManager.IsProtected(n.FullPath, out _);
        string kind;
        Brush? kindBrush = null;

        if (n.App is not null)
            kind = string.IsNullOrEmpty(n.App.Publisher) ? "Aplicación" : $"Aplicación · {n.App.Publisher}";
        else if (isProtected)
        {
            AppManager.IsProtected(n.FullPath, out var reason);
            kind = reason;
            kindBrush = UiKit.Brush("Br.Warn");
        }
        else kind = n.Kind switch
        {
            SpaceNodeKind.File => FileKind(n.Name),
            SpaceNodeKind.Aggregate => "Archivos pequeños agrupados",
            _ => ShellIcons.LooksLikeAppFolder(n) ? "Programa" : "Carpeta"
        };

        var detail = $"Tamaño: {UiKit.FormatSize(n.Size)}";
        if (n.Kind is SpaceNodeKind.Folder or SpaceNodeKind.Drive && n.ItemCount > 0)
            detail += $"  |  {UiKit.CompactCount(n.ItemCount)} ítems";

        return new BubbleItem
        {
            Key = n,
            Label = n.DisplayName,
            Size = n.Size,
            SizeText = UiKit.FormatSize(n.Size),
            Icon = ShellIcons.ForNode(n),
            Glyph = n.Kind switch
            {
                SpaceNodeKind.File => BubbleGlyph.File,
                SpaceNodeKind.Aggregate => BubbleGlyph.Others,
                SpaceNodeKind.Drive => BubbleGlyph.Drive,
                _ => BubbleGlyph.Folder
            },
            KindText = kind,
            KindBrush = kindBrush,
            DetailLine = detail,
            DateLine = n.Modified > DateTime.MinValue.AddYears(1)
                ? $"Modificación: {n.Modified.ToString("d MMM yyyy, H:mm", Spanish)}"
                : null,
            IsSelected = _selected.Contains(n)
        };
    }

    private void OnBubbleClicked(BubbleItem item)
    {
        if (ReferenceEquals(item.Key, OthersKey))
        {
            // «Otros elementos» son las filas de la lista que no caben en el mapa.
            if (ItemsList.ItemsSource is IList<SpaceRow> rows && rows.Count > MaxBubbles)
            {
                ItemsList.ScrollIntoView(rows[^1]);
                ItemsList.ScrollIntoView(rows[MaxBubbles]);
            }
            return;
        }
        if (item.Key is not SpaceNode node) return;

        if (node.IsBrowsable) Navigate(node);
        else if (node.Kind == SpaceNodeKind.File) OpenPreview(node);
    }

    private void BuildBreadcrumb(SpaceNode node)
    {
        BreadcrumbPanel.Children.Clear();
        var chain = new List<SpaceNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Insert(0, n);

        // Con muchas carpetas, los tramos intermedios se resumen en «…».
        var nullable = chain.Cast<SpaceNode?>().ToList();
        var shown = chain.Count > 5
            ? nullable.Take(1).Append(null).Concat(nullable.Skip(chain.Count - 3)).ToList()
            : nullable;

        for (int i = 0; i < shown.Count; i++)
        {
            var n = shown[i];
            if (i > 0)
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›",
                    Foreground = UiKit.Brush("Br.TextTertiary"),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, -2, 3, 0)
                });

            if (n is null)
            {
                BreadcrumbPanel.Children.Add(UiKit.Text("…", "T.Caption", margin: new Thickness(4, 0, 4, 0)));
                continue;
            }

            var isLast = i == shown.Count - 1;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(n.Kind == SpaceNodeKind.Drive ? "Glyph.Drive"
                    : n.Kind == SpaceNodeKind.AppsRoot ? "Glyph.Stack"
                    : n.Kind == SpaceNodeKind.Aggregate ? "Glyph.Others" : "Glyph.Folder"),
                Fill = UiKit.Brush("Br.GlyphFolder"),
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock
            {
                Text = n.DisplayName,
                MaxWidth = 190,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });

            var button = new Button
            {
                Style = UiKit.Style("BtnLink"),
                Content = content,
                IsEnabled = !isLast,
                Foreground = isLast ? UiKit.Brush("Br.TextPrimary") : UiKit.Brush("Br.TextSecondary")
            };
            if (isLast) button.Opacity = 1;
            var target = n;
            button.Click += (_, _) => Navigate(target);
            BreadcrumbPanel.Children.Add(button);
        }

        BreadcrumbScroller.ScrollToRightEnd();
    }

    private void UpdateVolume()
    {
        var path = _root?.FullPath;
        if (string.IsNullOrEmpty(path)) path = Environment.SystemDirectory;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            var used = drive.TotalSize - drive.TotalFreeSpace;
            var percent = drive.TotalSize > 0 ? used * 100.0 / drive.TotalSize : 0;
            VolumeName.Text = SpaceScanner.DriveTitle(drive.RootDirectory.FullName);
            VolumeText.Text = $"{UiKit.FormatSize(used)} de {UiKit.FormatSize(drive.TotalSize)} usados";
            VolumeMeter.Value = percent;
            VolumeMeter.Foreground = UiKit.LoadBrush(percent);
        }
        catch
        {
            VolumeName.Text = "";
            VolumeText.Text = "";
        }
    }

    private static void SetIcon(Image image, System.Windows.Shapes.Path glyph, SpaceNode node)
    {
        var icon = ShellIcons.ForNode(node);
        image.Source = icon;
        image.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        glyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        glyph.Data = (Geometry)Application.Current.FindResource(GlyphKey(node));
        glyph.Fill = UiKit.Brush(node.Kind == SpaceNodeKind.File ? "Br.GlyphFile" : "Br.GlyphFolder");
    }

    internal static string GlyphKey(SpaceNode node) => node.Kind switch
    {
        SpaceNodeKind.Drive => "Glyph.Drive",
        SpaceNodeKind.AppsRoot => "Glyph.Stack",
        SpaceNodeKind.File => "Glyph.File",
        SpaceNodeKind.Aggregate => "Glyph.Others",
        SpaceNodeKind.AppEntry => "Glyph.Stack",
        _ => "Glyph.Folder"
    };

    internal static string FileKind(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return ext.Length == 0 ? "Archivo" : $"Archivo {ext}";
    }

    // ══════════════════════════ Ficha de aplicación ══════════════════════════

    private void ShowAppCard(SpaceNode node)
    {
        var app = node.App;
        AppSpecs.Children.Clear();

        if (app is not null)
        {
            AppKindText.Text = "Aplicación instalada";
            var sizeText = node.Kind == SpaceNodeKind.AppEntry
                ? $"{UiKit.FormatSize(node.Size)} (declarado)"
                : UiKit.FormatSize(node.Size);

            var rows = new List<(string, string?)>
            {
                ("Editor", app.Publisher),
                ("Versión", app.Version),
                ("Instalada", app.InstallDate?.ToString("d 'de' MMMM 'de' yyyy", Spanish)),
                ("Tamaño en disco", sizeText)
            };
            if (app.EstimatedSizeBytes > 0 && node.Kind != SpaceNodeKind.AppEntry)
                rows.Add(("Declarado", UiKit.FormatSize(app.EstimatedSizeBytes)));
            UiKit.FillSpecs(AppSpecs, rows, labelWidth: 110);
            if (!string.IsNullOrEmpty(app.InstallLocation))
                AppSpecs.Children.Add(UiKit.SpecRow("Ubicación", app.InstallLocation, mono: true, labelWidth: 110));

            UninstallText.Text = "Desinstalar por completo";
            UninstallButton.Visibility = app.CanUninstall ? Visibility.Visible : Visibility.Collapsed;
            UninstallButton.Tag = node;
            AppFolderButton.Visibility = !string.IsNullOrEmpty(app.InstallLocation) ? Visibility.Visible : Visibility.Collapsed;
            AppFolderButton.Tag = app.InstallLocation;
            AppCard.Visibility = Visibility.Visible;
            return;
        }

        // Programa sin registrar (portátil) dentro de Program Files o AppData\Local\Programs.
        if (ShellIcons.LooksLikeAppFolder(node) && ShellIcons.MainExecutable(node) is { } exe)
        {
            var info = ShellIcons.VersionOf(exe);
            AppKindText.Text = "Programa sin instalador";
            UiKit.FillSpecs(AppSpecs, new (string, string?)[]
            {
                ("Producto", string.IsNullOrWhiteSpace(info?.ProductName) ? Path.GetFileNameWithoutExtension(exe) : info.ProductName),
                ("Fabricante", info?.CompanyName),
                ("Versión", info?.ProductVersion ?? info?.FileVersion),
                ("Ejecutable", Path.GetFileName(exe)),
                ("Tamaño en disco", UiKit.FormatSize(node.Size))
            }, labelWidth: 110);

            UninstallText.Text = "Eliminar programa";
            var canDelete = CanSelect(node);
            UninstallButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
            UninstallButton.Tag = node;
            AppFolderButton.Visibility = Visibility.Visible;
            AppFolderButton.Tag = node.FullPath;
            AppCard.Visibility = Visibility.Visible;
            return;
        }

        AppCard.Visibility = Visibility.Collapsed;
    }

    private void AppFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AppFolderButton.Tag is string path) AppManager.Reveal(path);
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallButton.Tag is not SpaceNode node) return;
        if (node.App is { CanUninstall: true } app) BeginUninstall(app, node);
        else ReviewDeletion(new[] { node }, portableName: node.DisplayName);
    }

    private void RowUninstall_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is SpaceRow { Node.App: { CanUninstall: true } app } row)
            BeginUninstall(app, row.Node);
    }

    // ══════════════════════════ Lista: eventos ══════════════════════════

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpaceRow row) return;
        var node = row.Node;

        if (node.IsBrowsable) Navigate(node);
        else if (node.Kind == SpaceNodeKind.File) OpenPreview(node);
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpaceRow row) return;
        e.Handled = true;
        ShowContextMenu(row.Node, (FrameworkElement)sender);
    }

    private void Row_Enter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SpaceRow row) Map.Highlight(row.Node);
    }

    private void Row_Leave(object sender, MouseEventArgs e) => Map.Highlight(null);

    // ══════════════════════════ Selección ══════════════════════════

    internal static bool CanSelect(SpaceNode node) =>
        node.Kind is SpaceNodeKind.Folder or SpaceNodeKind.File &&
        !string.IsNullOrEmpty(node.FullPath) &&
        !AppManager.IsProtected(node.FullPath, out _);

    internal bool IsSelected(SpaceNode node) => _selected.Contains(node);

    internal void SetSelected(SpaceNode node, bool selected)
    {
        if (selected) _selected.Add(node);
        else _selected.Remove(node);

        Map.RefreshSelection(k => k is SpaceNode n && _selected.Contains(n));
        UpdateSelectionFooter();
    }

    private void RefreshRows()
    {
        if (ItemsList.ItemsSource is IEnumerable<SpaceRow> rows)
            foreach (var row in rows) row.Refresh();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.ItemsSource is not IEnumerable<SpaceRow> rows) return;
        foreach (var row in rows.Where(r => r.CanSelect)) _selected.Add(row.Node);
        RefreshRows();
        Map.RefreshSelection(k => k is SpaceNode n && _selected.Contains(n));
        UpdateSelectionFooter();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.ItemsSource is not IEnumerable<SpaceRow> rows) return;
        foreach (var row in rows) _selected.Remove(row.Node);
        RefreshRows();
        Map.RefreshSelection(k => k is SpaceNode n && _selected.Contains(n));
        UpdateSelectionFooter();
    }

    /// <summary>Lo seleccionado, sin los elementos que ya van dentro de otro seleccionado.</summary>
    private List<SpaceNode> EffectiveSelection() =>
        _selected.Where(n => !_selected.Any(o => !ReferenceEquals(o, n) && o.IsAncestorOf(n)))
            .OrderByDescending(n => n.Size)
            .ToList();

    private void UpdateSelectionFooter()
    {
        var effective = EffectiveSelection();
        SelectionText.Text = effective.Count == 1
            ? "1 elemento seleccionado"
            : $"{effective.Count:N0} elementos seleccionados";
        SelectionSize.Text = UiKit.FormatSize(effective.Sum(n => n.Size));
        ReviewButton.IsEnabled = effective.Count > 0;
    }

    // ══════════════════════════ Revisar y eliminar ══════════════════════════

    private void Review_Click(object sender, RoutedEventArgs e) => ReviewDeletion(EffectiveSelection());

    private void ReviewDeletion(IReadOnlyList<SpaceNode> nodes, string? portableName = null)
    {
        if (nodes.Count == 0) return;

        var rows = nodes.Select(n => new CheckRow(
            title: n.DisplayName,
            detail: n.FullPath,
            size: n.Size,
            icon: ShellIcons.ForNode(n),
            glyphKey: GlyphKey(n),
            tag: n)).ToList();

        var hasApps = nodes.Any(n => n.App is { CanUninstall: true });
        var message = "Se moverán a la papelera de reciclaje: podrás recuperarlos desde allí mientras no la vacíes.";
        if (hasApps)
            message += "\n\nAlgunos son aplicaciones instaladas. Es mejor usar «Desinstalar por completo» para que Windows no se quede con entradas rotas.";

        OpenDialog(
            title: portableName is null ? "Revisar y eliminar" : $"Eliminar {portableName}",
            subtitle: UiKit.Plural(nodes.Count, "elemento", "elementos"),
            message: message,
            icon: portableName is not null ? ShellIcons.ForNode(nodes[0]) : null,
            glyphKey: "Glyph.Trash",
            rows: rows,
            primaryText: "Mover a la papelera",
            primary: () => RecycleRowsAsync(rows),
            secondaryText: "Cancelar",
            secondary: () => { CloseDialog(); return Task.CompletedTask; });
    }

    private async Task RecycleRowsAsync(IReadOnlyList<CheckRow> rows)
    {
        var chosen = rows.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0) { CloseDialog(); return; }

        SetDialogBusy("Moviendo a la papelera…");
        var paths = chosen.Select(r => r.Detail).ToList();
        var removed = await AppManager.RecycleAsync(paths, OwnerHandle());
        var freed = ApplyRemoval(removed, chosen);

        var failed = chosen.Count - removed.Count;
        Render();
        FooterMessage.Text = removed.Count > 0 ? $"Se liberaron {UiKit.FormatSize(freed)}" : "";

        if (failed == 0) CloseDialog();
        else
            ShowResult("No se pudo borrar todo",
                $"{UiKit.Plural(failed, "elemento no se pudo mover", "elementos no se pudieron mover")} a la papelera. " +
                "Puede que estén en uso o que haga falta permiso de administrador." +
                (removed.Count > 0 ? $"\n\nSe liberaron {UiKit.FormatSize(freed)}." : ""));
    }

    /// <summary>Quita del árbol lo que ya no existe y devuelve los bytes liberados.</summary>
    private long ApplyRemoval(IReadOnlyCollection<string> removedPaths, IEnumerable<CheckRow> rows)
    {
        var removed = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
        long freed = 0;

        foreach (var row in rows)
        {
            if (!removed.Contains(row.Detail)) continue;
            freed += row.Size;

            var node = row.Tag as SpaceNode ?? FindNode(row.Detail);
            if (node is null) continue;
            _selected.RemoveWhere(s => ReferenceEquals(s, node) || node.IsAncestorOf(s));
            FixHistory(node);
            node.Detach();
        }
        if (freed > 0) SaveCurrent();
        return freed;
    }

    /// <summary>Si la carpeta abierta (o una del historial) se borró, se sube al padre.</summary>
    private void FixHistory(SpaceNode removed)
    {
        bool Gone(SpaceNode n) => ReferenceEquals(n, removed) || removed.IsAncestorOf(n);

        if (_current is not null && Gone(_current)) _current = removed.Parent ?? _root;
        var back = _back.Where(n => !Gone(n)).Reverse().ToList();
        _back.Clear();
        foreach (var n in back) _back.Push(n);
        _forward.Clear();
    }

    private SpaceNode? FindNode(string path)
    {
        var node = _root;
        while (node is not null)
        {
            if (node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return node;
            node = node.Children.FirstOrDefault(c =>
                c.IsBrowsable && !string.IsNullOrEmpty(c.FullPath) &&
                (path.Equals(c.FullPath, StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith(c.FullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                ?? node.Children.FirstOrDefault(c => c.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (node is not null && node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return node;
        }
        return null;
    }

    private IntPtr OwnerHandle()
    {
        var window = Window.GetWindow(this);
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    // ══════════════════════════ Desinstalar ══════════════════════════

    private void BeginUninstall(InstalledApp app, SpaceNode? node)
    {
        var sub = string.Join(" · ", new[] { app.Publisher, app.Version is null ? null : $"v{app.Version}" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        OpenDialog(
            title: $"Desinstalar {app.DisplayName}",
            subtitle: sub,
            message: "Primero se abrirá el desinstalador oficial de la aplicación; sigue sus pasos. " +
                     "Cuando termine, la Lupa buscará lo que suele quedar atrás (configuración, caché, " +
                     "accesos directos y la carpeta de instalación) para que lo borres también.",
            icon: ShellIcons.ForApp(app, node),
            glyphKey: "Glyph.Trash",
            rows: null,
            primaryText: "Desinstalar",
            primary: () => RunUninstallerAsync(app, node),
            secondaryText: "Cancelar",
            secondary: () => { CloseDialog(); return Task.CompletedTask; });
    }

    private async Task RunUninstallerAsync(InstalledApp app, SpaceNode? node)
    {
        var (process, error) = AppManager.StartUninstaller(app);
        if (error is not null)
        {
            ShowResult("No se pudo desinstalar", error);
            return;
        }

        _uninstalling = app;
        DialogTitle.Text = $"Desinstalando {app.DisplayName}…";
        DialogMessage.Text = "Sigue los pasos del desinstalador. En cuanto Windows deje de registrar la aplicación " +
                             "la Lupa buscará los restos sola; si no, pulsa «Buscar restos» cuando haya terminado.";
        SetDialogButtons("Buscar restos", () => FindLeftoversAsync(app, node), "Cancelar",
            () => { _uninstalling = null; CloseDialog(); return Task.CompletedTask; });

        // Vigilancia: algunos desinstaladores se copian a Temp y el proceso original
        // termina al instante, por eso se comprueba el registro y no sólo el proceso.
        try
        {
            if (process is not null) await process.WaitForExitAsync();
        }
        catch { }

        for (int i = 0; i < 240 && ReferenceEquals(_uninstalling, app); i++)
        {
            if (!await Task.Run(() => AppManager.IsStillInstalled(app)))
            {
                if (ReferenceEquals(_uninstalling, app)) await FindLeftoversAsync(app, node);
                return;
            }
            await Task.Delay(1500);
        }
    }

    private async Task FindLeftoversAsync(InstalledApp app, SpaceNode? node)
    {
        _uninstalling = null;
        SetDialogBusy("Buscando restos…");

        var stillInstalled = await Task.Run(() => AppManager.IsStillInstalled(app));
        var apps = await EnsureAppsAsync(refresh: true);
        var leftovers = await Task.Run(() => AppManager.FindLeftovers(app, apps));

        // En la vista de aplicaciones, la entrada desaparece si ya no está instalada.
        if (!stillInstalled && node is not null && node.Kind == SpaceNodeKind.AppEntry)
        {
            FixHistory(node);
            node.Detach();
            Render();
        }

        if (leftovers.Count == 0)
        {
            ShowResult(stillInstalled ? "La aplicación sigue instalada" : $"{app.DisplayName} se desinstaló",
                stillInstalled
                    ? "Windows todavía la registra: puede que el desinstalador no haya terminado o se cancelara."
                    : "No quedan restos en el disco. Se desinstaló por completo.",
                success: !stillInstalled);
            if (!stillInstalled) RemoveUninstalledFromTree(app);
            return;
        }

        var rows = leftovers.Select(l => new CheckRow(
            title: l.Reason,
            detail: l.Path,
            size: l.Size,
            icon: null,
            glyphKey: l.IsDirectory ? "Glyph.Folder" : "Glyph.File",
            tag: null)).ToList();

        DialogTitle.Text = stillInstalled ? "La aplicación sigue instalada" : "Restos encontrados";
        DialogSubtitle.Text = app.DisplayName;
        DialogMessage.Text = stillInstalled
            ? "Windows todavía la registra: puede que el desinstalador no haya terminado. Aun así, esto es lo que ocupa en el disco. Revisa antes de borrar."
            : "El desinstalador dejó esto atrás. Se moverá a la papelera, así que podrás recuperarlo si hiciera falta.";
        ShowRows(rows);
        SetDialogButtons("Mover a la papelera", async () =>
        {
            var chosen = rows.Where(r => r.IsChecked).ToList();
            if (chosen.Count == 0) { CloseDialog(); return; }
            SetDialogBusy("Moviendo a la papelera…");
            var removed = await AppManager.RecycleAsync(chosen.Select(r => r.Detail).ToList(), OwnerHandle());
            var freed = ApplyRemoval(removed, chosen);
            if (!stillInstalled) RemoveUninstalledFromTree(app);
            Render();
            FooterMessage.Text = $"Se liberaron {UiKit.FormatSize(freed)}";
            ShowResult(stillInstalled ? "Restos borrados" : $"{app.DisplayName} se desinstaló por completo",
                $"Se liberaron {UiKit.FormatSize(freed)}." +
                (removed.Count < chosen.Count ? $" {chosen.Count - removed.Count} elementos no se pudieron borrar." : ""),
                success: true);
        }, "Omitir", () => { CloseDialog(); return Task.CompletedTask; });
    }

    /// <summary>La carpeta de la aplicación ya no tiene dueño: se quita del mapa si sigue en él.</summary>
    private void RemoveUninstalledFromTree(InstalledApp app)
    {
        if (_root is null) return;
        var stack = new Stack<SpaceNode>();
        stack.Push(_root);
        var toRemove = new List<SpaceNode>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.App is not null && n.App.DisplayName == app.DisplayName)
            {
                if (n.Kind == SpaceNodeKind.AppEntry ||
                    (!string.IsNullOrEmpty(n.FullPath) && !Directory.Exists(n.FullPath)))
                    toRemove.Add(n);
                else n.App = null;
                continue;
            }
            foreach (var c in n.Children) if (c.IsBrowsable) stack.Push(c);
        }
        foreach (var n in toRemove)
        {
            FixHistory(n);
            n.Detach();
        }
        Render();
    }

    // ══════════════════════════ Limpiar caché ══════════════════════════

    private CancellationTokenSource? _cacheCts;

    /// <summary>
    /// Mide las cachés y las muestra con casillas antes de borrar nada. Las
    /// que tienen algún efecto secundario (miniaturas, sombreadores, papelera)
    /// vienen desmarcadas.
    /// </summary>
    private async void CleanCache_Click(object sender, RoutedEventArgs e)
    {
        _cacheCts?.Cancel();
        var cts = _cacheCts = new CancellationTokenSource();

        OpenDialog(
            title: "Limpiar caché",
            subtitle: "Midiendo…",
            message: "Buscando temporales, cachés de navegadores y programas, e informes de errores…",
            icon: null,
            glyphKey: "Glyph.Broom",
            rows: null,
            primaryText: "Limpiar",
            primary: () => Task.CompletedTask,
            secondaryText: "Cancelar",
            secondary: () => { cts.Cancel(); CloseDialog(); return Task.CompletedTask; });
        DialogPrimary.IsEnabled = false;

        List<CacheTarget> targets;
        try { targets = await Task.Run(() => CacheCleaner.Measure(cts.Token)); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || Overlay.Visibility != Visibility.Visible) return;

        if (targets.Count == 0)
        {
            ShowResult("Todo limpio", "No hay caché ni temporales que valga la pena borrar ahora mismo.", success: true);
            return;
        }

        var rows = targets.Select(t => new CheckRow(
            title: t.Title,
            detail: t.Description,
            size: t.Size,
            icon: null,
            glyphKey: t.Id == CacheCleaner.RecycleBinId ? "Glyph.Trash" : "Glyph.Broom",
            tag: t,
            isChecked: t.CheckedByDefault,
            isPath: false)).ToList();

        DialogSubtitle.Text = $"{UiKit.FormatSize(targets.Sum(t => t.Size))} encontrados";
        DialogSubtitle.Visibility = Visibility.Visible;
        DialogMessage.Text = "Marca lo que quieras borrar. La caché se borra definitivamente, pero los programas " +
                             "la vuelven a crear cuando la necesitan; no se tocan documentos, contraseñas ni historiales. " +
                             "Lo que esté en uso se salta. Consejo: cierra el navegador antes para liberar más.";
        ShowRows(rows);
        SetDialogButtons("Limpiar", () => RunCacheCleanAsync(rows), "Cancelar",
            () => { CloseDialog(); return Task.CompletedTask; });
    }

    private static string LowerFirst(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLower(s[0]) + s[1..] : s;

    private async Task RunCacheCleanAsync(IReadOnlyList<CheckRow> rows)
    {
        var chosen = rows.Where(r => r.IsChecked).Select(r => (CacheTarget)r.Tag!).ToList();
        if (chosen.Count == 0) { CloseDialog(); return; }

        SetDialogBusy("Limpiando… los archivos en uso se saltan.");
        var planned = chosen.Sum(t => t.Size);

        // Barra de progreso: la lista se oculta y se ve qué se está borrando y cuánto va.
        DialogListHost.Visibility = Visibility.Collapsed;
        DialogTotal.Text = "";
        DialogProgressPanel.Visibility = Visibility.Visible;
        DialogProgressBar.Value = 0;
        DialogProgressText.Text = chosen[0].Title;
        DialogProgressPercent.Text = "0 %";
        DialogProgressFreed.Text = "";

        var progress = new Progress<CacheCleaner.CleanProgress>(p =>
        {
            DialogProgressText.Text = p.Current == "Listo" ? "Terminando…" : $"Limpiando {LowerFirst(p.Current)}…";
            DialogProgressPercent.Text = $"{p.Fraction * 100:0} %";
            DialogProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(p.Fraction, new Duration(TimeSpan.FromMilliseconds(180))));
            DialogProgressFreed.Text = $"{UiKit.FormatSize(p.Freed)} liberados de {UiKit.FormatSize(planned)}";
        });

        var freed = await Task.Run(() => CacheCleaner.Clean(chosen, progress));
        await Task.Delay(350);   // que se vea la barra llena antes del resultado
        DialogProgressPanel.Visibility = Visibility.Collapsed;
        DialogProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);

        UpdateVolume();
        FooterMessage.Text = $"Caché limpia: {UiKit.FormatSize(freed)} liberados";

        var skipped = planned - freed;
        var message = $"Se liberaron {UiKit.FormatSize(freed)}.";
        if (skipped > 1024 * 1024)
            message += $"\n\n{UiKit.FormatSize(skipped)} no se pudieron borrar porque estaban en uso o necesitan " +
                       "permisos de administrador. Cierra los programas abiertos (sobre todo el navegador) y vuelve a intentarlo.";
        if (_root is not null)
            message += "\n\nEl mapa sigue mostrando el análisis anterior; pulsa «Actualizar» para verlo al día.";

        ShowResult("Limpieza terminada", message, success: true);
    }

    // ══════════════════════════ Menú contextual ══════════════════════════

    private void ShowContextMenu(SpaceNode node, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        if (node.Kind == SpaceNodeKind.File)
        {
            menu.Items.Add(MenuEntry("Vista previa", "Glyph.Eye", () => OpenPreview(node), filled: true));
            menu.Items.Add(MenuEntry("Abrir", "Glyph.Open", () => Thumbnails.Open(node.FullPath)));
            menu.Items.Add(MenuEntry("Abrir con…", "Glyph.OpenWith", () => Thumbnails.OpenWith(node.FullPath)));
        }
        else if (node.IsBrowsable)
        {
            menu.Items.Add(MenuEntry("Entrar", "Glyph.Forward", () => Navigate(node)));
        }

        var folder = node.Kind == SpaceNodeKind.AppEntry ? node.App?.InstallLocation : node.FolderPath;
        if (!string.IsNullOrEmpty(node.FullPath) || !string.IsNullOrEmpty(folder))
            menu.Items.Add(MenuEntry("Mostrar en el Explorador", "Glyph.Folder.Line",
                () => AppManager.Reveal(string.IsNullOrEmpty(node.FullPath) ? folder! : node.FullPath)));

        if (node.App is { CanUninstall: true } app)
        {
            menu.Items.Add(new Separator { Style = UiKit.Style("MenuSeparator") });
            menu.Items.Add(MenuEntry("Desinstalar por completo", "Glyph.Trash", () => BeginUninstall(app, node)));
        }

        if (CanSelect(node))
        {
            menu.Items.Add(new Separator { Style = UiKit.Style("MenuSeparator") });
            var selected = _selected.Contains(node);
            menu.Items.Add(MenuEntry(selected ? "Quitar de la selección" : "Seleccionar para eliminar", "Glyph.Check", () =>
            {
                SetSelected(node, !selected);
                RefreshRows();
            }));
        }

        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string header, string glyphKey, Action action, bool filled = false)
    {
        var glyph = new System.Windows.Shapes.Path
        {
            Data = (Geometry)Application.Current.FindResource(glyphKey),
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        if (filled) glyph.Fill = UiKit.Brush("Br.AccentBright");
        else glyph.Stroke = UiKit.Brush("Br.AccentBright");

        var item = new MenuItem { Header = header, Icon = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    // ══════════════════════════ Visor ══════════════════════════

    /// <summary>Abre el visor en este archivo; las flechas recorren los de su misma carpeta.</summary>
    private void OpenPreview(SpaceNode file)
    {
        var siblings = file.Parent?.Children.Where(c => c.Kind == SpaceNodeKind.File).ToList()
                       ?? new List<SpaceNode> { file };
        _previewList = siblings;
        _previewIndex = Math.Max(0, siblings.IndexOf(file));

        PreviewOverlay.Visibility = Visibility.Visible;
        PreviewOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
        Focus();
        _ = ShowPreviewItemAsync();
    }

    private async Task ShowPreviewItemAsync()
    {
        if (_previewList.Count == 0) return;
        var version = ++_previewVersion;
        var node = _previewList[_previewIndex];
        var path = node.FullPath;

        StopMedia();
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewMedia.Visibility = Visibility.Collapsed;
        PreviewFallback.Visibility = Visibility.Collapsed;
        PreviewLoading.Visibility = Visibility.Collapsed;

        PreviewName.Text = node.Name;
        var meta = $"{FileKind(node.Name)}  ·  {UiKit.FormatSize(node.Size)}  ·  {node.Modified.ToString("d MMM yyyy, H:mm", Spanish)}";
        PreviewMeta.Text = meta;
        PreviewCounter.Text = _previewList.Count > 1 ? $"{_previewIndex + 1} de {_previewList.Count}" : "";
        PreviewPrevButton.IsEnabled = _previewIndex > 0;
        PreviewNextButton.IsEnabled = _previewIndex < _previewList.Count - 1;
        UpdatePreviewSelectButton(node);

        if (!File.Exists(path))
        {
            ShowPreviewFallback(node, "Este archivo ya no existe en el disco.");
            return;
        }

        if (Thumbnails.IsImage(path))
        {
            PreviewLoading.Visibility = Visibility.Visible;
            var max = (int)Math.Max(1200, Math.Max(ActualWidth, ActualHeight) * 1.5);
            var (image, width, height) = await Thumbnails.LargeAsync(path, max);
            if (version != _previewVersion) return;   // ya se pasó a otro archivo

            PreviewLoading.Visibility = Visibility.Collapsed;
            if (image is null)
            {
                ShowPreviewFallback(node, "Windows no sabe mostrar esta imagen aquí. Ábrela con otro programa.");
                return;
            }
            PreviewImage.Source = image;
            PreviewImage.Visibility = Visibility.Visible;
            if (width > 0) PreviewMeta.Text = $"{meta}  ·  {width:N0} × {height:N0} px";
        }
        else if (Thumbnails.IsMedia(path))
        {
            PreviewMedia.Source = new Uri(path);
            PreviewMedia.Visibility = Visibility.Visible;
            PreviewMedia.Play();
            _mediaPlaying = true;
            PreviewMeta.Text = $"{meta}  ·  clic en el vídeo para pausar";
        }
        else
        {
            ShowPreviewFallback(node, "No hay vista previa para este tipo de archivo. Ábrelo con su programa.");
        }
    }

    private void ShowPreviewFallback(SpaceNode node, string message)
    {
        PreviewFallbackIcon.Source = ShellIcons.ForFile(node.FullPath);
        PreviewFallbackText.Text = message;
        PreviewFallback.Visibility = Visibility.Visible;
    }

    private void UpdatePreviewSelectButton(SpaceNode node)
    {
        PreviewSelectButton.Visibility = CanSelect(node) ? Visibility.Visible : Visibility.Collapsed;
        PreviewSelectButton.Content = _selected.Contains(node) ? "Quitar de la selección" : "Seleccionar para eliminar";
    }

    private SpaceNode? PreviewNode =>
        _previewIndex >= 0 && _previewIndex < _previewList.Count ? _previewList[_previewIndex] : null;

    private void StopMedia()
    {
        try
        {
            PreviewMedia.Stop();
            PreviewMedia.Source = null;
        }
        catch { }
        _mediaPlaying = false;
    }

    private void ClosePreview()
    {
        _previewVersion++;
        StopMedia();
        PreviewImage.Source = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
    }

    private void MovePreview(int delta)
    {
        var next = _previewIndex + delta;
        if (next < 0 || next >= _previewList.Count) return;
        _previewIndex = next;
        _ = ShowPreviewItemAsync();
    }

    private void PreviewPrev_Click(object sender, RoutedEventArgs e) => MovePreview(-1);
    private void PreviewNext_Click(object sender, RoutedEventArgs e) => MovePreview(1);
    private void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void PreviewBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PreviewOverlay)) ClosePreview();
    }

    private void PreviewOpen_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewNode is { } n) { StopMedia(); Thumbnails.Open(n.FullPath); }
    }

    private void PreviewOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewNode is { } n) { StopMedia(); Thumbnails.OpenWith(n.FullPath); }
    }

    private void PreviewReveal_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewNode is { } n) AppManager.Reveal(n.FullPath);
    }

    private void PreviewSelect_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewNode is not { } n || !CanSelect(n)) return;
        SetSelected(n, !_selected.Contains(n));
        RefreshRows();
        UpdatePreviewSelectButton(n);
    }

    private void PreviewMedia_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_mediaPlaying) PreviewMedia.Pause();
        else PreviewMedia.Play();
        _mediaPlaying = !_mediaPlaying;
    }

    private void PreviewMedia_Failed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (PreviewNode is not { } n) return;
        PreviewMedia.Visibility = Visibility.Collapsed;
        ShowPreviewFallback(n, "Windows no tiene el códec para reproducir este archivo aquí. Ábrelo con su programa.");
    }

    /// <summary>Teclado: flechas y Esc en el visor; Retroceso para subir de carpeta.</summary>
    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PreviewOverlay.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case Key.Escape: ClosePreview(); e.Handled = true; break;
                case Key.Left: MovePreview(-1); e.Handled = true; break;
                case Key.Right: MovePreview(1); e.Handled = true; break;
                case Key.Space: PreviewSelect_Click(sender, e); e.Handled = true; break;
            }
            return;
        }
        if (Overlay.Visibility == Visibility.Visible) return;

        if (BrowsePanel.Visibility == Visibility.Visible && e.Key == Key.Back && e.OriginalSource is not TextBox)
        {
            GoBack();
            e.Handled = true;
        }
    }

    // ══════════════════════════ Diálogo ══════════════════════════

    private void OpenDialog(string title, string subtitle, string message, ImageSource? icon, string glyphKey,
        List<CheckRow>? rows, string primaryText, Func<Task> primary, string secondaryText, Func<Task> secondary)
    {
        DialogTitle.Text = title;
        DialogSubtitle.Text = subtitle;
        DialogSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        DialogMessage.Text = message;

        DialogImage.Source = icon;
        DialogImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        DialogGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        DialogGlyph.Data = (Geometry)FindResource(glyphKey);
        DialogGlyph.Stroke = UiKit.Brush("Br.AccentBright");
        DialogGlyph.Fill = null;

        ShowRows(rows);
        SetDialogButtons(primaryText, primary, secondaryText, secondary);

        Overlay.Visibility = Visibility.Visible;
        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));
    }

    private void ShowRows(List<CheckRow>? rows)
    {
        _dialogRows = rows ?? new List<CheckRow>();
        DialogList.ItemsSource = _dialogRows;
        DialogListHost.Visibility = _dialogRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _dialogRowsChanged = () =>
        {
            var chosen = _dialogRows.Where(r => r.IsChecked).ToList();
            DialogTotal.Text = _dialogRows.Count == 0
                ? ""
                : $"{UiKit.Plural(chosen.Count, "elemento", "elementos")} · {UiKit.FormatSize(chosen.Sum(r => r.Size))}";
            DialogPrimary.IsEnabled = _dialogRows.Count == 0 || chosen.Count > 0;
        };
        foreach (var row in _dialogRows) row.Changed = _dialogRowsChanged;
        _dialogRowsChanged();
    }

    private void SetDialogButtons(string primaryText, Func<Task> primary, string? secondaryText, Func<Task>? secondary)
    {
        DialogPrimary.Content = primaryText;
        DialogPrimary.IsEnabled = true;
        DialogPrimary.Visibility = Visibility.Visible;
        _dialogPrimary = primary;

        DialogSecondary.Content = secondaryText;
        DialogSecondary.IsEnabled = true;
        DialogSecondary.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
        _dialogSecondary = secondary;
        _dialogRowsChanged?.Invoke();
    }

    private void SetDialogBusy(string message)
    {
        DialogMessage.Text = message;
        DialogPrimary.IsEnabled = false;
        DialogSecondary.IsEnabled = false;
    }

    private void ShowResult(string title, string message, bool success = false)
    {
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogSubtitle.Visibility = Visibility.Collapsed;
        ShowRows(null);
        DialogTotal.Text = "";
        SetDialogButtons("Listo", () => { CloseDialog(); return Task.CompletedTask; }, null, null);
        DialogPrimary.Style = UiKit.Style(success ? "BtnPrimary" : "Btn");
    }

    private void CloseDialog()
    {
        Overlay.Visibility = Visibility.Collapsed;
        DialogProgressPanel.Visibility = Visibility.Collapsed;
        DialogPrimary.Style = UiKit.Style("BtnDestructive");
        DialogPrimary.Padding = new Thickness(18, 9, 18, 9);
        _dialogRows = new List<CheckRow>();
        DialogList.ItemsSource = null;
    }

    private async void DialogPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogPrimary is { } action) await RunDialogAction(action);
    }

    private async void DialogSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogSecondary is { } action) await RunDialogAction(action);
    }

    private async Task RunDialogAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowResult("Algo salió mal", ex.Message); }
    }

    // ══════════════════════════ Modelos de fila ══════════════════════════

    /// <summary>Fila de la lista de la izquierda. El icono se carga al hacerse visible.</summary>
    public sealed class SpaceRow : INotifyPropertyChanged
    {
        private readonly LupaView _owner;
        private ImageSource? _icon;
        private bool _iconLoaded;

        public SpaceRow(SpaceNode node, long parentSize, LupaView owner)
        {
            Node = node;
            _owner = owner;

            var share = parentSize > 0 ? Math.Clamp(node.Size / (double)parentSize, 0, 1) : 0;
            ShareWidth = new GridLength(Math.Max(share, 0.0001), GridUnitType.Star);
            RestWidth = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star);

            CanSelect = LupaView.CanSelect(node);
            if (!CanSelect && !string.IsNullOrEmpty(node.FullPath) && node.Kind != SpaceNodeKind.Aggregate &&
                AppManager.IsProtected(node.FullPath, out var reason))
                ProtectedReason = reason;
        }

        public SpaceNode Node { get; }
        public string Name => Node.DisplayName;
        public string FullPath => Node.FullPath;
        public string SizeText => UiKit.FormatSize(Node.Size);
        public bool CanSelect { get; }
        public string? ProtectedReason { get; }
        public bool CanUninstall => Node.App?.CanUninstall == true;
        public GridLength ShareWidth { get; }
        public GridLength RestWidth { get; }

        public string SubText => Node.Kind switch
        {
            SpaceNodeKind.Aggregate => "Archivos pequeños agrupados",
            SpaceNodeKind.AppEntry => string.Join(" · ", new[] { Node.App?.Publisher, "tamaño declarado" }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            SpaceNodeKind.File => $"{FileKind(Node.Name)} · {Node.Modified.ToString("d MMM yyyy", Spanish)}",
            _ when Node.App is not null => string.Join(" · ", new[]
            {
                Node.App.Publisher, Node.App.Version is null ? null : $"v{Node.App.Version}",
                $"{UiKit.CompactCount(Node.ItemCount)} ítems"
            }.Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => ProtectedReason is not null
                ? $"{ProtectedReason} · {UiKit.CompactCount(Node.ItemCount)} ítems"
                : $"{UiKit.CompactCount(Node.ItemCount)} ítems"
        };

        public bool IsChecked
        {
            get => _owner.IsSelected(Node);
            set
            {
                if (!CanSelect || value == _owner.IsSelected(Node)) return;
                _owner.SetSelected(Node, value);
                OnChanged(nameof(IsChecked));
            }
        }

        public Visibility CheckVisibility => CanSelect ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LockVisibility => ProtectedReason is not null ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? Icon
        {
            get
            {
                if (!_iconLoaded)
                {
                    _iconLoaded = true;
                    _icon = ShellIcons.ForNode(Node);
                    if (Node.Kind == SpaceNodeKind.File && Thumbnails.IsImage(Node.FullPath))
                        _ = LoadThumbnailAsync();
                }
                return _icon;
            }
        }

        /// <summary>En las fotos, la miniatura real sustituye al icono cuando termina de cargar.</summary>
        private async Task LoadThumbnailAsync()
        {
            var thumb = await Thumbnails.SmallAsync(Node.FullPath, 64);
            if (thumb is null) return;
            _icon = thumb;
            OnChanged(nameof(Icon));
            OnChanged(nameof(ImageVisibility));
            OnChanged(nameof(GlyphVisibility));
        }

        public Visibility ImageVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
        public Geometry Glyph => (Geometry)Application.Current.FindResource(GlyphKey(Node));
        public Brush GlyphBrush => UiKit.Brush(Node.Kind switch
        {
            SpaceNodeKind.File => "Br.GlyphFile",
            SpaceNodeKind.Aggregate => "Br.GlyphOthers",
            _ => "Br.GlyphFolder"
        });

        public void Refresh() => OnChanged(nameof(IsChecked));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Fila con casilla de los diálogos.</summary>
    public sealed class CheckRow : INotifyPropertyChanged
    {
        private bool _isChecked = true;

        public CheckRow(string title, string detail, long size, ImageSource? icon, string glyphKey, object? tag,
            bool isChecked = true, bool isPath = true)
        {
            _isChecked = isChecked;
            DetailFont = (FontFamily)Application.Current.FindResource(isPath ? "Font.Mono" : "Font.Text");
            DetailSize = isPath ? 10.5 : 11.5;
            DetailWrap = isPath ? TextWrapping.NoWrap : TextWrapping.Wrap;
            Title = title;
            Detail = detail;
            Size = size;
            Icon = icon;
            Glyph = (Geometry)Application.Current.FindResource(glyphKey);
            Tag = tag;
        }

        public string Title { get; }
        public string Detail { get; }
        public long Size { get; }
        public string SizeText => UiKit.FormatSize(Size);
        public FontFamily DetailFont { get; }
        public double DetailSize { get; }
        public TextWrapping DetailWrap { get; }
        public ImageSource? Icon { get; }
        public Geometry Glyph { get; }
        public Brush GlyphBrush => UiKit.Brush("Br.GlyphFolder");
        public Visibility ImageVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
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
}
