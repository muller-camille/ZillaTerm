using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ZillaTerm.App.Views;

/// <summary>
/// Texte annoncé par les lecteurs d'écran quand il change ou apparaît : WPF ne lève pas de lui-même l'événement
/// LiveRegionChanged d'une zone déclarée « annoncée » (LiveSetting). Zone « polie » par défaut, « assertive » si le XAML le
/// dit. Un texte qui avance tout seul (durée, compteur) se met à jour par <see cref="SetQuietly"/> : seul le changement
/// d'état est annoncé, pas chaque seconde.
/// </summary>
public class AnnouncedText : TextBlock
{
    private bool _quiet;

    static AnnouncedText()
    {
        TextProperty.OverrideMetadata(typeof(AnnouncedText), new FrameworkPropertyMetadata(string.Empty, OnShownChanged));
        VisibilityProperty.OverrideMetadata(typeof(AnnouncedText), new PropertyMetadata(Visibility.Visible, OnShownChanged));
    }

    public AnnouncedText() => AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

    /// <summary>Change le texte sans l'annoncer (la durée qui avance d'un état déjà annoncé).</summary>
    public void SetQuietly(string text)
    {
        _quiet = true;
        try
        {
            Text = text;
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>Annonce le texte affiché, même inchangé (message redit, texte posé avant l'ouverture de la fenêtre).</summary>
    public void Announce()
    {
        if (Visibility == Visibility.Visible && !string.IsNullOrEmpty(Text) && UIElementAutomationPeer.CreatePeerForElement(this) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnnouncedText { _quiet: false } text)
        {
            text.Announce();
        }
    }
}
