using Avalonia.Controls;

namespace ZillaTerm.App.Views;

/// <summary>
/// Champ de mot de passe : caractères masqués, ni copie ni coupe (TextBox d'Avalonia avec <see cref="TextBox.PasswordChar"/>).
/// Avalonia garde le texte dans une chaîne .NET (pas de SecureString comme le PasswordBox de WPF) : le champ est vidé dès
/// que le mot de passe a servi, et <see cref="Services.SecretInput"/> le rend en tableau à effacer.
/// </summary>
public class PasswordBox : TextBox
{
    public PasswordBox()
    {
        PasswordChar = '●';
        AcceptsReturn = false;
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Vide le champ.</summary>
    public new void Clear() => Text = "";
}
