using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.App.Views;

/// <summary>Intervention : bouton de la barre, indicateur d'enregistrement, fenêtre, et événements du journal.</summary>
public partial class MainWindow
{
    private InterventionWindow? _interventionWindow;

    // Enregistrement en cours au dernier changement : son début montre l'onglet « Intervention ».
    private bool _interventionWasRecording;

    // Rappels : regardés toutes les 5 secondes pendant l'enregistrement (le prochain s'affiche dans la barre d'état).
    private DispatcherTimer? _reminderTimer;

    // Intervention interrompue déjà proposée à la reprise dans ce lancement de ZillaTerm (une fenêtre principale peut en
    // suivre une autre : reconnexion, accès d'urgence).
    private static bool _resumeOffered;

    private static InterventionRecorder Intervention => InterventionRecorder.Current;

    /// <summary>Session CyberArk ou accès d'urgence, écrit au début de l'enregistrement.</summary>
    private string InterventionMode => _client is null ? Strings.InterventionModeEmergency : Text.Format(Strings.InterventionModeCyberArk, PvwaHost);

    private void InitIntervention()
    {
        // Enregistrement commencé avant cette fenêtre (reconnexion, accès d'urgence) : son onglet est là, sans être choisi.
        _interventionWasRecording = Intervention.IsRecording;
        InterventionView.Attach(Intervention);
        InterventionView.EndRequested += (_, _) => EndInterventionFromTab();
        Intervention.Changed += UpdateInterventionIndicator;
        InterventionView.ReminderSeen += reminder => Intervention.ReminderSeen(reminder);
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        Closed += (_, _) =>
        {
            _reminderTimer.Stop();
            Intervention.Changed -= UpdateInterventionIndicator;
            InterventionView.Detach();
        };
        UpdateInterventionIndicator();
        if (_client is null)
        {
            // L'enregistrement continue d'une fenêtre à l'autre : le passage en accès d'urgence y figure.
            Intervention.Record(InterventionKind.Action, "", Strings.InterventionEmergencyEntered);
        }
    }

    private void UpdateInterventionIndicator()
    {
        bool recording = Intervention.IsRecording;
        bool started = recording && !_interventionWasRecording;
        _interventionWasRecording = recording;
        UpdateInterventionTab();
        if (started && IsLoaded)
        {
            // Début de l'enregistrement : titre, notes et historique à côté de la session.
            ShowSideTab(InterventionTab);
        }

        InterventionBadge.Visibility = InterventionIndicator.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording)
        {
            _reminderTimer?.Start();
        }
        else
        {
            _reminderTimer?.Stop();
        }

        foreach (var view in _rdpViews)
        {
            view.InterventionRecording = recording;
        }

