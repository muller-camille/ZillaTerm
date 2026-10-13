using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.App.Views;

/// <summary>Intervention : bouton de la barre, indicateur d'enregistrement, fenêtre, et événements du journal.</summary>
public partial class MainWindow
{
    private InterventionWindow? _interventionWindow;

    // Enregistrement en cours au dernier changement : son début montre l'onglet « Intervention ».
    private bool _interventionWasRecording;

    private static InterventionRecorder Intervention => InterventionRecorder.Current;

    /// <summary>Session CyberArk ou accès d'urgence, écrit au début de l'enregistrement.</summary>
    private string InterventionMode => _client is null ? Strings.InterventionModeEmergency : Text.Format(Strings.InterventionModeCyberArk, PvwaHost);

    private void InitIntervention()
    {
        // Enregistrement commencé avant cette fenêtre (reconnexion, accès d'urgence) : son onglet est là, sans être choisi.
        _interventionWasRecording = Intervention.IsRecording;
        InterventionView.Attach(Intervention);
        InterventionView.CloseRequested += (_, _) => UpdateInterventionTab();
        Intervention.Changed += UpdateInterventionIndicator;
        Closed += (_, _) =>
        {
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
        foreach (var view in _rdpViews)
        {
            view.InterventionRecording = recording;
        }

        // Journal refusé par le disque : l'indicateur passe en erreur et la barre d'état dit quoi faire.
        var failure = recording ? Intervention.FailureMessage : null;
        InterventionIndicator.Text = failure is null ? Strings.InterventionRecordingIndicator : Strings.InterventionInterruptedIndicator;
        InterventionIndicator.ToolTip = failure ?? Strings.InterventionIndicatorTip;
        InterventionIndicator.SetResourceReference(TextBlock.ForegroundProperty, failure is null ? "StatusWarningForeground" : "StatusErrorForeground");
        if (failure is not null)
        {
            SetStatus(failure, isError: true);
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
    /// Onglet « Intervention » du panneau de gauche : pendant l'enregistrement, et après l'arrêt tant que son historique
    /// n'est pas fermé. S'il était affiché en disparaissant, « Mes serveurs » prend sa place.
    /// </summary>
    private void UpdateInterventionTab()
    {
        bool visible = Intervention.IsRecording || InterventionView.HasHistory;
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
            () => ShowSideTab(InterventionTab)) { Owner = this };
        _interventionWindow.Closed += (_, _) => _interventionWindow = null;
        _interventionWindow.Show();
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
