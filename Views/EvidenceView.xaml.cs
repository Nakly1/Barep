using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

public partial class EvidenceView : UserControl
{
    private EvidenceComparison? _lastComparison;

    public EvidenceView()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowPartCount();
    }

    /// <summary>
    /// Contar las piezas exige releer todo el SMBIOS, así que se hace fuera del
    /// hilo de interfaz: abrir la pestaña no debe congelar la ventana.
    /// </summary>
    private async void ShowPartCount()
    {
        if (PartCountText.Text.Length > 0) return;
        try
        {
            var doc = await Task.Run(() => AppServices.Evidence.Capture());
            PartCountText.Text = $"{doc.Parts.Count} piezas identificables en este equipo";
        }
        catch { PartCountText.Text = ""; }
    }

    // ────────────────────────── Generar ──────────────────────────

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Guardar acta de hardware",
            FileName = EvidenceService.SuggestedFileName(EvidenceService.FileExtension),
            Filter = "Acta de hardware (*.smev.json)|*.smev.json",
            InitialDirectory = EvidenceService.DefaultFolder,
            AddExtension = false
        };
        if (dialog.ShowDialog() != true) return;

        GenerateButton.IsEnabled = false;
        SetStatus("Leyendo el hardware y generando el acta…", "Br.TextSecondary");

        try
        {
            var note = NoteBox.Text;
            var jsonPath = EnsureExtension(dialog.FileName);
            var reportPath = Path.ChangeExtension(
                jsonPath.Replace(EvidenceService.FileExtension, ""), ".txt");

            var doc = await Task.Run(() =>
            {
                var captured = AppServices.Evidence.Capture(note);
                AppServices.Evidence.Save(captured, jsonPath);
                File.WriteAllText(reportPath, AppServices.Evidence.BuildReport(captured),
                    System.Text.Encoding.UTF8);
                return captured;
            });

            FingerprintText.Text = EvidenceService.FormatFingerprint(doc.Fingerprint);
            FingerprintCard.Visibility = Visibility.Visible;
            PartCountText.Text = $"{doc.Parts.Count} piezas registradas";

            SetStatus($"Acta guardada · {Path.GetFileName(jsonPath)} y {Path.GetFileName(reportPath)} " +
                      $"en {Path.GetDirectoryName(jsonPath)}", "Br.Positive");

            RenderPreview(doc);
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo guardar el acta: {ex.Message}", "Br.Critical");
        }
        finally { GenerateButton.IsEnabled = true; }
    }

    private static string EnsureExtension(string path) =>
        path.EndsWith(EvidenceService.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? path[..^5] + EvidenceService.FileExtension
                : path + EvidenceService.FileExtension;

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewCard.Visibility == Visibility.Visible)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            PreviewButton.Content = "Ver piezas detectadas";
            return;
        }

        try
        {
            RenderPreview(AppServices.Evidence.Capture(NoteBox.Text));
            PreviewButton.Content = "Ocultar piezas detectadas";
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo leer el hardware: {ex.Message}", "Br.Critical");
        }
    }

    private void RenderPreview(EvidenceDocument doc)
    {
        PreviewPanel.Children.Clear();

        foreach (var group in doc.Parts.GroupBy(p => p.Category))
        {
            PreviewPanel.Children.Add(UiKit.Text(group.Key, "T.Label",
                UiKit.Brush("Br.AccentBright"), new Thickness(0, 0, 0, 8)));

            foreach (var part in group)
            {
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                block.Children.Add(new TextBlock
                {
                    Text = part.Name,
                    Style = UiKit.Style("T.Value"),
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                block.Children.Add(new TextBlock
                {
                    Text = part.HasIdentity
                        ? $"Serie {part.Identity}" +
                          (string.IsNullOrWhiteSpace(part.Location) ? "" : $" · {part.Location}")
                        : "Sin número de serie informado por la BIOS" +
                          (string.IsNullOrWhiteSpace(part.Location) ? "" : $" · {part.Location}"),
                    Style = UiKit.Style("T.Mono"),
                    Foreground = part.HasIdentity
                        ? UiKit.Brush("Br.TextSecondary")
                        : UiKit.Brush("Br.TextTertiary"),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                PreviewPanel.Children.Add(block);
            }

            PreviewPanel.Children.Add(new Border
            {
                Style = UiKit.Style("Divider"),
                Margin = new Thickness(0, 4, 0, 16)
            });
        }

        PreviewCard.Visibility = Visibility.Visible;
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(EvidenceService.DefaultFolder)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo abrir la carpeta: {ex.Message}", "Br.Critical");
        }
    }

    // ────────────────────────── Verificar ──────────────────────────

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Elegir el acta a verificar",
            Filter = "Acta de hardware (*.smev.json)|*.smev.json|Todos los archivos|*.*",
            InitialDirectory = EvidenceService.DefaultFolder
        };
        if (dialog.ShowDialog() != true) return;

        VerifyButton.IsEnabled = false;
        SetStatus("Comparando el acta con el hardware actual…", "Br.TextSecondary");

        try
        {
            var path = dialog.FileName;
            var comparison = await Task.Run(() =>
            {
                var saved = AppServices.Evidence.Load(path);
                return AppServices.Evidence.CompareWithCurrent(saved);
            });

            _lastComparison = comparison;
            RenderComparison(comparison);
            ExportComparisonButton.Visibility = Visibility.Visible;
            SetStatus($"Acta del {comparison.Saved.CreatedAt:dd/MM/yyyy HH:mm} · " +
                      $"{comparison.Saved.Parts.Count} piezas registradas", "Br.TextSecondary");
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo leer el acta: {ex.Message}", "Br.Critical");
        }
        finally { VerifyButton.IsEnabled = true; }
    }

    private void RenderComparison(EvidenceComparison c)
    {
        PreviewCard.Visibility = Visibility.Collapsed;
        PreviewButton.Content = "Ver piezas detectadas";

        VerdictCard.Visibility = Visibility.Visible;
        var accent = c.Matches ? UiKit.Brush("Br.Positive") : UiKit.Brush("Br.Critical");

        VerdictTitle.Text = c.Matches
            ? "Todo coincide con el acta"
            : UiKit.Plural(c.Alerts.Count, "discrepancia detectada", "discrepancias detectadas");
        VerdictTitle.Foreground = accent;
        VerdictCard.BorderBrush = accent;

        var missing = c.Alerts.Count(d => d.Status == PartStatus.Missing);
        var changed = c.Alerts.Count(d => d.Status == PartStatus.Changed);
        var added = c.Alerts.Count(d => d.Status == PartStatus.Added);

        VerdictDetail.Text = c.Matches
            ? $"Se verificaron {UiKit.Plural(c.IntactCount, "pieza", "piezas")} contra el acta del " +
              $"{c.Saved.CreatedAt:dd/MM/yyyy 'a las' HH:mm}. Ninguna cambió."
            : $"Faltan {missing} · cambiaron {changed} · aparecieron {added}. " +
              $"{UiKit.Plural(c.IntactCount, "pieza sigue intacta", "piezas siguen intactas")}.";

        if (c.SavedFileIsAuthentic)
        {
            IntegrityWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            IntegrityWarning.Visibility = Visibility.Visible;
            IntegrityText.Text = "La huella SHA-256 guardada en el archivo no coincide con su contenido. " +
                                 "El acta fue editada después de generarse, así que no sirve como prueba. " +
                                 "La comparación se muestra igualmente, pero tómala como orientativa.";
        }

        ResultsCard.Visibility = Visibility.Visible;
        ResultsPanel.Children.Clear();
        foreach (var diff in c.Differences)
            ResultsPanel.Children.Add(BuildDiffRow(diff));
    }

    private static Grid BuildDiffRow(EvidenceDiff diff)
    {
        var (label, brush) = diff.Status switch
        {
            PartStatus.Missing => ("Falta", UiKit.Brush("Br.Critical")),
            PartStatus.Changed => ("Cambió", UiKit.Brush("Br.Warn")),
            PartStatus.Added => ("Nueva", UiKit.Brush("Br.AccentBright")),
            _ => ("Correcta", UiKit.Brush("Br.Positive"))
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pill = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            Background = Tint(brush),
            Padding = new Thickness(9, 3, 9, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 12, 0),
            Child = new TextBlock
            {
                Text = label,
                Style = UiKit.Style("ChipText"),
                Foreground = brush
            }
        };

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = $"{diff.Category} · {diff.Name}",
            Style = UiKit.Style("T.Value"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{diff.Category} · {diff.Name}"
        });

        var detail = string.IsNullOrWhiteSpace(diff.Location)
            ? diff.Detail
            : $"{diff.Location} — {diff.Detail}";
        body.Children.Add(new TextBlock
        {
            Text = detail,
            Style = UiKit.Style("T.Secondary"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0)
        });

        Grid.SetColumn(pill, 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(pill);
        row.Children.Add(body);
        return row;
    }

    /// <summary>Versión translúcida del color de estado, para el fondo de la píldora.</summary>
    private static Brush Tint(Brush source)
    {
        if (source is not SolidColorBrush solid) return Brushes.Transparent;
        var color = solid.Color;
        var tint = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        tint.Freeze();
        return tint;
    }

    private void ExportComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastComparison is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Guardar informe de verificación",
            FileName = EvidenceService.SuggestedFileName("-verificacion.txt"),
            Filter = "Documento de texto (*.txt)|*.txt",
            InitialDirectory = EvidenceService.DefaultFolder
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName,
                AppServices.Evidence.BuildComparisonReport(_lastComparison),
                System.Text.Encoding.UTF8);
            SetStatus($"Informe guardado en {dialog.FileName}", "Br.Positive");
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo guardar el informe: {ex.Message}", "Br.Critical");
        }
    }

    private void SetStatus(string message, string brushKey)
    {
        StatusText.Text = message;
        StatusText.Foreground = UiKit.Brush(brushKey);
    }
}
