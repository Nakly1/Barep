using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class ProcessesView : UserControl
{
    private static readonly CultureInfo Spanish = new("es-ES");

    /// <summary>Procesos que mantienen Windows en pie: cerrarlos apaga o reinicia el equipo.</summary>
    private static readonly string[] Untouchable =
    {
        "system", "idle", "registry", "secure system", "memory compression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "svchost", "fontdrvhost", "dwm", "sihost", "explorer"
    };

    private readonly ProcessService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly ObservableCollection<ProcRow> _rows = new();
    private readonly Dictionary<string, ProcRow> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private ProcessSnapshot? _snapshot;
    private string? _selectedKey;
    private bool _busy;
    private bool _hooked;

    /// <summary>Mientras se reordena la lista, los cambios de selección no son del usuario.</summary>
    private bool _updating;

    public ProcessesView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            RowsList.ItemsSource = _rows;
            _timer.Tick += async (_, _) => await SampleAsync();
            EmptyText.Text = "Leyendo la lista de procesos…";
            EmptyText.Visibility = Visibility.Visible;
        }

        _timer.Start();
        await SampleAsync();
    }

    // ────────────────────────── Lectura ──────────────────────────

    private async Task SampleAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snapshot = await Task.Run(_service.Sample);
            _snapshot = snapshot;
            Apply(snapshot, reorder: !RowsList.IsMouseOver);
        }
        catch { /* un proceso que desaparece a media lectura no es un error */ }
        finally { _busy = false; }
    }

    private void Apply(ProcessSnapshot snapshot, bool reorder)
    {
        CountChip.Text = $"{snapshot.ProcessCount:N0} procesos · {snapshot.ThreadCount:N0} hilos";
        CpuChip.Text = $"CPU {snapshot.CpuPercent:0.0} %";
        RamChip.Text = snapshot.RamTotalBytes > 0
            ? $"RAM {UiKit.FormatSize(snapshot.RamUsedBytes)} de {UiKit.FormatSize(snapshot.RamTotalBytes)}"
            : "RAM —";

        var visible = Visible(snapshot.Groups);
        var heaviest = visible.Count > 0 ? visible.Max(g => g.MemoryBytes) : 0;

        // Las filas se reutilizan: así no se pierde el icono ya cargado ni salta
        // la lista bajo el cursor cada dos segundos.
        foreach (var group in visible)
        {
            if (_byKey.TryGetValue(group.Key, out var row)) row.Update(group, heaviest);
            else _byKey[group.Key] = new ProcRow(group, heaviest);
        }

        foreach (var gone in _byKey.Keys.Except(visible.Select(g => g.Key), StringComparer.OrdinalIgnoreCase).ToList())
        {
            _rows.Remove(_byKey[gone]);
            _byKey.Remove(gone);
        }

        if (reorder || _rows.Count != visible.Count) Reorder(visible);

        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible.Count == 0)
            EmptyText.Text = SearchBox.Text.Length > 0
                ? $"Nada coincide con «{SearchBox.Text}»."
                : "No hay procesos que mostrar con este filtro.";

        RefreshDetail();
    }

    /// <summary>Coloca las filas en el orden pedido moviendo lo mínimo.</summary>
    private void Reorder(List<ProcessGroup> order)
    {
        _updating = true;
        try
        {
            if (_rows.Count == 0 || Math.Abs(_rows.Count - order.Count) > 8)
            {
                _rows.Clear();
                foreach (var group in order) _rows.Add(_byKey[group.Key]);
                return;
            }

            for (int i = 0; i < order.Count; i++)
            {
                var row = _byKey[order[i].Key];
                var current = _rows.IndexOf(row);
                if (current < 0) _rows.Insert(Math.Min(i, _rows.Count), row);
                else if (current != i) _rows.Move(current, i);
            }

            while (_rows.Count > order.Count) _rows.RemoveAt(_rows.Count - 1);
        }
        finally
        {
            RestoreSelection();
            _updating = false;
        }
    }

    private void RestoreSelection()
    {
        if (_selectedKey is null) return;
        if (RowsList.SelectedItem is ProcRow current && current.Key == _selectedKey) return;
        if (_byKey.TryGetValue(_selectedKey, out var row)) RowsList.SelectedItem = row;
    }

    // ────────────────────────── Filtro, búsqueda y orden ──────────────────────────

    private List<ProcessGroup> Visible(List<ProcessGroup> groups)
    {
        var query = SearchBox.Text.Trim();

        IEnumerable<ProcessGroup> list = groups;

        if (FilterApps.IsChecked == true) list = list.Where(g => g.Kind == ProcessKind.App);
        else if (FilterBackground.IsChecked == true) list = list.Where(g => g.Kind == ProcessKind.Background);
        else if (FilterWindows.IsChecked == true) list = list.Where(g => g.Kind == ProcessKind.Windows);

        if (query.Length > 0) list = list.Where(g => Matches(g, query));

        list = SortCpu.IsChecked == true
            ? list.OrderByDescending(g => g.CpuPercent).ThenByDescending(g => g.MemoryBytes)
            : SortName.IsChecked == true
                ? list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                : list.OrderByDescending(g => g.MemoryBytes);

        return list.ToList();
    }

    private static bool Matches(ProcessGroup group, string query)
    {
        bool Has(string? value) => value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

        return Has(group.Title) || Has(group.Name) || Has(group.Company) || Has(group.Services) ||
               Has(group.Path) || group.Items.Any(i => i.Pid.ToString() == query);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_snapshot is { } snapshot) Apply(snapshot, reorder: true);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _snapshot is not { } snapshot) return;
        Apply(snapshot, reorder: true);
    }

    // ────────────────────────── Ficha de la derecha ──────────────────────────

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _selectedKey = (RowsList.SelectedItem as ProcRow)?.Key;
        RefreshDetail();
    }

    private ProcessGroup? Selected =>
        _selectedKey is null
            ? null
            : _snapshot?.Groups.FirstOrDefault(g => g.Key.Equals(_selectedKey, StringComparison.OrdinalIgnoreCase));

    private void RefreshDetail()
    {
        var group = Selected;
        if (group is null)
        {
            DetailHint.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            return;
        }

        DetailHint.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        DetailTitle.Text = group.Title;
        DetailCompany.Text = group.Company ?? group.Name;

        var icon = Icon(group.Path);
        DetailImage.Source = icon;
        DetailImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        DetailGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;

        DetailKindText.Text = KindLabel(group.Kind);

        DetailNote.Text = group.Kind switch
        {
            ProcessKind.Windows => "Forma parte de Windows. Cerrarlo puede dejar el escritorio o el sonido sin funcionar hasta reiniciar.",
            ProcessKind.Background => "Trabaja sin ventana. Suele ser un actualizador o un ayudante del programa; al cerrarlo puede volver a abrirse solo.",
            _ => ""
        };
        DetailNote.Visibility = DetailNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var specs = new List<(string, string?)>
        {
            ("Memoria", UiKit.FormatSize(group.MemoryBytes)),
            ("Procesador", $"{group.CpuPercent:0.0} %"),
            ("Procesos", UiKit.Plural(group.Count, "proceso", "procesos")),
            ("Hilos", $"{group.Threads:N0}")
        };
        if (group.Started is { } started)
            specs.Add(("Abierto desde", started.ToString("HH:mm", Spanish) + " · " + Ago(started)));
        if (group.Version is not null) specs.Add(("Versión", group.Version));
        if (group.Services is not null) specs.Add(("Servicio", group.Services));
        UiKit.FillSpecs(DetailSpecs, specs, labelWidth: 104);

        DetailPath.Text = group.Path ?? "Windows no deja leer la ruta de este proceso.";

        var untouchable = Untouchable.Contains(group.Name, StringComparer.OrdinalIgnoreCase);
        KillButton.IsEnabled = !untouchable;
        KillButton.ToolTip = untouchable
            ? "Windows necesita este proceso para funcionar."
            : $"Cerrar «{group.Title}» y sus procesos";
        KillButton.Content = group.Count > 1 ? $"Finalizar los {group.Count}" : "Finalizar";
        RevealButton.IsEnabled = group.Path is not null && File.Exists(group.Path);

        PidHeader.Text = group.Count > 1 ? $"Sus {group.Count} procesos" : "Su proceso";
        PidList.ItemsSource = group.Items
            .Select(i => new PidRow(
                PidText: i.Pid.ToString(),
                Detail: $"{i.CpuPercent:0.0} % CPU · {i.Threads:N0} hilos" + (i.HasWindow ? " · con ventana" : ""),
                Tip: i.Path ?? i.Name,
                MemoryText: UiKit.FormatSize(i.MemoryBytes)))
            .ToList();
    }

    private static string Ago(DateTime at)
    {
        var elapsed = DateTime.Now - at;
        if (elapsed.TotalMinutes < 1) return "hace un momento";
        if (elapsed.TotalHours < 1) return $"hace {(int)elapsed.TotalMinutes} min";
        if (elapsed.TotalDays < 1) return $"hace {(int)elapsed.TotalHours} h";
        return $"hace {(int)elapsed.TotalDays} d";
    }

    private static string KindLabel(ProcessKind kind) => kind switch
    {
        ProcessKind.App => "Aplicación abierta",
        ProcessKind.Background => "En segundo plano",
        _ => "Proceso de Windows"
    };

    internal static ImageSource? Icon(string? path) =>
        path is not null && File.Exists(path) ? ShellIcons.ForFile(path) : null;

    // ────────────────────────── Acciones ──────────────────────────

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } group) await KillAsync(group);
    }

    private async void RowKill_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProcRow row) await KillAsync(row.Group);
    }

    private async Task KillAsync(ProcessGroup group)
    {
        if (Untouchable.Contains(group.Name, StringComparer.OrdinalIgnoreCase))
        {
            Status($"Windows necesita «{group.Title}» para funcionar: mejor no cerrarlo.", ok: false);
            return;
        }

        var warning = group.Kind == ProcessKind.Windows
            ? "\n\nEs un proceso de Windows: puede que algo del sistema deje de responder hasta que reinicies."
            : "";

        var confirm = MessageBox.Show(
            group.Count > 1
                ? $"Se cerrarán los {group.Count} procesos de «{group.Title}».\n\nSi hay trabajo sin guardar, se perderá.{warning}"
                : $"Se cerrará «{group.Title}».\n\nSi hay trabajo sin guardar, se perderá.{warning}",
            "Finalizar", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        var (closed, message) = ProcessService.Kill(group);
        Status(message, closed > 0);
        await SampleAsync();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e) => ProcessService.Reveal(Selected?.Path);

    private void Status(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = UiKit.Brush(ok ? "Br.Positive" : "Br.Critical");
    }

    // ────────────────────────── Filas ──────────────────────────

    private sealed record PidRow(string PidText, string Detail, string Tip, string MemoryText);

    /// <summary>Una fila de la lista: un programa con todos sus procesos sumados.</summary>
    public sealed class ProcRow : INotifyPropertyChanged
    {
        private ProcessGroup _group;
        private long _heaviest;
        private ImageSource? _icon;
        private bool _iconLoaded;

        public ProcRow(ProcessGroup group, long heaviest)
        {
            _group = group;
            _heaviest = heaviest;
        }

        public string Key => _group.Key;
        public ProcessGroup Group => _group;

        public void Update(ProcessGroup group, long heaviest)
        {
            var samePath = string.Equals(group.Path, _group.Path, StringComparison.OrdinalIgnoreCase);
            _group = group;
            _heaviest = heaviest;
            if (!samePath) { _icon = null; _iconLoaded = false; }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        public string Title => _group.Title;

        public string SubText
        {
            get
            {
                var parts = new List<string>();
                if (_group.Count > 1) parts.Add($"{_group.Count} procesos");
                if (_group.Services is { } services)
                    parts.Add(services.Length > 54 ? "Servicios: " + services[..51] + "…" : "Servicio: " + services);
                else if (_group.Company is { } company) parts.Add(company);
                if (_group.Kind != ProcessKind.App) parts.Add(KindLabel(_group.Kind));
                if (parts.Count == 0) parts.Add(_group.Name);
                return string.Join(" · ", parts);
            }
        }

        public string Tip => _group.Path ?? _group.Name;

        public string CpuText => _group.CpuPercent < 0.05 ? "—" : $"{_group.CpuPercent:0.0} %";

        public Brush CpuBrush => UiKit.Brush(_group.CpuPercent switch
        {
            >= 45 => "Br.Critical",
            >= 15 => "Br.Warn",
            _ => "Br.TextSecondary"
        });

        public string MemoryText => UiKit.FormatSize(_group.MemoryBytes);

        public double Share => _heaviest > 0 ? Math.Min(100, _group.MemoryBytes * 100.0 / _heaviest) : 0;

        public ImageSource? Icon
        {
            get
            {
                if (!_iconLoaded)
                {
                    _iconLoaded = true;
                    _icon = ProcessesView.Icon(_group.Path);
                }
                return _icon;
            }
        }

        public Visibility ImageVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
