using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Pastille « Équipe » à droite d'un réglage des Paramètres : le fichier d'environnement de l'équipe le définit (une
/// nouvelle version de ce fichier pourra le changer). Cachée tant que la fenêtre ne l'a pas montrée (voir
/// <see cref="SettingsDialog"/>).
/// </summary>
public sealed class TeamBadge : Border
{
    public static readonly DependencyProperty SettingProperty =
        DependencyProperty.Register(nameof(Setting), typeof(EnvironmentSetting), typeof(TeamBadge));

    public TeamBadge()
    {
        Visibility = Visibility.Collapsed;
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(6, 0, 6, 1);
        Margin = new Thickness(10, 1, 0, 0);
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Right;
        SetResourceReference(BackgroundProperty, "AccentLightBrush");
        var text = new TextBlock { Text = Strings.TeamBadge, FontSize = 11, FontWeight = FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Child = text;
        AutomationProperties.SetName(this, Strings.TeamBadge);
    }

    /// <summary>Réglage du fichier d'environnement que la pastille signale.</summary>
    public EnvironmentSetting Setting
    {
        get => (EnvironmentSetting)GetValue(SettingProperty);
        set => SetValue(SettingProperty, value);
    }
}
