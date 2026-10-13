using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ZillaTerm.Core;

namespace ZillaTerm.App;

/// <summary>
/// Couleurs de l'interface. Thème sombre (réglage, ou mode sombre des applications de Windows) : ThemeDark.xaml et des
/// icônes éclaircies s'ajoutent après Theme.xaml, et la barre de titre des fenêtres passe en sombre. Contraste élevé de
/// Windows : les couleurs de la palette sont remplacées par les couleurs système, quel que soit le thème choisi. Le
/// changement s'applique au démarrage et chaque fois que l'utilisateur change de thème, dans ZillaTerm ou dans Windows ;
/// l'interface référence ces couleurs en DynamicResource : les fenêtres déjà ouvertes suivent sans redémarrage.
/// </summary>
internal static class Palette
{
    private enum Role
    {
        Background,
        Text,
        Border,
        Highlight,
        HighlightText,
        Button,
        ButtonText,
        Line,
    }

    private static readonly (object Key, Role Role)[] Map =
    [
        // Surfaces.
        ("SidePanelBrush", Role.Background), ("ContentBrush", Role.Background), ("CodeBrush", Role.Background),
        ("AlternateRowBrush", Role.Background), ("MainTabBrush", Role.Background), ("MainTabHoverBrush", Role.Background),
        ("TabStripBrush", Role.Background), ("ToolbarBrush", Role.Background), ("SideStripBrush", Role.Background),
        ("StatusBrush", Role.Background), ("ErrorBackgroundBrush", Role.Background), ("WarningBackgroundBrush", Role.Background),
        ("AccentLightBrush", Role.Background), ("ToolHoverBrush", Role.Background), ("ToolPressedBrush", Role.Background),
        ("ToolFocusBrush", Role.Background), ("SideStripHoverBrush", Role.Background), ("DiffEmptyBrush", Role.Background),
        ("DropHintBrush", Role.Background),

        // Textes : la couleur ne porte plus de sens, le texte le dit (« Erreur », « ⚠ »…).
        ("MutedBrush", Role.Text), ("SidePanelForeground", Role.Text), ("HeadingBrush", Role.Text), ("PlaceholderBrush", Role.Text),
        ("SideStripForeground", Role.Text), ("StatusForeground", Role.Text), ("StatusMutedForeground", Role.Text),
        ("StatusOkForeground", Role.Text), ("StatusErrorForeground", Role.Text), ("StatusWarningForeground", Role.Text),
        ("ErrorBrush", Role.Text), ("WarningBrush", Role.Text), ("SuccessBrush", Role.Text), ("DiffRemovedForeground", Role.Text),
        ("DiffAddedForeground", Role.Text), ("LineNumberBrush", Role.Text),
        // Pastilles d'état : la forme (une par état, SessionTabHeader) et l'infobulle portent l'état.
        ("StateConnectedBrush", Role.Text), ("StateConnectingBrush", Role.Text), ("StateEndedBrush", Role.Text),
        ("StateFailedBrush", Role.Text),
        // « ! » d'une pastille sur StateFailedBrush : couleur du fond, pour rester lisible sur la pastille.
        ("BadgeForeground", Role.Background),

        // Bordures et séparateurs.
        ("ToolbarBorderBrush", Role.Border), ("MainTabBorderBrush", Role.Border), ("ErrorBorderBrush", Role.Border),
        ("WarningBorderBrush", Role.Border), ("CodeBorderBrush", Role.Border), ("GridLineBrush", Role.Line),

        // Accent, sélection et focus : couleur de sélection de Windows, avec son texte.
        ("AccentBrush", Role.Highlight), ("SideStripMarkerBrush", Role.Highlight), ("ToolHoverBorderBrush", Role.Highlight),
        ("SideStripFocusBrush", Role.Highlight), ("DangerFocusBrush", Role.Highlight), ("DiffRemovedBrush", Role.Highlight),
        ("DiffAddedBrush", Role.Highlight), ("BroadcastBrush", Role.Highlight),
        (SystemColors.InactiveSelectionHighlightBrushKey, Role.Highlight), (SystemColors.InactiveSelectionHighlightTextBrushKey, Role.HighlightText),
        ("SideStripSelectedForeground", Role.HighlightText), ("DiffTextBrush", Role.HighlightText),
        ("BroadcastForeground", Role.HighlightText),

        // Bouton d'action irréversible : un bouton système (le libellé dit le danger).
        ("DangerBrush", Role.Button), ("DangerHoverBrush", Role.Button), ("DangerPressedBrush", Role.Button),
        ("DangerForeground", Role.ButtonText), ("DangerBorderBrush", Role.ButtonText),
    ];

