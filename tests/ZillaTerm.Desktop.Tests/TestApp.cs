using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using ZillaTerm.Core.Localization;
using ZillaTerm.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace ZillaTerm.Desktop.Tests;

/// <summary>
/// Application des tests : celle de ZillaTerm (thème Fluent, couleurs, icônes et thèmes des contrôles de App.axaml) sur
/// une plateforme sans écran. Sans fenêtre principale de bureau, App ne lance ni l'écran de connexion ni le reste du
/// démarrage.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Sandbox.Enter();
        return AppBuilder.Configure<ZillaTerm.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }
}

/// <summary>
/// Dossiers de l'utilisateur (réglages, journaux, coffre local, interventions) dans un dossier temporaire : les tests
/// n'écrivent rien chez l'utilisateur. Sous Linux et macOS, ces dossiers suivent HOME et XDG_* ; sous Windows ils restent
/// ceux du compte (poste de CI éphémère).
/// </summary>
internal static class Sandbox
{
    private static string? _root;

    public static string Root => _root ?? throw new InvalidOperationException("Sandbox.Enter() pas encore appelé.");

    public static void Enter()
    {
        if (_root is not null)
        {
            return;
        }

        _root = Directory.CreateTempSubdirectory("zillaterm-tests-").FullName;
        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("HOME", _root);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, ".config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, ".local", "share"));
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_root, ".cache"));
        }
    }
}

/// <summary>Langue de l'interface pendant un test, puis celle d'avant.</summary>
internal sealed class Language : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    private Language(string code) => UiLanguage.Apply(CultureInfo.GetCultureInfo(code));

    public static Language Use(string code) => new(code);

    public void Dispose() => UiLanguage.Apply(_previous);
}

/// <summary>Fichiers du dépôt (XAML, textes) lus par les tests.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string Desktop => Path.Combine(Root, "src", "ZillaTerm.Desktop");

    public static string Localization => Path.Combine(Root, "src", "ZillaTerm.App", "Localization");

    /// <summary>Fichiers sources de ZillaTerm.Desktop (sans bin/ ni obj/).</summary>
    public static IEnumerable<string> DesktopFiles(string pattern) =>
        Directory.EnumerateFiles(Desktop, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "ZillaTerm.Desktop")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("src/ZillaTerm.Desktop");
    }
}
