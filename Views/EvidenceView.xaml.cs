using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Barep.Controls;
using Barep.Services;
using Path = System.IO.Path;

namespace Barep.Views;

/// <summary>
/// Evidencia de hardware en dos pasos: crear el acta antes de dejar el equipo y
/// comprobarla al recogerlo. El acta se ve dentro de la app como un documento,
/// y al comprobarla cada pieza queda marcada como igual, cambiada o ausente.
/// </summary>
public partial class EvidenceView : UserControl
{
    private static readonly CultureInfo Spanish = new("es-ES");

    private readonly ObservableCollection<ActaRow> _actas = new();
    private EvidenceService.SavedActa? _current;
    private EvidenceComparison? _comparison;
    private bool _loaded;

    public EvidenceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        ActaList.ItemsSource = _actas;
        _ = RefreshListAsync(selectPath: null);
        _ = CountPartsAsync();
    }

    /// <summary>Contar las piezas obliga a releer el SMBIOS: se hace fuera del hilo de la interfaz.</summary>
    private async Task CountPartsAsync()
    {
        try
        {
            var doc = await Task.Run(() => AppServices.Evidence.Capture());
            var withSerial = doc.Parts.Count(p => p.HasIdentity);
            PartCountText.Text = $"Este equipo tiene {UiKit.Plural(doc.Parts.Count, "pieza", "piezas")}, " +
                                 $"{withSerial} con número de serie";
        }
        catch { PartCountText.Text = ""; }
    }

    private async Task RefreshListAsync(string? selectPath)
    {
        var saved = await Task.Run(() => AppServices.Evidence.ListSaved());

        // Un acta abierta desde otra carpeta se mantiene en la lista.
        if (_current is not null && saved.All(s => !SamePath(s.Path, _current.Path)) && File.Exists(_current.Path))
            saved.Insert(0, _current);

        _actas.Clear();
        foreach (var acta in saved) _actas.Add(new ActaRow(acta));
        NoActasText.Visibility = _actas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActaList.Visibility = _actas.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var target = selectPath ?? _current?.Path;
        var row = target is null ? null : _actas.FirstOrDefault(a => SamePath(a.Acta.Path, target));
        if (row is not null) ActaList.SelectedItem = row;
        else if (_current is null) ShowActa(null);
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ────────────────────────── Paso 1 · crear ──────────────────────────

    private void NoteBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NoteHint.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        CreateButton.Content = "Leyendo las piezas…";
        try
        {
            var note = NoteBox.Text;
            var acta = await Task.Run(() => AppServices.Evidence.CreateAndSave(note));
            _current = acta;
            _comparison = null;
            NoteBox.Text = "";
            await RefreshListAsync(acta.Path);
            ShowActa(acta);
            Status($"Acta creada con {UiKit.Plural(acta.Document.Parts.Count, "pieza", "piezas")}. " +
                   "Apunta o fotografía el código de verificación: si alguien edita el archivo, dejará de coincidir.", ok: true);
        }
        catch (Exception ex)
        {
            Status($"No se pudo crear el acta: {ex.Message}", ok: false);
        }
        finally
        {
            CreateButton.IsEnabled = true;
            CreateButton.Content = "Crear acta";
        }
    }

    // ────────────────────────── Paso 2 · elegir y comprobar ──────────────────────────

    private void ActaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActaList.SelectedItem is not ActaRow row) return;
        if (_current is not null && SamePath(_current.Path, row.Acta.Path) && _comparison is not null) return;
        _current = row.Acta;
        _comparison = null;
        ShowActa(row.Acta);
    }

    private async void OpenOther_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Elegir un acta",
            Filter = "Acta de Barep (*.smev.json)|*.smev.json|Todos los archivos|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var path = dialog.FileName;
            var doc = await Task.Run(() => AppServices.Evidence.Load(path));
            _current = new EvidenceService.SavedActa(path, doc);
            _comparison = null;
            await RefreshListAsync(path);
            ShowActa(_current);
        }
        catch (Exception ex)
        {
            Status($"Ese archivo no es un acta válida: {ex.Message}", ok: false);
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } acta) return;

        CompareButton.IsEnabled = false;
        CompareButton.Content = "Comprobando…";
        try
        {
            _comparison = await Task.Run(() => AppServices.Evidence.CompareWithCurrent(acta.Document));
            ShowActa(acta);
            Status(_comparison.Matches
                ? "Comprobado: el equipo tiene las mismas piezas que cuando lo dejaste."
                : "Comprobado: hay piezas que no coinciden con el acta. Revisa la lista.", _comparison.Matches);
        }
        catch (Exception ex)
        {
            Status($"No se pudo comprobar: {ex.Message}", ok: false);
        }
        finally
        {
            CompareButton.IsEnabled = true;
            CompareButton.Content = "Comprobar otra vez";
        }
    }

    // ────────────────────────── El acta en pantalla ──────────────────────────

    private void ShowActa(EvidenceService.SavedActa? acta)
    {
        if (acta is null)
        {
            ActaEmpty.Visibility = Visibility.Visible;
            ActaView.Visibility = Visibility.Collapsed;
            return;
        }

        ActaEmpty.Visibility = Visibility.Collapsed;
        ActaView.Visibility = Visibility.Visible;

        var doc = acta.Document;
        ActaDate.Text = doc.CreatedAt.ToString("d 'de' MMMM 'de' yyyy · HH:mm", Spanish);

        var authentic = AppServices.Evidence.IsAuthentic(doc);
        IntegrityText.Text = authentic ? "Sin alterar" : "Editada después";
        IntegrityText.Foreground = UiKit.Brush(authentic ? "Br.Positive" : "Br.Critical");
        IntegrityChip.Background = UiKit.Brush(authentic ? "Br.PositiveTint" : "Br.CriticalTint");
        IntegrityChip.ToolTip = authentic
            ? "La huella SHA-256 coincide: nadie ha tocado el archivo desde que se creó."
            : "La huella no coincide: el archivo se modificó después de crearse y no sirve como prueba.";

        var specs = new List<(string, string?)>
        {
            ("Equipo", doc.MachineName),
            ("Usuario", doc.UserName),
            ("Sistema", doc.OperatingSystem),
            ("Se dejó a", string.IsNullOrWhiteSpace(doc.Note) ? null : doc.Note),
            ("Código", EvidenceService.ShortCode(doc.Fingerprint))
        };
        UiKit.FillSpecs(ActaSpecs, specs.Where(s => !string.IsNullOrWhiteSpace(s.Item2)), labelWidth: 96);
        if (ActaSpecs.Children.Count > 0 && ActaSpecs.Children[^1] is FrameworkElement code)
            code.ToolTip = "Huella SHA-256 completa:\n" + EvidenceService.FormatFingerprint(doc.Fingerprint);

        CompareButton.Content = _comparison is null ? "Comprobar ahora" : "Comprobar otra vez";
        RenderVerdict();
        RenderParts(doc);
    }

    private void RenderVerdict()
    {
        if (_comparison is not { } c)
        {
            VerdictBanner.Visibility = Visibility.Collapsed;
            return;
        }

        VerdictBanner.Visibility = Visibility.Visible;
        var missing = c.Alerts.Count(d => d.Status == PartStatus.Missing);
        var changed = c.Alerts.Count(d => d.Status == PartStatus.Changed);
        var added = c.Alerts.Count(d => d.Status == PartStatus.Added);

        VerdictBanner.Background = UiKit.Brush(c.Matches ? "Br.PositiveTint" : "Br.CriticalTint");
        VerdictIcon.Data = (Geometry)FindResource(c.Matches ? "Glyph.Check" : "Glyph.Info");
        if (c.Matches)
        {
            VerdictIcon.Fill = null;
            VerdictIcon.Stroke = UiKit.Brush("Br.Positive");
            VerdictIcon.StrokeThickness = 2.6;
        }
        else
        {
            VerdictIcon.Stroke = null;
            VerdictIcon.Fill = UiKit.Brush("Br.Critical");
        }

        VerdictTitle.Text = c.Matches
            ? "Todo coincide"
            : UiKit.Plural(c.Alerts.Count, "pieza no coincide", "piezas no coinciden");
        VerdictTitle.Foreground = UiKit.Brush(c.Matches ? "Br.Positive" : "Br.Critical");

        var parts = new List<string>();
        if (missing > 0) parts.Add(UiKit.Plural(missing, "falta", "faltan"));
        if (changed > 0) parts.Add(UiKit.Plural(changed, "cambió", "cambiaron"));
        if (added > 0) parts.Add(UiKit.Plural(added, "es nueva", "son nuevas"));
        VerdictDetail.Text = c.Matches
            ? $"Las {c.IntactCount} piezas son las mismas que el {c.Saved.CreatedAt:d 'de' MMMM}. Comprobado hoy a las {DateTime.Now:HH:mm}."
            : string.Join(" · ", parts) + $". {UiKit.Plural(c.IntactCount, "pieza sigue igual", "piezas siguen iguales")}." +
              (c.SavedFileIsAuthentic ? "" : " Ojo: el archivo del acta fue editado, así que tómalo como orientativo.");
    }

    /// <summary>Las piezas del acta por categorías; tras comprobar, cada una con su estado.</summary>
    private void RenderParts(EvidenceDocument doc)
    {
        PartsPanel.Children.Clear();

        if (_comparison is { } c)
        {
            foreach (var group in c.Differences.GroupBy(d => d.Category))
            {
                PartsPanel.Children.Add(CategoryHeader(group.Key));
                foreach (var diff in group)
                    PartsPanel.Children.Add(PartRow(diff.Name, diff.Location, diff.Detail, diff.Status));
            }
            return;
        }

        foreach (var group in doc.Parts.GroupBy(p => p.Category))
        {
            PartsPanel.Children.Add(CategoryHeader(group.Key));
            foreach (var part in group)
            {
                var serial = part.HasIdentity ? $"Serie {part.Identity}" : "Sin número de serie";
                PartsPanel.Children.Add(PartRow(part.Name, part.Location, serial, status: null));
            }
        }
    }

    private static TextBlock CategoryHeader(string category) =>
        UiKit.Text(category.ToUpper(Spanish), "T.Label", UiKit.Brush("Br.AccentBright"),
            new Thickness(0, 10, 0, 8));

    private static FrameworkElement PartRow(string name, string location, string detail, PartStatus? status)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var title = UiKit.Text(name, "T.Value");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.ToolTip = name;
        text.Children.Add(title);

        var line = string.IsNullOrWhiteSpace(location) ? detail : $"{location} · {detail}";
        var caption = UiKit.Text(line, status is PartStatus.Intact or null ? "T.Mono" : "T.Caption",
            margin: new Thickness(0, 3, 0, 0));
        caption.TextWrapping = TextWrapping.Wrap;
        if (status is PartStatus.Missing or PartStatus.Changed) caption.Foreground = UiKit.Brush("Br.TextPrimary");
        text.Children.Add(caption);
        grid.Children.Add(text);

        if (status is { } s)
        {
            var (label, fg, bg) = s switch
            {
                PartStatus.Missing => ("Falta", "Br.Critical", "Br.CriticalTint"),
                PartStatus.Changed => ("Cambió", "Br.Warn", "Br.WarnTint"),
                PartStatus.Added => ("Nueva", "Br.AccentBright", "Br.AccentTint"),
                _ => ("Igual", "Br.Positive", "Br.PositiveTint")
            };
            var chip = UiKit.Chip(label, UiKit.Brush(fg), UiKit.Brush(bg));
            chip.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
        }

        var wrapper = new StackPanel();
        wrapper.Children.Add(grid);
        return wrapper;
    }

    // ────────────────────────── Acciones del acta ──────────────────────────

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } acta) return;

        var dialog = new SaveFileDialog
        {
            Title = "Guardar el acta para imprimir",
            FileName = Path.GetFileName(acta.Path).Replace(EvidenceService.FileExtension, ".txt"),
            Filter = "Documento de texto (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var text = new StringBuilder(AppServices.Evidence.BuildReport(acta.Document));
            if (_comparison is not null)
            {
                text.AppendLine();
                text.Append(AppServices.Evidence.BuildComparisonReport(_comparison));
            }
            File.WriteAllText(dialog.FileName, text.ToString(), Encoding.UTF8);
            Status($"Guardado en {dialog.FileName}. Imprímelo y que lo firme quien recibe el equipo.", ok: true);
        }
        catch (Exception ex)
        {
            Status($"No se pudo guardar: {ex.Message}", ok: false);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } acta) AppManager.Reveal(acta.Path);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } acta) return;
        var answer = MessageBox.Show(
            $"Se moverá a la papelera el acta del {acta.Document.CreatedAt:d 'de' MMMM, HH:mm}.\n\n" +
            "Si todavía no has recogido el equipo, la necesitarás para comprobarlo.",
            "Borrar acta", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        var window = Window.GetWindow(this);
        var handle = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        await AppManager.RecycleAsync(new[] { acta.Path }, handle);

        _current = null;
        _comparison = null;
        await RefreshListAsync(selectPath: null);
        ShowActa(null);
        Status("Acta movida a la papelera.", ok: true);
    }

    // ────────────────────────── Ficha del equipo ──────────────────────────

    private async void Sheet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Guardar la ficha del equipo",
            FileName = $"ficha-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Documento de texto (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() != true) return;

        SheetButton.IsEnabled = false;
        try
        {
            var path = dialog.FileName;
            await Task.Run(() =>
            {
                var statics = AppServices.Hardware.GetStatic();
                var realtime = AppServices.Monitor.Latest ?? AppServices.Hardware.GetRealtime();
                File.WriteAllText(path, SystemReport.Build(statics, realtime), Encoding.UTF8);
            });
            Status($"Ficha guardada en {path}", ok: true);
        }
        catch (Exception ex)
        {
            Status($"No se pudo guardar la ficha: {ex.Message}", ok: false);
        }
        finally { SheetButton.IsEnabled = true; }
    }

    private void Status(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = UiKit.Brush(ok ? "Br.Positive" : "Br.Critical");
    }

    // ────────────────────────── Fila de la lista ──────────────────────────

    public sealed class ActaRow
    {
        public ActaRow(EvidenceService.SavedActa acta) => Acta = acta;

        public EvidenceService.SavedActa Acta { get; }

        public string Title => Acta.Document.CreatedAt.ToString("d 'de' MMMM yyyy, HH:mm", Spanish);

        public string Subtitle
        {
            get
            {
                var doc = Acta.Document;
                var who = string.IsNullOrWhiteSpace(doc.Note) ? doc.MachineName : doc.Note;
                return $"{who} · {UiText.Plural(doc.Parts.Count, "pieza", "piezas")}";
            }
        }
    }
}
