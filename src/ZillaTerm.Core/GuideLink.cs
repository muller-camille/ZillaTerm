namespace ZillaTerm.Core;

/// <summary>
/// Adresse du guide d'utilisation en ligne (<c>docs/guide*.md</c> du dépôt), dans la langue de l'interface et à la
/// version en cours : le guide décrit ce que l'utilisateur a sous les yeux, pas une version plus récente.
/// </summary>
public static class GuideLink
{
    /// <summary>
    /// Début des ancres des pages des Paramètres (<c>&lt;a id="settings-general"&gt;</c>…), les mêmes dans les trois
    /// guides : elles ne changent pas quand un titre est reformulé ou traduit.
    /// </summary>
    public const string SettingsPrefix = "settings-";

    /// <param name="language">Code à deux lettres de la langue de l'interface (fr, it ; anglais sinon).</param>
    public static string Url(string language, Version version, string anchor)
    {
        var file = language switch
        {
            "fr" => "guide.fr.md",
            "it" => "guide.it.md",
            _ => "guide.md",
        };
        return $"{UpdateChecker.ProjectUrl}/blob/v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}/docs/{file}#{anchor}";
    }
}
