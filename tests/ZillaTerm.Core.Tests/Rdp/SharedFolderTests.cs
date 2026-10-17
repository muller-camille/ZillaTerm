using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

/// <summary>
/// Dossiers du poste partagés à la place de tous les disques : noms et chemins acceptés (rien qui couperait l'option du
/// client FreeRDP, jamais un chemin relatif), liste vérifiée, et dossiers à partager à la connexion.
/// </summary>
public class SharedFolderTests
{
    [Theory]
    [InlineData("Partage", true)]
    [InlineData("logs_2026.v-1", true)]
    [InlineData("  Partage  ", true)]
    [InlineData("A23456789012345678901234567890AB", true)]
    [InlineData("A234567890123456789012345678901234", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Mon partage", false)]
    [InlineData("Données", false)]
    [InlineData("-Partage", false)]
    [InlineData(".", false)]
    [InlineData("a,b", false)]
    [InlineData("a\\b", false)]
    [InlineData("*", false)]
    [InlineData("%", false)]
    public void NamesAreAsciiWithoutSeparators(string? name, bool valid) => Assert.Equal(valid, SharedFolderRules.IsValidName(name));

    [Theory]
    [InlineData(@"C:\Partage", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"d:/logs", true)]
    [InlineData(@"C:\Users\Jean Dupont\Partage", true)]
    [InlineData(@"\\fs01\equipe\partage", true)]
    [InlineData(@"\\fs01\equipe", true)]
    [InlineData(@"%USERPROFILE%\Partage", true)]
    [InlineData(@"%USERPROFILE%", true)]
    [InlineData(@"%ProgramFiles(x86)%\Outil", true)]
    [InlineData(@"Partage", false)]
    [InlineData(@"C:Partage", false)]
    [InlineData(@"\Partage", false)]
    [InlineData(@"\\fs01", false)]
    [InlineData(@"\\fs01\", false)]
    [InlineData(@"\\?\C:\Partage", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"\\\fs01\equipe", false)]
    [InlineData(@"C:\Partage,D:\Autre", false)]
    [InlineData("C:\\Par\"tage", false)]
    [InlineData(@"C:\Partage\*", false)]
    [InlineData(@"C:\Partage:flux", false)]
    [InlineData("C:\\Par\ntage", false)]
    [InlineData(@"%USERPROFILE%Partage", false)]
    [InlineData(@"%USERPROFILE%\a:b", false)]
    [InlineData(@"%1\Partage", false)]
    [InlineData("*", false)]
    [InlineData("%", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PathsAreFullOrStartWithAVariable(string? path, bool valid) => Assert.Equal(valid, SharedFolderRules.IsValidPath(path));

    [Fact]
    public void TooLongPathIsRefused()
    {
        Assert.True(SharedFolderRules.IsValidPath(@"C:\" + new string('a', SharedFolderRules.MaxPathLength - 3)));
        Assert.False(SharedFolderRules.IsValidPath(@"C:\" + new string('a', SharedFolderRules.MaxPathLength - 2)));
    }

    [Fact]
    public void ListIsCheckedForNamesPathsDuplicatesAndCount()
    {
        Assert.Null(SharedFolderRules.Check([new SharedFolder("Partage", @"C:\Partage"), new SharedFolder("Logs", @"\\fs\logs")]));
        Assert.Null(SharedFolderRules.Check([]));
        Assert.Equal(SharedFolderProblem.InvalidName, SharedFolderRules.Check([new SharedFolder("Mon partage", @"C:\Partage")])?.Problem);
        Assert.Equal(SharedFolderProblem.InvalidName, SharedFolderRules.Check([null])?.Problem);
        Assert.Equal(SharedFolderProblem.InvalidPath, SharedFolderRules.Check([new SharedFolder("Partage", "Partage")])?.Problem);
        Assert.Equal(SharedFolderProblem.InvalidPath, SharedFolderRules.Check([new SharedFolder("Partage", null!)])?.Problem);
        var duplicate = SharedFolderRules.Check([new SharedFolder("Partage", @"C:\a"), new SharedFolder("PARTAGE", @"C:\b")]);
        Assert.Equal((SharedFolderProblem.DuplicateName, "PARTAGE"), duplicate);
        var many = Enumerable.Range(0, SharedFolderRules.MaxFolders + 1).Select(i => new SharedFolder($"D{i}", $@"C:\d{i}")).ToList();
        Assert.Equal(SharedFolderProblem.TooMany, SharedFolderRules.Check(many)?.Problem);
    }

    [Fact]
    public void SanitizeKeepsValidFoldersOnce()
    {
        var folders = SharedFolderRules.Sanitize(
        [
            new SharedFolder(" Partage ", @" C:\Partage "),
            new SharedFolder("partage", @"D:\Autre"),
            new SharedFolder("Relatif", "Partage"),
            null,
            new SharedFolder("Logs", @"%TEMP%\logs"),
        ]);
        Assert.Equal([new SharedFolder("Partage", @"C:\Partage"), new SharedFolder("Logs", @"%TEMP%\logs")], folders);
        Assert.Empty(SharedFolderRules.Sanitize(null));
        var many = Enumerable.Range(0, SharedFolderRules.MaxFolders + 5).Select(i => new SharedFolder($"D{i}", $@"C:\d{i}"));
        Assert.Equal(SharedFolderRules.MaxFolders, SharedFolderRules.Sanitize(many).Count);
    }

    /// <summary>
    /// À la connexion : variables remplacées ; un dossier absent, une variable inconnue ou un chemin devenu invalide une
    /// fois la variable remplacée ne sont pas partagés, et rendus à part.
    /// </summary>
    [Fact]
    public void ResolveExpandsVariablesAndKeepsExistingFoldersOnly()
    {
        var variables = new Dictionary<string, string>
        {
            ["%USERPROFILE%"] = @"C:\Users\jdupont",
            ["%ODD%"] = @"C:\Users\a,b",
        };
        string Expand(string path) => variables.Aggregate(path, (p, v) => p.Replace(v.Key, v.Value, StringComparison.OrdinalIgnoreCase));
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\jdupont\Partage", @"\\fs\logs", @"C:\Users\a,b\x" };

        var (shared, missing) = SharedFolderRules.Resolve(
        [
            new SharedFolder("Partage", @"%USERPROFILE%\Partage"),
            new SharedFolder("Logs", @"\\fs\logs"),
            new SharedFolder("Absent", @"D:\absent"),
            new SharedFolder("Inconnue", @"%NOPE%\x"),
            new SharedFolder("Virgule", @"%ODD%\x"),
        ], Expand, existing.Contains);

        Assert.Equal([new SharedFolder("Partage", @"C:\Users\jdupont\Partage"), new SharedFolder("Logs", @"\\fs\logs")], shared);
        Assert.Equal(["Absent", "Inconnue", "Virgule"], missing.Select(f => f.Name));
    }

    [Fact]
    public void DescriptionAndNetworkServer()
    {
        SharedFolder[] folders = [new("Partage", @"C:\Partage"), new("Logs", @"\\fs01\logs")];
        Assert.Equal(@"Partage (C:\Partage), Logs (\\fs01\logs)", SharedFolderRules.Describe(folders));
        Assert.Equal("fs01", SharedFolderRules.NetworkServer(folders));
        Assert.Null(SharedFolderRules.NetworkServer([new SharedFolder("Partage", @"%USERPROFILE%\Partage")]));
    }

    [Fact]
    public void DriveSharingFollowsTheTwoSettings()
    {
        Assert.Equal(DriveSharing.AllDrives, new AppSettings().DriveSharing());
        Assert.Equal(DriveSharing.Folders, new AppSettings { PsmShareFoldersOnly = true }.DriveSharing());
        Assert.Equal(DriveSharing.None, new AppSettings { PsmRedirectDrives = false, PsmShareFoldersOnly = true }.DriveSharing());
    }
}
