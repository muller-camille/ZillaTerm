using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Views;

/// <summary>
/// « ✕ » d'un champ de recherche ou de filtre : visible seulement quand le champ n'est pas vide, il le vide et y remet
/// le curseur. Placé par-dessus le champ, aligné à droite (le champ garde une marge intérieure à droite pour lui). Hors
/// de l'ordre de tabulation : au clavier, Échap vide déjà ces champs.
/// </summary>
public sealed class ClearFieldButton : Button
{
    public static readonly StyledProperty<TextBox?> TargetProperty = AvaloniaProperty.Register<ClearFieldButton, TextBox?>(nameof(Target));

    public ClearFieldButton()
    {
        Focusable = false;
        IsTabStop = false;
        IsVisible = false;
        ToolTip.SetTip(this, Strings.ClearField);
        AutomationProperties.SetName(this, Strings.ClearField);
        AttachedToLogicalTree += (_, _) =>
        {
            if (Theme is null && this.TryFindResource("ClearFieldButton", out var theme) && theme is ControlTheme clear)
            {
                Theme = clear;
            }
        };
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Champ vidé par le bouton.</summary>
    public TextBox? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (Target is { } target)
        {
            target.Clear();
            target.Focus();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty)
        {
            if (change.OldValue is TextBox old)
            {
                old.PropertyChanged -= OnTargetChanged;
            }

            if (change.NewValue is TextBox target)
            {
                target.PropertyChanged += OnTargetChanged;
            }

            Update();
        }
    }

    private void OnTargetChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty || e.Property == IsEnabledProperty || e.Property == TextBox.IsReadOnlyProperty)
        {
            Update();
        }
    }

    private void Update() =>
        IsVisible = Target is { IsEnabled: true, IsReadOnly: false } target && !string.IsNullOrEmpty(target.Text);
}
