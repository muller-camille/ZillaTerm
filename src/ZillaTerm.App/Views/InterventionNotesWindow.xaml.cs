using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.App.Views;

/// <summary>
/// Fenêtre flottante de l'intervention en cours, ouverte au début de l'enregistrement et au premier plan (case pour
/// l'enlever) : le titre de l'intervention, une note, la capture d'un Bureau à distance, et l'historique de ce qui s'est
/// passé (connexions, actions, transferts, notes, captures, texte des terminaux). Une commande d'un terminal et son retour
/// forment un bloc ; un long bloc est réduit à ses premières lignes et se déplie en entier.
/// </summary>
/// <remarks>Une seule pour l'application (<see cref="Open"/>) : elle suit l'enregistrement, d'une fenêtre principale à l'autre.</remarks>
public partial class InterventionNotesWindow : Window
{
    private static InterventionNotesWindow? _current;

    private readonly InterventionRecorder _recorder;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<InterventionHistoryItem> _items = [];
    private InterventionBlockBuilder _blocks = new();
    private InterventionHistory? _history;
    private int _read;

    // État d'enregistrement déjà dit par la ligne d'état (annoncée seulement quand il change).
    private bool _shownRecording;
    private bool _shownInterrupted;

    internal InterventionNotesWindow(InterventionRecorder recorder)
    {
        InitializeComponent();
        _recorder = recorder;
        HistoryList.ItemsSource = _items;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            ReadHistory();
            UpdateState();
        };
        recorder.Changed += OnRecorderChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            recorder.Changed -= OnRecorderChanged;
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        };
        Follow(recorder.History);
        TitleBox.Text = recorder.Title;
        UpdateState();
        _timer.Start();
        // Titre à donner d'abord ; une fois donné, la note.
        Loaded += (_, _) => (TitleBox.Text.Length == 0 && TitleBox.IsEnabled ? TitleBox : NoteBox.IsEnabled ? NoteBox : (Control)HistoryList).Focus();
    }

    /// <summary>Fenêtre ouverte, s'il y en a une.</summary>
    internal static InterventionNotesWindow? Current => _current;

    /// <summary>Ouvre la fenêtre (une seule pour l'application), près de <paramref name="near"/>, ou la ramène devant.</summary>
    public static InterventionNotesWindow Open(InterventionRecorder recorder, Window? near)
    {
        if (_current is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return open;
        }

        var window = new InterventionNotesWindow(recorder);
        window.PlaceNear(near);
        _current = window;
        window.Show();
        return window;
    }

    /// <summary>
    /// En haut à droite de la fenêtre principale, dans l'écran : elle ne cache ni les onglets ni la liste des comptes, et
    /// reste visible quand la fenêtre principale est réduite (elle ne lui appartient pas).
    /// </summary>
    private void PlaceNear(Window? near)
    {
        if (near is not null && PresentationSource.FromVisual(near)?.CompositionTarget is { } target)
        {
            var corner = target.TransformFromDevice.Transform(near.PointToScreen(new Point(near.ActualWidth, 0)));
            Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(corner.X - Width - 24,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(corner.Y + 80,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
            return;
        }

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnRecorderChanged()
    {
        if (!ReferenceEquals(_recorder.History, _history))
        {
            // Fin de l'enregistrement suivi : ses derniers événements, puis l'historique du nouveau s'il y en a un.
            ReadHistory();
            if (_recorder.History is { } next)
            {
                Follow(next);
                TitleBox.Text = "";
                ShowMessage("");
            }
            else
            {
                ShowMessage(Strings.InterventionNotesStopped);
            }
        }

        if (_recorder.IsRecording && !TitleBox.IsKeyboardFocusWithin)
        {
            TitleBox.Text = _recorder.Title;
        }

        UpdateState();
    }

    /// <summary>Suit l'historique d'un enregistrement (depuis son début).</summary>
    private void Follow(InterventionHistory? history)
    {
        _history = history;
        _read = 0;
        _blocks = new InterventionBlockBuilder();
        _items.Clear();
        ReadHistory();
    }

    /// <summary>Ajoute à la liste les événements arrivés depuis la dernière lecture ; la liste suit la fin si elle y était.</summary>
    internal void ReadHistory()
    {
        if (_history is not { } history)
        {
            return;
        }

        var entries = history.Since(_read);
        CappedText.Visibility = history.TerminalCapped ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0)
        {
            return;
        }

        bool atEnd = IsAtEnd();
        _read += entries.Count;
        var grown = new HashSet<InterventionHistoryItem>();
        foreach (var entry in entries)
        {
            if (_blocks.Add(entry))
            {
                grown.Add(_items[^1]);
            }
            else
            {
                _items.Add(new InterventionHistoryItem(_blocks.Blocks[^1]));
            }
        }

        // Un bloc qui grandit (retour d'une commande) : son aperçu et son nombre de lignes changent.
        foreach (var item in grown)
        {
            item.Refresh();
        }

        if (atEnd)
        {
            HistoryList.ScrollIntoView(_items[^1]);
            ScrollViewerOf(HistoryList)?.ScrollToEnd();
        }
    }

    private bool IsAtEnd() => ScrollViewerOf(HistoryList) is not { } viewer || viewer.VerticalOffset >= viewer.ScrollableHeight - 2;

    private static ScrollViewer? ScrollViewerOf(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
        {
            return viewer;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (ScrollViewerOf(VisualTreeHelper.GetChild(element, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Enregistrement suivi en cours (pas arrêté, pas remplacé par un autre).</summary>
    private bool Recording => _history is not null && ReferenceEquals(_recorder.History, _history);

    private void UpdateState()
    {
        bool recording = Recording;
        // Journal refusé par le disque : plus rien n'est gardé, ni titre, ni note, ni capture.
        bool keeping = recording && _recorder.Failure is null;
        TitleBox.IsEnabled = keeping;
        NoteBox.IsEnabled = keeping;
        NoteButton.IsEnabled = keeping && NoteBox.Text.Trim().Length > 0;
        CaptureButton.IsEnabled = keeping;
        RecordingDot.Visibility = keeping ? Visibility.Visible : Visibility.Collapsed;
        var status = recording ? InterventionWindow.RecordingStatus(_recorder) : Strings.InterventionNotRecording;
        bool interrupted = recording && _recorder.FailedAt is not null;
        if (recording == _shownRecording && interrupted == _shownInterrupted)
        {
            StatusText.SetQuietly(status);
        }
        else
        {
            StatusText.Text = status;
        }

        _shownRecording = recording;
        _shownInterrupted = interrupted;
        if (recording && _recorder.FailureMessage is { } failure)
        {
            ShowMessage(failure, error: true);
        }
    }

    private void ShowMessage(string text, bool error = false)
    {
        MessageText.Text = text;
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTitle();
        }
    }

    private void OnTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTitle();

    /// <summary>Titre gardé dans le journal s'il a changé (Entrée, ou en quittant le champ).</summary>
    private void CommitTitle()
    {
        if (!Recording)
        {
            return;
        }

        if (_recorder.SetTitle(TitleBox.Text))
        {
            ShowMessage(Strings.InterventionTitleSaved);
            ReadHistory();
        }

        if (TitleBox.Text != _recorder.Title)
        {
            TitleBox.Text = _recorder.Title;
        }
    }

    private void OnNoteChanged(object sender, TextChangedEventArgs e) =>
        NoteButton.IsEnabled = NoteBox.IsEnabled && NoteBox.Text.Trim().Length > 0;

    /// <summary>Entrée ajoute la note ; Maj+Entrée passe à la ligne.</summary>
    private void OnNotePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            AddNote();
        }
    }

    private void OnAddNote(object sender, RoutedEventArgs e) => AddNote();

    private void AddNote()
    {
        var note = NoteBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (note.Length == 0 || !Recording)
        {
            return;
        }

        _recorder.Record(InterventionKind.Note, "", note);
        NoteBox.Clear();
        ShowMessage(Strings.InterventionNoteAdded);
        ReadHistory();
    }

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var windows = WindowCapture.RemoteDesktopWindows();
        switch (windows.Count)
        {
            case 0:
                ShowMessage(Strings.InterventionNoRdpWindow, error: true);
                break;
            case 1:
                Capture(windows[0]);
                break;
            default:
                var menu = new ContextMenu { PlacementTarget = CaptureButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var window in windows)
                {
                    var item = new MenuItem { Header = window.Title.Length > 0 ? window.Title : window.Handle.ToString(CultureInfo.InvariantCulture) };
                    item.Click += (_, _) => Capture(window);
                    menu.Items.Add(item);
                }

                menu.IsOpen = true;
                break;
        }
    }

    private void Capture(RemoteDesktopWindow window)
    {
        var png = WindowCapture.CapturePng(window.Handle);
        if (png is null)
        {
            ShowMessage(Strings.InterventionCaptureFailed, error: true);
            return;
        }

        _recorder.Record(InterventionKind.Screenshot, window.Title, Text.Format(Strings.InterventionCaptureOf, window.Title), png);
        ShowMessage(Text.Format(Strings.InterventionCaptured, window.Title));
        ReadHistory();
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InterventionHistoryItem item })
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = HistoryList.SelectedItem is InterventionHistoryItem;

    /// <summary>Ctrl+C : l'événement choisi en entier (heure, nature, session, texte).</summary>
    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is InterventionHistoryItem item)
        {
            try
            {
                Clipboard.SetText(item.CopyText);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Presse-papiers pris par une autre application : rien n'est copié.
            }
        }
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e) => Topmost = TopmostBox.IsChecked == true;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Bloc de l'historique de l'intervention affiché dans la fenêtre flottante.</summary>
public sealed class InterventionHistoryItem : INotifyPropertyChanged
{
    /// <summary>Lignes montrées d'un bloc réduit.</summary>
    internal const int PreviewLines = 6;

