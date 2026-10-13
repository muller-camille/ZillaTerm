using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Intervention : les consignes d'astreinte de l'équipe (texte ou fichier), l'enregistrement de ce qui est fait, et les
/// interventions enregistrées avec leur rapport. Titre, notes, captures et historique de l'intervention en cours sont dans
/// l'onglet « Intervention » du panneau de gauche (<see cref="InterventionPanel"/>).
/// </summary>
public partial class InterventionWindow : Window
{
    private readonly InterventionRecorder _recorder;
    private readonly string _who;
    private readonly string _mode;
    private readonly Action? _showNotes;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _reminderRead;

    // État d'enregistrement déjà dit par la ligne d'état (annoncée seulement quand il change).
    private bool _shownRecording;
    private bool _shownInterrupted;

    /// <summary>Attente au plus du fichier des consignes (partage réseau qui ne répond pas).</summary>
    private static readonly TimeSpan ReminderTimeout = TimeSpan.FromSeconds(5);

    /// <param name="teamText">Consignes d'astreinte enregistrées (Paramètres ou fichier d'environnement).</param>
    /// <param name="teamFile">Fichier des consignes, relu à l'ouverture ; vide : pas de fichier.</param>
    /// <param name="who">Utilisateur, écrit au début de l'enregistrement.</param>
    /// <param name="mode">Session CyberArk ou accès d'urgence, écrit au début de l'enregistrement.</param>
    /// <param name="showNotes">Montre l'onglet « Intervention » de la fenêtre principale (titre, notes, historique).</param>
    public InterventionWindow(string teamText, string teamFile, InterventionRecorder recorder, string who, string mode, Action? showNotes = null)
    {
        InitializeComponent();
        _recorder = recorder;
        _who = who;
        _mode = mode;
        _showNotes = showNotes;
        _ = ShowReminderAsync(teamText, teamFile);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();
        recorder.Changed += OnRecorderChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _reminderRead?.Cancel();
            recorder.Changed -= OnRecorderChanged;
        };
        UpdateState();
        RefreshJournals();
        Loaded += (_, _) => RecordButton.Focus();
    }

    /// <summary>
    /// Consignes d'astreinte : le texte enregistré tout de suite, puis celui du fichier s'il y en a un (relu à chaque
    /// ouverture, les responsables le tenant à jour). Fichier illisible ou trop lent : le texte enregistré reste, avec la
    /// raison.
    /// </summary>
    public async Task ShowReminderAsync(string text, string file)
    {
        _reminderRead?.Cancel();
        SetTeamText(text);
        if (string.IsNullOrWhiteSpace(file))
        {
            ShowReminderSource("", warning: false);
            return;
        }

        var read = _reminderRead = new CancellationTokenSource();
        ShowReminderSource(Strings.DutyFileReading, warning: false);
        string message;
        bool warning = true;
        try
        {
            // Ouverture sur un autre fil : sur un partage qui ne répond pas, elle peut bloquer longtemps.
            var reminder = await Task.Run(() => DutyReminderFile.ReadAsync(file, read.Token), read.Token).WaitAsync(ReminderTimeout, read.Token);
            if (read.IsCancellationRequested)
            {
                // Fenêtre fermée ou consignes redemandées entre-temps : cette lecture est dépassée.
                return;
            }

            SetTeamText(reminder.Text);
            message = Text.Format(Strings.DutyFileSource, file.Trim());
            if (reminder.Truncated)
            {
                message += "\n" + Strings.DutyFileTruncated;
            }
            else
            {
                warning = false;
            }
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            return;
        }
        catch (TimeoutException)
        {
            read.Cancel();
            message = Text.Format(Strings.DutyFileUnreadable, Strings.DutyFileSlow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            message = Text.Format(Strings.DutyFileUnreadable, ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => Strings.DutyFileMissing,
                UnauthorizedAccessException or System.Security.SecurityException => Strings.DutyFileDenied,
                _ => ex.Message,
            });
        }

        if (ReferenceEquals(_reminderRead, read))
        {
            ShowReminderSource(message, warning);
        }
    }

    private void ShowReminderSource(string text, bool warning)
    {
        ReminderSource.Text = text;
        ReminderSource.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReminderSource.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "MutedBrush");
    }

    /// <summary>Consignes affichées telles quelles, sans interprétation.</summary>
    private void SetTeamText(string text)
    {
        bool empty = string.IsNullOrWhiteSpace(text);
        TeamText.Text = empty ? "" : text;
        TeamText.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NoTeamText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Durée lisible (« 1 h 05 »).</summary>
    internal static string Duration(TimeSpan duration) =>
        Text.Format(Strings.InterventionDuration, (int)Math.Max(0, duration.TotalHours), Math.Max(0, duration.Minutes));

    /// <summary>Nom d'une nature d'événement, dans la langue de l'interface.</summary>
    internal static string KindName(InterventionKind kind) => kind switch
    {
        InterventionKind.Started => Strings.InterventionKindStarted,
        InterventionKind.Stopped => Strings.InterventionKindStopped,
        InterventionKind.Note => Strings.InterventionKindNote,
        InterventionKind.Terminal => Strings.InterventionKindTerminal,
        InterventionKind.Connection => Strings.InterventionKindConnection,
        InterventionKind.Action => Strings.InterventionKindAction,
        InterventionKind.Transfer => Strings.InterventionKindTransfer,
        InterventionKind.Title => Strings.InterventionKindTitle,
        _ => Strings.InterventionKindScreenshot,
    };

    /// <summary>Ligne d'état d'un enregistrement : depuis quand, durée et nombre d'événements, ou depuis quand il est interrompu.</summary>
    internal static string RecordingStatus(InterventionRecorder recorder) =>
        !recorder.IsRecording || recorder.StartedAt is not { } start ? Strings.InterventionNotRecording
            : recorder.FailedAt is { } failedAt
                ? Text.Format(Strings.InterventionInterruptedAt, failedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
                : Text.Format(Strings.InterventionRecordingSince, start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
                    Duration(DateTime.UtcNow - start), recorder.Count);

    /// <summary>Textes du rapport dans la langue de l'interface.</summary>
    internal static InterventionReportText ReportText() => new()
    {
        Title = Strings.InterventionReportTitle,
        Period = Strings.InterventionReportPeriod,
        Unfinished = Strings.InterventionReportUnfinished,
        Damaged = Strings.InterventionReportDamaged,
        Confidential = Strings.InterventionReportConfidential,
        Summary = Strings.InterventionReportSummary,
        Timeline = Strings.InterventionReportTimeline,
        TimeHeader = Strings.InterventionReportTime,
        SourceHeader = Strings.InterventionReportSession,
        EventHeader = Strings.InterventionReportEvent,
        Kind = KindName,
        Count = (kind, count) => Text.Format(kind switch
        {
            InterventionKind.Connection => Strings.InterventionCountConnection,
            InterventionKind.Action => Strings.InterventionCountAction,
            InterventionKind.Transfer => Strings.InterventionCountTransfer,
            InterventionKind.Screenshot => Strings.InterventionCountScreenshot,
            InterventionKind.Note => Strings.InterventionCountNote,
            _ => Strings.InterventionCountTerminal,
        }, count),
        Duration = Duration,
    };

    private void OnRecorderChanged()
    {
        UpdateState();
        RefreshJournals();
    }

    private void UpdateState()
    {
        bool recording = _recorder.IsRecording;
        RecordLabel.Text = recording ? Strings.InterventionStopRecording : Strings.InterventionStartRecording;
        RecordDot.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        StopSquare.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        NotesButton.IsEnabled = recording;
        var status = RecordingStatus(_recorder);
        // Annoncé au début et à la fin de l'enregistrement, et quand il s'interrompt, pas à chaque seconde (durée, nombre
        // d'événements).
        bool interrupted = _recorder.FailedAt is not null;
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
        if (_recorder.FailureMessage is { } failure)
        {
            ShowMessage(failure, error: true);
        }
        else if (_recorder.TerminalTextCapped)
        {
            ShowMessage(Strings.InterventionTerminalCapped, error: true);
        }
    }

    private void ShowMessage(string text, bool error = false)
    {
        MessageText.Text = text;
        // Couleur de texte d'erreur (DangerBrush est le fond d'un bouton : illisible en sombre et en contraste élevé).
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            // L'intervention arrêtée reste sélectionnée : son rapport s'exporte tout de suite.
            var path = _recorder.JournalPath;
            _recorder.Stop(Strings.InterventionStoppedByUser);
            JournalList.SelectedItem = JournalList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => item.Tag is InterventionFile file && string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
            ShowMessage(Strings.InterventionStoppedHint);
            return;
        }

        try
        {
            _recorder.Start(_who, _mode);
            MessageText.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowMessage(Text.Format(Strings.InterventionStartFailed, ex.Message), error: true);
        }
    }

    /// <summary>Titre, notes, captures et historique de l'intervention en cours (onglet du panneau de gauche).</summary>
    private void OnOpenNotes(object sender, RoutedEventArgs e) => _showNotes?.Invoke();

    private void RefreshJournals()
    {
        var selected = (JournalList.SelectedItem as ListBoxItem)?.Tag as InterventionFile;
        JournalList.Items.Clear();
        IReadOnlyList<InterventionFile> files;
        try
        {
            files = InterventionJournal.List(_recorder.Directory, _recorder.Protector);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        foreach (var file in files)
        {
            var start = file.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var label = file.Title is { } title
                ? Text.Format(Strings.InterventionJournalItemTitled, start, title, RemotePath.FormatSize(file.Length))
                : Text.Format(Strings.InterventionJournalItem, start, RemotePath.FormatSize(file.Length));
            if (string.Equals(file.Path, _recorder.JournalPath, StringComparison.OrdinalIgnoreCase))
            {
                label = Text.Format(Strings.InterventionInProgress, label);
            }

            var item = new ListBoxItem { Content = label, Tag = file };
            JournalList.Items.Add(item);
            if (selected is not null && string.Equals(selected.Path, file.Path, StringComparison.OrdinalIgnoreCase))
            {
                JournalList.SelectedItem = item;
            }
        }

        if (files.Count == 0)
        {
            JournalList.Items.Add(new ListBoxItem { Content = Strings.InterventionNoJournal, IsEnabled = false });
        }

        UpdateJournalButtons();
    }

    private InterventionFile? SelectedJournal => (JournalList.SelectedItem as ListBoxItem)?.Tag as InterventionFile;

    private void OnJournalSelected(object sender, SelectionChangedEventArgs e) => UpdateJournalButtons();

    private void UpdateJournalButtons()
    {
        var file = SelectedJournal;
        ExportButton.IsEnabled = file is not null;
        // L'intervention en cours ne se supprime pas : il faut d'abord l'arrêter.
        DeleteButton.IsEnabled = file is not null && !string.Equals(file.Path, _recorder.JournalPath, StringComparison.OrdinalIgnoreCase);
    }

    private void OnJournalDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedJournal is not null)
        {
            Export(SelectedJournal);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (SelectedJournal is { } file)
        {
            Export(file);
        }
    }

    private void Export(InterventionFile file)
    {
        InterventionRecording recording;
        try
        {
            if (string.Equals(file.Path, _recorder.JournalPath, StringComparison.OrdinalIgnoreCase))
            {
                _recorder.Flush();
            }

            recording = InterventionJournal.Read(file.Path, _recorder.Protector);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            ShowMessage(Text.Format(Strings.InterventionReadFailed, ex.Message), error: true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = InterventionReport.FileName(file.Start.ToLocalTime(), recording.Title),
            DefaultExt = ".html",
            Filter = "HTML (*.html)|*.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var html = InterventionReport.ToHtml(recording, ReportText(), TimeZoneInfo.Local, CultureInfo.CurrentUICulture);
            File.WriteAllText(dialog.FileName, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(ex.Message, error: true);
            return;
        }

        ShowMessage(Text.Format(Strings.InterventionReportSaved, dialog.FileName));
        try
        {
            WindowsExplorer.ShowFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Explorateur non lancé : le rapport est enregistré, et son chemin est affiché.
            Core.Diagnostics.DebugLog.Write("intervention", $"Explorateur non lancé pour le rapport : {ex.Message}");
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedJournal is not { } file || string.Equals(file.Path, _recorder.JournalPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.InterventionTitle,
                Heading = Strings.InterventionDeleteHeading,
                Message = Text.Format(Strings.InterventionDeleteDetail, file.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                Kind = ConfirmKind.Warning,
                Actions = [Strings.InterventionDeleteAction],
                DangerAction = 0,
            }))
        {
            return;
        }

        try
        {
            InterventionJournal.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(ex.Message, error: true);
        }

        RefreshJournals();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
