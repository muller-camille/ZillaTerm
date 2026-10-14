using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace ZillaTerm.App.Views;

/// <summary>
/// En-tête d'une entrée de menu suivie, à droite, d'un texte de raccourci traduit (« Ctrl+Maj+F ») ou d'une remarque
/// (InputGestureText de WPF) : InputGesture d'Avalonia n'affiche que des noms de touches anglais.
/// </summary>
internal static class MenuHeader
{
    /// <param name="header">Texte de l'entrée, touche d'accès (« _ ») comprise.</param>
    /// <param name="gesture">Raccourci ou remarque ; rien à droite si vide.</param>
    public static object WithGesture(string header, string? gesture)
    {
        if (string.IsNullOrEmpty(gesture))
        {
            return header;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new AccessText { Text = header, VerticalAlignment = VerticalAlignment.Center });
        var shortcut = new TextBlock
        {
            Text = gesture,
            Margin = new Thickness(24, 0, 0, 0),
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(shortcut, 1);
        grid.Children.Add(shortcut);
        return grid;
    }
}
