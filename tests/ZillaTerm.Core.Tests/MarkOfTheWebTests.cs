namespace ZillaTerm.Core.Tests;

public sealed class MarkOfTheWebTests
{
    /// <summary>
    /// Sous Windows, flux Zone.Identifier en zone Internet, avec l'adresse https seulement (jamais celle d'un serveur
    /// interne) ; ailleurs, rien n'est écrit (« fichier:Zone.Identifier » y serait un autre fichier) et rien n'échoue.
    /// </summary>
    [Fact]
    public void MarksAFileAsComingFromTheInternet()
    {
        var folder = Directory.CreateTempSubdirectory("zt-motw-").FullName;
        try
        {
            var path = Path.Combine(folder, "rapport.xlsm");
            File.WriteAllText(path, "contenu");

            bool written = MarkOfTheWeb.Apply(path, new Uri("https://github.com/example/releases/download/v1/a b.zip"));

            Assert.Equal("contenu", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(written);
                Assert.Null(MarkOfTheWeb.Read(path));
                Assert.Equal([path], Directory.GetFiles(folder));
                return;
            }

            Assert.True(written);
            Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/example/releases/download/v1/a%20b.zip\r\n", MarkOfTheWeb.Read(path));

            var other = Path.Combine(folder, "outil.exe");
            File.WriteAllText(other, "x");
            Assert.True(MarkOfTheWeb.Apply(other, new Uri("sftp://srv01.corp.local/opt/outil.exe")));
            Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", MarkOfTheWeb.Read(other));
            Assert.False(MarkOfTheWeb.Apply(Path.Combine(folder, "absent", "x.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
