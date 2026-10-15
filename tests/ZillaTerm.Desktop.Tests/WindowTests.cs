using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using ZillaTerm.App;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Desktop.Tests;

/// <summary>
/// Les fenêtres et les vues se chargent avec le thème de l'application, dans chaque langue et chaque thème : une erreur
/// au chargement du XAML (ressource manquante, gestionnaire absent, élément utilisé avant d'exister) les ferait planter
/// chez l'utilisateur.
/// </summary>
public sealed class WindowTests
{
    public static TheoryData<string, string> LanguagesAndThemes => new()
    {
        { "fr", "Light" }, { "en", "Light" }, { "it", "Light" },
        { "fr", "Dark" }, { "en", "Dark" }, { "it", "Dark" },
    };

    /// <summary>Chaque fenêtre et chaque vue (constructeur sans paramètre, celui du chargeur XAML) se charge.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(LanguagesAndThemes))]
    public void EveryViewLoadsItsXaml(string language, string theme)
    {
        using var _ = Language.Use(language);
        using var __ = Theme.Use(theme);
        var failures = new List<string>();
        foreach (var type in ViewTypes())
        {
            try
            {
                var control = (Control)Activator.CreateInstance(type)!;
                if (control is Window window)
                {
                    window.Close();
                }
            }
            catch (Exception e)
            {
                failures.Add($"{type.Name} : {(e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.NotEmpty(ViewTypes());
    }

    /// <summary>
    /// Accès d'urgence (sans CyberArk) : la fenêtre principale s'ouvre, dans la langue choisie, avec ses panneaux
    /// « Fichiers » et « Intervention » (référence nulle à l'ouverture, corrigée).
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(LanguagesAndThemes))]
    public void EmergencyMainWindowOpens(string language, string theme)
    {
        using var _ = Language.Use(language);
        using var __ = Theme.Use(theme);
        using var keePass = new KeePassManager(new TestProtector());
        var main = new MainWindow(null, new AppSettings(), "", "", keePass);
        main.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(main.IsVisible);
        Assert.Equal(Strings.EmergencyTitle, main.Title);
        main.Close();
    }

    /// <summary>Écran de connexion : s'ouvre dans la langue choisie.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(LanguagesAndThemes))]
    public void LoginWindowOpens(string language, string theme)
    {
        using var _ = Language.Use(language);
        using var __ = Theme.Use(theme);
        var login = new LoginWindow(new AppSettings());
        login.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(login.IsVisible);
        login.Close();
    }

    /// <summary>Vues de ZillaTerm chargées par le XAML : fenêtres et contrôles utilisateur, avec un constructeur sans paramètre.</summary>
    private static List<Type> ViewTypes() =>
        typeof(App.App).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericType: false }
                        && (typeof(Window).IsAssignableFrom(t) || typeof(UserControl).IsAssignableFrom(t))
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Protection d'essai du coffre local (jamais utilisée : aucun coffre n'est créé).</summary>
    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => throw new InvalidOperationException("Pas de coffre local dans ces tests.");

        public byte[] Unprotect(byte[] data) => throw new InvalidOperationException("Pas de coffre local dans ces tests.");
    }
}

/// <summary>Thème clair ou sombre pendant un test, puis celui d'avant.</summary>
internal sealed class Theme : IDisposable
{
    private readonly ThemeVariant? _previous = Avalonia.Application.Current!.RequestedThemeVariant;

    private Theme(string name) =>
        Avalonia.Application.Current!.RequestedThemeVariant = name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    public static Theme Use(string name) => new(name);

    public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = _previous;
}
