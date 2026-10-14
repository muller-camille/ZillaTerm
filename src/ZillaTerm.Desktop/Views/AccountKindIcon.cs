using Avalonia;
using Avalonia.Controls;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Icône du type d'un compte (Windows, Unix, base de données, réseau, autre) dans l'en-tête des dialogues d'un compte :
/// les mêmes icônes que KindIconConverter (SessionNodes.cs, WPF) pour un compte ou un serveur de « Mes serveurs ».
/// L'image est liée à la ressource du thème : elle suit le passage du clair au sombre.
/// </summary>
internal static class AccountKindIcon
{
    public static void Show(Image image, PvwaAccount account) => Bind(image, IconFor(AccountClassifier.Classify(account)));

    /// <summary>Serveur de « Mes serveurs » dont le compte n'est pas (ou plus) dans la liste : type d'après sa plateforme.</summary>
    public static void Show(Image image, SavedSession session) =>
        Bind(image, IconFor(AccountClassifier.Classify(new PvwaAccount { PlatformId = session.PlatformId })));

    private static void Bind(Image image, string key) => image[!Image.SourceProperty] = image.GetResourceObservable(key).ToBinding();

    private static string IconFor(AccountKind kind) => kind switch
    {
        AccountKind.Windows => "IconWindows",
        AccountKind.Unix => "IconUnix",
        AccountKind.Database => "IconDatabase",
        AccountKind.Network => "IconNetwork",
        _ => "IconOther",
    };
}
