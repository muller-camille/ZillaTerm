using System.Windows;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.App.Views;

/// <summary>Intervention : bouton de la barre, indicateur d'enregistrement, fenêtre, et événements du journal.</summary>
public partial class MainWindow
{
    private InterventionWindow? _interventionWindow;

    private static InterventionRecorder Intervention => InterventionRecorder.Current;

    /// <summary>Session CyberArk ou accès d'urgence, écrit au début de l'enregistrement.</summary>
    private string InterventionMode => _client is null ? Strings.InterventionModeEmergency : Text.Format(Strings.InterventionModeCyberArk, PvwaHost);

    private void InitIntervention()
    {
        Intervention.Changed += UpdateInterventionIndicator;
        Closed += (_, _) => Intervention.Changed -= UpdateInterventionIndicator;
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
        InterventionBadge.Visibility = InterventionIndicator.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        foreach (var view in _rdpViews)
        {
            view.InterventionRecording = recording;
        }
    }

    private void OnIntervention(object sender, RoutedEventArgs e) => ShowIntervention();

    private void OnInterventionIndicatorClick(object sender, MouseButtonEventArgs e) => ShowIntervention();

    private void ShowIntervention()
    {
        if (_interventionWindow is { IsLoaded: true })
        {
            _ = _interventionWindow.ShowReminderAsync(_settings.DutyText, _settings.DutyTextFile);
            _interventionWindow.Activate();
            return;
        }

        _interventionWindow = new InterventionWindow(_settings.DutyText, _settings.DutyTextFile, Intervention, _sessionUser, InterventionMode) { Owner = this };
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
