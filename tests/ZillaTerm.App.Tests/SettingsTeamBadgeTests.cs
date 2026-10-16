using ZillaTerm.App.Views;
using ZillaTerm.Core;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Paramètres : pastille « Équipe » sur les réglages que le fichier d'environnement de l'équipe définit et dont la
/// valeur est celle en vigueur.
/// </summary>
public sealed class SettingsTeamBadgeTests
{
    [Fact]
    public void BadgeOnlyWhereTheTeamValueIsInEffect()
    {
        var settings = new AppSettings { KeepPvwaSessionAlive = true, PsmRedirectDrives = true, ShareDirect = true, SharePort = 47001 };
        var team = new EnvironmentProfile { KeepPvwaSessionAlive = true, PsmRedirectDrives = false, SharePort = 47001 };

        var applied = SettingsDialog.TeamSettings([(@"\\fs01\equipe\zillaterm.env.json", team)], settings);

        // Disques : valeur refusée à l'import ou changée depuis, pas de pastille. Port : sur la ligne « Partage direct ».
        Assert.Equal([EnvironmentSetting.KeepPvwaSessionAlive, EnvironmentSetting.ShareDirect], applied.Keys.Order());
        Assert.All(applied.Values, path => Assert.Equal(@"\\fs01\equipe\zillaterm.env.json", path));
    }

    [Fact]
    public void LaterFileWinsAndEmptyFilesShowNothing()
    {
        var settings = new AppSettings { KeepPvwaSessionAlive = true };
        var local = new EnvironmentProfile { KeepPvwaSessionAlive = true };
        var central = new EnvironmentProfile { KeepPvwaSessionAlive = true };

        Assert.Equal(@"C:\central.env.json",
            SettingsDialog.TeamSettings([(@"C:\ZillaTerm.env.json", local), (@"C:\central.env.json", central)], settings)
                [EnvironmentSetting.KeepPvwaSessionAlive]);
        Assert.Empty(SettingsDialog.TeamSettings([(@"C:\ZillaTerm.env.json", new EnvironmentProfile())], settings));
        Assert.Empty(SettingsDialog.TeamSettings([], settings));
    }
}
