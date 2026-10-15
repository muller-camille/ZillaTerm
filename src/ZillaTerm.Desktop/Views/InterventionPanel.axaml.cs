using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.App.Views;

/// <summary>
/// Onglet « Intervention » du panneau de gauche, affiché au début de l'enregistrement : le titre de l'intervention, une
/// note, la capture d'un Bureau à distance, et l'historique de ce qui s'est passé (connexions, actions, transferts, notes,
/// captures, texte des terminaux), à côté de la session. Une commande d'un terminal et son retour forment un bloc ; un
/// long bloc est réduit à ses premières lignes et se déplie en entier. « Terminer l'intervention » arrête
/// l'enregistrement ; à l'arrêt, d'où qu'il vienne, l'onglet se vide et la fenêtre principale le retire.
/// </summary>
public partial class InterventionPanel : UserControl
{
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<InterventionHistoryItem> _items = [];
    private InterventionRecorder? _recorder;
    private InterventionBlockBuilder _blocks = new();
    private InterventionHistory? _history;
    private int _read;

    // État d'enregistrement déjà dit par la ligne d'état (annoncée seulement quand il change).
    private bool _shownRecording;
    private bool _shownInterrupted;

    // Rappels arrivés à leur heure, pas encore vus : le premier est affiché.
    private readonly Queue<InterventionReminder> _reminders = new();

    // Note de l'historique en cours de modification dans le champ de la note ; null pour une nouvelle note. La note en
    // cours de saisie, mise de côté pendant la modification, revient ensuite.
    private InterventionHistoryItem? _editing;
    private string _draft = "";

    /// <summary>Les notes de l'historique se modifient : enregistrement en cours, journal accepté.</summary>
    public static readonly StyledProperty<bool> NotesEditableProperty =
        AvaloniaProperty.Register<InterventionPanel, bool>(nameof(NotesEditable));

    public bool NotesEditable
    {
        get => GetValue(NotesEditableProperty);
        set => SetValue(NotesEditableProperty, value);
    }

