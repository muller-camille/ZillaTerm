using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace ZillaTerm.App.Views;

/// <summary>
/// Texte annoncé par les lecteurs d'écran quand il change ou apparaît (zone « polie » par défaut, « assertive » si le XAML
/// le dit). Un texte qui avance tout seul (durée, compteur) se met à jour par <see cref="SetQuietly"/> : seul le changement
/// d'état est annoncé, pas chaque seconde.
/// </summary>
public class AnnouncedText : TextBlock
{
    private bool _quiet;

    public AnnouncedText() => AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

    protected override Type StyleKeyOverride => typeof(TextBlock);

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
        if (IsVisible && !string.IsNullOrEmpty(Text) && ControlAutomationPeer.CreatePeerForElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, null, Text);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_quiet && (change.Property == TextProperty || change.Property == IsVisibleProperty))
        {
            Announce();
        }
    }
}

/// <summary>
/// Ligne d'erreur d'un dialogue, la même partout : couleur d'erreur du thème, texte replié, et annoncée par les lecteurs
/// d'écran dès qu'elle apparaît ou change (zone assertive).
/// </summary>
public class ErrorLine : AnnouncedText
{
    public ErrorLine()
    {
        this[!ForegroundProperty] = this.GetResourceObservable("ErrorBrush").ToBinding();
        TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }
}
