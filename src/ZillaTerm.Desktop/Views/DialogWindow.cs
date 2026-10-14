using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ZillaTerm.App.Views;

/// <summary>
/// Fenêtre de dialogue au comportement de WPF : <see cref="DialogResult"/> la ferme avec ce résultat, un bouton
/// « Annuler » (<see cref="Button.IsCancel"/>, Échap) la ferme sans résultat. Avalonia n'a que des dialogues asynchrones :
/// <see cref="ShowDialogAsync"/> remplace <c>ShowDialog() == true</c>.
/// </summary>
public class DialogWindow : Window
{
    private bool? _result;

    public DialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // Après les gestionnaires du bouton (handledEventsToo) : comme sous WPF, « Annuler » ferme la fenêtre.
        AddHandler(Button.ClickEvent, OnAnyClick, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Résultat du dialogue ; le fixer ferme la fenêtre (vrai : accepté).</summary>
    public bool? DialogResult
    {
        get => _result;
        set
        {
            _result = value;
            Close(value);
        }
    }

    /// <summary>
    /// Affiche la fenêtre en dialogue modal de <paramref name="owner"/> (ou seule, au centre de l'écran, sans propriétaire
    /// visible : écran de connexion) ; vrai si elle est fermée par <c>DialogResult = true</c>.
    /// </summary>
    public async Task<bool> ShowDialogAsync(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            await ShowDialog<bool?>(owner);
            return _result == true;
        }

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        var closed = new TaskCompletionSource();
        Closed += (_, _) => closed.TrySetResult();
        Show();
        await closed.Task;
        return _result == true;
    }

    /// <summary>Met le focus clavier sur <paramref name="control"/> une fois la fenêtre affichée.</summary>
    protected void FocusLater(Control control) => Dispatcher.UIThread.Post(() => control.Focus(), DispatcherPriority.Input);

    private void OnAnyClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { IsCancel: true } && IsVisible)
        {
            DialogResult = false;
        }
    }
}
