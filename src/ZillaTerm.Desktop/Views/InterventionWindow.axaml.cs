using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Services.Secrets;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Intervention : les consignes d'astreinte de l'équipe (texte ou fichier), l'enregistrement de ce qui est fait, et les
/// interventions enregistrées avec leur rapport. Titre, notes, captures et historique de l'intervention en cours sont dans
/// l'onglet « Intervention » du panneau de gauche (<see cref="InterventionPanel"/>).
/// </summary>
/// <remarks>
/// Hors de Windows, le journal est protégé par le trousseau du bureau : sa clé est lue sans bloquer la fenêtre avant de
/// commencer, de lister ou de relire (<see cref="InterventionRecorder.PrepareAsync"/>…). Sans trousseau, « Enregistrer
/// l'intervention » est grisé avec la raison en infobulle ; un trousseau refusé ou une clé perdue est dit, et les journaux
/// restent listés (sans leur titre) pour être supprimés.
/// </remarks>
public partial class InterventionWindow : DialogWindow
{
    private readonly InterventionRecorder _recorder = InterventionRecorder.Current;
    private readonly string _who = "";
    private readonly string _mode = "";
    private readonly Action? _showNotes;
    private readonly Action? _beforeStop;
    private readonly DispatcherTimer _timer = new();
    private CancellationTokenSource? _reminderRead;

    // État d'enregistrement déjà dit par la ligne d'état (annoncée seulement quand il change).
    private bool _shownRecording;
    private bool _shownInterrupted;

    // Fenêtre fermée : les lectures en cours (liste, rapport, trousseau) ne touchent plus à rien.
    private bool _closed;

    // Raison pour laquelle la protection du journal ne peut pas servir (trousseau absent, macOS) ; null si elle le peut.
    private string? _unavailable;
    private bool _checking;

    // Démarrage en cours (clé du trousseau lue d'avance, peut-être avec sa fenêtre de déverrouillage).
    private bool _starting;

    // Lecture de la liste la plus récente : une lecture dépassée par une autre ne remplit pas la liste.
    private int _listing;

    // Intervention qui vient de s'arrêter, à choisir quand la liste est relue.
    private string? _selectStopped;

    /// <summary>Attente au plus du fichier des consignes (partage réseau qui ne répond pas).</summary>
    private static readonly TimeSpan ReminderTimeout = TimeSpan.FromSeconds(5);

    public InterventionWindow() => InitializeComponent();

