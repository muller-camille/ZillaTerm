using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ZillaTerm.App.Views;

/// <summary>
/// Liste modifiable (<see cref="ComboBox.IsEditable"/>) : son texte, son changement (saisi ou choisi dans la liste) et le
/// curseur dans son champ, comme le TextBox « PART_EditableTextBox » de WPF.
/// </summary>
internal static class EditableComboText
{
    /// <summary>Texte saisi ou choisi ; vide plutôt que null.</summary>
    public static string Of(ComboBox box) => box.Text ?? "";

    /// <summary>Appelle <paramref name="changed"/> à chaque changement du texte, même avant l'affichage de la fenêtre.</summary>
    public static void OnChanged(ComboBox box, Action changed) =>
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
            {
                changed();
            }
        };

    /// <summary>Curseur dans le champ de la liste (texte sélectionné si <paramref name="selectAll"/>), une fois affichée.</summary>
    public static void Focus(ComboBox box, bool selectAll = false) => Dispatcher.UIThread.Post(() =>
    {
        if (box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } text)
        {
            text.Focus();
            if (selectAll)
            {
                text.SelectAll();
            }
        }
        else
        {
            box.Focus();
        }
    }, DispatcherPriority.Input);
}
