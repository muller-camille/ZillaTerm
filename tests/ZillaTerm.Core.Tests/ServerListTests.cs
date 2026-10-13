using System.Text.Json;

namespace ZillaTerm.Core.Tests;

public class ServerListTests
{
    private static PvwaAccount Account(string id, string address = "srv") =>
        new() { Id = id, UserName = "root", Address = address, PlatformId = "UnixSSH", SafeName = "S" };

    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        SessionLibrary.AddFolder(settings, "Vide");
        var web = SessionLibrary.AddSession(settings, Account("1", "web01"), "pvwa", "Prod/Web");
        web.Mode = ConnectMode.Ssh;
        web.StartDirectory = "/var/log";
        web.Reason = "INC42";
        web.RememberTail("/var/log/messages");
        var db = SessionLibrary.AddSession(settings, Account("2", "db01"), "pvwa", "");
        db.Component = "PSM-RDP";
        db.RemoteMachine = "machine01";
        SessionLibrary.AddSession(settings, Account("3", "other"), "autre-pvwa", "");
        return settings;
    }

    [Fact]
    public void ExportsTheServersOfThisPvwaWithoutPersonalFiles()
    {
        var file = ServerListFile.Export(Settings(), "PVWA");
        var json = file.ToJson();

        Assert.Equal(ServerListFile.ExportFormat, file.Format);
        Assert.Equal(["root@db01", "root@web01"], file.Servers.Select(s => s.Name).Order());
        Assert.Equal(["Prod", "Prod/Web", "Vide"], file.Folders);
        Assert.DoesNotContain("messages", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pvwaHost", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"Ssh\"", json, StringComparison.Ordinal);

        var read = ServerListFile.Parse(json);
        Assert.False(read.IsShared);
        var web = read.Servers.Single(s => s.AccountId == "1");
        Assert.Equal(("Prod/Web", ConnectMode.Ssh, "/var/log", "INC42"), (web.Folder, web.Mode, web.StartDirectory, web.Reason));
        Assert.Equal("machine01", read.Servers.Single(s => s.AccountId == "2").RemoteMachine);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"Autre\"}")]
    [InlineData("{}")]
    public void RejectsWhatIsNotAServerList(string json) =>
        Assert.Throws<InvalidDataException>(() => ServerListFile.Parse(json));

    [Fact]
    public void RejectsANewerFormat()
    {
        var e = Assert.Throws<InvalidDataException>(() => ServerListFile.Parse("{\"format\":\"CyberArkTerm.Servers\",\"version\":2}"));
        Assert.Contains("2", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsingDropsServersWithoutAccountAndNormalizesFolders()
    {
        var file = ServerListFile.Parse("""
            {"format":"CyberArkTerm.Servers","folders":[" A / B ","a/b",""],
             "servers":[{"accountId":"","name":"x"},null,{"accountId":"7","folder":"\\C\\D\\","name":null}]}
            """);

        var server = Assert.Single(file.Servers);
        Assert.Equal(("C/D", ""), (server.Folder, server.Name));
        Assert.False(string.IsNullOrEmpty(server.Id));
        Assert.Equal(["A/B"], file.Folders);
        Assert.Equal(["A", "A/B", "C", "C/D"], file.AllFolders().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Liste partagée modifiée à la main : un motif n'est jamais repris (il serait envoyé au PVWA comme celui de chaque
    /// utilisateur) ; un serveur sans « id » garde le même identifiant d'une lecture à l'autre (sinon impossible à retirer).
    /// </summary>
    [Fact]
    public void SharedListsNeverCarryAReasonAndKeepStableIds()
    {
        const string Json = """
            {"format":"CyberArkTerm.SharedServers","pvwa":"pvwa",
             "servers":[{"accountId":"7","name":"web01","reason":"CHG123 approuvé"},{"accountId":"8","name":"db01"}]}
            """;
        var first = ServerListFile.Parse(Json);
        var second = ServerListFile.Parse(Json);

        Assert.All(first.Servers, s => Assert.Null(s.Reason));
        Assert.Equal(first.Servers.Select(s => s.Id), second.Servers.Select(s => s.Id));
        Assert.NotEqual(first.Servers[0].Id, first.Servers[1].Id);
        Assert.Null(first.Servers[0].ToSession("pvwa").Reason);

        // Un export personnel garde le motif par défaut de son auteur.
        var export = ServerListFile.Parse(Json.Replace("SharedServers", "Servers", StringComparison.Ordinal));
        Assert.Equal("CHG123 approuvé", export.Servers[0].Reason);
    }

    [Fact]
    public void ImportSkipsServersAlreadyThereAndAddsTheMissingFolders()
    {
        var source = Settings();
        var file = ServerListFile.Parse(ServerListFile.Export(source, "pvwa").ToJson());
        file.Servers.Add(new ServerEntry { AccountId = "9", Name = "new", Folder = "Recette/Batch", RemoteMachine = "m2" });

        var target = new AppSettings();
        SessionLibrary.AddSession(target, Account("1", "web01"), "pvwa", "Prod/Web").Mode = ConnectMode.Ssh;
        SessionLibrary.AddSession(target, Account("2", "db01"), "pvwa", "Autre");

        var plan = ServerImport.Plan(target, "pvwa", file);

        Assert.Equal(1, plan.Duplicates);
        Assert.Equal(["2", "9"], plan.Added.Select(s => s.AccountId).Order());
        Assert.Equal(["m2", "machine01"], plan.WithTargetMachine.Select(s => s.RemoteMachine!).Order());
        Assert.Equal(["Recette", "Recette/Batch", "Vide"], plan.NewFolders);

        Assert.Equal(2, plan.Apply(target, "pvwa"));
        Assert.Equal(4, target.Sessions.Count);
        Assert.All(target.Sessions, s => Assert.Equal("pvwa", s.PvwaHost));
        Assert.Equal(target.Sessions.Count, target.Sessions.Select(s => s.Id).Distinct().Count());
        Assert.Contains("Recette/Batch", target.SessionFolderList);
        Assert.Contains("Vide", target.SessionFolderList);
        Assert.Empty(ServerImport.Plan(target, "pvwa", file).Added);
    }

    [Fact]
    public void BuildsATreeFromAListFile()
    {
        var file = ServerListFile.Export(Settings(), "pvwa");
        var root = SessionLibrary.BuildTree(file.AllFolders(), file.Servers.Select(s => s.ToSession("pvwa", keepId: true)));

        Assert.Equal(["Prod", "Vide"], root.Folders.Select(f => f.Name));
        Assert.Equal("root@web01", root.Folders[0].Folders.Single().Sessions.Single().Name);
        Assert.Equal(file.Servers.Single(s => s.AccountId == "2").Id, root.Sessions.Single().Id);
        Assert.Equal(2, root.TotalSessions);
    }
}

public sealed class SharedServerListTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cat-shared-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static ServerEntry Server(string account, string name, string folder = "", string? reason = null) =>
        new() { AccountId = account, Name = name, Folder = folder, Reason = reason, Address = name, UserName = "root" };

    [Fact]
    public void CreatesAnEmptyListThatOthersCanOpen()
    {
        var created = SharedServerList.Create(PathOf("Équipe Unix.json"), "Équipe Unix", "pvwa", "alice (DOM\\alice)");

        Assert.Throws<IOException>(() => SharedServerList.Create(PathOf("Équipe Unix.json"), "x", "pvwa", "bob"));
        var opened = new SharedServerList(PathOf("Équipe Unix.json"));
        Assert.True(opened.Load());
        Assert.Equal("Équipe Unix", opened.Name);
        Assert.True(opened.Content!.IsShared);
        Assert.Equal(1, opened.Content.Revision);
        var change = Assert.Single(opened.Content.Changes);
        Assert.Equal((SharedAction.Created, "alice (DOM\\alice)"), (change.Action, change.By));
        Assert.Empty(opened.Versions());
        Assert.Equal(created.Path, opened.Path);
    }

    [Fact]
    public void ChangesFromSeveralPeopleAddUpWithTheirAuthorAndAVersionEach()
    {
        var path = PathOf("liste.json");
        var alice = SharedServerList.Create(path, "Liste", "pvwa", "alice");
        var bob = new SharedServerList(path);
        Assert.True(bob.Load());

        Assert.Equal(2, alice.Add([Server("1", "web01", "Prod", reason: "INC1"), Server("2", "db01")], "alice"));
        // Bob n'a pas relu la liste : sa modification part quand même de la version à jour.
        Assert.Equal(1, bob.Add([Server("3", "app01"), Server("1", "web01", "prod")], "bob"));
        Assert.Equal(0, alice.Add([Server("2", "db01")], "alice"));

        Assert.True(alice.Load());
        var content = alice.Content!;
        Assert.Equal(3, content.Revision);
        Assert.Equal(["app01", "db01", "web01"], content.Servers.Select(s => s.Name).Order());
        Assert.All(content.Servers, s => Assert.Null(s.Reason));
        Assert.Equal("bob", content.Servers.Single(s => s.Name == "app01").AddedBy);
        Assert.Equal(
            ["Created  by alice r1", "Added web01 by alice r2", "Added db01 by alice r2", "Added app01 by bob r3"],
            content.Changes.Select(c => $"{c.Action} {c.Server} by {c.By} r{c.Revision}"));
        Assert.Equal([2, 1], alice.Versions().Select(v => v.Revision));
        Assert.Equal(Path.Combine(_directory, "liste.versions"), alice.VersionsDirectory);

        var web = content.Servers.Single(s => s.Name == "web01");
        Assert.Equal(1, bob.Remove([web.Id, "absent"], "bob"));
        Assert.Equal(0, alice.Remove([web.Id], "alice"));
        Assert.True(bob.Load());
        Assert.Equal(4, bob.Content!.Revision);
        Assert.Equal((SharedAction.Removed, "web01", "Prod"), (bob.Content.Changes[^1].Action, bob.Content.Changes[^1].Server, bob.Content.Changes[^1].Folder));
    }

    [Fact]
    public void RestoringAVersionIsANewRevision()
    {
        var path = PathOf("liste.json");
        var list = SharedServerList.Create(path, "Liste", "pvwa", "alice");
        list.Add([Server("1", "web01")], "alice");
        list.Add([Server("2", "db01")], "bob");
        list.Remove(list.Content!.Servers.Select(s => s.Id).ToList(), "carol");
        Assert.Empty(list.Content!.Servers);

        var revision2 = list.Versions().Single(v => v.Revision == 2);
        list.Restore(revision2, "alice");

        Assert.Equal(5, list.Content!.Revision);
        Assert.Equal(["web01"], list.Content.Servers.Select(s => s.Name));
        Assert.Equal((SharedAction.Restored, "2"), (list.Content.Changes[^1].Action, list.Content.Changes[^1].Detail));
        Assert.Equal([4, 3, 2, 1], list.Versions().Select(v => v.Revision));
        Assert.True(new SharedServerList(path).Load());
    }

    /// <summary>Liste vidée par une écriture interrompue : « Restaurer » la remet d'aplomb au lieu d'échouer.</summary>
    [Fact]
    public void RestoringRepairsAnUnreadableList()
    {
        var path = PathOf("coupee.json");
        var list = SharedServerList.Create(path, "Liste", "pvwa", "alice");
        list.Add([Server("1", "web01")], "alice");
        list.Add([Server("2", "db01")], "bob");
        File.WriteAllText(path, "{\"format\":\"CyberArkTe");
        Assert.False(new SharedServerList(path).Load());

        list.Restore(list.Versions().Single(v => v.Revision == 2), "alice");

        var reread = new SharedServerList(path);
        Assert.True(reread.Load());
        Assert.Equal(3, reread.Content!.Revision);
        Assert.Equal(["web01"], reread.Content.Servers.Select(s => s.Name));
        Assert.Equal(SharedAction.Restored, reread.Content.Changes[^1].Action);
    }

    [Fact]
    public void KeepsOnlyTheLatestVersions()
    {
        var saved = SharedServerList.MaxVersions;
        SharedServerList.MaxVersions = 3;
        try
        {
            var list = SharedServerList.Create(PathOf("liste.json"), "Liste", "pvwa", "alice");
            for (int i = 0; i < 6; i++)
            {
                list.Add([Server(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"srv{i}")], "alice");
            }

            Assert.Equal([6, 5, 4], list.Versions().Select(v => v.Revision));
            Assert.Equal(7, list.Content!.Revision);
        }
        finally
        {
            SharedServerList.MaxVersions = saved;
        }
    }

    [Fact]
    public void AnExportIsNotASharedList()
    {
        var path = PathOf("export.json");
        File.WriteAllText(path, new ServerListFile { Format = ServerListFile.ExportFormat, Pvwa = "pvwa" }.ToJson());
        var list = new SharedServerList(path);

        Assert.True(list.Load());
        Assert.False(list.Content!.IsShared);
        Assert.Throws<InvalidDataException>(() => list.Add([Server("1", "web01")], "alice"));
        Assert.False(Directory.Exists(list.VersionsDirectory));
    }

    [Fact]
    public void AnUnreadableListReportsItsErrorAndKeepsThePreviousContent()
    {
        var path = PathOf("liste.json");
        var list = SharedServerList.Create(path, "Liste", "pvwa", "alice");
        File.WriteAllText(path, "{ cassé");

        Assert.False(list.Load());
        Assert.NotNull(list.Error);
        Assert.Equal("Liste", list.Name);
        Assert.Throws<InvalidDataException>(() => list.Add([Server("1", "web01")], "alice"));
        Assert.Equal("{ cassé", File.ReadAllText(path));
    }

    [Fact]
    public void AListBeingWrittenElsewhereIsReportedAsBusy()
    {
        var path = PathOf("liste.json");
        var list = SharedServerList.Create(path, "Liste", "pvwa", "alice");
        var (attempts, delay) = (SharedServerList.LockAttempts, SharedServerList.LockDelay);
        SharedServerList.LockAttempts = 3;
        SharedServerList.LockDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var e = Assert.Throws<IOException>(() => list.Add([Server("1", "web01")], "alice"));
                Assert.Equal(ZillaTerm.Core.Localization.CoreStrings.ServerListBusy, e.Message);
                Assert.False(list.Load());
            }

            Assert.Equal(1, list.Add([Server("1", "web01")], "alice"));
        }
        finally
        {
            (SharedServerList.LockAttempts, SharedServerList.LockDelay) = (attempts, delay);
        }
    }

    /// <summary>
    /// Liste modifiée à la main et réécrite avec un BOM UTF-8 (PowerShell 5.1, « UTF-8 avec BOM ») : lue, modifiable et
    /// restaurable comme sans BOM.
    /// </summary>
    [Fact]
    public void AListWrittenWithAByteOrderMarkIsRead()
    {
        var path = PathOf("equipe.json");
        var list = SharedServerList.Create(path, "Équipe", "pvwa", "alice");
        list.Add([Server("1", "web01")], "alice");
        File.WriteAllText(path, File.ReadAllText(path).Replace("web01", "web01-main", StringComparison.Ordinal), new System.Text.UTF8Encoding(true));
        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);

        var other = new SharedServerList(path);
        Assert.True(other.Load());
        Assert.Equal(["web01-main"], other.Content!.Servers.Select(s => s.Name));
        Assert.Equal(1, other.Add([Server("2", "db01")], "bob"));

        // La version gardée (avec son BOM) se restaure.
        other.Restore(other.Versions().Single(v => v.Revision == 2), "alice");
        Assert.True(list.Load());
        Assert.Equal(["web01-main"], list.Content!.Servers.Select(s => s.Name));
    }

    [Fact]
    public void WritesValidJsonWithTheJournal()
    {
        var path = PathOf("liste.json");
        var list = SharedServerList.Create(path, "Liste", "pvwa", "alice");
        list.Add([Server("1", "web01", "Prod")], "alice");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(ServerListFile.SharedFormat, root.GetProperty("format").GetString());
        Assert.Equal(2, root.GetProperty("revision").GetInt32());
        Assert.Equal("Added", root.GetProperty("changes")[1].GetProperty("action").GetString());
        Assert.Equal("alice", root.GetProperty("servers")[0].GetProperty("addedBy").GetString());
    }

    [Fact]
    public void WhoNamesTheCyberArkAndWindowsAccounts()
    {
        Assert.Equal($"jdoe ({Environment.UserDomainName}\\{Environment.UserName})", SharedServerList.Who("jdoe"));
        Assert.Equal($"{Environment.UserDomainName}\\{Environment.UserName}", SharedServerList.Who(" "));
    }
}