    /// <param name="teamText">Consignes d'astreinte enregistrées (Paramètres ou fichier d'environnement).</param>
    /// <param name="teamFile">Fichier des consignes, relu à l'ouverture ; vide : pas de fichier.</param>
    /// <param name="who">Utilisateur, écrit au début de l'enregistrement.</param>
    /// <param name="mode">Session CyberArk ou accès d'urgence, écrit au début de l'enregistrement.</param>
    /// <param name="showNotes">Montre l'onglet « Intervention » de la fenêtre principale (titre, notes, historique).</param>
    /// <param name="beforeStop">Juste avant l'arrêt : la note en cours de saisie dans l'onglet part au journal.</param>
    public InterventionWindow(string teamText, string teamFile, InterventionRecorder recorder, string who, string mode, Action? showNotes = null,
        Action? beforeStop = null)
        : this()
    {
        _recorder = recorder;
        _who = who;
        _mode = mode;
        _showNotes = showNotes;
        _beforeStop = beforeStop;
        _ = ShowReminderAsync(teamText, teamFile);
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();
        recorder.Changed += OnRecorderChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _reminderRead?.Cancel();
            recorder.Changed -= OnRecorderChanged;
        };
        UpdateState();
        _ = RefreshJournalsAsync();
        _ = CheckProtectionAsync();
        // Trousseau démarré (ou arrêté) entre-temps : redit en revenant sur la fenêtre.
        Activated += (_, _) => _ = CheckProtectionAsync();
        Opened += (_, _) => RecordButton.Focus();
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
        ReminderSource.IsVisible = text.Length > 0;
        ReminderSource[!ForegroundProperty] = ReminderSource.GetResourceObservable(warning ? "WarningBrush" : "MutedBrush").ToBinding();
    }

    /// <summary>Consignes affichées telles quelles, sans interprétation.</summary>
    private void SetTeamText(string text)
    {
        bool empty = string.IsNullOrWhiteSpace(text);
        TeamText.Text = empty ? "" : text;
        TeamText.IsVisible = !empty;
        NoTeamText.IsVisible = empty;
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
        InterventionKind.Screen => Strings.InterventionKindScreen,
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
        Screens = Strings.InterventionReportScreens,
        ScreenHelp = Strings.InterventionReportScreenHelp,
        ScreenHeading = Strings.InterventionReportScreenHeading,
        Play = Strings.InterventionReportPlay,
        Pause = Strings.InterventionReportPause,
        Speed = Strings.InterventionReportSpeed,
        Activity = Strings.InterventionReportActivity,
        ScriptNeeded = Strings.InterventionReportScriptNeeded,
        ScreenGap = Strings.InterventionReportScreenGap,
        NoteEdited = Strings.InterventionReportNoteEdited,
        NoteMarker = Strings.InterventionReportNoteMarker,
        Redacted = count => count == 0 ? Strings.InterventionReportRedactedNone : Text.Format(Strings.InterventionReportRedacted, count),
        Parts = count => Text.Format(Strings.InterventionReportParts, count),
        Part = Strings.InterventionReportPart,
    };

    private void OnRecorderChanged()
    {
        UpdateState();
        _ = RefreshJournalsAsync();
    }

    private void UpdateState()
    {
        bool recording = _recorder.IsRecording;
        RecordLabel.Text = recording ? Strings.InterventionStopRecording : Strings.InterventionStartRecording;
        RecordDot.IsVisible = !recording;
        StopSquare.IsVisible = recording;
        // Arrêter reste toujours possible ; commencer demande la protection du journal (trousseau hors de Windows).
        RecordButton.IsEnabled = recording || (_unavailable is null && !_starting);
        ToolTip.SetTip(RecordButton, recording ? null : _unavailable);
        AutomationProperties.SetHelpText(RecordButton, recording ? null : _unavailable);
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
        MessageText[!ForegroundProperty] = MessageText.GetResourceObservable(error ? "ErrorBrush" : "MutedBrush").ToBinding();
    }

    /// <summary>
    /// La protection du journal peut-elle servir (trousseau présent hors de Windows) ? Sinon « Enregistrer l'intervention »
    /// est grisé, la raison en infobulle et sous les boutons.
    /// </summary>
    private async Task CheckProtectionAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        string? reason;
        try
        {
            // Bus de session lu sur un autre fil : la fenêtre ne s'arrête jamais sur lui.
            reason = await Task.Run(() => SecretProtectors.CheckAvailabilityAsync());
        }
        finally
        {
            _checking = false;
        }

        if (_closed)
        {
            return;
        }

        var previous = _unavailable;
        _unavailable = reason;
        UpdateState();
        if (reason == previous || _recorder.IsRecording)
        {
            return;
        }

        if (reason is not null)
        {
            ShowMessage(reason, error: true);
        }
        else if (MessageText.Text == previous)
        {
            // Trousseau revenu : sa raison ne s'affiche plus.
            ShowMessage("");
        }
    }

    private async void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            _beforeStop?.Invoke();
            var path = _recorder.JournalPath;
            _recorder.Stop(Strings.InterventionStoppedByUser);
            ShowStopped(path);
            return;
        }

        if (_starting)
        {
            return;
        }

        _starting = true;
        UpdateState();
        try
        {
            // Clé du trousseau lue d'avance, sans bloquer la fenêtre (sa fenêtre de déverrouillage au besoin) ; créée si
            // aucun journal ne l'utilise encore.
            await _recorder.PrepareAsync();
            if (_closed || _recorder.IsRecording)
            {
                return;
            }

            _recorder.Start(_who, _mode);
            MessageText.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SecretProtectorUnavailableException)
        {
            DebugLog.Write("intervention", $"Enregistrement de l'intervention impossible : {ex.Message}");
            if (!_closed)
            {
                ShowMessage(Text.Format(Strings.InterventionStartFailed, ex.Message), error: true);
            }
        }
        finally
        {
            _starting = false;
            if (!_closed)
            {
                UpdateState();
            }
        }
    }

    /// <summary>Intervention qui vient de s'arrêter : sélectionnée, son rapport s'exporte tout de suite.</summary>
    public void ShowStopped(string? path)
    {
        // La liste est relue après l'arrêt (sans bloquer la fenêtre) : l'intervention y est choisie dès qu'elle y est.
        _selectStopped = path;
        SelectStopped();
        ShowMessage(Strings.InterventionStoppedHint);
    }

    private void SelectStopped()
    {
        if (_selectStopped is not { } path
            || JournalList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is InterventionFile file && SamePath(file.Path, path)) is not { } found)
        {
            return;
        }

        JournalList.SelectedItems?.Clear();
        JournalList.SelectedItem = found;
        JournalList.ScrollIntoView(found);
    }

    /// <summary>Titre, notes, captures et historique de l'intervention en cours (onglet du panneau de gauche).</summary>
    private void OnOpenNotes(object? sender, RoutedEventArgs e) => _showNotes?.Invoke();

    /// <summary>
    /// Interventions enregistrées, lues hors du fil de l'interface (la clé du trousseau d'abord, pour leur titre). Trousseau
    /// absent ou refusé : les journaux sans leur titre (ils se suppriment encore), et la raison.
    /// </summary>
    private async Task RefreshJournalsAsync()
    {
        int listing = ++_listing;
        IReadOnlyList<InterventionFile> files;
        string? problem = null;
        try
        {
            files = await _recorder.ListAsync();
        }
        catch (SecretProtectorUnavailableException ex)
        {
            problem = Text.Format(Strings.InterventionReadFailed, ex.Message);
            files = ListWithoutTitles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            files = [];
        }

        if (_closed || listing != _listing)
        {
            return;
        }

        var keep = SelectedJournals.Select(file => file.Path).ToHashSet(PathComparer);
        JournalList.Items.Clear();
        foreach (var file in files)
        {
            var start = file.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var label = file.Title is { } title
                ? Text.Format(Strings.InterventionJournalItemTitled, start, title, RemotePath.FormatSize(file.Length))
                : Text.Format(Strings.InterventionJournalItem, start, RemotePath.FormatSize(file.Length));
            if (_recorder.JournalPath is { } current && SamePath(file.Path, current))
            {
                label = Text.Format(Strings.InterventionInProgress, label);
            }

            var item = new ListBoxItem { Content = label, Tag = file };
            JournalList.Items.Add(item);
            if (keep.Contains(file.Path))
            {
                JournalList.SelectedItems?.Add(item);
            }
        }

        if (files.Count == 0)
        {
            JournalList.Items.Add(new ListBoxItem { Content = Strings.InterventionNoJournal, IsEnabled = false });
        }

        SelectStopped();
        UpdateJournalButtons();
        if (problem is not null)
        {
            ShowMessage(problem, error: true);
        }
    }

    /// <summary>Journaux du dossier sans leur titre (trousseau indisponible) ; vide si le dossier est illisible.</summary>
    private IReadOnlyList<InterventionFile> ListWithoutTitles()
    {
        try
        {
            return InterventionJournal.List(_recorder.Directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Chemins comparés comme le système de fichiers : sans la casse sous Windows et macOS seulement.</summary>
    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool SamePath(string a, string b) => PathComparer.Equals(a, b);

    /// <summary>Interventions choisies, de la plus ancienne à la plus récente.</summary>
    private List<InterventionFile> SelectedJournals =>
        [.. (JournalList.SelectedItems?.OfType<ListBoxItem>() ?? []).Select(item => item.Tag).OfType<InterventionFile>().OrderBy(file => file.Start)];

    private void OnJournalSelected(object? sender, SelectionChangedEventArgs e) => UpdateJournalButtons();

    private void UpdateJournalButtons()
    {
        var files = SelectedJournals;
        ExportButton.IsEnabled = files.Count > 0;
        // L'intervention en cours ne se supprime pas : il faut d'abord l'arrêter.
        DeleteButton.IsEnabled = files.Count > 0 && !files.Any(IsRecording);
    }

    private bool IsRecording(InterventionFile file) => _recorder.JournalPath is { } current && SamePath(file.Path, current);

    private async void OnJournalDoubleClick(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
        {
            // Double-clic hors d'une ligne (barre de défilement, vide) : rien à exporter.
            return;
        }

        if (SelectedJournals is { Count: > 0 } files)
        {
            await ExportAsync(files);
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (SelectedJournals is { Count: > 0 } files)
        {
            await ExportAsync(files);
        }
    }

    /// <summary>
    /// Rapport des interventions choisies : un seul pour plusieurs (leurs événements à la suite), le texte masqué si la
    /// case est cochée. Les journaux sont relus hors du fil de l'interface, la clé du trousseau lue d'avance.
    /// </summary>
    private async Task ExportAsync(IReadOnlyList<InterventionFile> files)
    {
        ExportButton.IsEnabled = false;
        try
        {
            await ExportCoreAsync(files);
        }
        finally
        {
            if (!_closed)
            {
                UpdateJournalButtons();
            }
        }
    }

    private async Task ExportCoreAsync(IReadOnlyList<InterventionFile> files)
    {
        var recordings = new List<InterventionRecording>();
        try
        {
            foreach (var file in files)
            {
                if (IsRecording(file))
                {
                    _recorder.Flush();
                }

                recordings.Add(await _recorder.ReadAsync(file.Path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException
                                       or SecretProtectorUnavailableException)
        {
            if (!_closed)
            {
                ShowMessage(Text.Format(Strings.InterventionReadFailed, ex.Message), error: true);
            }

            return;
        }

        if (_closed)
        {
            return;
        }

        var recording = InterventionRecording.Merge(recordings);
        int? redacted = null;
        if (RedactBox.IsChecked == true)
        {
            recording = InterventionRedaction.Apply(recording, Strings.InterventionRedactedMask, out int count);
            redacted = count;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = await Pickers.SaveFileAsync(this, "HTML (*.html)|*.html", InterventionReport.FileName(files[0].Start.ToLocalTime(), recording.Title),
            folder: documents.Length > 0 && System.IO.Directory.Exists(documents) ? documents : null);
        if (path is null || _closed)
        {
            return;
        }

        try
        {
            var report = recording;
            var html = await Task.Run(() => InterventionReport.ToHtml(report, ReportText(), TimeZoneInfo.Local, CultureInfo.CurrentUICulture, redacted));
            WriteReport(path, html);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_closed)
            {
                ShowMessage(ex.Message, error: true);
            }

            return;
        }

        if (_closed)
        {
            return;
        }

        ShowMessage(redacted is { } masked
            ? Text.Format(Strings.InterventionReportSavedRedacted, path, masked)
            : Text.Format(Strings.InterventionReportSaved, path));
        try
        {
            FileManager.ShowFile(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Gestionnaire de fichiers non lancé : le rapport est enregistré, et son chemin est affiché.
            DebugLog.Write("intervention", $"Gestionnaire de fichiers non lancé pour le rapport : {ex.Message}");
        }
    }

    /// <summary>
    /// Rapport HTML en UTF-8 sans BOM. Il n'est pas chiffré : hors de Windows (où le dossier de l'utilisateur le protège),
    /// lisible par l'utilisateur seul.
    /// </summary>
    private static void WriteReport(string path, string html)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(html);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Fichier remplacé : il garde sinon ses anciens droits.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var files = SelectedJournals;
        if (files.Count == 0 || files.Any(IsRecording))
        {
            return;
        }

        string Start(InterventionFile file) => file.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (!await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
            {
                Title = Strings.InterventionTitle,
                Heading = files.Count == 1 ? Strings.InterventionDeleteHeading : Text.Format(Strings.InterventionDeleteManyHeading, files.Count),
                Message = files.Count == 1 ? Text.Format(Strings.InterventionDeleteDetail, Start(files[0])) : Strings.InterventionDeleteManyDetail,
                Items = files.Count == 1 ? [] : [.. files.Select(file => file.Title is { } title ? $"{Start(file)} — {title}" : Start(file))],
                Kind = ConfirmKind.Warning,
                Actions = [Strings.InterventionDeleteAction],
                DangerAction = 0,
            }))
        {
            return;
        }

        // Enregistrement repris entre-temps sur l'une d'elles : elle ne se supprime pas.
        foreach (var file in files.Where(file => !IsRecording(file)))
        {
            try
            {
                InterventionJournal.Delete(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowMessage(ex.Message, error: true);
            }
        }

        if (!_closed)
        {
            await RefreshJournalsAsync();
        }
    }
}
