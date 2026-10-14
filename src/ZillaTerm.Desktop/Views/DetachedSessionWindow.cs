using Avalonia.Controls;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Views;

/// <summary>
/// Fenêtre séparée d'un onglet SSH détaché (sur un autre écran, par exemple) : le terminal y est déplacé, la session
/// continue. Fermer la fenêtre ramène le terminal dans son onglet, sans fermer la session ; seule la fermeture de la
/// session (onglet, application) la ferme pour de bon.
/// </summary>
public sealed class DetachedSessionWindow : Window
{
    /// <param name="icon">Icône de l'application (celle de la fenêtre principale).</param>
    public DetachedSessionWindow(SshSessionView view, string label, WindowIcon? icon = null)
    {
        View = view;
        // L'étiquette du serveur en tête du titre : dans la barre des tâches aussi, on voit qu'on est en production.
        Title = (view.ServerTag is { } tag ? $"[{tag.Name}] " : "") + Text.Format(Strings.DetachedTitle, label);
        Icon = icon;
        this[!BackgroundProperty] = this.GetResourceObservable("ContentBrush").ToBinding();
        ShowInTaskbar = true;
        Content = view;
        Activated += (_, _) => view.FocusTerminal();
    }

    public SshSessionView View { get; }

    /// <summary>La session se ferme : la fenêtre se ferme sans ramener le terminal dans l'onglet.</summary>
    public bool SessionClosed { get; set; }

    /// <summary>Retire le terminal de la fenêtre (pour le remettre dans son onglet).</summary>
    public SshSessionView TakeView()
    {
        Content = null;
        return View;
    }
}
