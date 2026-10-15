using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Textes et accessibilité : erreurs des serveurs expliquées, comptes du même nom distingués, serveurs introuvables dits
/// en clair et lisibles, pluriels accordés, champs et boutons-symboles nommés pour les lecteurs d'écran.
/// </summary>
public sealed partial class UxTextTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ErrorText_ExplainsCommonServerErrorsAndKeepsTheOriginal()
    {
        using var _ = Language("fr-FR");
        var refused = new PvwaException(HttpStatusCode.Forbidden, "ITATS004E", "Authentication failure for User [jdoe]. (ITATS004E)",
            "Authentication failure for User [jdoe].");

        var text = ErrorText.Describe(refused);

        Assert.StartsWith("Le PVWA refuse l'identifiant ou le mot de passe.", text, StringComparison.Ordinal);
        Assert.EndsWith("Message du PVWA : Authentication failure for User [jdoe]. (ITATS004E)", text, StringComparison.Ordinal);
        // Erreur sans explication propre : le texte d'origine, comme avant.
        Assert.Equal("Réponse vide du PVWA.", ErrorText.Describe(new PvwaException(HttpStatusCode.OK, null, "Réponse vide du PVWA.")));
        Assert.StartsWith(Text.Format(Strings.ErrorPvwaUnreachable, ""), ErrorText.Describe(new HttpRequestException("x")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fr-FR", 1, "1 élément affiché sur 4 (filtre)")]
    [InlineData("fr-FR", 2, "2 éléments affichés sur 4 (filtre)")]
    [InlineData("en-US", 1, "1 of 4 items shown (filter)")]
    [InlineData("it-IT", 1, "1 elemento visualizzato su 4 (filtro)")]
    [InlineData("it-IT", 3, "3 elementi visualizzati su 4 (filtro)")]
    public void Counts_AgreeWithTheirNumber(string culture, int shown, string expected)
    {
        using var _ = Language(culture);

        Assert.Equal(expected, Text.Format(Strings.ResourceManager.GetString(nameof(Strings.FilesFilterSummary), CultureInfo.CurrentUICulture)!, shown, 4));
    }

    [Fact]
    public void AccountNode_SaysWhatTellsItApartAndWhyItMatches()
    {
        using var _ = Language("fr-FR");
        var account = new PvwaAccount
        {
            Id = "1", UserName = "adm-t0", Address = "corp.local", PlatformId = "WinDomain", SafeName = "T0-ADMINS",
            RemoteMachinesAccess = new RemoteMachinesAccess { RemoteMachines = "srv01;srv02;srv03;srv04" },
        };

        var node = new AccountNode(account, "→ srv01, srv02 (+2)", "srv04", "T0-ADMINS");

        Assert.Equal("→ srv01, srv02 (+2) · correspond : machine srv04", node.Hint);
        Assert.Contains(node.Hint, node.ToString(), StringComparison.Ordinal);
        Assert.Equal("", new AccountNode(account).Hint);
        // Mot visible dans la ligne ou dans le dossier : rien à expliquer.
        Assert.Equal("", new AccountNode(account, null, "adm-t0 t0-admins", "T0-ADMINS").Hint);
        Assert.Equal("correspond : plateforme WinDomain", new AccountNode(account, null, "windomain", "T0-ADMINS").Hint);
    }

    [Fact]
    public void MissingServers_AreSaidInTextOnceTheAccountsAreLoaded()
    {
        using var _ = Language("fr-FR");
        var session = new SavedSession { AccountId = "9", PvwaHost = "pvwa.corp.local", Name = "root@srv01", UserName = "root", Address = "srv01.corp.local" };

        var loading = new SavedSessionNode(session, null, accountsKnown: false);
        var missing = new SavedSessionNode(session, null);
        var found = new SavedSessionNode(session, new PvwaAccount { Id = "9" });

        Assert.False(loading.IsMissing);
        Assert.DoesNotContain(Strings.SavedAccountMissing, loading.Details, StringComparison.Ordinal);
        Assert.True(missing.IsMissing);
        Assert.EndsWith(", introuvable dans CyberArk", missing.ToString(), StringComparison.Ordinal);
        Assert.Contains(Strings.SavedAccountMissing, missing.Details, StringComparison.Ordinal);
        Assert.False(found.IsMissing);
    }

    /// <summary>Textes discrets du bandeau latéral (mode, mention « introuvable ») : contraste WCAG AA (4,5:1) au moins.</summary>
    [Theory]
    [InlineData("MutedBrush")]
    [InlineData("WarningBrush")]
    public void SidePanelTexts_HaveEnoughContrast(string brush)
    {
        var colors = XDocument.Load(Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App", "Theme.xaml")).Root!
            .Elements(Wpf + "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => (string)e.Attribute("Color")!);

        Assert.True(Contrast(colors[brush], colors["SidePanelBrush"]) >= 4.5, $"{brush} : {Contrast(colors[brush], colors["SidePanelBrush"]):F2}:1");
    }

    /// <summary>
    /// Champs de saisie et boutons sans texte (« ✕ », « ↑ », image seule) : un nom pour les lecteurs d'écran (nom explicite,
    /// étiquette liée, ou style qui le prend de l'infobulle). WPF ne nomme un bouton ou une case que d'un contenu texte ou
    /// TextBlock : un libellé AccessText (seul ou avec une image) est nommé par <see cref="Views.AccessibleName"/>, tout autre
    /// contenu (image, panneau sans AccessText) demande un nom explicite.
    /// </summary>
    [Fact]
    public void FieldsAndSymbolButtons_HaveAnAccessibleName()
    {
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App", "Views"), "*.xaml"))
        {
            var root = XDocument.Load(path).Root!;
            var labelled = root.Descendants().Select(e => (string?)e.Attribute("Target")).OfType<string>()
                .Select(t => ElementName().Match(t)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (var element in root.Descendants().Where(e => e.Name.Namespace == Wpf))
            {
                var name = (string?)element.Attribute(Xaml + "Name");
                bool named = element.Attribute("AutomationProperties.Name") is not null || element.Attribute("AutomationProperties.LabeledBy") is not null
                             || (name is not null && labelled.Contains(name));
                bool field = element.Name.LocalName is "TextBox" or "ComboBox" or "PasswordBox";
                var content = (string?)element.Attribute("Content");
                var style = (string?)element.Attribute("Style") ?? "";
                var children = element.Elements().Where(c => !c.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
                bool symbol = element.Name.LocalName is "Button" or "ToggleButton" or "CheckBox" or "RadioButton" or "RepeatButton"
                              && (content is not null ? Symbol().IsMatch(content)
                                  : children.Count > 0 && children is not [{ Name.LocalName: "TextBlock" }]
                                    && !element.Descendants(Wpf + "AccessText").Any());
                if ((field || (symbol && !style.Contains("SmallToolButton", StringComparison.Ordinal))) && !named)
                {
                    problems.Add($"{Path.GetFileName(path)} : {element.Name.LocalName} {name ?? content}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Zones « annoncées » (LiveSetting) : WPF ne lève pas de lui-même l'événement qui les fait lire. Chacune est une
    /// ErrorLine ou un AnnouncedText, qui le lèvent quand le texte change ou apparaît.
    /// </summary>
    [Fact]
    public void LiveRegions_RaiseTheirAnnouncement()
    {
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App", "Views"), "*.xaml"))
        {
            foreach (var element in XDocument.Load(path).Root!.Descendants().Where(e => e.Attribute("AutomationProperties.LiveSetting") is not null))
            {
                if (element.Name.LocalName is not ("ErrorLine" or "AnnouncedText"))
                {
                    problems.Add($"{Path.GetFileName(path)} : {element.Name.LocalName} {(string?)element.Attribute(Xaml + "Name")}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// « Mes serveurs » : un dossier n'ouvre jamais tous ses serveurs (un onglet chacun) ; seule une sélection le fait, dans
    /// la limite de <see cref="Views.MainWindow.MaxSessionsAtOnce"/>.
    /// </summary>
    [Fact]
    public void FolderMenu_NeverOpensEveryServer()
    {
        var root = XDocument.Load(Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App", "Views", "MainWindow.xaml")).Root!;
        var menu = root.Descendants(Wpf + "ContextMenu").Single(m => (string?)m.Attribute(Xaml + "Key") == "SavedFolderMenu");
        Assert.DoesNotContain(menu.Descendants(Wpf + "MenuItem"), i => (string?)i.Attribute("Tag") == "connectmany");
        var selection = root.Descendants(Wpf + "ContextMenu").Single(m => (string?)m.Attribute(Xaml + "Key") == "SavedSessionMenu");
        Assert.Contains(selection.Descendants(Wpf + "MenuItem"), i => (string?)i.Attribute("Tag") == "connectmany");
        Assert.Equal(10, Views.MainWindow.MaxSessionsAtOnce);
    }

    /// <summary>Paramètres : chaque case a une touche d'accès dans les trois langues.</summary>
    [Fact]
    public void SettingsCheckBoxes_HaveAnAccessKey()
    {
        var app = Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App");
        var keys = XDocument.Load(Path.Combine(app, "Views", "SettingsDialog.xaml")).Root!.Descendants(Wpf + "CheckBox")
            .Select(c => (string?)c.Attribute("Content") ?? (string?)c.Element(Wpf + "AccessText")?.Attribute("Text") ?? (string?)c.Element(Wpf + "TextBlock")?.Attribute("Text"))
            .Select(v => StaticKey().Match(v ?? "").Groups[1].Value)
            .ToList();
        Assert.Equal(15, keys.Count);
        foreach (var suffix in new[] { "", ".fr", ".it" })
        {
            var texts = XDocument.Load(Path.Combine(app, "Localization", $"Strings{suffix}.resx")).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
            Assert.All(keys, k => Assert.True(AccessKeysTests.AccessKey(texts[k]) is not null, $"Strings{suffix}/{k}"));
        }
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            static double Channel(int value)
            {
                double c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            var rgb = Convert.FromHexString(hex.TrimStart('#')[^6..]);
            return (0.2126 * Channel(rgb[0])) + (0.7152 * Channel(rgb[1])) + (0.0722 * Channel(rgb[2]));
        }

        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Langue de l'interface (et formats) du test en cours, rétablie à la fin.</summary>
    private static IDisposable Language(string name)
    {
        var (ui, formats) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        return new Restore(() =>
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = formats;
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [GeneratedRegex(@"ElementName=(\w+)")]
    private static partial Regex ElementName();

    [GeneratedRegex(@"^[^\w{]{1,2}$")]
    private static partial Regex Symbol();

    [GeneratedRegex(@"^\{x:Static loc:Strings\.(\w+)\}$")]
    private static partial Regex StaticKey();
}
