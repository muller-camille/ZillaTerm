using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZillaTerm.App.Views;

/// <summary>
/// Affiche un avertissement tant que Verr. Maj est activé pendant la saisie d'un mot de passe ; annoncé par les lecteurs
/// d'écran quand il apparaît (<see cref="AnnouncedText"/>).
/// </summary>
internal static class CapsLockWarning
{
    public static void Attach(AnnouncedText warning, params PasswordBox[] boxes)
    {
        void Update()
        {
            bool show = boxes.Any(b => b.IsKeyboardFocused) && Keyboard.IsKeyToggled(Key.CapsLock);
            warning.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var box in boxes)
        {
            box.GotKeyboardFocus += (_, _) => Update();
            box.LostKeyboardFocus += (_, _) => Update();
            // Après la touche : l'état de Verr. Maj est alors à jour.
            box.PreviewKeyUp += (_, _) => Update();
        }
    }
}
