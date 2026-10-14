using Avalonia.Interactivity;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Views;

/// <summary>Saisie d'un nom (dossier, renommage...).</summary>
public partial class InputDialog : DialogWindow
{
    private readonly Func<string, string?>? _validate;

    public InputDialog() => InitializeComponent();

    /// <param name="validate">Renvoie un message d'erreur, ou null si la valeur est acceptée.</param>
    public InputDialog(string title, string label, string initial = "", Func<string, string?>? validate = null)
        : this()
    {
        Title = title;
        LabelText.Text = label;
        ValueBox.Text = initial;
        _validate = validate;
        Opened += (_, _) =>
        {
            FocusLater(ValueBox);
            ValueBox.SelectAll();
        };
    }

    public string Value => (ValueBox.Text ?? "").Trim();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var error = Value.Length == 0 ? Strings.EnterValue : _validate?.Invoke(Value);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        DialogResult = true;
    }
}
