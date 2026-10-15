using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ZillaTerm.Desktop.Tests;

/// <summary>Mise en page avec les styles de l'application.</summary>
public sealed class LayoutTests
{
    /// <summary>
    /// Liste modifiable : son texte est à la même hauteur que celui d'une liste simple (le champ de saisie du thème Fluent
    /// le collait en haut, 2 à 3 px plus haut ; voir Themes/Styles.axaml).
    /// </summary>
    [AvaloniaFact]
    public void EditableComboTextLinesUpWithPlainCombo()
    {
        var plain = new ComboBox { ItemsSource = new[] { "PSM-RDP" }, SelectedIndex = 0, Width = 200 };
        var editable = new ComboBox { ItemsSource = new[] { "PSM-RDP" }, IsEditable = true, Text = "PSM-RDP", Width = 200 };
        var window = new Window { Content = new StackPanel { Spacing = 10, Children = { plain, editable } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shown = plain.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "PSM-RDP");
        var typed = editable.GetVisualDescendants().OfType<TextPresenter>().Single();
        double plainTop = shown.TranslatePoint(default, plain)!.Value.Y;
        double editableTop = typed.TranslatePoint(default, editable)!.Value.Y;
        window.Close();

        Assert.True(Math.Abs(plainTop - editableTop) <= 0.5, $"texte de la liste simple à {plainTop}, de la liste modifiable à {editableTop}");
    }
}