    /// <summary>Au-delà de ce nombre de lignes, un bloc est réduit (il se déplie en entier).</summary>
    internal const int CollapseAbove = 8;

    private readonly InterventionBlock _block;
    private bool _expanded;

    public InterventionHistoryItem(InterventionBlock block) => _block = block;

    public event PropertyChangedEventHandler? PropertyChanged;

    public InterventionBlock Block => _block;

    public string Time => _block.Start.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    public string Kind => InterventionWindow.KindName(_block.Kind);

    public string Source => _block.Source;

    public bool IsTerminal => _block.Kind == InterventionKind.Terminal;

    public int LineCount => _block.Lines.Count;

    public bool IsLong => LineCount > CollapseAbove;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded != value)
            {
                _expanded = value;
                Refresh();
            }
        }
    }

    /// <summary>Texte montré replié : tout, ou les premières lignes d'un long bloc.</summary>
    public string Preview => IsLong ? string.Join('\n', _block.Lines.Take(PreviewLines)) + "\n…" : AllText;

    /// <summary>Texte du bloc déplié (vide replié : un long retour n'est mis en page qu'à la demande).</summary>
    public string FullText => _expanded ? AllText : "";

    public string AllText => string.Join('\n', _block.Lines);

    public string ToggleText => _expanded ? Strings.InterventionCollapse : Text.Format(Strings.InterventionShowAll, LineCount);

    /// <summary>Nom lu par les lecteurs d'écran : heure, nature, session et première ligne.</summary>
    public string AutomationName
    {
        get
        {
            var first = _block.Lines.Count > 0 ? _block.Lines[0] : "";
            var name = string.Join(", ", new[] { Time, Kind, Source, first }.Where(part => part.Length > 0));
            return IsLong ? $"{name}, {Text.Format(Strings.InterventionShowAll, LineCount)}" : name;
        }
    }

    /// <summary>Texte copié (Ctrl+C) : en-tête puis le bloc entier.</summary>
    public string CopyText => string.Join("  ", new[] { Time, Kind, Source }.Where(part => part.Length > 0)) + Environment.NewLine
        + string.Join(Environment.NewLine, _block.Lines);

    /// <summary>Le bloc a changé (lignes ajoutées) : l'affichage relit tout.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
