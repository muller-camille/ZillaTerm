using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace ZillaTerm.App.Views;

/// <summary>
/// Nom des boutons et des cases pour les lecteurs d'écran. WPF ne le tire que d'un contenu texte (Content="…") ou d'un
/// TextBlock : un libellé AccessText (touche d'accès), seul ou avec une image (bouton « Enregistrer l'intervention »,
/// « Accès d'urgence »…), laisse le contrôle sans nom. Sans nom explicite, le nom suit alors ce libellé, sans le marqueur de
/// la touche d'accès.
/// </summary>
internal static class AccessibleName
{
    private static bool _following;

    /// <summary>Nomme les boutons et les cases à leur chargement, dans toutes les fenêtres.</summary>
    public static void Follow()
    {
        if (_following)
        {
            return;
        }

        _following = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply((ButtonBase)sender)));
    }

    /// <summary>Lie le nom du contrôle au libellé AccessText de son contenu, s'il n'a pas déjà un nom.</summary>
    internal static void Apply(ButtonBase button)
    {
        if (button.Content is string or TextBlock || AutomationProperties.GetLabeledBy(button) is not null
            || !string.IsNullOrEmpty(AutomationProperties.GetName(button)) || BindingOperations.IsDataBound(button, AutomationProperties.NameProperty)
            || Label(button.Content) is not { } label)
        {
            return;
        }

        BindingOperations.SetBinding(button, AutomationProperties.NameProperty,
            new Binding(nameof(AccessText.Text)) { Source = label, Converter = WithoutMarker.Instance });
    }

    /// <summary>Premier libellé AccessText du contenu (lui-même, ou dans un panneau ou un cadre).</summary>
    private static AccessText? Label(object? content) => content switch
    {
        AccessText text => text,
        DependencyObject parent => LogicalTreeHelper.GetChildren(parent).OfType<object>().Select(Label).FirstOrDefault(t => t is not null),
        _ => null,
    };

    /// <summary>Libellé sans le marqueur de la touche d'accès : « _Enregistrer » → « Enregistrer », « __ » → « _ ».</summary>
    internal static string WithoutAccessKey(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool marked = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && i + 1 < text.Length && text[i + 1] == '_')
            {
                builder.Append('_');
                i++;
            }
            else if (text[i] == '_' && !marked)
            {
                marked = true;
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private sealed class WithoutMarker : IValueConverter
    {
        public static readonly WithoutMarker Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => WithoutAccessKey(value as string ?? "");

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