    public InterventionPanel()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _items;
        // Entrée, Échap (note) et F2 (historique) avant le champ ou la liste.
        NoteBox.AddHandler(KeyDownEvent, OnNotePreviewKeyDown, RoutingStrategies.Tunnel);
        HistoryList.AddHandler(KeyDownEvent, OnHistoryKeyDown, RoutingStrategies.Tunnel);
        // Ctrl+C après le champ d'un bloc déplié, qui copie lui-même sa sélection.
        HistoryList.KeyDown += OnHistoryCopyKey;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            ReadHistory();
            UpdateState();
        };
        UpdateState();
    }

    /// <summary>« Terminer l'intervention » : la fenêtre principale confirme, puis arrête l'enregistrement.</summary>
    public event EventHandler? EndRequested;

    /// <summary>Rappel vu (« Vu ») : à noter au journal.</summary>
    public event Action<InterventionReminder>? ReminderSeen;

    /// <summary>Rappel arrivé à son heure : affiché en tête de l'onglet jusqu'à « Vu » (les suivants attendent leur tour).</summary>
    public void ShowReminder(InterventionReminder reminder)
    {
        _reminders.Enqueue(reminder);
        if (_reminders.Count == 1)
        {
            ShowNextReminder();
        }
    }

    private void ShowNextReminder()
    {
        if (_reminders.TryPeek(out var reminder))
        {
            ReminderText.Text = Text.Format(Strings.InterventionReminderStatus, reminder.Minutes, reminder.Text);
            ReminderBanner.IsVisible = true;
        }
        else
        {
            ReminderText.SetQuietly("");
            ReminderBanner.IsVisible = false;
        }
    }

    private void OnReminderSeen(object? sender, RoutedEventArgs e)
    {
        if (_reminders.TryDequeue(out var reminder))
        {
            ReminderSeen?.Invoke(reminder);
        }

        ShowNextReminder();
    }

    /// <summary>Note tapée, ou modification d'une note, pas encore au journal.</summary>
    public bool HasPendingNote
    {
        get
        {
            var text = (NoteBox.Text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            bool typed = text.Length > 0 && (_editing is null || text != _editing.AllText);
            return typed || _draft.Trim().Length > 0;
        }
    }

    /// <summary>
    /// Avant l'arrêt de l'enregistrement : la note en cours de saisie (ou la modification d'une note) part au journal,
    /// puis la note mise de côté pendant une modification, pour ne rien perdre en fermant l'onglet.
    /// </summary>
    public void CommitPendingNote()
    {
        AddNote();
        if (_editing is not null)
        {
            // Modification vidée : abandonnée, la note mise de côté revient dans le champ.
            EndEdit();
        }

        if ((NoteBox.Text ?? "").Trim().Length > 0)
        {
            AddNote();
        }
    }

    /// <summary>Suit les enregistrements de <paramref name="recorder"/> (celui en cours depuis son début).</summary>
    public void Attach(InterventionRecorder recorder)
    {
        Detach();
        _recorder = recorder;
        recorder.Changed += OnRecorderChanged;
        Follow(recorder.History);
        TitleBox.Text = recorder.Title;
        ShowMessage("");
        UpdateState();
        _timer.Start();
    }

    /// <summary>Ne suit plus les enregistrements (fenêtre principale fermée).</summary>
    public void Detach()
    {
        _timer.Stop();
        if (_recorder is not null)
        {
            _recorder.Changed -= OnRecorderChanged;
            _recorder = null;
        }
    }

    /// <summary>Clavier dans l'onglet affiché : le titre s'il manque, sinon la note.</summary>
    public void FocusInput() =>
        (string.IsNullOrEmpty(TitleBox.Text) && TitleBox.IsEnabled ? TitleBox : NoteBox.IsEnabled ? NoteBox : (Control)HistoryList).Focus();

    private void OnRecorderChanged()
    {
        if (_recorder is null)
        {
            return;
        }

        if (_recorder.History is { } next && !ReferenceEquals(next, _history))
        {
            // Nouvel enregistrement : la fin du précédent, puis son historique à lui depuis le début.
            ReadHistory();
            Follow(next);
            TitleBox.Text = "";
            ShowMessage("");
        }
        else if (_recorder.History is null && _history is not null)
        {
            // Arrêt : l'onglet n'a plus rien à montrer (la fenêtre principale le retire) ; le rapport s'exporte depuis la
            // fenêtre Intervention.
            Follow(null);
            TitleBox.Clear();
            NoteBox.Clear();
            ShowMessage("");
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
        EndEdit();
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
        CappedText.IsVisible = history.TerminalCapped;
        if (entries.Count == 0)
        {
            return;
        }

        bool atEnd = IsAtEnd();
        _read += entries.Count;
        var grown = new HashSet<InterventionHistoryItem>();
        foreach (var entry in entries)
        {
            // Note modifiée : son bloc prend le nouveau texte, à sa place.
            if (_blocks.Edit(entry) is { } edited)
            {
                if (_items.FirstOrDefault(i => i.Block == edited) is { } item)
                {
                    grown.Add(item);
                }

                continue;
            }

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

        if (atEnd && _items.Count > 0)
        {
            HistoryList.ScrollIntoView(_items.Count - 1);
            // Après la mise en page des blocs ajoutés : tout en bas, même le dernier bloc plus haut que la liste.
            Dispatcher.UIThread.Post(() => HistoryViewer?.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private ScrollViewer? HistoryViewer => HistoryList.FindDescendantOfType<ScrollViewer>();

    private bool IsAtEnd() => HistoryViewer is not { } viewer || viewer.Offset.Y >= viewer.Extent.Height - viewer.Viewport.Height - 2;

    /// <summary>Enregistreur de l'enregistrement suivi s'il est en cours (pas arrêté, pas remplacé par un autre) ; null sinon.</summary>
    private InterventionRecorder? Active =>
        _recorder is { History: { } history } recorder && ReferenceEquals(history, _history) ? recorder : null;

    private void UpdateState()
    {
        var active = Active;
        bool recording = active is not null;
        // Journal refusé par le disque (ou le trousseau) : plus rien n'est gardé, ni titre, ni note, ni capture.
        bool keeping = active is { Failure: null };
        TitleBox.IsEnabled = keeping;
        NoteBox.IsEnabled = keeping;
        NoteButton.IsEnabled = keeping && (NoteBox.Text ?? "").Trim().Length > 0;
        CaptureButton.IsEnabled = keeping;
        NotesEditable = keeping;
        if (!keeping)
        {
            EndEdit();
        }

        if (!recording && _reminders.Count > 0)
        {
            // Enregistrement arrêté : ses rappels n'ont plus lieu d'être.
            _reminders.Clear();
            ShowNextReminder();
        }

        RecordingDot.IsVisible = keeping;
        ScreenThrottledText.IsVisible = active is { ScreenThrottled: true };
        EndButton.IsVisible = recording;
        var status = active is not null ? InterventionWindow.RecordingStatus(active) : Strings.InterventionNotRecording;
        bool interrupted = active is { FailedAt: not null };
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
        if (active?.FailureMessage is { } failure)
        {
            ShowMessage(failure, error: true);
        }
    }

    private void ShowMessage(string text, bool error = false)
    {
        MessageText.Text = text;
        MessageText[!ForegroundProperty] = MessageText.GetResourceObservable(error ? "ErrorBrush" : "MutedBrush").ToBinding();
    }

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTitle();
        }
    }

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e) => CommitTitle();

    /// <summary>Titre gardé dans le journal s'il a changé (Entrée, ou en quittant le champ).</summary>
    private void CommitTitle()
    {
        if (Active is not { } recorder)
        {
            return;
        }

        if (recorder.SetTitle(TitleBox.Text ?? ""))
        {
            ShowMessage(Strings.InterventionTitleSaved);
            ReadHistory();
        }

        if (TitleBox.Text != recorder.Title)
        {
            TitleBox.Text = recorder.Title;
        }
    }

    private void OnNoteChanged(object? sender, TextChangedEventArgs e) =>
        NoteButton.IsEnabled = NoteBox.IsEnabled && (NoteBox.Text ?? "").Trim().Length > 0;

    /// <summary>Entrée ajoute la note (ou enregistre la modification) ; Maj+Entrée passe à la ligne ; Échap annule une modification.</summary>
    private void OnNotePreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            AddNote();
        }
        else if (e.Key == Key.Escape && _editing is not null)
        {
            e.Handled = true;
            EndEdit();
        }
    }

    /// <summary>F2 sur une note de l'historique : la modifier.</summary>
    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && HistoryList.SelectedItem is InterventionHistoryItem { IsNote: true } item && NotesEditable)
        {
            e.Handled = true;
            StartEdit(item);
        }
    }

    private void OnEditNote(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InterventionHistoryItem item })
        {
            StartEdit(item);
        }
    }

    private void OnCancelEdit(object? sender, RoutedEventArgs e)
    {
        EndEdit();
        NoteBox.Focus();
    }

    /// <summary>Modification d'une note : son texte dans le champ de la note, qui l'enregistre au lieu d'en ajouter une.</summary>
    internal void StartEdit(InterventionHistoryItem item)
    {
        if (!item.IsNote || !NotesEditable)
        {
            return;
        }

        if (_editing is null)
        {
            _draft = NoteBox.Text ?? "";
        }

        _editing = item;
        NoteBox.Text = item.AllText;
        NoteBox.SelectAll();
        NoteButtonText.Text = Strings.InterventionSaveNoteEdit;
        CancelEditButton.IsVisible = true;
        EditingText.Text = Text.Format(Strings.InterventionEditingNote, item.Time);
        EditingText.IsVisible = true;
        NoteBox.Focus();
    }

    /// <summary>Fin de la modification (enregistrée ou annulée) : le champ redevient celui d'une nouvelle note.</summary>
    private void EndEdit()
    {
        if (_editing is null)
        {
            return;
        }

        _editing = null;
        NoteBox.Text = _draft;
        NoteBox.CaretIndex = _draft.Length;
        _draft = "";
        NoteButtonText.Text = Strings.InterventionAddNote;
        CancelEditButton.IsVisible = false;
        EditingText.IsVisible = false;
    }

    private void OnAddNote(object? sender, RoutedEventArgs e) => AddNote();

    private void AddNote()
    {
        var note = (NoteBox.Text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (note.Length == 0 || Active is not { } recorder)
        {
            return;
        }

        if (_editing is { } editing)
        {
            // Texte inchangé : rien à garder.
            if (note != editing.AllText && recorder.EditNote(editing.Block.Start, note))
            {
                ShowMessage(Strings.InterventionNoteSaved);
            }

            EndEdit();
            ReadHistory();
            return;
        }

        recorder.Record(InterventionKind.Note, "", note);
        NoteBox.Clear();
        ShowMessage(Strings.InterventionNoteAdded);
        ReadHistory();
    }

    private void OnCapture(object? sender, RoutedEventArgs e)
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
                var menu = new ContextMenu { Placement = PlacementMode.Bottom };
                foreach (var window in windows)
                {
                    var item = new MenuItem { Header = window.Title.Length > 0 ? window.Title : window.Handle.ToString(CultureInfo.InvariantCulture) };
                    item.Click += (_, _) => Capture(window);
                    menu.Items.Add(item);
                }

                menu.Open(CaptureButton);
                break;
        }
    }

    private void Capture(RemoteDesktopWindow window)
    {
        if (Active is not { } recorder)
        {
            return;
        }

        var png = WindowCapture.CapturePng(window.Handle);
        if (png is null)
        {
            ShowMessage(Strings.InterventionCaptureFailed, error: true);
            return;
        }

        recorder.Record(InterventionKind.Screenshot, window.Title, Text.Format(Strings.InterventionCaptureOf, window.Title), png);
        ShowMessage(Text.Format(Strings.InterventionCaptured, window.Title));
        ReadHistory();
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InterventionHistoryItem item })
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    /// <summary>Ctrl+C (raccourci « Copier » du système) : l'événement choisi en entier (heure, nature, session, texte).</summary>
    private async void OnHistoryCopyKey(object? sender, KeyEventArgs e)
    {
        var copy = Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy;
        if (copy is null || !copy.Any(gesture => gesture.Matches(e)) || HistoryList.SelectedItem is not InterventionHistoryItem item)
        {
            return;
        }

        e.Handled = true;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await ClipboardExtensions.SetTextAsync(clipboard, item.CopyText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            // Presse-papiers pris par une autre application : rien n'est copié.
        }
    }

    /// <summary>Enregistrement arrêté : l'historique est oublié et l'onglet se retire.</summary>
    private void OnEnd(object? sender, RoutedEventArgs e)
    {
        if (Active is not null)
        {
            EndRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Bloc de l'historique de l'intervention affiché dans l'onglet « Intervention ».</summary>
public sealed class InterventionHistoryItem : INotifyPropertyChanged
{
    /// <summary>Lignes montrées d'un bloc réduit.</summary>
    internal const int PreviewLines = 6;

    /// <summary>Au-delà de ce nombre de lignes, un bloc est réduit (il se déplie en entier).</summary>
    internal const int CollapseAbove = 8;

    // Propriétés qui changent quand le bloc grandit, est modifié ou se déplie.
    private static readonly string[] Changing =
        [nameof(Edited), nameof(LineCount), nameof(IsLong), nameof(IsExpanded), nameof(Preview), nameof(FullText), nameof(AllText),
         nameof(ToggleText), nameof(AutomationName), nameof(CopyText)];

    private readonly InterventionBlock _block;
    private bool _expanded;

    public InterventionHistoryItem(InterventionBlock block) => _block = block;

    public event PropertyChangedEventHandler? PropertyChanged;

    public InterventionBlock Block => _block;

    public string Time => _block.Start.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    public string Kind => InterventionWindow.KindName(_block.Kind);

    public string Source => _block.Source;

    public bool IsTerminal => _block.Kind == InterventionKind.Terminal;

    public bool IsNote => _block.Kind == InterventionKind.Note;

    /// <summary>Note modifiée : « modifiée à … » ; vide sinon.</summary>
    public string Edited => _block.EditedAt is { } at
        ? Text.Format(Strings.InterventionNoteEditedAt, at.ToLocalTime().ToString("T", CultureInfo.CurrentCulture))
        : "";

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
            var name = string.Join(", ", new[] { Time, Kind, Edited, Source, first }.Where(part => part.Length > 0));
            return IsLong ? $"{name}, {Text.Format(Strings.InterventionShowAll, LineCount)}" : name;
        }
    }

    /// <summary>Texte copié (Ctrl+C) : en-tête puis le bloc entier.</summary>
    public string CopyText => string.Join("  ", new[] { Time, Kind, Source }.Where(part => part.Length > 0)) + Environment.NewLine
        + string.Join(Environment.NewLine, _block.Lines);

    /// <summary>Le bloc a changé (lignes ajoutées) : l'affichage relit tout.</summary>
    public void Refresh()
    {
        foreach (var name in Changing)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
