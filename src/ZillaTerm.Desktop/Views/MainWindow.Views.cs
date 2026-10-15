using Avalonia.Controls;
using Avalonia.Interactivity;
using ZillaTerm.App.Services;

namespace ZillaTerm.App.Views;

/// <summary>
/// Panneaux du côté gauche créés par le code (« Fichiers » et « Intervention ») et accès au terminal d'une session.
/// </summary>
public partial class MainWindow
{
    /// <summary>Explorateur de fichiers de la session affichée (onglet « Fichiers » du panneau de gauche).</summary>
    private FileBrowserPanel FilesPanel { get; set; } = null!;

    /// <summary>Titre, notes et historique de l'intervention en cours (onglet « Intervention »).</summary>
    private InterventionPanel InterventionView { get; set; } = null!;

    /// <summary>Panneaux « Fichiers » et « Intervention », apparence des terminaux, glisser-déposer des arbres.</summary>
    private void InitSessionViews()
    {
        CompareWindow.CleanTemporaryFiles();
        ApplyTerminalAppearance();
        FilesPanel = new FileBrowserPanel();
        FilesHost.Content = FilesPanel;
        FilesPanel.Initialize(_settings, SaveSettings);
        FilesPanel.OpenSessions = () => MainTabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<RemoteSession>().ToList();
        FilesPanel.TagOf = TagOf;
        FilesPanel.ShowTerminalRequested += ShowTerminal;
        FilesPanel.TransfersChanged += UpdateFilesBadge;
        FilesPanel.WidenRequested += WidenSidePanel;
        InterventionView = new InterventionPanel();
        InterventionHost.Content = InterventionView;
        InitDragDrop();
        UserActivity.Track(this);
    }

    /// <summary>Thème, taille de police et clic droit des terminaux, d'après les réglages.</summary>
    private void ApplyTerminalAppearance() =>
        ZillaTerm.App.Terminal.TerminalAppearance.Apply(_settings.TerminalTheme, _settings.TerminalFontSize, _settings.TerminalRightClickPastes);

    /// <summary>Curseur dans la session d'un contenu d'onglet ; faux si ce n'en est pas une.</summary>
    private static bool FocusSessionContent(Control content)
    {
        switch (content)
        {
            case SshSessionView view:
                view.FocusTerminal();
                return true;
            case ParallelView parallel:
                parallel.FocusActive();
                return true;
            case SharedSessionView watched:
                watched.FocusContent();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Bouton Historique : envois et téléchargements de l'onglet Fichiers, même sans session ; seulement les échecs et
    /// transferts non vérifiés quand un échec est signalé (« ! ») et pas encore vu.
    /// </summary>
    private void OnTransferHistory(object? sender, RoutedEventArgs e)
    {
        bool problems = FilesPanel.UnseenProblems > 0;
        // L'historique montre les transferts en échec : ils sont vus.
        FilesPanel.MarkTransfersSeen();
        UpdateFilesBadge();
        FilesPanel.ShowHistory(problemsOnly: problems);
    }

    /// <summary>« Terminal » de l'onglet Fichiers : la session est affichée là où est son terminal.</summary>
    private void ShowTerminal(SshSession session)
    {
        if (_detached.TryGetValue(session, out var window))
        {
            window.Activate();
            window.View.FocusTerminal();
        }
        else if (_parallel?.ViewOf(session) is { } inParallel)
        {
            ShowParallel();
            inParallel.FocusTerminal();
        }
        else if (TabOf(session) is { } tab)
        {
            MainTabs.SelectedItem = tab;
            (tab.Content as SshSessionView)?.FocusTerminal();
        }
    }
}
