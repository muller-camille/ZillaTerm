using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ZillaTerm.App.Views;

/// <summary>
/// Nom lu par les lecteurs d'écran des boutons (cases à cocher, boutons radio compris) et des onglets dont le texte a une
/// touche d'accès (« _Valider ») : le texte sans le « _ », comme sous WPF. Avalonia le donnerait tel quel (le libellé et
/// l'entrée de menu le retirent déjà). Un nom posé explicitement (AutomationProperties.Name) est gardé.
/// </summary>
internal static class AccessKeyNames
{
    /// <summary>À appeler une fois, avant la première fenêtre.</summary>
    public static void Register()
    {
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>((button, e) => Update(button, e.OldValue, e.NewValue));
        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<TabItem>((tab, e) => Update(tab, e.OldValue, e.NewValue));
    }

    /// <summary>Texte sans le marqueur de touche d'accès : le premier « _ » seul est retiré, « __ » devient « _ ».</summary>
    internal static string WithoutAccessKey(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        bool removed = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && i + 1 < text.Length)
            {
                if (text[i + 1] == '_')
                {
                    result.Append('_');
                    i++;
                    continue;
                }

                if (!removed)
                {
                    removed = true;
                    continue;
                }
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }

    private static void Update(Control control, object? old, object? now)
    {
        string? current = AutomationProperties.GetName(control);
        // Nom posé ici pour l'ancien texte (ou aucun nom) : il suit le texte. Un nom posé ailleurs reste.
        bool ours = current is null || (old is string previous && current == WithoutAccessKey(previous));
        if (!ours)
        {
            return;
        }

        if (now is string text && text.Contains('_'))
        {
            AutomationProperties.SetName(control, WithoutAccessKey(text));
        }
        else if (current is not null)
        {
            control.ClearValue(AutomationProperties.NameProperty);
        }
    }
}
