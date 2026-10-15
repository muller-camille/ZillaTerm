using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ZillaTerm.App.Services;
using ZillaTerm.App.Views;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Desktop.Tests;

/// <summary>Noms lus par les lecteurs d'écran : sans le « _ » des touches d'accès, comme sous WPF.</summary>
public sealed class AccessibilityTests
{
    [AvaloniaFact]
    public void AccessKeyControlsAreNamedWithoutTheMarker()
    {
        var button = new Button { Content = "_Valider" };
        var check = new CheckBox { Content = "_Mémoriser" };
        var radio = new RadioButton { Content = "Mot de _passe" };
        var tab = new TabItem { Header = "_Général" };
        var named = new Button { Content = "_Copier" };
        AutomationProperties.SetName(named, "Copier le mot de passe");
        var window = new Window
        {
            Content = new StackPanel { Children = { button, check, radio, new TabControl { Items = { tab } }, named } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Valider", NameOf(button));
        Assert.Equal("Mémoriser", NameOf(check));
        Assert.Equal("Mot de passe", NameOf(radio));
        Assert.Equal("Général", NameOf(tab));
        Assert.Equal("Copier le mot de passe", NameOf(named));

        // Texte changé par le code (« Partager… » / « Gérer le partage… ») : le nom suit.
        button.Content = "_Fermer";
        Assert.Equal("Fermer", NameOf(button));
        button.Content = "Sans touche";
        Assert.Equal("Sans touche", NameOf(button));
        window.Close();
    }

    /// <summary>Terminal d'une session (non connectée ici) : nommé d'après la session, dans la langue de l'interface.</summary>
    [AvaloniaFact]
    public void TerminalIsNamedAfterItsSession()
    {
        using var _ = Language.Use("fr");
        var connector = new SshConnector("srv01.corp.local", 22, "jdoe", new NoInteraction());
        var session = new SshSession(null, "jdoe@srv01", connector, Dispatcher.UIThread, followTerminal: false, saved: null);
        var view = new SshSessionView(session, "jdoe@srv01.corp.local", "Connexion…");

        Assert.Equal("Terminal SSH : jdoe@srv01.corp.local", NameOf(view.Terminal));
    }

    [Theory]
    [InlineData("_Valider", "Valider")]
    [InlineData("Mot de _passe", "Mot de passe")]
    [InlineData("a__b _c", "a_b c")]
    [InlineData("_a _b", "a _b")]
    [InlineData("fin_", "fin_")]
    public void MarkerRemoval(string text, string expected) => Assert.Equal(expected, AccessKeyNames.WithoutAccessKey(text));

    private static string NameOf(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetName();

    /// <summary>Session jamais connectée : aucune question n'est posée.</summary>
    private sealed class NoInteraction : ISshInteraction
    {
        public bool CheckHostKey(string host, int port, string algorithm, string sha256Fingerprint) => false;

        public SshAnswer? Prompt(SshQuestion question) => null;
    }
}