        // Journal refusé par le disque : l'indicateur passe en erreur et la barre d'état dit quoi faire.
        var failure = recording ? Intervention.FailureMessage : null;
        InterventionIndicator.Text = failure is null ? IndicatorText() : Strings.InterventionInterruptedIndicator;
        InterventionIndicator.ToolTip = failure ?? Strings.InterventionIndicatorTip;
        InterventionIndicator.SetResourceReference(TextBlock.ForegroundProperty, failure is null ? "StatusWarningForeground" : "StatusErrorForeground");
        if (failure is not null)
        {
            SetStatus(failure, isError: true);
        }
    }

    /// <summary>« ● Intervention enregistrée », suivi du prochain rappel (son début) et du temps qui reste.</summary>
    private string IndicatorText()
    {
        if (Intervention.NextReminder(_settings.InterventionReminders, DateTime.UtcNow) is not { } next)
        {
            return Strings.InterventionRecordingIndicator;
        }

        var (reminder, remaining) = next;

        var text = reminder.Text.Length <= 40 ? reminder.Text : reminder.Text[..(char.IsHighSurrogate(reminder.Text[39]) ? 39 : 40)].TrimEnd() + "…";
        return Strings.InterventionRecordingIndicator + " · "
               + Text.Format(Strings.InterventionNextReminder, text, (int)Math.Ceiling(remaining.TotalMinutes));
    }

    /// <summary>
    /// Rappels arrivés à leur heure (notés au journal par l'enregistrement) : montrés dans l'onglet « Intervention » et la
    /// barre d'état, et le bouton de la fenêtre clignote dans la barre des tâches (s'il n'est pas au premier plan).
    /// </summary>
    private void CheckReminders()
    {
        if (!Intervention.IsRecording)
        {
            return;
        }

        var due = Intervention.DueReminders(_settings.InterventionReminders, DateTime.UtcNow);
        foreach (var reminder in due)
        {
            InterventionView.ShowReminder(reminder);
        }

        if (due.Count > 0)
        {
            var last = due[^1];
            // Ni onglet changé ni focus déplacé : la frappe en cours (terminal) ne doit pas partir ailleurs.
            SetStatus(Text.Format(Strings.InterventionReminderStatus, last.Minutes, last.Text));
            TailAlerts.Flash(this);
        }

        if (Intervention.FailureMessage is null)
        {
            InterventionIndicator.Text = IndicatorText();
        }
    }

    /// <summary>
    /// Au démarrage : l'enregistrement le plus récent n'a pas de fin (ZillaTerm arrêté pendant l'intervention, il y a moins
    /// de 12 heures) ; le reprendre dans le même journal, le clore, ou décider plus tard (redemandé au prochain lancement).
    /// </summary>
    private void OfferInterventionResume()
    {
        if (_resumeOffered || Intervention.IsRecording || !IsLoaded)
        {
            return;
        }

        _resumeOffered = true;
        if (InterventionJournal.FindUnfinished(Intervention.Directory, Intervention.Protector, DateTime.UtcNow) is not { } unfinished)
        {
            return;
        }

        var file = unfinished.File;
        int choice = ConfirmDialog.Ask(this, new ConfirmRequest
        {
            Title = Strings.InterventionResumeTitle,
            Heading = Strings.InterventionResumeHeading,
            Subject = file.Title,
            Message = Text.Format(Strings.InterventionResumeDetail, file.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                unfinished.LastEvent.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)),
            Bullets = [Strings.InterventionResumeBullet, Strings.InterventionCloseBullet],
            Actions = [Strings.InterventionResumeAction, Strings.InterventionCloseAction],
            DefaultAction = 0,
            CancelLabel = Strings.InterventionResumeLater,
        });
        if (choice < 0)
        {
            return;
        }

        try
        {
            if (choice == 0)
            {
                Intervention.Resume(file.Path, _sessionUser, InterventionMode);
                SetStatus(Strings.InterventionResumed);
            }
            else
            {
                Intervention.CloseUnfinished(file.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            DebugLog.Write("intervention", "Reprise de l'intervention impossible", ex);
            SetStatus(Text.Format(Strings.InterventionResumeFailed, ex.Message), isError: true);
        }
    }

    private void OnIntervention(object sender, RoutedEventArgs e) => ShowIntervention();

    /// <summary>Indicateur d'enregistrement : l'onglet « Intervention » (titre, notes, historique).</summary>
    private void OnInterventionIndicatorClick(object sender, MouseButtonEventArgs e)
    {
        if (InterventionTab.Visibility == Visibility.Visible)
        {
            ShowSideTab(InterventionTab);
        }
        else
        {
            ShowIntervention();
        }
    }

    /// <summary>
    /// Onglet « Intervention » du panneau de gauche : seulement pendant l'enregistrement. S'il était affiché en
    /// disparaissant, « Mes serveurs » prend sa place.
    /// </summary>
    private void UpdateInterventionTab()
    {
        bool visible = Intervention.IsRecording;
        InterventionTab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InterventionTabDot.Visibility = Intervention.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && InterventionTab.IsSelected)
        {
            SelectSideTab(CurrentTab, expand: false);
        }
    }

    private void ShowIntervention()
    {
        if (_interventionWindow is { IsLoaded: true })
        {
            _ = _interventionWindow.ShowReminderAsync(_settings.DutyText, _settings.DutyTextFile);
            _interventionWindow.Activate();
            return;
        }

        _interventionWindow = new InterventionWindow(_settings.DutyText, _settings.DutyTextFile, Intervention, _sessionUser, InterventionMode,
            () => ShowSideTab(InterventionTab), InterventionView.CommitPendingNote) { Owner = this };
        _interventionWindow.Closed += (_, _) => _interventionWindow = null;
        _interventionWindow.Show();
    }

    /// <summary>
    /// « Terminer l'intervention » de l'onglet : après confirmation, la note en cours part au journal, l'enregistrement
    /// s'arrête (l'onglet se retire) et la fenêtre Intervention s'ouvre sur l'intervention terminée, pour son rapport.
    /// </summary>
    private void EndInterventionFromTab()
    {
        if (!Intervention.IsRecording)
        {
            return;
        }

        var bullets = new List<string> { Strings.InterventionEndBulletStop, Strings.InterventionEndBulletReport };
        if (InterventionView.HasPendingNote)
        {
            bullets.Insert(0, Strings.InterventionEndBulletNote);
        }

        int choice = ConfirmDialog.Ask(this, new ConfirmRequest
        {
            Title = Strings.InterventionEndTitle,
            Heading = Strings.InterventionEndHeading,
            Subject = string.IsNullOrWhiteSpace(Intervention.Title) ? null : Intervention.Title,
            Bullets = bullets,
            Actions = [Strings.InterventionEndAction],
            DefaultAction = 0,
            CancelLabel = Strings.InterventionEndCancel,
        });
        if (choice != 0 || !Intervention.IsRecording)
        {
            return;
        }

        InterventionView.CommitPendingNote();
        var path = Intervention.JournalPath;
        Intervention.Stop(Strings.InterventionStoppedByUser);
        ShowIntervention();
        _interventionWindow?.ShowStopped(path);
    }

    /// <summary>Événement du journal d'intervention (sans effet hors enregistrement).</summary>
    private static void InterventionRecord(InterventionKind kind, string source, string text) => Intervention.Record(kind, source, text);

    /// <summary>Session PSM ouverte : composant, machine cible, motif et ticket, pour le journal.</summary>
    private static string InterventionPsmText(ConnectRequest request, bool inTab)
    {
        var lines = new List<string> { Text.Format(inTab ? Strings.InterventionPsmTab : Strings.InterventionPsmLaunched, request.Component) };
        if (!string.IsNullOrWhiteSpace(request.RemoteMachine))
        {
            lines.Add(Text.Format(Strings.InterventionMachine, request.RemoteMachine));
        }

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            lines.Add(Text.Format(Strings.InterventionReason, request.Reason));
        }

        if (!string.IsNullOrWhiteSpace(request.TicketingSystem) || !string.IsNullOrWhiteSpace(request.TicketId))
        {
            lines.Add(Text.Format(Strings.InterventionTicket, request.TicketingSystem ?? "", request.TicketId ?? ""));
        }

        return string.Join('\n', lines);
    }

    /// <summary>Message de la barre d'état d'une action sur un compte, gardé aussi dans le journal d'intervention.</summary>
    private void ActionStatus(string source, string text)
    {
        SetStatus(text);
        Intervention.Record(InterventionKind.Action, source, text);
    }

    /// <summary>Message de la barre d'état d'une session, gardé aussi dans le journal d'intervention.</summary>
    private void SessionStatus(string source, string text, bool isError = false)
    {
        SetStatus(text, isError);
        Intervention.Record(InterventionKind.Connection, source, text);
    }
}
