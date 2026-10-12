using System.Globalization;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Tests;

public sealed class SafePermissionTextTests
{
    /// <summary>Chaque droit renvoyé par le PVWA a un libellé dans les trois langues de l'interface.</summary>
    /// <remarks>
    /// Langue du thread du test seulement : Strings.Culture vaut pour tout le processus et changerait la langue des
    /// tests qui tournent en même temps.
    /// </remarks>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void EveryPermissionHasALabel(string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(language);
            var labels = new SafePermissions().All().Select(p => SafePermissionText.Label(p.Name)).ToList();

            Assert.Equal(22, labels.Count);
            Assert.All(new SafePermissions().All(), p => Assert.NotEqual(p.Name, SafePermissionText.Label(p.Name)));
            Assert.Equal(labels.Count, labels.Distinct().Count());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
