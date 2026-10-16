using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Barep.Controls;
using Barep.Services;

namespace Barep.Views;

/// <summary>
/// Optimizar: una columna de ajustes de Windows, cada uno con su interruptor,
/// qué es en palabras llanas y qué conviene saber antes de cambiarlo. Si
/// Windows no deja cambiar algo desde aquí, se abre su página de Configuración.
/// </summary>
public partial class OptimizeView : UserControl
{
    private const string All = "Todos";
    private const string Pending = "Pendientes";

    private readonly List<TweakRow> _rows = new();
    private readonly Dictionary<string, (Border Header, TextBlock Count, StackPanel Items, Border Group)> _sections = new();
    private readonly HashSet<TweakRestart> _pendingRestarts = new();
    private string _filter = All;
    private bool _loaded;
    private List<(CheckBox Box, Tweak Tweak)> _dialogItems = new();

    public OptimizeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            // Al volver a la sección se relee el estado: pudo cambiarse desde Configuración.
            await RefreshAllAsync();
            return;
        }
        _loaded = true;

        var isAdmin = TweakCatalog.IsAdmin;
        var adminCount = TweakCatalog.All.Count(t => t.NeedsAdmin && t.Applies());
        if (!isAdmin && adminCount > 0)
        {
            AdminButton.Visibility = Visibility.Visible;
            AdminNote.Text = $"{UiKit.Plural(adminCount, "ajuste necesita", "ajustes necesitan")} permisos de administrador. " +
                             "Pulsa «Abrir como administrador» para poder cambiarlos.";
            AdminNote.Visibility = Visibility.Visible;
        }

        BuildFilters();
        BuildRows();
        await RefreshAllAsync();
    }

    // ────────────────────────── Construcción ──────────────────────────

    private void BuildFilters()
    {
        FilterPanel.Children.Clear();
        foreach (var name in new[] { All, Pending }.Concat(TweakCatalog.Categories))
        {
            var chip = new RadioButton
            {
                Style = UiKit.Style("FilterChip"),
                Content = name,
                IsChecked = name == All
            };
            chip.Checked += (_, _) =>
            {
                _filter = name;
                ApplyFilter();
            };
            FilterPanel.Children.Add(chip);
        }
    }

    /// <summary>
    /// Cada categoría es un grupo al estilo de Configuración de macOS/iOS: un
    /// bloque redondeado con las filas separadas por líneas finas, sin tarjetas
    /// sueltas ni óvalos.
    /// </summary>
    private void BuildRows()
    {
        TweaksPanel.Children.Clear();
        _rows.Clear();
        _sections.Clear();

        foreach (var category in TweakCatalog.Categories)
        {
            var tweaks = TweakCatalog.All.Where(t => t.Category == category && t.Applies()).ToList();
            if (tweaks.Count == 0) continue;

            var headerGrid = new Grid { Margin = new Thickness(4, 22, 4, 8) };
            var title = UiKit.Text(category, "T.Headline");
            title.FontSize = 13;
            title.Foreground = UiKit.Brush("Br.TextSecondary");
            var count = UiKit.Text("", "T.Caption");
            count.HorizontalAlignment = HorizontalAlignment.Right;
            count.VerticalAlignment = VerticalAlignment.Center;
            headerGrid.Children.Add(title);
            headerGrid.Children.Add(count);
            var header = new Border { Child = headerGrid };

            var items = new StackPanel();
            var group = new Border
            {
                Background = UiKit.Brush("Br.Surface"),
                BorderBrush = UiKit.Brush("Br.Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = items,
                SnapsToDevicePixels = true
            };

            foreach (var tweak in tweaks)
            {
                var row = new TweakRow(tweak, this);
                _rows.Add(row);
                items.Children.Add(row.Root);
            }

            TweaksPanel.Children.Add(header);
            TweaksPanel.Children.Add(group);
            _sections[category] = (header, count, items, group);
        }
    }

    // ────────────────────────── Estado ──────────────────────────

    private async Task RefreshAllAsync()
    {
        var states = await Task.Run(() => _rows.Select(r => r.Tweak.Read?.Invoke()).ToList());
        for (int i = 0; i < _rows.Count; i++) _rows[i].SetState(states[i]);
        UpdateSummary();
    }

    internal void UpdateSummary()
    {
        var recommended = _rows.Where(r => r.Tweak.Recommended is not null && !r.Tweak.IsLinkOnly).ToList();
        var done = recommended.Count(r => r.State == r.Tweak.Recommended);

        SummaryCount.Text = done.ToString();
        SummaryTotal.Text = $"de {recommended.Count}";
        var fraction = recommended.Count == 0 ? 1 : done / (double)recommended.Count;
        SummaryMeter.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(fraction, new Duration(TimeSpan.FromMilliseconds(400))) { EasingFunction = new CubicEase() });
        SummaryMeter.Foreground = UiKit.Brush(fraction >= 1 ? "Br.Positive" : "Br.Accent");
        ApplyAllButton.IsEnabled = done < recommended.Count;

        foreach (var (category, section) in _sections)
        {
            var inCategory = recommended.Where(r => r.Tweak.Category == category).ToList();
            section.Count.Text = inCategory.Count == 0
                ? ""
                : $"{inCategory.Count(r => r.State == r.Tweak.Recommended)} de {inCategory.Count} optimizados";
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var row in _rows)
        {
            var visible = _filter switch
            {
                All => true,
                Pending => row.Tweak.Recommended is not null && !row.Tweak.IsLinkOnly && row.State != row.Tweak.Recommended,
                _ => row.Tweak.Category == _filter
            };
            row.Root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var (_, section) in _sections)
        {
            var visible = _rows.Where(r => section.Items.Children.Contains(r.Root) && r.Root.Visibility == Visibility.Visible)
                .ToList();
            section.Header.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            section.Group.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // La línea separadora sólo entre filas visibles, nunca debajo de la última.
            for (int i = 0; i < visible.Count; i++) visible[i].SetDivider(i < visible.Count - 1);
        }
    }

    internal void NoteRestart(TweakRestart restart)
    {
        if (restart == TweakRestart.None) return;
        _pendingRestarts.Add(restart);

        RestartExplorerButton.Visibility = _pendingRestarts.Contains(TweakRestart.Explorer)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var parts = new List<string>();
        if (_pendingRestarts.Contains(TweakRestart.Explorer))
            parts.Add("Algunos cambios se verán al reiniciar el Explorador (la barra de tareas parpadea un segundo; tus ventanas no se cierran).");
        if (_pendingRestarts.Contains(TweakRestart.SignOut))
            parts.Add("Otros se aplican al cerrar sesión y volver a entrar.");
        if (_pendingRestarts.Contains(TweakRestart.Reboot))
            parts.Add("Y alguno necesita reiniciar el equipo.");
        RestartText.Text = string.Join(" ", parts);

        if (RestartBanner.Visibility != Visibility.Visible)
        {
            RestartBanner.Visibility = Visibility.Visible;
            RestartBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(250))));
        }
    }

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        RestartExplorerButton.IsEnabled = false;
        await TweakService.RestartExplorerAsync();
        RestartExplorerButton.IsEnabled = true;
        _pendingRestarts.Remove(TweakRestart.Explorer);
        if (_pendingRestarts.Count == 0) RestartBanner.Visibility = Visibility.Collapsed;
        else NoteRestart(_pendingRestarts.First());
    }

    internal void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (TweakService.RelaunchAsAdmin()) Application.Current.Shutdown();
    }

    // ────────────────────────── Aplicar lo recomendado ──────────────────────────

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = _rows
            .Where(r => r.Tweak.Recommended is { } rec && !r.Tweak.IsLinkOnly && r.State != rec)
            .Select(r => r.Tweak)
            .ToList();
        if (pending.Count == 0) return;

        var isAdmin = TweakCatalog.IsAdmin;
        DialogTitle.Text = "Aplicar lo recomendado";
        DialogMessage.Text = "Esto es lo que se va a cambiar. Desmarca lo que prefieras dejar como está; " +
                             "cualquier cambio se puede deshacer luego con su interruptor.";
        DialogList.Children.Clear();
        _dialogItems = new();

        foreach (var tweak in pending)
        {
            var blockedByAdmin = tweak.NeedsAdmin && !isAdmin;
            var box = new CheckBox
            {
                Style = UiKit.Style("Check"),
                IsChecked = !blockedByAdmin,
                IsEnabled = !blockedByAdmin,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 12, 0)
            };
            box.Checked += (_, _) => UpdateDialogTotal();
            box.Unchecked += (_, _) => UpdateDialogTotal();

            var text = new StackPanel();
            var action = tweak.Recommended == true ? "Activar" : "Desactivar";
            text.Children.Add(UiKit.Text($"{action}: {tweak.Title}", "T.Value"));
            var detail = UiKit.Text(blockedByAdmin ? "Necesita abrir la app como administrador." : tweak.Note, "T.Caption",
                blockedByAdmin ? UiKit.Brush("Br.Warn") : null, new Thickness(0, 3, 0, 0));
            detail.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(detail);

            var grid = new Grid { Margin = new Thickness(8, 8, 8, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(box);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // Clic en cualquier parte de la fila marca o desmarca.
            grid.Background = Brushes.Transparent;
            grid.MouseLeftButtonUp += (_, args) =>
            {
                if (box.IsEnabled && args.OriginalSource is not CheckBox) box.IsChecked = box.IsChecked != true;
            };

            DialogList.Children.Add(grid);
            _dialogItems.Add((box, tweak));
        }

        DialogListHost.Visibility = Visibility.Visible;
        DialogProgressPanel.Visibility = Visibility.Collapsed;
        DialogCancel.IsEnabled = true;
        DialogCancel.Visibility = Visibility.Visible;
        UpdateDialogTotal();

        Overlay.Visibility = Visibility.Visible;
        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));
    }

    private void UpdateDialogTotal()
    {
        var count = _dialogItems.Count(i => i.Box.IsChecked == true);
        DialogTotal.Text = UiKit.Plural(count, "ajuste", "ajustes");
        DialogConfirm.Content = count > 0 ? $"Aplicar ({count})" : "Aplicar";
        DialogConfirm.IsEnabled = count > 0;
    }

    private void DialogCancel_Click(object sender, RoutedEventArgs e) => Overlay.Visibility = Visibility.Collapsed;

    private async void DialogConfirm_Click(object sender, RoutedEventArgs e)
    {
        // Segundo uso del botón: cerrar el resumen final.
        if (DialogListHost.Visibility != Visibility.Visible && DialogProgressPanel.Visibility != Visibility.Visible)
        {
            Overlay.Visibility = Visibility.Collapsed;
            return;
        }

        var chosen = _dialogItems.Where(i => i.Box.IsChecked == true).Select(i => i.Tweak).ToList();
        if (chosen.Count == 0) return;

        DialogListHost.Visibility = Visibility.Collapsed;
        DialogProgressPanel.Visibility = Visibility.Visible;
        DialogConfirm.IsEnabled = false;
        DialogCancel.IsEnabled = false;
        DialogTotal.Text = "";

        var blocked = new List<Tweak>();
        for (int i = 0; i < chosen.Count; i++)
        {
            var tweak = chosen[i];
            DialogProgressText.Text = $"{tweak.Title}…";
            DialogProgressBar.BeginAnimation(RangeBase.ValueProperty,
                new DoubleAnimation(i / (double)chosen.Count, new Duration(TimeSpan.FromMilliseconds(150))));

            var outcome = await Task.Run(() => TweakService.Apply(tweak, tweak.Recommended == true));
            if (outcome == TweakOutcome.Done) NoteRestart(tweak.Restart);
            else blocked.Add(tweak);
        }
        DialogProgressBar.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(150))));
        await Task.Delay(300);
        await RefreshAllAsync();

        DialogProgressPanel.Visibility = Visibility.Collapsed;
        DialogTitle.Text = "Listo";
        DialogMessage.Text = blocked.Count == 0
            ? $"Se aplicaron {UiKit.Plural(chosen.Count, "ajuste", "ajustes")}."
            : $"Se aplicaron {chosen.Count - blocked.Count} de {chosen.Count}. Windows no dejó cambiar desde aquí: " +
              string.Join(", ", blocked.Select(b => $"«{b.Title}»")) +
              ". Búscalos en la lista y pulsa «Abrir en Configuración» para hacerlo con un clic.";
        DialogCancel.Visibility = Visibility.Collapsed;
        DialogConfirm.Content = "Cerrar";
        DialogConfirm.IsEnabled = true;
    }

    // ────────────────────────── Fila ──────────────────────────

    /// <summary>
    /// Una fila del grupo: título con su estado en texto (sin óvalos), qué es,
    /// qué conviene saber y, a la derecha, el interruptor.
    /// </summary>
    private sealed class TweakRow
    {
        private readonly OptimizeView _owner;
        private readonly ToggleButton? _switch;
        private readonly TextBlock? _stateText;
        private readonly Ellipse _statusDot;
        private readonly TextBlock _statusText;
        private readonly TextBlock _message;
        private readonly StackPanel _messageActions;
        private readonly Border _divider;
        private bool _updating;

        public Tweak Tweak { get; }
        public Grid Root { get; }
        public bool? State { get; private set; }

        public TweakRow(Tweak tweak, OptimizeView owner)
        {
            Tweak = tweak;
            _owner = owner;

            var grid = new Grid { Margin = new Thickness(20, 16, 18, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 28, 0) };

            // Título + estado en texto con un punto de color.
            var titleRow = new WrapPanel();
            var title = UiKit.Text(tweak.Title, "T.Headline");
            title.FontSize = 14;
            title.Margin = new Thickness(0, 0, 12, 2);
            title.TextWrapping = TextWrapping.Wrap;
            titleRow.Children.Add(title);

            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 2) };
            _statusDot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _statusText = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(_statusDot);
            status.Children.Add(_statusText);
            titleRow.Children.Add(status);
            left.Children.Add(titleRow);

            left.Children.Add(UiKit.Text(tweak.What, "T.Secondary", margin: new Thickness(0, 3, 0, 0)));

            // «Ten en cuenta» como texto normal, sin caja.
            var noteText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), LineHeight = 18 };
            noteText.Inlines.Add(new System.Windows.Documents.Run("Ten en cuenta: ")
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = UiKit.Brush("Br.TextPrimary")
            });
            noteText.Inlines.Add(new System.Windows.Documents.Run(tweak.Note) { Foreground = UiKit.Brush("Br.TextSecondary") });
            left.Children.Add(noteText);

            // Pie discreto: «Necesita administrador · Se aplica al reiniciar · Abrir en Configuración ›»
            var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var tags = new List<(string Text, string Brush)>();
            if (tweak.NeedsAdmin) tags.Add(("Necesita administrador", "Br.Warn"));
            var restartTag = tweak.Restart switch
            {
                TweakRestart.Explorer => "Se ve al reiniciar el Explorador",
                TweakRestart.SignOut => "Se aplica al cerrar sesión",
                TweakRestart.Reboot => "Se aplica al reiniciar el equipo",
                _ => null
            };
            if (restartTag is not null) tags.Add((restartTag, "Br.TextTertiary"));
            for (int i = 0; i < tags.Count; i++)
            {
                if (i > 0) footer.Children.Add(Dot());
                footer.Children.Add(new TextBlock
                {
                    Text = tags[i].Text, FontSize = 11.5, Foreground = UiKit.Brush(tags[i].Brush),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            if (!string.IsNullOrEmpty(tweak.SettingsUri) && !tweak.IsLinkOnly)
            {
                if (tags.Count > 0) footer.Children.Add(Dot());
                footer.Children.Add(LinkText("Abrir en Configuración ›", () => TweakService.OpenSettings(tweak.SettingsUri)));
            }
            if (footer.Children.Count > 0) left.Children.Add(footer);

            _message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            left.Children.Add(_message);
            _messageActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            left.Children.Add(_messageActions);

            grid.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 86 };
            if (tweak.IsLinkOnly)
            {
                var open = new Button { Style = UiKit.Style("Btn"), Content = "Abrir…", Padding = new Thickness(16, 7, 16, 7) };
                open.Click += (_, _) => TweakService.OpenSettings(tweak.SettingsUri);
                right.Children.Add(open);
                _statusText.Text = "Se revisa en Configuración";
                _statusText.Foreground = UiKit.Brush("Br.TextTertiary");
                _statusDot.Fill = UiKit.Brush("Br.TextTertiary");
            }
            else
            {
                _switch = new ToggleButton { Style = UiKit.Style("Switch"), HorizontalAlignment = HorizontalAlignment.Center };
                _switch.Checked += (_, _) => OnToggled(true);
                _switch.Unchecked += (_, _) => OnToggled(false);
                right.Children.Add(_switch);
                _stateText = UiKit.Text("…", "T.Caption", margin: new Thickness(0, 7, 0, 0));
                _stateText.HorizontalAlignment = HorizontalAlignment.Center;
                right.Children.Add(_stateText);
            }
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            _divider = new Border
            {
                Height = 1,
                Background = UiKit.Brush("Br.StrokeSoft"),
                Margin = new Thickness(20, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            Root = new Grid();
            Root.Children.Add(grid);
            Root.Children.Add(_divider);
        }

        public void SetDivider(bool visible) => _divider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        private static TextBlock Dot() => new()
        {
            Text = "·", FontSize = 12, Margin = new Thickness(7, 0, 7, 0),
            Foreground = UiKit.Brush("Br.TextTertiary"), VerticalAlignment = VerticalAlignment.Center
        };

        private static TextBlock LinkText(string text, Action onClick)
        {
            var link = new TextBlock
            {
                Text = text, FontSize = 11.5, FontWeight = FontWeights.Medium, Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = UiKit.Brush("Br.AccentBright"), VerticalAlignment = VerticalAlignment.Center
            };
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            link.MouseLeftButtonUp += (_, _) => onClick();
            return link;
        }

        public void SetState(bool? state)
        {
            State = state;
            if (_switch is null || _stateText is null) return;

            _updating = true;
            _switch.IsChecked = state == true;
            _switch.IsEnabled = state is not null;
            _updating = false;

            // Bajo el interruptor: cómo está la función y, si ya es lo recomendado, un ✓ en verde.
            var optimal = state is not null && Tweak.Recommended is not null && state == Tweak.Recommended;
            _stateText.Text = state switch { true => "Activado", false => "Desactivado", _ => "Desconocido" } + (optimal ? " ✓" : "");
            _stateText.Foreground = UiKit.Brush(optimal ? "Br.Positive" : state == true ? "Br.TextPrimary" : "Br.TextTertiary");
            _switch.ToolTip = Tweak.Recommended is null
                ? "Déjalo como prefieras"
                : optimal
                    ? $"Está {(state == true ? "activado" : "desactivado")}, que es lo recomendado"
                    : $"Recomendado: {(Tweak.Recommended == true ? "activarlo" : "desactivarlo")}";

            if (Tweak.Recommended is null)
                SetStatus("A tu gusto", "Br.TextTertiary");
            else if (state == Tweak.Recommended)
                SetStatus("Optimizado", "Br.Positive");
            else
                SetStatus(Tweak.Recommended == true ? "Recomendado activar" : "Recomendado desactivar", "Br.AccentBright");
        }

        private void SetStatus(string text, string brush)
        {
            _statusText.Text = text;
            _statusText.Foreground = UiKit.Brush(brush);
            _statusDot.Fill = UiKit.Brush(brush);
        }

        private async void OnToggled(bool on)
        {
            if (_updating || _switch is null) return;
            _switch.IsEnabled = false;
            HideMessage();

            var outcome = await Task.Run(() => TweakService.Apply(Tweak, on));
            var now = await Task.Run(() => Tweak.Read?.Invoke());
            SetState(now);

            switch (outcome)
            {
                case TweakOutcome.Done:
                    _owner.NoteRestart(Tweak.Restart);
                    break;
                case TweakOutcome.NeedsAdmin:
                    ShowMessage("Este ajuste necesita permisos de administrador. Abre la app como administrador o cámbialo en Configuración.",
                        "Br.Warn", withAdmin: true);
                    break;
                case TweakOutcome.Blocked:
                    ShowMessage("Windows no deja cambiar esto desde otra aplicación. Te abrimos la página de Configuración para que lo hagas con un clic.",
                        "Br.Warn", withAdmin: false);
                    TweakService.OpenSettings(Tweak.SettingsUri);
                    break;
                default:
                    ShowMessage("No se pudo cambiar este ajuste.", "Br.Critical", withAdmin: false);
                    break;
            }
            _owner.UpdateSummary();
        }

        private void ShowMessage(string text, string brush, bool withAdmin)
        {
            _message.Text = text;
            _message.Foreground = UiKit.Brush(brush);
            _message.Visibility = Visibility.Visible;

            _messageActions.Children.Clear();
            if (withAdmin)
            {
                var admin = new Button { Style = UiKit.Style("Btn"), Content = "Abrir como administrador", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
                admin.Click += _owner.Admin_Click;
                _messageActions.Children.Add(admin);
            }
            if (!string.IsNullOrEmpty(Tweak.SettingsUri))
            {
                var settings = new Button { Style = UiKit.Style("BtnGhost"), Content = "Abrir en Configuración", Padding = new Thickness(12, 6, 12, 6) };
                settings.Click += (_, _) => TweakService.OpenSettings(Tweak.SettingsUri);
                _messageActions.Children.Add(settings);
            }
            _messageActions.Visibility = _messageActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideMessage()
        {
            _message.Visibility = Visibility.Collapsed;
            _messageActions.Visibility = Visibility.Collapsed;
        }
    }
}