    private static ResourceDictionary? _applied;
    private static ResourceDictionary? _dark;
    private static AppTheme _choice = AppTheme.System;
    private static (bool HighContrast, bool Dark)? _state;

    /// <summary>Thème sombre affiché (hors contraste élevé).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Couleurs changées : pour ce qui n'est pas en DynamicResource (suivi tail -f).</summary>
    public static event Action? Changed;

    /// <summary>Applique la palette maintenant, puis suit les changements de thème, de contraste et de couleurs système.</summary>
    public static void Follow(Application app, AppTheme choice)
    {
        _choice = choice;
        Apply(app, force: true);
        // Barre de titre de chaque fenêtre à son ouverture.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window)
            {
                TitleBar(window);
            }
        }));
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                app.Dispatcher.BeginInvoke(() => Apply(app, force: true));
            }
        };
        // Passer d'un thème contrasté à un autre ne change que les couleurs système ; le mode sombre des applications de
        // Windows arrive en « General ».
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.Accessibility
                or Microsoft.Win32.UserPreferenceCategory.General)
            {
                bool force = e.Category != Microsoft.Win32.UserPreferenceCategory.General && SystemParameters.HighContrast;
                app.Dispatcher.BeginInvoke(() => Apply(app, force));
            }
        };
    }

    /// <summary>Thème choisi dans les Paramètres, appliqué aux fenêtres ouvertes.</summary>
    public static void Choose(Application app, AppTheme choice)
    {
        _choice = choice;
        Apply(app, force: false);
    }

    private static void Apply(Application app, bool force)
    {
        bool highContrast = SystemParameters.HighContrast;
        bool dark = !highContrast && (_choice == AppTheme.Dark || (_choice == AppTheme.System && WindowsAppsAreDark()));
        if (!force && _state == (highContrast, dark))
        {
            return;
        }

        _state = (highContrast, dark);
        var merged = app.Resources.MergedDictionaries;
        if (_applied is not null)
        {
            merged.Remove(_applied);
            _applied = null;
        }

        // Dernier dictionnaire fusionné : il l'emporte sur Theme.xaml.
        _applied = highContrast ? HighContrast() : dark ? _dark ??= Dark(merged) : null;
        if (_applied is not null)
        {
            merged.Add(_applied);
        }

        IsDark = dark;
        foreach (Window window in app.Windows)
        {
            TitleBar(window);
        }

        Changed?.Invoke();
    }

    /// <summary>Thème sombre : ThemeDark.xaml et les icônes de Theme.xaml éclaircies.</summary>
    internal static ResourceDictionary Dark(IEnumerable<ResourceDictionary> themes)
    {
        var dictionary = new ResourceDictionary { Source = new Uri("pack://application:,,,/ZillaTerm;component/ThemeDark.xaml") };
        foreach (var theme in themes)
        {
            foreach (var key in theme.Keys.OfType<string>().Where(k => k.StartsWith("Icon", StringComparison.Ordinal)))
            {
                if (theme[key] is DrawingImage icon && !BadgeIcons.Contains(key))
                {
                    dictionary[key] = DarkIcon(icon);
                }
            }
        }

        return dictionary;
    }

    /// <summary>Mode sombre des applications de Windows (Paramètres → Personnalisation → Couleurs).</summary>
    private static bool WindowsAppsAreDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    // ===================== Icônes sur fond sombre =====================

    /// <summary>Pastilles des dialogues (information, question, avertissement, danger) : déjà lisibles sur tout fond.</summary>
    private static readonly HashSet<string> BadgeIcons = new(StringComparer.Ordinal) { "IconInfo", "IconQuestion", "IconWarning", "IconDanger" };

    /// <summary>Écran de terminal, presque noir : cerné d'un trait clair plutôt qu'éclairci.</summary>
    private static readonly Color TerminalScreen = Color.FromRgb(0x26, 0x32, 0x38);

    private static readonly Color ScreenOutline = Color.FromRgb(0x8C, 0x99, 0xA6);

    /// <summary>Gris et bleus foncés des icônes, éclaircis (3:1 au moins sur la barre d'outils sombre).</summary>
    private static readonly Dictionary<Color, Color> Lighter = new()
    {
        [Color.FromRgb(0x5A, 0x65, 0x73)] = Color.FromRgb(0xAE, 0xB8, 0xC2),
        [Color.FromRgb(0x6B, 0x77, 0x85)] = Color.FromRgb(0xAE, 0xB8, 0xC2),
        [Color.FromRgb(0x15, 0x65, 0xC0)] = Color.FromRgb(0x5A, 0xA9, 0xF0),
        [Color.FromRgb(0x0D, 0x47, 0xA1)] = Color.FromRgb(0x3F, 0x86, 0xD8),
        [Color.FromRgb(0x6A, 0x4F, 0xA3)] = Color.FromRgb(0x8A, 0x6F, 0xC8),
        // Trait du curseur de texte (renommer), à cheval sur le cadre blanc et le fond : gris moyen.
        [Color.FromRgb(0x26, 0x32, 0x38)] = Color.FromRgb(0x94, 0xA3, 0xB0),
    };

    internal static DrawingImage DarkIcon(DrawingImage icon)
    {
        var copy = icon.Clone();
        Recolor(copy.Drawing);
        copy.Freeze();
        return copy;
    }

    private static void Recolor(Drawing drawing)
    {
        if (drawing is DrawingGroup group)
        {
            foreach (var child in group.Children)
            {
                Recolor(child);
            }

            return;
        }

        if (drawing is not GeometryDrawing geometry)
        {
            return;
        }

        if (geometry.Brush is SolidColorBrush { Color: var fill })
        {
            if (fill == TerminalScreen && geometry.Pen is null)
            {
                geometry.Pen = new Pen(new SolidColorBrush(ScreenOutline), 1);
            }
            else if (Lighter.TryGetValue(fill, out var lighter))
            {
                geometry.Brush = new SolidColorBrush(lighter);
            }
        }

        if (geometry.Pen is { Brush: SolidColorBrush { Color: var stroke } } pen && Lighter.TryGetValue(stroke, out var light))
        {
            pen.Brush = new SolidColorBrush(light);
        }
    }

    /// <summary>Icône de Theme.xaml créée par le code, qui suit le changement de thème.</summary>
    internal static System.Windows.Controls.Image Icon(string key, double size)
    {
        var image = new System.Windows.Controls.Image { Width = size, Height = size };
        image.SetResourceReference(System.Windows.Controls.Image.SourceProperty, key);
        return image;
    }

    /// <summary>Texte secondaire créé par le code, dans la couleur du thème en vigueur.</summary>
    internal static System.Windows.Controls.TextBlock Muted(System.Windows.Controls.TextBlock text)
    {
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedBrush");
        return text;
    }

    // ===================== Barre de titre =====================

    /// <summary>Barre de titre sombre avec le thème sombre (Windows 10 1809 et suivants ; ignoré ailleurs).</summary>
    private static void TitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            // Windows 10 avant 20H1 : ancien numéro de l'attribut.
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Couleurs de la palette en contraste élevé (couleurs système du moment).</summary>
    internal static ResourceDictionary HighContrast()
    {
        var dictionary = new ResourceDictionary();
        foreach (var (key, role) in Map)
        {
            dictionary[key] = Brush(role);
        }

        return dictionary;
    }

    /// <summary>Clés de la palette remplacées en contraste élevé.</summary>
    internal static IEnumerable<string> Keys => Map.Select(m => m.Key).OfType<string>();

    private static Brush Brush(Role role) => role switch
    {
        Role.Background => SystemColors.WindowBrush,
        Role.Text => SystemColors.WindowTextBrush,
        Role.Border => SystemColors.WindowTextBrush,
        Role.Line => SystemColors.GrayTextBrush,
        Role.Highlight => SystemColors.HighlightBrush,
        Role.HighlightText => SystemColors.HighlightTextBrush,
        Role.Button => SystemColors.ControlBrush,
        _ => SystemColors.ControlTextBrush,
    };
}
