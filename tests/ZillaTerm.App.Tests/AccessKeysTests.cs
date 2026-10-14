using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Touches d'accès (lettre soulignée, Alt+lettre) : deux entrées d'un même menu ou deux contrôles d'une même fenêtre
/// (d'un même onglet, dans une fenêtre à onglets) ne partagent pas la même lettre, dans aucune langue. Les groupes sont
/// lus dans le XAML ; les menus construits dans le code sont listés ici.
/// </summary>
public sealed partial class AccessKeysTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] SessionActions =
        ["MenuTabReconnect", "MenuTabDuplicate", "MenuTabDetach", "MenuTabAddParallel", "MenuTabRemoveParallel", "MenuAddToMyServers",
            "MenuTabShare", "MenuTabShareManage", "MenuTabClose"];

    /// <summary>Menus construits dans le code (MainWindow.Ssh.cs, TerminalView.cs, MainWindow.Parallel.cs…).</summary>
    private static readonly (string Name, string[] Keys)[] CodeMenus =
    [
        ("TabMenu", [.. SessionActions, "MenuTerminalSearch", "MenuTerminalSave", "MenuTabCloseOthers"]),
        ("TerminalMenu", [.. SessionActions, "MenuTerminalCopy", "MenuTerminalPaste", "MenuTerminalSelectAll", "MenuTerminalSearch",
            "MenuTerminalSave", "MenuTerminalClear", "MenuTerminalFont"]),
        ("FontMenu", ["MenuFontBigger", "MenuFontSmaller", "MenuFontDefault"]),
        ("ParallelTabMenu", ["MenuTabDetach", "ParallelClose"]),
        ("KeePassConnectAs", ["MenuKeePassSsh", "MenuKeePassRdp", "MenuKeePassVnc", "MenuKeePassSftp", "MenuKeePassFtp"]),
        ("TailFiles", ["TailFilesAll", "TailFilesForget"]),
    ];

    [Fact]
    public void NoMenuOrWindowSharesAnAccessKey()
    {
        var app = Path.Combine(RepositoryRoot(), "src", "ZillaTerm.App");
        var groups = new List<(string Name, HashSet<string> Keys)>();
        foreach (var path in Directory.GetFiles(Path.Combine(app, "Views"), "*.xaml"))
        {
            groups.AddRange(Groups(Path.GetFileName(path), XDocument.Load(path).Root!));
        }

        groups.AddRange(CodeMenus.Select(m => ($"code:{m.Name}", m.Keys.ToHashSet())));
        var problems = new List<string>();
        foreach (var suffix in new[] { "", ".fr", ".it" })
        {
            var texts = XDocument.Load(Path.Combine(app, "Localization", $"Strings{suffix}.resx")).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
            foreach (var (name, keys) in groups)
            {
                foreach (var clash in keys.Where(texts.ContainsKey)
                             .Select(k => (Key: k, Letter: AccessKey(texts[k])))
                             .Where(x => x.Letter is not null)
                             .GroupBy(x => x.Letter)
                             .Where(g => g.Count() > 1))
                {
                    problems.Add($"{name} [{(suffix.Length == 0 ? "en" : suffix[1..])}] '{clash.Key}' : {string.Join(", ", clash.Select(x => x.Key))}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Lettre soulignée : celle qui suit le premier « _ » (« __ » est un « _ » littéral).</summary>
    internal static char? AccessKey(string text)
    {
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '_')
            {
                continue;
            }

            if (text[i + 1] == '_')
            {
                i++;
                continue;
            }

            return char.ToLowerInvariant(text[i + 1]);
        }

        return null;
    }

    [Fact]
    public void AccessKeyReadsTheFirstSingleUnderscore()
    {
        Assert.Equal('f', AccessKey("_Fichier"));
        Assert.Equal('p', AccessKey("Mot de _passe"));
        Assert.Equal('t', AccessKey("a__b _test"));
        Assert.Null(AccessKey("Aucun"));
    }

    /// <summary>Un groupe par menu (sous-menus compris) et un groupe pour le reste de la fenêtre.</summary>
    private static IEnumerable<(string, HashSet<string>)> Groups(string file, XElement root)
    {
        var inMenus = new HashSet<XElement>();
        var groups = new List<(string, HashSet<string>)>();
        void WalkMenu(XElement menu, string name)
        {
            var items = menu.Elements(Wpf + "MenuItem").ToList();
            groups.Add(($"{file}:{name}", items.Select(i => KeyOf((string?)i.Attribute("Header"))).OfType<string>().ToHashSet()));
            foreach (var item in items.Where(i => i.Elements(Wpf + "MenuItem").Any()))
            {
                WalkMenu(item, $"{name}>{KeyOf((string?)item.Attribute("Header"))}");
            }
        }

        foreach (var menu in root.Descendants(Wpf + "ContextMenu"))
        {
            WalkMenu(menu, (string?)menu.Attribute(Xaml + "Key") ?? "menu");
            inMenus.UnionWith(menu.DescendantsAndSelf());
        }

        string[] controls = ["Label", "AccessText", "Button", "CheckBox", "RadioButton", "TabItem", "GroupBox"];
        HashSet<string> Keys(IEnumerable<XElement> elements) => elements
            .Where(e => !inMenus.Contains(e) && controls.Contains(e.Name.LocalName) && e.Name.Namespace == Wpf)
            .SelectMany(e => new[] { "Content", "Text", "Header" }.Select(a => KeyOf((string?)e.Attribute(a))))
            .OfType<string>()
            .ToHashSet();

        // Fenêtre à onglets (Paramètres) : seul l'onglet affiché a ses touches d'accès actives, WPF ignorant les éléments
        // invisibles ; chaque onglet forme donc un groupe avec le reste de la fenêtre (en-têtes des onglets, boutons du bas).
        var pages = root.Descendants(Wpf + "TabItem")
            .Select(t => (Name: (string?)t.Attribute(Xaml + "Name") ?? "TabItem",
                Content: t.Elements().Where(e => !e.Name.LocalName.EndsWith(".Header", StringComparison.Ordinal)).SelectMany(e => e.DescendantsAndSelf()).ToHashSet()))
            .Where(p => p.Content.Count > 0)
            .ToList();
        var inPages = pages.SelectMany(p => p.Content).ToHashSet();
        var common = Keys(root.Descendants().Where(e => !inPages.Contains(e)));
        groups.Add((file, common));
        groups.AddRange(pages.Select(p => ($"{file}:{p.Name}", common.Concat(Keys(p.Content)).ToHashSet())));
        return groups;
    }

    private static string? KeyOf(string? value) => value is not null && StaticString().Match(value) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^\{x:Static loc:Strings\.(\w+)\}$")]
    private static partial Regex StaticString();

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "ZillaTerm.App", "Views")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("src/ZillaTerm.App/Views");
    }
}
