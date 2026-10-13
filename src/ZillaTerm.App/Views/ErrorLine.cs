using System.Windows;
using System.Windows.Automation;

namespace ZillaTerm.App.Views;

/// <summary>
/// Ligne d'erreur d'un dialogue, la même partout : couleur d'erreur du thème (contraste élevé compris), texte replié,
/// et annoncée par les lecteurs d'écran dès qu'elle apparaît ou change (<see cref="AnnouncedText"/>, zone assertive).
/// </summary>
public class ErrorLine : AnnouncedText
{
    public ErrorLine()
    {
        SetResourceReference(ForegroundProperty, "ErrorBrush");
        TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }
}
